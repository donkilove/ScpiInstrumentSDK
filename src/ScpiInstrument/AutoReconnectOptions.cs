namespace ScpiInstrument;

/// <summary>
/// 自动重连策略配置。
/// </summary>
public sealed class AutoReconnectOptions
{
    public static AutoReconnectOptions Default { get; } = new();

    /// <summary>单次操作失败后最多自动重连次数（含首次失败后的重连），默认 3。</summary>
    public int MaxReconnectAttempts { get; init; } = 3;

    /// <summary>首次重连前等待的退避时间，默认 200ms。</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>退避时间上限（指数退避封顶），默认 5s。</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 自定义"是否值得重连"的异常判定。缺省时仅连接类异常（未连接/连接已断开/网络 IO 错误）触发重连。
    /// </summary>
    public Func<Exception, bool>? IsTransient { get; init; }
}
