using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScpiInstrument.Tests;

/// <summary>
/// M66（规格 <c>MachineWorker/docs/specs/m66-query-timeout-budget.md</c>，修复审查 SI-01）：
/// 查询的 <c>timeout</c> 是<b>纯 IO 预算</b>（从拿到 gate 起算，覆盖写+读）——
/// 排队等待不消耗预算（仅受外部取消约束）。此前 CTS 在入队前创建，慢查询占用 gate 时
/// 后续查询会在排队期就超时，且排队期超时的 OCE 与外部取消不可区分。
/// </summary>
public class QueryTimeoutBudgetTests
{
    /// <summary>AC-1：慢查询（600ms）占用 gate 时，300ms 预算的后续文本查询排队不超时。</summary>
    [Fact]
    public async Task QueryAsync_TimeoutBudgetStartsAfterGate_NotWhileQueued()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();

            var first = await ReadAsciiLineAsync(stream);
            Assert.Equal("SLOW?", first);
            await Task.Delay(600);   // 慢查询占用 gate
            await stream.WriteAsync(Encoding.ASCII.GetBytes("SLOW-OK\n"));
            await stream.FlushAsync();

            var second = await ReadAsciiLineAsync(stream);
            Assert.Equal("FAST?", second);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("FAST-OK\n"));
            await stream.FlushAsync();
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var slowTask = channel.QueryAsync("SLOW?", TimeSpan.FromSeconds(5));
        var fastResult = await channel.QueryAsync("FAST?", TimeSpan.FromMilliseconds(300));

        Assert.Equal("SLOW-OK", await slowTask);
        Assert.Equal("FAST-OK", fastResult);
        await serverTask;
    }

    /// <summary>AC-2：二进制查询同语义（排队不占预算）。</summary>
    [Fact]
    public async Task QueryBinaryAsync_TimeoutBudgetStartsAfterGate_NotWhileQueued()
    {
        var payload = Enumerable.Range(0, 12).Select(i => (byte)(i + 1)).ToArray();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();

            var first = await ReadAsciiLineAsync(stream);
            Assert.Equal("SLOWBIN?", first);
            await Task.Delay(600);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"#2{payload.Length:D2}"));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();

            var second = await ReadAsciiLineAsync(stream);
            Assert.Equal("FASTBIN?", second);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"#2{payload.Length:D2}"));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var slowTask = channel.QueryBinaryAsync("SLOWBIN?", expectedBytes: 4096, timeout: TimeSpan.FromSeconds(5));
        var fastResult = await channel.QueryBinaryAsync("FASTBIN?", expectedBytes: 4096, timeout: TimeSpan.FromMilliseconds(300));

        Assert.Equal(payload, await slowTask);
        Assert.Equal(payload, fastResult);
        await serverTask;
    }

    /// <summary>AC-3：排队期间外部取消仍即时取消（排队不是不可取消的黑洞）。</summary>
    [Fact]
    public async Task QueryAsync_CancelledWhileQueued_ThrowsOperationCanceled()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();

            var first = await ReadAsciiLineAsync(stream);
            Assert.Equal("SLOW?", first);
            await Task.Delay(600);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("SLOW-OK\n"));
            await stream.FlushAsync();
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var slowTask = channel.QueryAsync("SLOW?", TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var queuedTask = channel.QueryAsync("QUEUED?", TimeSpan.FromSeconds(5), cts.Token);
        await Task.Delay(100);   // 确保排队者已入队
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedTask);
        Assert.Equal("SLOW-OK", await slowTask);   // 排队者取消不影响在途慢查询
        await serverTask;
    }

    /// <summary>AC-4：IO 阶段超时仍抛 OCE（类型不变，调用方以 OCE 判定仪器超时）。</summary>
    [Fact]
    public async Task QueryAsync_IoTimeout_StillThrowsOperationCanceled()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ReadAsciiLineAsync(stream);   // 读到命令后刻意不响应
            await Task.Delay(1000);             // 保持连接存活（早于超时关闭会变成 ConnectionClosed 而非超时）
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => channel.QueryAsync("NORESP?", TimeSpan.FromMilliseconds(200)));
        await serverTask;
    }

    private static async Task<string> ReadAsciiLineAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0 || buffer[0] == 10)
                break;

            if (buffer[0] != 13)
                bytes.Add(buffer[0]);
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
