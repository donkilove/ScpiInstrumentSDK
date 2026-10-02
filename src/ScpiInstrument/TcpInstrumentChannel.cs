using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScpiInstrument;

public class TcpInstrumentChannel : IInstrumentChannel, IDisposable
{
    private const int MaxLineLength = 64 * 1024;   // 审计 SC-02：文本响应行长度上限（防畸形无限行）

    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly Encoding _encoding = Encoding.ASCII;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _queryGate = new(1, 1);   // 审计 SC-01：操作级串行化（写+读原子，防并发查询响应串扰）
    private bool _disposed;

    /// <summary>
    /// SI-04：流污染标志——查询超时/取消或 IO 错误后，仪器迟到的响应仍可能在途（连接未死），
    /// 下次操作写命令前须先排空（<see cref="DrainIfDirtyAsync"/>），防「下一个查询拿到
    /// 上一个查询的响应」错配。volatile：置脏（catch 路径）与读脏（gate 内）跨线程可见。
    /// </summary>
    private volatile bool _streamDirty;

    /// <summary>SI-04：排空静默期——持续无新数据的时长达到该值即认为迟到响应已排净。</summary>
    private static readonly TimeSpan StaleDrainQuietPeriod = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// SI-04（复核补修）：排空总预算上限——排空总时长达到该值即停止并抛出。
    /// 防对端以 &lt;100ms 间隔持续供数（异常/流模式仪器）时静默期永不满、
    /// 排空无限阻塞（把「错配」换成「挂起」）。
    /// </summary>
    private static readonly TimeSpan StaleDrainMaxBudget = TimeSpan.FromMilliseconds(2000);

    public bool IsConnected => _client is { Connected: true };

