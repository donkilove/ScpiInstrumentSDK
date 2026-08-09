namespace ScpiInstrument;

public interface IInstrumentChannel
{
    Task ConnectAsync(string host, int port, CancellationToken ct);
    Task DisconnectAsync();
    Task<string> QueryAsync(string scpiCommand, TimeSpan timeout, CancellationToken ct = default);
    Task SendAsync(string scpiCommand, CancellationToken ct = default);
    Task<byte[]> QueryBinaryAsync(string scpiCommand, int expectedBytes, TimeSpan timeout, CancellationToken ct = default);
    bool IsConnected { get; }
}
