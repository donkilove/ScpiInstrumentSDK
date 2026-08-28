using System.Net;
using System.Net.Sockets;
using System.Text;
using ScpiInstrument.SpectrumAnalyzers;

namespace ScpiInstrument.Tests;

public class TcpInstrumentChannelTests
{
    [Fact]
    public async Task QueryBinaryAsync_reads_scpi_binary_block_payload_declared_length()
    {
        var payload = Enumerable.Range(0, 12).Select(i => (byte)(i + 1)).ToArray();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var command = await ReadAsciiLineAsync(stream);

            Assert.Equal(SpectrumAnalyzerCommands.QueryTrace1(), command);

            var header = Encoding.ASCII.GetBytes($"#2{payload.Length:D2}");
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var result = await channel.QueryBinaryAsync(
            SpectrumAnalyzerCommands.QueryTrace1(),
            expectedBytes: 4096,
            timeout: TimeSpan.FromSeconds(1));

        Assert.Equal(payload, result);
        await serverTask;
    }

    [Fact]
    public async Task QueryBinaryAsync_consumes_block_terminator_before_next_text_query()
    {
        var payload = Enumerable.Range(0, 12).Select(i => (byte)(i + 1)).ToArray();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();

            var binaryCommand = await ReadAsciiLineAsync(stream);
            Assert.Equal(SpectrumAnalyzerCommands.QueryTrace1(), binaryCommand);

            var header = Encoding.ASCII.GetBytes($"#2{payload.Length:D2}");
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
            await stream.WriteAsync("\n"u8.ToArray());
            await stream.FlushAsync();

            var textCommand = await ReadAsciiLineAsync(stream);
            Assert.Equal(SpectrumAnalyzerCommands.QueryCenterFrequency(), textCommand);

            await stream.WriteAsync(Encoding.ASCII.GetBytes("2.460000000E+09\n"));
            await stream.FlushAsync();
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var binaryResult = await channel.QueryBinaryAsync(
            SpectrumAnalyzerCommands.QueryTrace1(),
            expectedBytes: 4096,
            timeout: TimeSpan.FromSeconds(1));
        var textResult = await channel.QueryAsync(
            SpectrumAnalyzerCommands.QueryCenterFrequency(),
            timeout: TimeSpan.FromSeconds(1));

        Assert.Equal(payload, binaryResult);
        Assert.Equal("2.460000000E+09", textResult);
        await serverTask;
    }

    [Fact]
    public async Task QueryBinaryAsync_reads_binary_block_payload_when_header_and_payload_are_split()
    {
        var payload = Enumerable.Range(0, 16).Select(i => (byte)(i + 10)).ToArray();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var command = await ReadAsciiLineAsync(stream);

            Assert.Equal(SpectrumAnalyzerCommands.QueryTrace1(), command);

            var response = Encoding.ASCII.GetBytes($"#2{payload.Length:D2}")
                .Concat(payload)
                .ToArray();

            foreach (var value in response)
            {
                await stream.WriteAsync(new[] { value });
                await stream.FlushAsync();
                await Task.Delay(1);
            }
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var result = await channel.QueryBinaryAsync(
            SpectrumAnalyzerCommands.QueryTrace1(),
            expectedBytes: 4096,
            timeout: TimeSpan.FromSeconds(2));

        Assert.Equal(payload, result);
        await serverTask;
    }

    [Fact]
    public async Task QueryBinaryAsync_throws_when_binary_block_payload_is_incomplete()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadAsciiLineAsync(stream);

            await stream.WriteAsync(Encoding.ASCII.GetBytes("#212"));
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
            await stream.FlushAsync();
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ConnectionClosedException>(() =>
            channel.QueryBinaryAsync(
                SpectrumAnalyzerCommands.QueryTrace1(),
                expectedBytes: 4096,
                timeout: TimeSpan.FromSeconds(2)));

