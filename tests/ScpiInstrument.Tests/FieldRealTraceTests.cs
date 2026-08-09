using System.Net;
using System.Net.Sockets;
using System.Text;
using ScpiInstrument.SpectrumAnalyzers;

namespace ScpiInstrument.Tests;

/// <summary>
/// 基于内部生产测试主机的现场验证报告（2026-06-11，真实 Agilent N9020A）的离线测试：
/// 模拟真机响应序列，并校验解析结果复现现场实测读数。
/// </summary>
public class FieldRealTraceTests
{
    [Fact]
    public void FieldTrace_has_exact_real_device_geometry()
    {
        var trace = FieldRealResponses.CreateFieldTrace();

        Assert.Equal(FieldRealResponses.TracePayloadBytes, trace.Length);
        Assert.Equal(FieldRealResponses.TracePointCount, trace.Length / sizeof(float));
    }

    [Fact]
    public void FieldTrace_parses_to_field_readings()
    {
        var result = FieldRealResponses.ParseFieldTrace();

        Assert.Equal(FieldRealResponses.FieldMaxFreqMHz, result.MaxFrequencyMHz, precision: 6);
        Assert.Equal(FieldRealResponses.FieldPowerDbm, result.PowerDbm, precision: 6);
        Assert.Equal(FieldRealResponses.FieldSnDeltaDb, result.SignalNoiseDeltaDb, precision: 3);
    }

    [Fact]
    public async Task RealChannelSequence_replays_field_session()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();

            // 1. SYST:IDN? => 真实设备识别
            var idnCmd = await ReadAsciiLineAsync(stream);
            Assert.Equal(SpectrumAnalyzerCommands.QueryIdn(), idnCmd);
            await WriteLineAsync(stream, FieldRealResponses.DeviceIdn);

            // 2. :FREQ:CENT? => 真实读回
            var freqCmd = await ReadAsciiLineAsync(stream);
            Assert.Equal(SpectrumAnalyzerCommands.QueryCenterFrequency(), freqCmd);
            await WriteLineAsync(stream, FieldRealResponses.ReadbackCenterFreq);

            // 3. :FREQ:SPAN? => 真实读回
            var spanCmd = await ReadAsciiLineAsync(stream);
            Assert.Equal(SpectrumAnalyzerCommands.QuerySpan(), spanCmd);
            await WriteLineAsync(stream, FieldRealResponses.ReadbackSpan);

            // 4. DISP:WIND:TRAC:Y:RLEV? => 真实读回
            var rlevCmd = await ReadAsciiLineAsync(stream);
            Assert.Equal(SpectrumAnalyzerCommands.QueryRefLevel(), rlevCmd);
            await WriteLineAsync(stream, FieldRealResponses.ReadbackRefLevel);

            // 5. :TRAC? TRACE1 => 真实 #44004 binary block + 4004 字节 payload + \n 结束符
            var traceCmd = await ReadAsciiLineAsync(stream);
            Assert.Equal(SpectrumAnalyzerCommands.QueryTrace1(), traceCmd);

            var payload = FieldRealResponses.CreateFieldTrace();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(FieldRealResponses.TraceBlockHeader));
            await stream.WriteAsync(payload);
            await stream.WriteAsync("\n"u8.ToArray());
            await stream.FlushAsync();
        });

        using var channel = new TcpInstrumentChannel();
        await channel.ConnectAsync(IPAddress.Loopback.ToString(), port, CancellationToken.None);

        var idn = await channel.QueryAsync(SpectrumAnalyzerCommands.QueryIdn(), TimeSpan.FromSeconds(2));
        Assert.Equal(FieldRealResponses.DeviceIdn, idn);

        var center = await channel.QueryAsync(SpectrumAnalyzerCommands.QueryCenterFrequency(), TimeSpan.FromSeconds(2));
        Assert.Equal(FieldRealResponses.ReadbackCenterFreq, center);

        var span = await channel.QueryAsync(SpectrumAnalyzerCommands.QuerySpan(), TimeSpan.FromSeconds(2));
        Assert.Equal(FieldRealResponses.ReadbackSpan, span);

        var rlev = await channel.QueryAsync(SpectrumAnalyzerCommands.QueryRefLevel(), TimeSpan.FromSeconds(2));
        Assert.Equal(FieldRealResponses.ReadbackRefLevel, rlev);

        var trace = await channel.QueryBinaryAsync(
            SpectrumAnalyzerCommands.QueryTrace1(),
            expectedBytes: FieldRealResponses.TracePayloadBytes,
            timeout: TimeSpan.FromSeconds(3));

        Assert.Equal(FieldRealResponses.TracePayloadBytes, trace.Length);

        var result = TraceDataParser.ParseReal32Trace(
            trace,
            centerFreqHz: FieldRealResponses.CenterFreqMHz * 1e6,
            spanHz: FieldRealResponses.SpanMHz * 1e6);

        Assert.Equal(FieldRealResponses.FieldMaxFreqMHz, result.MaxFrequencyMHz, precision: 6);
        Assert.Equal(FieldRealResponses.FieldPowerDbm, result.PowerDbm, precision: 6);
        Assert.Equal(FieldRealResponses.FieldSnDeltaDb, result.SignalNoiseDeltaDb, precision: 3);

        await serverTask;
    }

    private static async Task WriteLineAsync(Stream stream, string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
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
