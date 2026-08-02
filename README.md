# Spswj.Instrumentation

[![NuGet](https://img.shields.io/badge/NuGet-0.2.0-blue)](https://github.com/donkilove/Spswj.Instrumentation/pkgs/nuget/Spswj.Instrumentation)
[![CI](https://github.com/donkilove/Spswj.Instrumentation/actions/workflows/ci.yml/badge.svg)](https://github.com/donkilove/Spswj.Instrumentation/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![TargetFramework](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)

A .NET library for TCP/IP + SCPI instrument communication and spectrum-analyzer trace parsing.

Extracted from the production test host `SPSWJ v0.1.3` and published as a reusable component, so multiple test-host applications can share one well-tested communication stack instead of duplicating source code.

## Features

- **TCP/IP instrument channel** — connect / disconnect / state check with connection and read timeouts.
- **SCPI text I/O** — send commands and query responses with cancellation support.
- **SCPI definite-length binary blocks** — full `#<n><len>` header parsing, split-packet handling, and optional `\n` / `\r\n` terminator consumption.
- **Spectrum-analyzer command builder** — center frequency, span, reference level, REAL,32 data format, MaxHold, and trace queries (InvariantCulture-safe).
- **`REAL,32` trace parsing** — returns peak frequency, peak power, and a simple signal-to-noise delta.
- **Auto-reconnect decorator** — transparently reconnects and replays the operation on connection errors, with exponential backoff and configurable retry limits.
- **Programmable mock channel** — preset text/binary responses per command, records sent commands for assertions, and supports injecting transient failures for offline development and automated tests.

## Installation

The package is published to GitHub Packages (NuGet feed):

```bash
dotnet add package Spswj.Instrumentation --version 0.2.0 \
  --source "https://nuget.pkg.github.com/donkilove/index.json"
```

> The GitHub Packages feed requires authentication. Configure a token with `read:packages` scope, for example via `gh auth token` in the environment, or add the feed as a NuGet source in your `NuGet.Config`.

## Quick Start

Requirements: .NET 8 SDK

```bash
# Build
dotnet build Spswj.Instrumentation.sln

# Test
dotnet test Spswj.Instrumentation.sln
```

## Usage

### 1. Real instrument (TCP/IP + SCPI)

```csharp
using Spswj.Instrumentation;
using Spswj.Instrumentation.SpectrumAnalyzers;

using var channel = new TcpInstrumentChannel();
await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

var idn = await channel.QueryAsync(
    SpectrumAnalyzerCommands.QueryIdn(),
    TimeSpan.FromSeconds(3));

await channel.SendAsync(SpectrumAnalyzerCommands.SetCenterFrequency(2400));
await channel.SendAsync(SpectrumAnalyzerCommands.SetSpan(100));
await channel.SendAsync(SpectrumAnalyzerCommands.SetDataFormatReal32());
await channel.SendAsync(SpectrumAnalyzerCommands.SetDataByteOrderSwap());

var rawTrace = await channel.QueryBinaryAsync(
    SpectrumAnalyzerCommands.QueryTrace1(),
    expectedBytes: 4096,
    timeout: TimeSpan.FromSeconds(5));

var trace = TraceDataParser.ParseReal32Trace(
    rawTrace,
    centerFreqHz: 2400e6,
    spanHz: 100e6);
```

### 2. Auto-reconnect (optional)

```csharp
using var inner = new TcpInstrumentChannel();
using var channel = new AutoReconnectChannel(inner, new AutoReconnectOptions
{
    MaxReconnectAttempts = 3,
    InitialBackoff = TimeSpan.FromMilliseconds(200),
    MaxBackoff = TimeSpan.FromSeconds(5)
});

await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

// Connection errors are handled transparently: reconnect + replay, no caller changes
var idn = await channel.QueryAsync(
    SpectrumAnalyzerCommands.QueryIdn(),
    TimeSpan.FromSeconds(3));
```

By default only connection-class errors (not connected / connection lost / socket / IO) trigger a reconnect. Protocol or data errors (e.g. incomplete payloads) never reconnect, so real problems are not masked. Customize the transient-error predicate via `AutoReconnectOptions.IsTransient`.

### 3. Mock instrument (no hardware needed)

```csharp
using var channel = new MockInstrumentChannel();
channel.AddTextResponse(SpectrumAnalyzerCommands.QueryIdn(), "Agilent Technologies,N9020A,MY51288077,A.14.13");
channel.AddTextResponse(SpectrumAnalyzerCommands.QueryCenterFrequency(), "2.460000000E+09");
channel.AddBinaryResponse(SpectrumAnalyzerCommands.QueryTrace1(), new byte[4004]);
await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

var idn = await channel.QueryAsync(
    SpectrumAnalyzerCommands.QueryIdn(),
    TimeSpan.FromSeconds(1));

// channel.SentCommands asserts which commands were actually sent
```

## Project Structure

```text
src/
  Spswj.Instrumentation/
    IInstrumentChannel.cs          # channel abstraction
    TcpInstrumentChannel.cs        # real TCP/IP + SCPI channel
    AutoReconnectChannel.cs        # reconnect decorator
    AutoReconnectOptions.cs        # reconnect configuration
    MockInstrumentChannel.cs       # programmable fake instrument
    SpectrumAnalyzers/
      SpectrumAnalyzerCommands.cs  # SCPI command builder
      TraceDataParser.cs           # REAL,32 trace parsing
      TraceAnalysisResult.cs       # parsed trace result record
tests/
  Spswj.Instrumentation.Tests/     # xUnit suite (33 tests)
```

## API Overview

| Type | Purpose |
|------|---------|
| `IInstrumentChannel` | Channel abstraction: connect, disconnect, query, send, binary query |
| `TcpInstrumentChannel` | Real TCP/IP implementation with 5s connect/read timeouts and SCPI binary block parsing |
| `AutoReconnectChannel` | Decorator that auto-reconnects on connection errors and replays the operation |
| `AutoReconnectOptions` | Max retries, initial/max backoff, custom transient-error predicate |
| `MockInstrumentChannel` | Fake instrument with preset responses, command recording, and transient-failure injection |
| `SpectrumAnalyzerCommands` | Static SCPI command builders (freq/span/ref-level/format/trace) |
| `TraceDataParser` | Parses `REAL,32` trace bytes into peak frequency, power, and SNR delta |
| `TraceAnalysisResult` | Immutable result record |

## Validation

- 33 xUnit tests covering protocol parsing, split packets, error paths, reconnect behavior, and mock scenarios.
- Field fixtures from a real Agilent N9020A (captured 2026-06-11) are embedded in the test suite, so offline tests closely mirror real-device behavior.

## Versioning

`v0.1.x` was the incubator snapshot extracted from `SPSWJ v0.1.3`. `v0.2.0` adds auto-reconnect, the mock channel, and field-data fixtures, and is published as a NuGet package. The API is not yet committed to long-term stability; stricter compatibility management will begin once multiple test hosts actually consume the library.

## License

[MIT](LICENSE)
