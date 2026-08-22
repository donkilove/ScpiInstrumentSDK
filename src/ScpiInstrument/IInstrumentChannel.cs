namespace ScpiInstrument;

public interface IInstrumentChannel
{
    Task ConnectAsync(string host, int port, CancellationToken ct);
    Task DisconnectAsync();
    Task<string> QueryAsync(string scpiCommand, TimeSpan timeout, CancellationToken ct = default);
    Task SendAsync(string scpiCommand, CancellationToken ct = default);

    /// <summary>批量发送多条命令（合并为一条 TCP 消息，\n 分隔，仪器顺序执行）——减少往返，加速配置类命令序列</summary>
    Task SendManyAsync(IReadOnlyList<string> commands, CancellationToken ct = default);

    Task<byte[]> QueryBinaryAsync(string scpiCommand, int expectedBytes, TimeSpan timeout, CancellationToken ct = default);
    bool IsConnected { get; }
}
