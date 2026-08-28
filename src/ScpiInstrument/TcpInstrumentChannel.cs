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

    public bool IsConnected => _client is { Connected: true };

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

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // 审计 SC-01：写+读整体串行化——并发查询时响应与命令一一对应，不再串扰
        await _queryGate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_stream is not { CanWrite: true }) throw new InvalidOperationException("连接已断开");
                _stream.Write(buffer, 0, buffer.Length);
                _stream.Flush();
            }

            return await ReadResponseLineAsync(linkedCts.Token).ConfigureAwait(false);
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
            lock (_lock)
            {
                if (_stream is not { CanWrite: true }) throw new InvalidOperationException("连接已断开");
                _stream.Write(buf, 0, buf.Length);
                _stream.Flush();
            }
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
            lock (_lock)
            {
                if (_stream is not { CanWrite: true }) throw new InvalidOperationException("连接已断开");
                _stream.Write(buf, 0, buf.Length);
                _stream.Flush();
            }
        }
        finally
        {
            _queryGate.Release();
        }
    }

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

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // 审计 SC-01：写+读整体串行化（不再经 SendAsync 避免 gate 重入死锁）
        await _queryGate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            // 审计 SC-06：_stream 防御守卫（未连接/连接已断时不抛裸 NRE）
            if (_stream is not { CanWrite: true }) throw new InvalidOperationException("未连接仪器");

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
                throw new InvalidOperationException($"数据不完整: 期望 {expectedBytes} 字节，实际收到 {totalRead} 字节");

            return buffer;
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
        }

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // 审计 SC-11：DisconnectAsync 为纯同步清理（无真实异步等待），
            // sync-over-async 无死锁风险（无 SynchronizationContext 依赖）
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _stream?.Dispose();
        _client?.Dispose();
    }
}
