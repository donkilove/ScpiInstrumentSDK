using System.Net.Sockets;

namespace ScpiInstrument.Tests;

public class AutoReconnectChannelTests
{
    [Fact]
    public async Task Query_reconnects_and_recovers_after_transient_connection_error()
    {
        var inner = new StubChannel
        {
            TransientFailure = new InvalidOperationException("连接已断开")
        };

        using var channel = new AutoReconnectChannel(inner, new AutoReconnectOptions
        {
            InitialBackoff = TimeSpan.Zero
        });
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        var result = await channel.QueryAsync("SYST:IDN?", TimeSpan.FromSeconds(1));

        Assert.Equal("OK", result);
        Assert.Equal(2, inner.OperationCalls);   // 首次失败 + 重连后重放
        Assert.Equal(1, inner.ReconnectCalls);   // 自动重连 1 次
    }

    [Fact]
    public async Task Query_throws_after_reconnect_attempts_exhausted()
    {
        var inner = new StubChannel
        {
            TransientFailure = new InvalidOperationException("未连接仪器")
        };
        inner.AlwaysFail = true; // 重连后仍失败

        using var channel = new AutoReconnectChannel(inner, new AutoReconnectOptions
        {
            MaxReconnectAttempts = 2,
            InitialBackoff = TimeSpan.Zero
        });
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.QueryAsync("SYST:IDN?", TimeSpan.FromSeconds(1)));

        Assert.Contains("自动重连失败", ex.Message);
        Assert.Equal(2, inner.ReconnectCalls);   // 到达上限后不再继续
    }

    [Fact]
    public async Task Query_does_not_reconnect_on_protocol_error()
    {
        var inner = new StubChannel
        {
            TransientFailure = new InvalidOperationException("数据不完整: 期望 4004 字节，实际收到 12 字节")
        };

        using var channel = new AutoReconnectChannel(inner, new AutoReconnectOptions
        {
            InitialBackoff = TimeSpan.Zero
        });
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.QueryAsync(":TRAC? TRACE1", TimeSpan.FromSeconds(1)));

        Assert.Contains("数据不完整", ex.Message);
        Assert.Equal(0, inner.ReconnectCalls);   // 协议错误不触发重连
    }

    [Fact]
    public async Task Query_reconnects_on_network_socket_error()
    {
        var inner = new StubChannel
        {
            TransientFailure = new SocketException(10054) // 连接被对端重置
        };

        using var channel = new AutoReconnectChannel(inner, new AutoReconnectOptions
        {
            InitialBackoff = TimeSpan.Zero
        });
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        var result = await channel.QueryAsync("SYST:IDN?", TimeSpan.FromSeconds(1));

        Assert.Equal("OK", result);
        Assert.Equal(1, inner.ReconnectCalls);
    }

    [Fact]
    public async Task Uses_custom_transient_predicate_when_provided()
    {
        var inner = new StubChannel
        {
            TransientFailure = new InvalidOperationException("任意业务异常")
        };

        using var channel = new AutoReconnectChannel(inner, new AutoReconnectOptions
        {
            InitialBackoff = TimeSpan.Zero,
            IsTransient = ex => ex is InvalidOperationException
        });
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        var result = await channel.QueryAsync("SYST:IDN?", TimeSpan.FromSeconds(1));

        Assert.Equal("OK", result);
        Assert.Equal(1, inner.ReconnectCalls);
    }

    [Fact]
    public async Task Send_reconnects_and_replays_after_connection_error()
    {
        var inner = new StubChannel
        {
            TransientFailure = new InvalidOperationException("连接已断开")
        };

        using var channel = new AutoReconnectChannel(inner, new AutoReconnectOptions
        {
            InitialBackoff = TimeSpan.Zero
        });
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        await channel.SendAsync(":FREQ:CENT 2460 MHz");

        Assert.Equal(2, inner.OperationCalls);
        Assert.Equal(1, inner.ReconnectCalls);
    }

    /// <summary>可注入故障的假通道：前 N 次操作抛指定异常，之后恢复正常。</summary>
    private sealed class StubChannel : IInstrumentChannel
    {
        public Exception? TransientFailure;
        public bool AlwaysFail;
        public int OperationCalls;
        public int ReconnectCalls;
        public bool Connected;

        public bool IsConnected => Connected;

        public Task ConnectAsync(string host, int port, CancellationToken ct)
        {
            if (Connected)
                ReconnectCalls++; // 已连接状态下再次 ConnectAsync 视为自动重连

            Connected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            Connected = false;
            return Task.CompletedTask;
        }

        public Task<string> QueryAsync(string scpiCommand, TimeSpan timeout, CancellationToken ct = default)
        {
            OperationCalls++;
            MaybeFail();
            return Task.FromResult("OK");
        }

        public Task SendAsync(string scpiCommand, CancellationToken ct = default)
        {
            OperationCalls++;
            MaybeFail();
            return Task.CompletedTask;
        }

        public Task SendManyAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
        {
            OperationCalls++;
            MaybeFail();
            return Task.CompletedTask;
        }

        public Task<byte[]> QueryBinaryAsync(
            string scpiCommand,
            int expectedBytes,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            OperationCalls++;
            MaybeFail();
            return Task.FromResult(new byte[expectedBytes]);
        }

        private void MaybeFail()
        {
            if (AlwaysFail)
                throw TransientFailure ?? new InvalidOperationException("连接已断开");

            if (TransientFailure is not null)
            {
                var failure = TransientFailure;
                TransientFailure = null; // 只失败一次，之后恢复
                throw failure;
            }
        }
    }
}
