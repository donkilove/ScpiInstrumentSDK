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

    // ---- 审计 SC-03：ConnectionClosedException（EOF）触发重连（类型判定，不依赖消息文本） ----

    [Fact]
    public async Task Query_reconnects_on_connection_closed_exception()
    {
        var inner = new StubChannel
        {
            TransientFailure = new ConnectionClosedException("响应流已结束：连接已关闭")
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

    // ---- 审计 SC-05：并发失败操作的重连互斥串行化（防连接风暴） ----

    [Fact]
    public async Task Concurrent_failures_reconnect_serially()
    {
        var inner = new StubChannel
        {
            FailTimes = 3,   // 并发 3 个操作各自失败一次
            Failure = new ConnectionClosedException("响应流已结束：连接已关闭")
        };

        using var channel = new AutoReconnectChannel(inner, new AutoReconnectOptions
        {
            InitialBackoff = TimeSpan.Zero
        });
        await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

        await Task.WhenAll(
            channel.QueryAsync("SYST:IDN?", TimeSpan.FromSeconds(2)),
            channel.QueryAsync("SYST:IDN?", TimeSpan.FromSeconds(2)),
            channel.QueryAsync("SYST:IDN?", TimeSpan.FromSeconds(2)));

        Assert.Equal(1, inner.MaxConcurrentConnects);   // 重连互斥：任意时刻最多 1 个连接建立（修复前并发 → 3）
        Assert.Equal(6, inner.OperationCalls);          // 3 次失败 + 3 次重放
    }

    // ---- 审计 SC-10：退避抖动（±25% 内、有随机性） ----

    [Fact]
    public void BackoffFor_applies_jitter_within_range()
    {
        var channel = new AutoReconnectChannel(new StubChannel(), new AutoReconnectOptions
        {
            InitialBackoff = TimeSpan.FromMilliseconds(100),
            MaxBackoff = TimeSpan.FromMilliseconds(1000)
        });

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            for (var i = 0; i < 20; i++)
            {
                var backoff = channel.BackoffFor(attempt);
                var baseMs = Math.Min(100 * Math.Pow(2, attempt - 1), 1000);
                Assert.InRange(backoff.TotalMilliseconds, baseMs * 0.75, baseMs * 1.25);
            }
        }
    }

    /// <summary>可注入故障的假通道：前 N 次操作抛指定异常，之后恢复正常。</summary>
    private sealed class StubChannel : IInstrumentChannel
    {
        public Exception? TransientFailure;   // 只失败一次
        public Exception? Failure;            // 与 FailTimes 配合：失败 N 次
        public int FailTimes;
        public bool AlwaysFail;
        public int OperationCalls;
        public int ReconnectCalls;
        public bool Connected;
        private int _failCount;
        private int _activeConnects;
        private int _maxConcurrentConnects;

        /// <summary>历史最大并发连接建立数（SC-05 互斥验证：串行化时恒为 1）</summary>
        public int MaxConcurrentConnects => Volatile.Read(ref _maxConcurrentConnects);

        public bool IsConnected => Connected;

        public async Task ConnectAsync(string host, int port, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _activeConnects);
            UpdateMax(active);
            try
            {
                if (Connected)
                    ReconnectCalls++; // 已连接状态下再次 ConnectAsync 视为自动重连

                await Task.Delay(50, ct);   // 放大并发重叠窗口（SC-05 串行化验证）
                Connected = true;
            }
            finally
            {
                Interlocked.Decrement(ref _activeConnects);
            }
        }

        private void UpdateMax(int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maxConcurrentConnects);
                if (current <= observed
                    || Interlocked.CompareExchange(ref _maxConcurrentConnects, current, observed) == observed)
                {
                    return;
                }
            }
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

            if (Failure is not null && Interlocked.Increment(ref _failCount) <= FailTimes)
            {
                throw Failure;   // 失败 N 次（并发场景共享计数，确定性）
            }
        }
    }
}
