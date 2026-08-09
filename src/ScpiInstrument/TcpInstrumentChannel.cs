using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScpiInstrument;

public class TcpInstrumentChannel : IInstrumentChannel, IDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly Encoding _encoding = Encoding.ASCII;
    private readonly object _lock = new();
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

        var cmd = scpiCommand + "\n";
        var buffer = _encoding.GetBytes(cmd);

        lock (_lock)
        {
            if (_stream is not { CanWrite: true }) throw new InvalidOperationException("连接已断开");
            _stream.Write(buffer, 0, buffer.Length);
            _stream.Flush();
        }

        using var reader = new StreamReader(_stream, _encoding, leaveOpen: true);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var response = await reader.ReadLineAsync(linkedCts.Token);
        return response ?? string.Empty;
    }

    public async Task SendAsync(string scpiCommand, CancellationToken ct = default)
    {
        if (_stream is not { CanWrite: true }) throw new InvalidOperationException("未连接仪器");
        if (_disposed) throw new ObjectDisposedException(nameof(TcpInstrumentChannel));

        var cmd = scpiCommand + "\n";
        var buf = _encoding.GetBytes(cmd);

        lock (_lock)
        {
            if (_stream is not { CanWrite: true }) throw new InvalidOperationException("连接已断开");
            _stream.Write(buf, 0, buf.Length);
            _stream.Flush();
        }

        await Task.CompletedTask;
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

        await SendAsync(scpiCommand, ct);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

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
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _stream?.Dispose();
        _client?.Dispose();
    }
}
