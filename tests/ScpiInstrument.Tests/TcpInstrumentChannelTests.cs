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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
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
