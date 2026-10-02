namespace ScpiInstrument;

public interface IInstrumentChannel
{
    Task ConnectAsync(string host, int port, CancellationToken ct);
    Task DisconnectAsync();
    Task<string> QueryAsync(string scpiCommand, TimeSpan timeout, CancellationToken ct = default);
    Task SendAsync(string scpiCommand, CancellationToken ct = default);

    /// <summary>批量发送多条命令（合并为一条 TCP 消息，\n 分隔，仪器顺序执行）——减少往返，加速配置类命令序列</summary>
    Task SendManyAsync(IReadOnlyList<string> commands, CancellationToken ct = default);

    /// <summary>发送 SCPI 命令并读取二进制响应（SI-06：<c>expectedBytes</c> 语义随响应形态而定——
    /// 响应首字节为 <c>#</c>（SCPI 定长块）时按块头声明长度读取返回，<c>expectedBytes</c> 不参与校验；
    /// 非块模式时 <c>expectedBytes</c> 为精确读取字节数，不足抛 <see cref="ConnectionClosedException"/>）</summary>
    Task<byte[]> QueryBinaryAsync(string scpiCommand, int expectedBytes, TimeSpan timeout, CancellationToken ct = default);
    bool IsConnected { get; }
}