        Assert.Contains("数据不完整", ex.Message);
        await serverTask;
    }

    [Fact]
    public async Task QueryBinaryAsync_rejects_unsupported_binary_block_length_digit()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadAsciiLineAsync(stream);

            await stream.WriteAsync(Encoding.ASCII.GetBytes("#0"));
            await stream.FlushAsync();
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.QueryBinaryAsync(
                SpectrumAnalyzerCommands.QueryTrace1(),
                expectedBytes: 4096,
                timeout: TimeSpan.FromSeconds(1)));

        Assert.Contains("不支持的 SCPI binary block 头", ex.Message);
        await serverTask;
    }

    [Fact]
    public async Task QueryBinaryAsync_rejects_binary_block_payload_length_above_limit()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadAsciiLineAsync(stream);

            await stream.WriteAsync(Encoding.ASCII.GetBytes("#74194305"));
            await stream.FlushAsync();
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.QueryBinaryAsync(
                SpectrumAnalyzerCommands.QueryTrace1(),
                expectedBytes: 4096,
                timeout: TimeSpan.FromSeconds(1)));

        Assert.Contains("SCPI binary block 长度超出范围", ex.Message);
        await serverTask;
    }

    [Fact]
    public async Task QueryBinaryAsync_reads_exact_non_block_payload_when_response_has_no_block_header()
    {
        var payload = Encoding.ASCII.GetBytes("ABCDEF");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadAsciiLineAsync(stream);

            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var result = await channel.QueryBinaryAsync(
            SpectrumAnalyzerCommands.QueryTrace1(),
            expectedBytes: payload.Length,
            timeout: TimeSpan.FromSeconds(1));

        Assert.Equal(payload, result);
        await serverTask;
    }

    [Fact]
    public async Task SendManyAsync_writes_all_commands_in_one_message()
    {
        var commands = new[] { ":FREQ:CENT 2460 MHz", ":FREQ:SPAN 100 MHz", "DISP:WIND:TRAC:Y:RLEV 20" };
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            // 一次 TCP 读应收到全部命令（合并发送，\n 分隔）
            var received = await ReadAsciiLineAsync(stream);
            Assert.Equal(commands[0], received);
            Assert.Equal(commands[1], await ReadAsciiLineAsync(stream));
            Assert.Equal(commands[2], await ReadAsciiLineAsync(stream));
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        await channel.SendManyAsync(commands, CancellationToken.None);
        await serverTask;
    }

    [Fact]
    public async Task QueryAsync_reuses_reader_across_queries_no_byte_loss()
    {
        // 稳定：StreamReader 复用（成员级），连续文本查询不得因缓冲重建丢失字节
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            for (var i = 0; i < 3; i++)
            {
                _ = await ReadAsciiLineAsync(stream);
                // 分两个 TCP 段发送（部分字节先到，考验读取缓冲）
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"RESP-{i}"));
                await stream.FlushAsync();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"-TAIL\n"));
                await stream.FlushAsync();
            }
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            var r = await channel.QueryAsync($"QUERY{i}", TimeSpan.FromSeconds(2));
            Assert.Equal($"RESP-{i}-TAIL", r);
        }

        await serverTask;
    }

    // ---- 审计 SC-01：并发查询响应串扰（写锁内/读锁外 → 操作级串行化） ----

    /// <summary>
    /// SC-01: 查询 A（CMD1，仪器不响应 → 超时）与查询 B（CMD2，立即响应）并发——
    /// 修复前读在锁外：A 的挂起读会抢到先到达的 RESP-B（A 不抛超时、或 B 拿不到响应）；
    /// 修复后写+读整体串行化：A 超时释放后 B 正常拿到自己的响应。
    /// </summary>
    [Fact]
    public async Task QueryAsync_timeout_does_not_leak_response_to_concurrent_query()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadAsciiLineAsync(stream);   // CMD1（不回复）
            _ = await ReadAsciiLineAsync(stream);   // CMD2（修复后：A 超时释放 gate 后 B 才写入）
            await stream.WriteAsync(Encoding.ASCII.GetBytes("RESP-B\n"));
            await stream.FlushAsync();
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var timeoutTask = Assert.ThrowsAsync<OperationCanceledException>(() =>
            channel.QueryAsync("CMD1", TimeSpan.FromMilliseconds(500)));
        var concurrentTask = channel.QueryAsync("CMD2", TimeSpan.FromSeconds(2));

        await timeoutTask;
        Assert.Equal("RESP-B", await concurrentTask);   // 修复前：RESP-B 被 A 的挂起读抢走/串扰
        await serverTask;
    }

    // ---- 审计 SC-02：StreamReader 缓冲与裸流混用（文本/二进制交错丢字节） ----

    /// <summary>
    /// SC-02: 仪器把文本响应与二进制块同段发送（流水线场景）——修复前 StreamReader
    /// 在 ReadLineAsync 时把块头吞进内部缓冲，后续裸流读取丢字节挂起；
    /// 修复后统一字节级读取，文本行只消费到 '\n'，二进制块完整可读。
    /// </summary>
    [Fact]
    public async Task QueryAsync_then_QueryBinaryAsync_no_byte_loss_when_responses_share_segment()
    {
        var payload = Enumerable.Range(0, 12).Select(i => (byte)(i + 1)).ToArray();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadAsciiLineAsync(stream);   // CMD1

            // 同段发送：文本响应 + 二进制块头 + 负载（模拟仪器一次发出多响应）
            var combined = Encoding.ASCII.GetBytes("RESP-A\n#2" + payload.Length.ToString("D2"))
                .Concat(payload)
                .ToArray();
            await stream.WriteAsync(combined);
            await stream.FlushAsync();

            _ = await ReadAsciiLineAsync(stream);   // CMD2（数据已提前发送）
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var text = await channel.QueryAsync("CMD1", TimeSpan.FromSeconds(2));
        var binary = await channel.QueryBinaryAsync(
            "CMD2", expectedBytes: 4096, timeout: TimeSpan.FromSeconds(2));

        Assert.Equal("RESP-A", text);
        Assert.Equal(payload, binary);   // 修复前：块头被 StreamReader 吞掉 → 挂起/异常
        await serverTask;
    }

    // ---- 审计 SC-03：EOF 抛连接类异常（不再静默空串） ----

    [Fact]
    public async Task QueryAsync_remote_closes_connection_throws_connection_closed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadAsciiLineAsync(stream);
            // 不回复直接关闭（EOF）
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        await Assert.ThrowsAsync<ConnectionClosedException>(() =>
            channel.QueryAsync("CMD1", TimeSpan.FromSeconds(2)));
        await serverTask;
    }

    // ---- 审计 SC-06：QueryBinaryAsync 未连接守卫（不抛裸 NRE） ----

    [Fact]
    public async Task QueryBinaryAsync_not_connected_throws_invalid_operation()
    {
        using var channel = new TcpInstrumentChannel();   // 未连接

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.QueryBinaryAsync(":TRAC?", expectedBytes: 4096, timeout: TimeSpan.FromSeconds(1)));
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
