using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScpiInstrument;

public class TcpInstrumentChannel : IInstrumentChannel, IDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private StreamReader? _reader;   // 连接生命周期内复用：避免每次查询新建缓冲导致粘包残留/字节丢失
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

        _client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await _client.ConnectAsync(host, port).WaitAsync(timeoutCts.Token);
            _stream = _client.GetStream();
            _stream.ReadTimeout = 5000;
            _reader = new StreamReader(_stream, _encoding, leaveOpen: true);
        }
        catch
        {
            _client?.Dispose();
            _client = null;
            _stream = null;
            throw;
        }
    }

    public async Task<string> QueryAsync(string scpiCommand, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_stream is not { CanWrite: true }) throw new InvalidOperationException("未连接仪器");
        if (_disposed) throw new ObjectDisposedException(nameof(TcpInstrumentChannel));
        if (_reader is null) throw new InvalidOperationException("未连接仪器");

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

            var response = await _reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
            return response ?? string.Empty;
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
        var first = await TryReadAvailableByteAsync(stream, TimeSpan.FromMilliseconds(100), ct);
        if (first is null)
            return;

        if (first == (byte)'\n')
            return;

        if (first == (byte)'\r')
        {
            var second = await TryReadAvailableByteAsync(stream, TimeSpan.FromMilliseconds(20), ct);
            if (second is null || second == (byte)'\n')
                return;

            throw new InvalidOperationException($"SCPI binary block 结束符无效: 0x{second.Value:X2}");
        }

        throw new InvalidOperationException($"SCPI binary block 结束符无效: 0x{first.Value:X2}");
    }

    private static async Task<byte?> TryReadAvailableByteAsync(
        NetworkStream stream,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!stream.DataAvailable)
        {
            if (sw.Elapsed >= timeout)
                return null;

            await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
        }

        return await ReadByteAsync(stream, ct);
    }

    private static async Task<byte> ReadByteAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, 0, 1, ct);
        if (read == 0)
            throw new InvalidOperationException("未收到二进制数据");

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
                throw new InvalidOperationException($"数据不完整: 期望 {length} 字节，实际收到 {totalRead} 字节");

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
                _reader?.Dispose();
                _stream?.Close();
                _client?.Close();
            }
            catch
            {
            }
            finally
            {
                _reader = null;
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
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _reader?.Dispose();
        _stream?.Dispose();
        _client?.Dispose();
    }
}
