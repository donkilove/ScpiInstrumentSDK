namespace ScpiInstrument;

/// <summary>
/// 连接已关闭（EOF/对端关闭连接）。继承 <see cref="InvalidOperationException"/> 保持向后兼容，
/// 同时作为独立类型供自动重连判定（审计 SC-03/SC-07）：EOF 不再静默返回空串、
/// 不再依赖中文消息文本匹配。
/// </summary>
public sealed class ConnectionClosedException : InvalidOperationException
{
    public ConnectionClosedException(string message) : base(message)
    {
    }

    public ConnectionClosedException(string message, Exception inner) : base(message, inner)
    {
    }
}