    /// <summary>
    /// 建立到仪器的 TCP 连接（5 秒连接超时）。
    /// </summary>
    /// <remarks>
    /// SI-05：<paramref name="host"/> 仅接受 IP 字面量（如 192.168.1.100）——
    /// 内部按 <see cref="IPAddress.TryParse"/> 校验；主机名/localhost 不支持，
    /// 传入即抛 <see cref="ArgumentException"/>。需要主机名解析时请调用方自行
    /// <see cref="System.Net.Dns.GetHostAddresses(string)"/> 后传 IP。
    /// </remarks>
    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TcpInstrumentChannel));

        if (!IPAddress.TryParse(host, out _))
            throw new ArgumentException($"Invalid IP address: {host}", nameof(host));

        if (port <= 0 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 1-65535 范围内");

        // 审计 SC-04：连接建立整体串行化——并发 ConnectAsync 时"关旧+新建+赋值"原子，
        // 消除状态替换竞态（此前新连接创建/赋值在锁外，后完成的覆盖先完成的导致连接泄漏）
        await _queryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_client is { Connected: true })
                {
                    try { _stream?.Close(); } catch { }
                    try { _client?.Close(); } catch { }
                    _stream = null;
                    _client = null;
                }
            }

            var newClient = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await newClient.ConnectAsync(host, port).WaitAsync(timeoutCts.Token);
                var newStream = newClient.GetStream();
                newStream.ReadTimeout = 5000;
                newStream.WriteTimeout = 5000;   // 审计 SC-11：同步写超时（防写阻塞无限挂起）
                // 审计 SC-02：不再使用 StreamReader——其内部缓冲会吞掉文本行之后的字节，
                // 与 QueryBinaryAsync 的裸流读取交错时丢字节；统一字节级读取
                _client = newClient;
                _stream = newStream;
                _streamDirty = false;   // SI-04：新流无历史残留，脏标志失效
            }
            catch
            {
                newClient.Dispose();
                throw;
            }
        }
        finally
        {
            _queryGate.Release();
        }
    }

    public async Task<string> QueryAsync(string scpiCommand, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_stream is not { CanWrite: true }) throw new InvalidOperationException("未连接仪器");
        if (_disposed) throw new ObjectDisposedException(nameof(TcpInstrumentChannel));

        var cmd = scpiCommand + "\n";
        var buffer = _encoding.GetBytes(cmd);

        // 审计 SC-01：写+读整体串行化——并发查询时响应与命令一一对应，不再串扰
        // SI-01：排队仅受外部取消约束——timeout 是 IO 预算，拿到 gate 后才启动（不再被排队挤占）
        await _queryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // SI-04：上次超时/取消留下的迟到响应先排空，防本次查询错配（排空计入 timeout 预算）
            await DrainIfDirtyAsync(linkedCts.Token).ConfigureAwait(false);

            lock (_lock)
            {
                if (_stream is not { CanWrite: true }) throw new InvalidOperationException("连接已断开");
                _stream.Write(buffer, 0, buffer.Length);
                _stream.Flush();
            }

            return await ReadResponseLineAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            // SI-04：超时/取消后仪器迟到响应会污染流（连接未死），IO 错误后流内容同样不可信——
            // 置脏，下次操作写命令前排空。ConnectionClosedException 继承 InvalidOperationException，
            // 不命中本过滤器（连接已死，重连即新流，无需置脏）。
            _streamDirty = true;
            throw;
        }
        finally
        {
            _queryGate.Release();
        }
    }

    public async Task SendAsync(string scpiCommand, CancellationToken ct = default)
    {
        if (_stream is not { CanWrite: true }) throw new InvalidOperationException("未连接仪器");
        if (_disposed) throw new ObjectDisposedException(nameof(TcpInstrumentChannel));

        var cmd = scpiCommand + "\n";
        var buf = _encoding.GetBytes(cmd);

        // 审计 SC-01：与查询互斥（避免写命令穿插在查询的读写之间）
        await _queryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // SI-04：上次超时/取消留下的迟到响应先排空，防污染后续查询
            await DrainIfDirtyAsync(ct).ConfigureAwait(false);

            lock (_lock)
            {
                if (_stream is not { CanWrite: true }) throw new InvalidOperationException("连接已断开");
                _stream.Write(buf, 0, buf.Length);
                _stream.Flush();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            _streamDirty = true;   // SI-04：写错误后流内容不可信（注释详意见 QueryAsync）
            throw;
        }
        finally
        {
            _queryGate.Release();
        }
    }

    public async Task SendManyAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
    {
        if (_stream is not { CanWrite: true }) throw new InvalidOperationException("未连接仪器");
        if (_disposed) throw new ObjectDisposedException(nameof(TcpInstrumentChannel));

        // 合并为一条 TCP 消息（\n 分隔）：SCPI 仪器顺序执行，省去逐条往返
        var buf = _encoding.GetBytes(string.Join('\n', commands) + "\n");

        // 审计 SC-01：与查询互斥；合并消息整体发送不受影响
        await _queryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // SI-04：上次超时/取消留下的迟到响应先排空，防污染后续查询
            await DrainIfDirtyAsync(ct).ConfigureAwait(false);

            lock (_lock)
            {
                if (_stream is not { CanWrite: true }) throw new InvalidOperationException("连接已断开");
                _stream.Write(buf, 0, buf.Length);
                _stream.Flush();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            _streamDirty = true;   // SI-04：写错误后流内容不可信（注释详意见 QueryAsync）
            throw;
        }
        finally
        {
            _queryGate.Release();
        }
    }

    /// <summary>
    /// 发送 SCPI 命令并读取二进制响应。
    /// </summary>
    /// <remarks>
    /// SI-06：<paramref name="expectedBytes"/> 的语义随响应形态而定——
    /// ① 响应首字节为 <c>#</c>（SCPI 定长二进制块）时，按块头声明的长度读取并返回，
    /// <paramref name="expectedBytes"/> <b>不参与校验</b>（仅作为非块模式上限参照，返回长度可与
    /// 其不同）；② 非块模式时 <paramref name="expectedBytes"/> 为精确读取字节数，
    /// 不足即抛 <see cref="ConnectionClosedException"/>。
    /// </remarks>
    public async Task<byte[]> QueryBinaryAsync(
        string scpiCommand,
        int expectedBytes,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TcpInstrumentChannel));

        const int maxReadSize = 4 * 1024 * 1024;
        if (expectedBytes <= 0 || expectedBytes > maxReadSize)
            throw new ArgumentOutOfRangeException(nameof(expectedBytes), $"expectedBytes 必须在 1-{maxReadSize} 范围内");

        // 审计 SC-01：写+读整体串行化（不再经 SendAsync 避免 gate 重入死锁）
        // SI-01：排队仅受外部取消约束——timeout 是 IO 预算，拿到 gate 后才启动
        await _queryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // 审计 SC-06：_stream 防御守卫（未连接/连接已断时不抛裸 NRE）
            if (_stream is not { CanWrite: true }) throw new InvalidOperationException("未连接仪器");

            // SI-04：上次超时/取消留下的迟到响应先排空，防本次二进制读取错位（计入 timeout 预算）
            await DrainIfDirtyAsync(linkedCts.Token).ConfigureAwait(false);

            var cmd = scpiCommand + "\n";
            var buf = _encoding.GetBytes(cmd);
            lock (_lock)
            {
                if (_stream is not { CanWrite: true }) throw new InvalidOperationException("连接已断开");
                _stream.Write(buf, 0, buf.Length);
                _stream.Flush();
            }

            var firstByte = await ReadByteAsync(_stream!, linkedCts.Token);
            if (firstByte == (byte)'#')
                return await ReadScpiDefiniteLengthBlockAsync(_stream!, maxReadSize, linkedCts.Token);

            var buffer = new byte[expectedBytes];
            buffer[0] = firstByte;
            var totalRead = 1;

            while (totalRead < expectedBytes)
            {
                var read = await _stream!.ReadAsync(buffer, totalRead, expectedBytes - totalRead, linkedCts.Token);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead != expectedBytes)
                throw new ConnectionClosedException($"数据不完整: 期望 {expectedBytes} 字节，实际收到 {totalRead} 字节（连接已关闭）");   // 审计建议：与 ReadExactAsync 的 EOF 语义统一

            return buffer;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            _streamDirty = true;   // SI-04：超时/IO 错误后流内容不可信（注释详意见 QueryAsync）
            throw;
        }
        finally
        {
            _queryGate.Release();
        }
    }

    private static async Task<byte[]> ReadScpiDefiniteLengthBlockAsync(
        NetworkStream stream,
        int maxReadSize,
        CancellationToken ct)
    {
        var lengthDigitByte = await ReadByteAsync(stream, ct);
        if (lengthDigitByte < (byte)'1' || lengthDigitByte > (byte)'9')
            throw new InvalidOperationException($"不支持的 SCPI binary block 头: #{(char)lengthDigitByte}");

        var lengthDigitCount = lengthDigitByte - (byte)'0';
        var lengthBytes = await ReadExactAsync(stream, lengthDigitCount, ct);
        var lengthText = Encoding.ASCII.GetString(lengthBytes);
        if (!int.TryParse(lengthText, out var payloadLength))
            throw new InvalidOperationException($"SCPI binary block 长度无效: {lengthText}");

        if (payloadLength <= 0 || payloadLength > maxReadSize)
            throw new InvalidOperationException($"SCPI binary block 长度超出范围: {payloadLength}");

        var payload = await ReadExactAsync(stream, payloadLength, ct);
        await ConsumeOptionalBlockTerminatorAsync(stream, ct);
        return payload;
    }

    private static async Task ConsumeOptionalBlockTerminatorAsync(NetworkStream stream, CancellationToken ct)
    {
        // 审计 SC-02：CTS 带超时无条件读取（替代 DataAvailable 轮询）——可取消、
        // 无忙轮询；块尾迟到时仍能等到，不因轮询相位错过
        var first = await TryReadByteWithTimeoutAsync(stream, TimeSpan.FromMilliseconds(100), ct);
        if (first is null)
            return;

        if (first == (byte)'\n')
            return;

        if (first == (byte)'\r')
        {
            var second = await TryReadByteWithTimeoutAsync(stream, TimeSpan.FromMilliseconds(20), ct);
            if (second is null || second == (byte)'\n')
                return;

            throw new InvalidOperationException($"SCPI binary block 结束符无效: 0x{second.Value:X2}");
        }

        throw new InvalidOperationException($"SCPI binary block 结束符无效: 0x{first.Value:X2}");
    }

    private static async Task<byte?> TryReadByteWithTimeoutAsync(
        NetworkStream stream,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var buf = new byte[1];
        try
        {
            var n = await stream.ReadAsync(buf.AsMemory(0, 1), cts.Token).ConfigureAwait(false);
            return n == 0 ? null : buf[0];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;   // 超时无数据
        }
    }

    /// <summary>
    /// SI-04：查询超时/取消后仪器迟到响应会污染流（连接未死、响应仍在途），下次操作
    /// 写命令前排空可读字节，防「下一个查询拿到上一个查询的响应」错配。
    /// 排空策略（TryReadByteWithTimeoutAsync 风格）：单次读等待 100ms；读到数据重置静默
    /// 计时，静默累计满 <see cref="StaleDrainQuietPeriod"/> 无新数据视为迟到响应已排净，
    /// 清脏返回。读到 EOF：流已关闭，保持脏标志（重连后由 ConnectAsync 重置）并抛
    /// <see cref="ConnectionClosedException"/> 传播给 AutoReconnect 触发重连。
    /// 总预算（复核补修）：排空总时长达到 <see cref="StaleDrainMaxBudget"/> 仍未排净
    /// （对端持续供数）→ <b>保持脏标志</b>并抛 <see cref="InvalidOperationException"/>
    /// （消息不含连接状态关键词，不触发 AutoReconnect 自动重连——流未排净，重建连接是
    /// 唯一安全出路，交由调用方决策），杜绝「无限排空挂起」。
    /// 须在 <c>_queryGate</c> 内调用（与既有操作互斥，防并发排空/读写交错）。
    /// </summary>
    private async Task DrainIfDirtyAsync(CancellationToken ct)
    {
        if (!_streamDirty)
        {
            return;
        }

        var stream = _stream;
        if (stream is null)
        {
            _streamDirty = false;   // 无流可排（未连接场景），脏标志失去意义
            return;
        }

        var quiet = Stopwatch.StartNew();
        var total = Stopwatch.StartNew();
        while (true)
        {
            if (total.ElapsedMilliseconds >= StaleDrainMaxBudget.TotalMilliseconds)
            {
                // 保持 _streamDirty = true：流未排净，不得继续在此流上操作（防错配回归）。
                // 本异常不命中四操作的置脏过滤器（InvalidOperationException），脏标志原样保留。
                throw new InvalidOperationException(
                    $"排空迟到响应超出总预算（{StaleDrainMaxBudget.TotalMilliseconds:0}ms）：对端持续供数据，流未排净，请断开并重建连接后再操作");
            }

            var b = await TryReadByteDiscerningEofAsync(stream, TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            if (b is null)
            {
                if (quiet.ElapsedMilliseconds >= StaleDrainQuietPeriod.TotalMilliseconds)
                {
                    _streamDirty = false;
                    return;
                }

                continue;   // 静默未满 150ms：迟到响应可能仍在途，继续等
            }

            quiet.Restart();   // 读到数据（已丢弃）：重置静默计时
        }
    }

    /// <summary>
    /// SI-04 排空用单字节读：返回 null = 超时无数据；EOF（read==0）抛 ConnectionClosedException
    /// （与 <see cref="TryReadByteWithTimeoutAsync"/> 把 EOF 折叠为 null 不同——排空场景 EOF
    /// 是必须传播给调用方/AutoReconnect 的连接事件，不得静默吞掉）。
    /// </summary>
    private static async Task<byte?> TryReadByteDiscerningEofAsync(
        NetworkStream stream,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var buf = new byte[1];
        try
        {
            var n = await stream.ReadAsync(buf.AsMemory(0, 1), cts.Token).ConfigureAwait(false);
            if (n == 0)
            {
                throw new ConnectionClosedException("排空迟到响应时读到 EOF：连接已关闭");
            }

            return buf[0];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;   // 超时无数据
        }
    }

    /// <summary>逐字节读取文本响应行（至 '\n'，过滤 '\r'）；审计 SC-02：与二进制读取共用字节级通道，无缓冲混用。</summary>
    private async Task<string> ReadResponseLineAsync(CancellationToken ct)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("未连接仪器");
        }

        var bytes = new List<byte>(64);
        while (true)
        {
            var b = await ReadByteAsync(_stream, ct).ConfigureAwait(false);
            if (b == (byte)'\n')
            {
                break;
            }

            if (b != (byte)'\r')
            {
                bytes.Add(b);
            }

            if (bytes.Count > MaxLineLength)
            {
                throw new InvalidOperationException($"响应行长度超限：{MaxLineLength} 字节");
            }
        }

        return _encoding.GetString(bytes.ToArray());
    }

    private static async Task<byte> ReadByteAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, 0, 1, ct);
        if (read == 0)
            throw new ConnectionClosedException("响应流已结束：连接已关闭");   // 审计 SC-03：EOF 抛连接类异常（触发重连）

        return buffer[0];
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken ct)
    {
        var buffer = new byte[length];
        var totalRead = 0;

        while (totalRead < length)
        {
            var read = await stream.ReadAsync(buffer, totalRead, length - totalRead, ct);
            if (read == 0)
                throw new ConnectionClosedException($"数据不完整: 期望 {length} 字节，实际收到 {totalRead} 字节（连接已关闭）");   // 审计 SC-03：中途 EOF 属连接关闭

            totalRead += read;
        }

        return buffer;
    }

    public async Task DisconnectAsync()
    {
        // 审计建议：与 ConnectAsync 对称取 gate——防断开与在途操作交错（_stream 中途置空）
        await _queryGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                try
                {
                    _stream?.Close();
                    _client?.Close();
                }
                catch
                {
                }
                finally
                {
                    _stream = null;
                    _client = null;
                }

                _streamDirty = false;   // SI-04：流已销毁，脏标志失效（重连后即为新流）
            }
        }
        finally
        {
            _queryGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // 审计 SC-11/复审：DisconnectAsync 会等待在途操作（有界——查询受超时约束、
            // 写受 WriteTimeout 约束、连接受连接超时约束）后清理；sync-over-async 无死锁
            // 风险（无 SynchronizationContext 依赖），但最长阻塞可达在途操作超时
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _stream?.Dispose();
        _client?.Dispose();
    }
}
