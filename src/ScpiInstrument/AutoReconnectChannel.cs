using System.IO;
using System.Net.Sockets;

namespace ScpiInstrument;

/// <summary>
/// 自动重连装饰器：包装任意 <see cref="IInstrumentChannel"/>，在连接类异常时自动重连并重放原操作。
/// 协议/数据类错误不触发重连（避免掩盖真实问题）。
/// 注意（审计 SC-09）：操作重放为 at-least-once 语义——写命令（SendAsync/SendManyAsync）
/// 在"写入成功但响应丢失"或重连重放场景下可能重复执行，调用方需保证写命令幂等。
/// 边界（审计 SC-03）：半开连接（对端不回复不关闭）由调用方 timeout 兜底抛
/// OperationCanceledException，不触发重连；对端关闭（EOF）抛 <see cref="ConnectionClosedException"/> 触发重连。
/// </summary>
public sealed class AutoReconnectChannel : IInstrumentChannel, IDisposable
{
    private readonly IInstrumentChannel _inner;
    private readonly AutoReconnectOptions _options;
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);   // 审计 SC-05：重连互斥（防并发失败操作重连风暴）
    private volatile string? _host;                              // 审计 SC-10：跨线程读写可见性
    private volatile int _port;

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
        // 审计 SC-03：EOF/连接关闭用独立类型判定（置于 InvalidOperationException 分支之前，
        // 模式匹配按声明顺序，子类分支在后会永远不命中）
        ConnectionClosedException => true,
        // 审计 SC-07：旧路径（InvalidOperationException + 中文消息匹配）保留为兼容层，
        // 新代码应抛 ConnectionClosedException 走类型判定
        InvalidOperationException => IsConnectionStateMessage(ex.Message),
        _ => false
    };

    private static bool IsConnectionStateMessage(string message)
        => message.Contains("未连接仪器", StringComparison.Ordinal)
            || message.Contains("连接已断开", StringComparison.Ordinal);

    /// <summary>
    /// 指数退避 + 随机抖动（±25%，Random.Shared 线程安全），防多通道同步重试共振（审计 SC-10）。
    /// 先 clamp 再构造 TimeSpan（防 attempt 大时 2^n 溢出 OverflowException，审计建议）；
    /// 抖动可使结果略超 MaxBackoff（±25% 上限内，属设计意图）。
    /// </summary>
    internal TimeSpan BackoffFor(int attempt)
    {
        var backoffMs = _options.InitialBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var cappedMs = Math.Min(backoffMs, _options.MaxBackoff.TotalMilliseconds);
        if (double.IsNaN(cappedMs))   // 病态配置（InitialBackoff=0 且 attempt≥1024 → 0×∞）
        {
            cappedMs = _options.MaxBackoff.TotalMilliseconds;
        }

        var jitter = cappedMs * 0.25 * (Random.Shared.NextDouble() * 2 - 1);
        return TimeSpan.FromMilliseconds(Math.Max(0, cappedMs + jitter));
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        if (_host is null)
            throw new InvalidOperationException("尚未建立过连接，无法自动重连");

        // 审计 SC-05：重连互斥——并发失败操作的重连排队串行执行（不再并发连接风暴）；
        // 连接建立本身幂等，串行多次重连无害（每次重连后重放操作，成功即止）
        await _reconnectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _inner.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        }
        finally
        {
            _reconnectGate.Release();
        }
    }

    /// <summary>
    /// SI-03：与在途重连并发时 <c>_reconnectGate.Dispose()</c> 会使在途操作抛
    /// <see cref="ObjectDisposedException"/> 并顶替原始连接异常。调用方须保证
    /// <b>Dispose 前所有在途操作与重连已完成</b>（静默期）——本类不提供并发 Dispose 防护。
    /// </summary>
    /// <summary>
    /// SI-03（方案 2b）：gate 不再 Dispose（<see cref="SemaphoreSlim.Dispose"/> 非线程安全且
    /// 仅释放惰性 WaitHandle——不使用 AvailableWaitHandle 时无需 Dispose）。在途重连与
    /// Dispose 并发时其 finally <c>Release()</c> 不再抛 ObjectDisposedException 顶替原异常。
    /// 批次 B 的静默期声明保留：重连中直接 Dispose 仍可能得到未预期的重连结果。
    /// </summary>
    public void Dispose()
    {
        (_inner as IDisposable)?.Dispose();
    }
}
