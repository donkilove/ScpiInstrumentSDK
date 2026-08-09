using System.Collections.ObjectModel;

namespace ScpiInstrument;

/// <summary>
/// 可编程的假仪器通道：按命令返回预设响应，并记录所有发送的命令。
/// 用于单元测试、上位机联调和离线演示，无需真实仪器。
/// </summary>
public sealed class MockInstrumentChannel : IInstrumentChannel, IDisposable
{
    private readonly Dictionary<string, string> _textResponses = new();
    private readonly Dictionary<string, byte[]> _binaryResponses = new();
    private readonly List<string> _sentCommands = new();
    private readonly object _lock = new();

    private Queue<Exception>? _transientFailures;
    private bool _disposed;

    public bool IsConnected { get; private set; }
    public string? Host { get; private set; }
    public int? Port { get; private set; }

    /// <summary>按发送顺序记录的全部命令（Query 与 Send 都记录）。</summary>
    public IReadOnlyList<string> SentCommands
    {
        get
        {
            lock (_lock)
            {
                return new ReadOnlyCollection<string>(_sentCommands.ToArray());
            }
        }
    }

    /// <summary>为指定命令预设文本响应。</summary>
    public void AddTextResponse(string command, string response)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(response);

        lock (_lock)
        {
            _textResponses[command] = response;
        }
    }

    /// <summary>为指定命令预设二进制响应。</summary>
    public void AddBinaryResponse(string command, byte[] response)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(response);

        lock (_lock)
        {
            _binaryResponses[command] = response;
        }
    }

    /// <summary>
    /// 注入瞬态故障：接下来 <paramref name="times"/> 次操作（Query/Send/QueryBinary）抛出
    /// <paramref name="exception"/>，之后恢复正常。默认只失败 1 次。
    /// </summary>
    public void AddTransientFailure(Exception exception, int times = 1)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (times < 1) throw new ArgumentOutOfRangeException(nameof(times));

        lock (_lock)
        {
            _transientFailures ??= new Queue<Exception>();
            for (var i = 0; i < times; i++)
                _transientFailures.Enqueue(exception);
        }
    }

    public Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        ThrowIfDisposed();
        Host = host;
        Port = port;
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        ThrowIfDisposed();
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<string> QueryAsync(string scpiCommand, TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfNotConnected();
        RecordCommand(scpiCommand);
        ThrowIfTransient();

        lock (_lock)
        {
            if (_textResponses.TryGetValue(scpiCommand, out var response))
                return Task.FromResult(response);
        }

        throw new InvalidOperationException($"未预设响应: {scpiCommand}");
    }

    public Task SendAsync(string scpiCommand, CancellationToken ct = default)
    {
        ThrowIfNotConnected();
        RecordCommand(scpiCommand);
        ThrowIfTransient();
        return Task.CompletedTask;
    }

    public Task<byte[]> QueryBinaryAsync(
        string scpiCommand,
        int expectedBytes,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ThrowIfNotConnected();
        RecordCommand(scpiCommand);
        ThrowIfTransient();

        lock (_lock)
        {
            if (_binaryResponses.TryGetValue(scpiCommand, out var response))
                return Task.FromResult(response);
        }

        throw new InvalidOperationException($"未预设二进制响应: {scpiCommand}");
    }

    private void RecordCommand(string scpiCommand)
    {
        lock (_lock)
        {
            _sentCommands.Add(scpiCommand);
        }
    }

    private void ThrowIfTransient()
    {
        lock (_lock)
        {
            if (_transientFailures is not { Count: > 0 })
                return;

            var failure = _transientFailures.Dequeue();
            throw failure;
        }
    }

    private void ThrowIfNotConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("未连接仪器");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        _disposed = true;
        IsConnected = false;
    }
}
