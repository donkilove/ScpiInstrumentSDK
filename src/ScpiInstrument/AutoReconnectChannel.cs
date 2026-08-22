using System.IO;
using System.Net.Sockets;

namespace ScpiInstrument;

/// <summary>
/// 自动重连装饰器：包装任意 <see cref="IInstrumentChannel"/>，在连接类异常时自动重连并重放原操作。
/// 协议/数据类错误不触发重连（避免掩盖真实问题）。
/// </summary>
public sealed class AutoReconnectChannel : IInstrumentChannel, IDisposable
{
    private readonly IInstrumentChannel _inner;
    private readonly AutoReconnectOptions _options;
    private string? _host;
    private int _port;

    public AutoReconnectChannel(IInstrumentChannel inner, AutoReconnectOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? AutoReconnectOptions.Default;
    }

    public bool IsConnected => _inner.IsConnected;

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        _host = host;
        _port = port;
        await _inner.ConnectAsync(host, port, ct);
    }

    public Task DisconnectAsync() => _inner.DisconnectAsync();

    public Task<string> QueryAsync(string scpiCommand, TimeSpan timeout, CancellationToken ct = default)
        => ExecuteWithReconnectAsync(token => _inner.QueryAsync(scpiCommand, timeout, token), ct);

    public Task SendAsync(string scpiCommand, CancellationToken ct = default)
        => ExecuteWithReconnectAsync(token => _inner.SendAsync(scpiCommand, token), ct);

    public Task SendManyAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
        => ExecuteWithReconnectAsync(token => _inner.SendManyAsync(commands, token), ct);

    public Task<byte[]> QueryBinaryAsync(
        string scpiCommand,
        int expectedBytes,
        TimeSpan timeout,
        CancellationToken ct = default)
        => ExecuteWithReconnectAsync(token => _inner.QueryBinaryAsync(scpiCommand, expectedBytes, timeout, token), ct);

    private async Task ExecuteWithReconnectAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken ct)
        => await ExecuteWithReconnectAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            ct).ConfigureAwait(false);

    private async Task<T> ExecuteWithReconnectAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        var attempt = 0;

        while (true)
        {
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                attempt++;
                if (attempt > _options.MaxReconnectAttempts)
                    throw new InvalidOperationException(
                        $"自动重连失败：已尝试 {_options.MaxReconnectAttempts} 次重连仍失败", ex);

                await Task.Delay(BackoffFor(attempt), ct).ConfigureAwait(false);
                await ReconnectAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private bool IsTransient(Exception ex)
        => _options.IsTransient is not null
            ? _options.IsTransient(ex)
            : IsConnectionError(ex);

    private static bool IsConnectionError(Exception ex) => ex switch
    {
        SocketException => true,
        IOException => true,
        InvalidOperationException => IsConnectionStateMessage(ex.Message),
        _ => false
    };

    private static bool IsConnectionStateMessage(string message)
        => message.Contains("未连接仪器", StringComparison.Ordinal)
            || message.Contains("连接已断开", StringComparison.Ordinal);

    private TimeSpan BackoffFor(int attempt)
    {
        var backoff = TimeSpan.FromMilliseconds(
            _options.InitialBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1));

        return backoff > _options.MaxBackoff ? _options.MaxBackoff : backoff;
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        if (_host is null)
            throw new InvalidOperationException("尚未建立过连接，无法自动重连");

        await _inner.ConnectAsync(_host, _port, ct);
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
