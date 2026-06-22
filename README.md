# Spswj.Instrumentation

这是一个面向 .NET 的 TCP/IP + SCPI 仪器通信孵化库，当前主要沉淀频谱仪通信与 trace 数据解析能力。

本仓库从 `SPSWJ v0.1.3` 提取通信和频谱仪解析代码，用于后续其他上位机复用。当前 `SPSWJ` 尚未引用本库，现有上位机项目仍保持独立发布和运行。

## 当前范围

- TCP/IP 仪器连接、断开和连接状态检查。
- SCPI 文本命令发送与查询。
- SCPI definite-length binary block 读取。
- 频谱仪常用 SCPI 命令构造。
- `REAL,32` trace 数据解析，返回最大功率点频率、功率和简单信噪差。

## 项目结构

```text
src/
  Spswj.Instrumentation/
    IInstrumentChannel.cs
    TcpInstrumentChannel.cs
    SpectrumAnalyzers/
      SpectrumAnalyzerCommands.cs
      TraceDataParser.cs
      TraceAnalysisResult.cs
tests/
  Spswj.Instrumentation.Tests/
```

## 快速开始

环境要求：

- .NET 8 SDK

构建：

```powershell
dotnet build .\Spswj.Instrumentation.sln
```

测试：

```powershell
dotnet test .\Spswj.Instrumentation.sln
```

## 示例

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

## 版本口径

`v0.1.0` 是孵化版本：代码来自已发布的 `SPSWJ v0.1.3`，已搬运并通过通信和解析相关测试，但 API 暂不承诺长期稳定。后续如果多个上位机实际复用，再进入更严格的兼容性管理。
