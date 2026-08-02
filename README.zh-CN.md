# Spswj.Instrumentation

[![NuGet](https://img.shields.io/badge/NuGet-0.2.0-blue)](https://github.com/donkilove/Spswj.Instrumentation/pkgs/nuget/Spswj.Instrumentation)
[![CI](https://github.com/donkilove/Spswj.Instrumentation/actions/workflows/ci.yml/badge.svg)](https://github.com/donkilove/Spswj.Instrumentation/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![TargetFramework](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)

[English](README.md) · **简体中文**

面向 .NET 的 TCP/IP + SCPI 仪器通信与频谱仪 trace 解析库。

从生产上位机 `SPSWJ v0.1.3` 提取通信和频谱仪解析代码，发布为可复用组件——多个测试上位机可以共享同一套经过验证的通信栈，而不是各自复制源码。

## 功能特性

- **TCP/IP 仪器通道** —— 连接 / 断开 / 状态检查，带连接与读取超时。
- **SCPI 文本读写** —— 命令发送与查询，支持取消。
- **SCPI definite-length binary block** —— 完整的 `#<n><len>` 头解析、分片处理、可选 `\n` / `\r\n` 结束符消费。
- **频谱仪命令构造** —— 中心频率、Span、参考电平、REAL,32 数据格式、MaxHold、trace 查询（InvariantCulture 安全）。
- **`REAL,32` trace 解析** —— 返回峰值频率、峰值功率和简单信噪差。
- **自动重连装饰器** —— 连接异常时透明重连并重放操作，指数退避、重试次数可配置。
- **可编程 mock 通道** —— 按命令预设文本/二进制响应、记录发送的命令（供断言）、可注入瞬态故障，用于无仪器联调与自动化测试。

## 安装

包发布在 GitHub Packages（NuGet 源）：

```bash
dotnet add package Spswj.Instrumentation --version 0.2.0 \
  --source "https://nuget.pkg.github.com/donkilove/index.json"
```

> GitHub Packages 源需要认证。请配置带 `read:packages` 权限的 token（例如环境变量中使用 `gh auth token`），或把该源加入你的 `NuGet.Config`。

## 快速开始

环境要求：.NET 8 SDK

```bash
# 构建
dotnet build Spswj.Instrumentation.sln

# 测试
dotnet test Spswj.Instrumentation.sln
```

## 使用示例

### 1. 真实仪器（TCP/IP + SCPI）

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

### 2. 自动重连（可选）

```csharp
using var inner = new TcpInstrumentChannel();
using var channel = new AutoReconnectChannel(inner, new AutoReconnectOptions
{
    MaxReconnectAttempts = 3,
    InitialBackoff = TimeSpan.FromMilliseconds(200),
    MaxBackoff = TimeSpan.FromSeconds(5)
});

await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

// 连接异常时自动重连并重放，调用方无需处理
var idn = await channel.QueryAsync(
    SpectrumAnalyzerCommands.QueryIdn(),
    TimeSpan.FromSeconds(3));
```

默认只有连接类错误（未连接 / 连接已断开 / Socket / IO）才触发重连；协议或数据错误（如 payload 不完整）不会重连，避免掩盖真实问题。可通过 `AutoReconnectOptions.IsTransient` 自定义"可重连异常"判定。

### 3. Mock 仪器（无需硬件）

```csharp
using var channel = new MockInstrumentChannel();
channel.AddTextResponse(SpectrumAnalyzerCommands.QueryIdn(), "Agilent Technologies,N9020A,MY51288077,A.14.13");
channel.AddTextResponse(SpectrumAnalyzerCommands.QueryCenterFrequency(), "2.460000000E+09");
channel.AddBinaryResponse(SpectrumAnalyzerCommands.QueryTrace1(), new byte[4004]);
await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

var idn = await channel.QueryAsync(
    SpectrumAnalyzerCommands.QueryIdn(),
    TimeSpan.FromSeconds(1));

// channel.SentCommands 可断言实际发送了哪些命令
```

## 项目结构

```text
src/
  Spswj.Instrumentation/
    IInstrumentChannel.cs          # 通道抽象
    TcpInstrumentChannel.cs        # 真实 TCP/IP + SCPI 通道
    AutoReconnectChannel.cs        # 自动重连装饰器
    AutoReconnectOptions.cs        # 重连配置
    MockInstrumentChannel.cs       # 可编程假仪器
    SpectrumAnalyzers/
      SpectrumAnalyzerCommands.cs  # SCPI 命令构造
      TraceDataParser.cs           # REAL,32 trace 解析
      TraceAnalysisResult.cs       # 解析结果记录
tests/
  Spswj.Instrumentation.Tests/     # xUnit 测试套件（33 个）
```

## API 概览

| 类型 | 用途 |
|------|------|
| `IInstrumentChannel` | 通道抽象：连接、断开、查询、发送、二进制查询 |
| `TcpInstrumentChannel` | 真实 TCP/IP 实现，5s 连接/读取超时，支持 SCPI binary block 解析 |
| `AutoReconnectChannel` | 装饰器：连接错误时自动重连并重放操作 |
| `AutoReconnectOptions` | 最大重试、初始/最大退避、自定义瞬态异常判定 |
| `MockInstrumentChannel` | 假仪器：预设响应、命令记录、瞬态故障注入 |
| `SpectrumAnalyzerCommands` | 静态 SCPI 命令构造（频率/Span/参考电平/格式/trace） |
| `TraceDataParser` | 将 `REAL,32` trace 字节解析为峰值频率、功率和信噪差 |
| `TraceAnalysisResult` | 不可变结果记录 |

## 验证情况

- 33 个 xUnit 测试，覆盖协议解析、分片、异常路径、重连行为和 mock 场景。
- 测试套件内置了真实 Agilent N9020A 的现场数据 fixture（2026-06-11 采集），离线测试与真实设备行为高度一致。

## 版本口径

`v0.1.x` 是从 `SPSWJ v0.1.3` 提取的孵化快照。`v0.2.0` 新增自动重连、mock 通道和现场数据 fixture，并以 NuGet 包形式发布。API 暂不承诺长期稳定；待多个上位机实际引用后，再进入更严格的兼容性管理。

## 许可证

[MIT](LICENSE)
