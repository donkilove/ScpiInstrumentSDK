# ScpiInstrument

[![NuGet](https://img.shields.io/badge/NuGet-0.4.1-blue)](https://github.com/donkilove/ScpiInstrument/pkgs/nuget/ScpiInstrument)
[![CI](https://github.com/donkilove/ScpiInstrument/actions/workflows/ci.yml/badge.svg)](https://github.com/donkilove/ScpiInstrument/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![TargetFramework](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)

**English** · [简体中文](README.zh-CN.md)

A .NET 10 library for communicating with test and measurement instruments over TCP/IP using SCPI, including spectrum-analyzer trace parsing.

Extracted from an internal production test host and published as a reusable component, so multiple test-host applications can share one well-tested communication stack instead of duplicating source code.

## Table of Contents

- [Features](#features)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Usage](#usage)
  - [Real instrument (TCP/IP + SCPI)](#1-real-instrument-tcpip--scpi)
  - [Auto-reconnect (optional)](#2-auto-reconnect-optional)
  - [Mock instrument (no hardware needed)](#3-mock-instrument-no-hardware-needed)
- [API Overview](#api-overview)
- [Project Structure](#project-structure)
- [Validation](#validation)
- [Versioning](#versioning)
- [Contributing](#contributing)
- [License](#license)

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
dotnet add package ScpiInstrument --version 0.4.1 \
  --source "https://nuget.pkg.github.com/donkilove/index.json"
```

> The GitHub Packages feed requires authentication. Configure a token with `read:packages` scope, for example via `gh auth token` in the environment, or add the feed as a NuGet source in your `NuGet.Config`.

## Quick Start

Requirements: .NET 10 SDK

```bash
# Build
dotnet build ScpiInstrument.sln

# Test
dotnet test ScpiInstrument.sln
```

## Usage

### 1. Real instrument (TCP/IP + SCPI)

```csharp
using ScpiInstrument;
using ScpiInstrument.SpectrumAnalyzers;

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

## Project Structure

```text
src/
  ScpiInstrument/
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
  ScpiInstrument.Tests/            # xUnit suite (33 tests)
```

## Validation

- 33 xUnit tests covering protocol parsing, split packets, error paths, reconnect behavior, and mock scenarios.
- Field fixtures from a real Agilent N9020A (captured 2026-06-11) are embedded in the test suite, so offline tests closely mirror real-device behavior.
- Continuous integration runs the full build and test suite on every push and pull request (`.github/workflows/ci.yml`).

## Versioning

`v0.4.0` upgrades the target framework to .NET 10 (TFM, CI, dependencies); all 37 tests remain green. `v0.1.x` was the incubator snapshot extracted from an internal production test host. `v0.2.0` adds auto-reconnect, the mock channel, and field-data fixtures, and is the first release published under the `ScpiInstrument` name as a NuGet package. `v0.3.0` adds `SendManyAsync` (batch command sending to cut TCP round-trips) and reuses a single `StreamReader` per connection to eliminate buffered-byte loss across queries. `v0.3.1` adds `SpectrumAnalyzerCommands.LinkQueries` for combined SCPI queries (one round-trip, multiple values). `v0.4.1` is an audit-fix batch (SC-01~12): per-operation serialization (no cross-response under concurrency), unified byte-level reading (no StreamReader/bare-stream mixing), typed `ConnectionClosedException` for EOF (now triggers auto-reconnect), reconnect mutex (no reconnect storms), NaN/Infinity trace rejection, jittered backoff, `SplitLinkedResponses` for linked queries, and hardened Connect/Dispose paths (49 tests green). The API is not yet committed to long-term stability; stricter compatibility management will begin once multiple test hosts actually consume the library.

## Contributing

Contributions are welcome. Please open an issue to discuss changes before submitting a pull request, and make sure the existing test suite passes.

## License

[MIT](LICENSE)
