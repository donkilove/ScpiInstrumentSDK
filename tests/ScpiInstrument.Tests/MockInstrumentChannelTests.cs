using System.Text;

namespace ScpiInstrument.Tests;

public class MockInstrumentChannelTests
{
    [Fact]
    public async Task Connect_sets_connected_and_remembers_endpoint()
    {
        using var channel = new MockInstrumentChannel();

        Assert.False(channel.IsConnected);

        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        Assert.True(channel.IsConnected);
        Assert.Equal("192.168.1.1", channel.Host);
        Assert.Equal(5025, channel.Port);
    }

    [Fact]
    public async Task QueryAsync_returns_preset_text_response_and_records_command()
    {
        using var channel = new MockInstrumentChannel();
        channel.AddTextResponse("SYST:IDN?", "Agilent Technologies,N9020A,MY51288077,A.14.13");
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        var result = await channel.QueryAsync("SYST:IDN?", TimeSpan.FromSeconds(1));

        Assert.Equal("Agilent Technologies,N9020A,MY51288077,A.14.13", result);
        Assert.Equal(new[] { "SYST:IDN?" }, channel.SentCommands);
    }

    [Fact]
    public async Task SendAsync_records_command_without_response()
    {
        using var channel = new MockInstrumentChannel();
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        await channel.SendAsync(":FREQ:CENT 2460 MHz");
        await channel.SendAsync(":FREQ:SPAN 100 MHz");

        Assert.Equal(new[] { ":FREQ:CENT 2460 MHz", ":FREQ:SPAN 100 MHz" }, channel.SentCommands);
    }

    [Fact]
    public async Task QueryBinaryAsync_returns_preset_binary_response()
    {
        var payload = Encoding.ASCII.GetBytes("#44004-fake-payload");
        using var channel = new MockInstrumentChannel();
        channel.AddBinaryResponse(":TRAC? TRACE1", payload);
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        var result = await channel.QueryBinaryAsync(":TRAC? TRACE1", 4096, TimeSpan.FromSeconds(1));

        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task QueryAsync_throws_when_command_has_no_preset_response()
    {
        using var channel = new MockInstrumentChannel();
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.QueryAsync(":FREQ:CENT?", TimeSpan.FromSeconds(1)));

        Assert.Contains("未预设", ex.Message);
    }

    [Fact]
    public async Task QueryBinaryAsync_throws_when_command_has_no_preset_binary()
    {
        using var channel = new MockInstrumentChannel();
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.QueryBinaryAsync(":TRAC? TRACE1", 4096, TimeSpan.FromSeconds(1)));

        Assert.Contains("未预设", ex.Message);
    }

    [Fact]
    public async Task Operation_throws_when_not_connected()
    {
        using var channel = new MockInstrumentChannel();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.QueryAsync("SYST:IDN?", TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.SendAsync("SYST:IDN?"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.QueryBinaryAsync(":TRAC? TRACE1", 4096, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Disconnect_clears_connected_state()
    {
        using var channel = new MockInstrumentChannel();
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        await channel.DisconnectAsync();

        Assert.False(channel.IsConnected);
    }

    [Fact]
    public async Task Transient_failure_throws_for_configured_attempts_then_recovers()
    {
        using var channel = new MockInstrumentChannel();
        channel.AddTextResponse("SYST:IDN?", "OK");
        channel.AddTransientFailure(new InvalidOperationException("连接已断开"), times: 2);
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.QueryAsync("SYST:IDN?", TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.QueryAsync("SYST:IDN?", TimeSpan.FromSeconds(1)));

        var result = await channel.QueryAsync("SYST:IDN?", TimeSpan.FromSeconds(1));

        Assert.Equal("OK", result);
    }
}
