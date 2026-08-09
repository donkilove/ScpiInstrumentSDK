# ScpiInstrument

[![NuGet](https://img.shields.io/badge/NuGet-0.2.0-blue)](https://github.com/donkilove/ScpiInstrument/pkgs/nuget/ScpiInstrument)
[![CI](https://github.com/donkilove/ScpiInstrument/actions/workflows/ci.yml/badge.svg)](https://github.com/donkilove/ScpiInstrument/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![TargetFramework](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)

[English](README.md) · **简体中文**

一个基于 .NET 8 的仪器通信库:通过 TCP/IP 使用 SCPI 协议与测试测量仪器通信,并支持频谱分析仪 trace 数据解析。

本库从内部生产测试主机中提取并发布为可复用组件,让多个测试主机应用可以共享一套经过充分验证的通信栈,而无需重复维护源代码。

## 目录

- [功能特性](#功能特性)
- [安装](#安装)
- [快速开始](#快速开始)
- [用法](#用法)
  - [真实仪器(TCP/IP + SCPI)](#1-真实仪器tcpip--scpi)
  - [自动重连(可选)](#2-自动重连可选)
  - [模拟仪器(无需硬件)](#3-模拟仪器无需硬件)
- [API 总览](#api-总览)
- [项目结构](#项目结构)
- [测试与验证](#测试与验证)
- [版本说明](#版本说明)
- [参与贡献](#参与贡献)
- [许可协议](#许可协议)

## 功能特性

- **TCP/IP 仪器通道** —— 连接 / 断开 / 状态检查,支持连接与读取超时。
- **SCPI 文本收发** —— 发送命令与查询响应,支持取消。
- **SCPI 定长二进制数据块** —— 完整解析 `#<n><len>` 头,处理分包到达,可选消费结尾 `\n` / `\r\n`。
- **频谱分析仪命令构建器** —— 中心频率、扫宽、参考电平、REAL,32 数据格式、MaxHold 与 trace 查询(InvariantCulture 安全)。
- **`REAL,32` trace 解析** —— 返回峰值频率、峰值功率与简单的信噪比差值。
- **自动重连装饰器** —— 连接出错时透明重连并重放操作,支持指数退避与可配置重试上限。
- **可编程模拟通道** —— 按命令预设文本/二进制响应,记录已发送命令用于断言,并支持注入瞬时故障,便于离线开发与自动化测试。

## 安装

包发布在 GitHub Packages(NuGet 源):

```bash
dotnet add package ScpiInstrument --version 0.2.0 \
  --source "https://nuget.pkg.github.com/donkilove/index.json"
```

> GitHub Packages 源需要认证。请配置具有 `read:packages` 权限的令牌,例如在环境中使用 `gh auth token`,或将源添加到你的 `NuGet.Config` 中。

## 快速开始

环境要求:.NET 8 SDK

```bash
# 构建
dotnet build ScpiInstrument.sln

# 测试
dotnet test ScpiInstrument.sln
```

## 用法

### 1. 真实仪器(TCP/IP + SCPI)

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

### 2. 自动重连(可选)

```csharp
using var inner = new TcpInstrumentChannel();
using var channel = new AutoReconnectChannel(inner, new AutoReconnectOptions
{
    MaxReconnectAttempts = 3,
    InitialBackoff = TimeSpan.FromMilliseconds(200),
    MaxBackoff = TimeSpan.FromSeconds(5)
});

await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

// 连接错误透明处理:自动重连 + 重放操作,调用方无需改动
var idn = await channel.QueryAsync(
    SpectrumAnalyzerCommands.QueryIdn(),
    TimeSpan.FromSeconds(3));
```

默认只有连接类错误(未连接 / 连接断开 / socket / IO)会触发重连;协议或数据错误(如不完整的数据包)从不重连,以免掩盖真实问题。可通过 `AutoReconnectOptions.IsTransient` 自定义瞬时错误判定。

### 3. 模拟仪器(无需硬件)

```csharp
using var channel = new MockInstrumentChannel();
channel.AddTextResponse(SpectrumAnalyzerCommands.QueryIdn(), "Agilent Technologies,N9020A,MY51288077,A.14.13");
channel.AddTextResponse(SpectrumAnalyzerCommands.QueryCenterFrequency(), "2.460000000E+09");
channel.AddBinaryResponse(SpectrumAnalyzerCommands.QueryTrace1(), new byte[4004]);
await channel.ConnectAsync("192.168.1.1", 5025, CancellationToken.None);

var idn = await channel.QueryAsync(
    SpectrumAnalyzerCommands.QueryIdn(),
    TimeSpan.FromSeconds(1));

// channel.SentCommands 可用于断言实际发送了哪些命令
```

## API 总览

| 类型 | 用途 |
|------|------|
| `IInstrumentChannel` | 通道抽象:连接、断开、查询、发送、二进制查询 |
| `TcpInstrumentChannel` | 真实 TCP/IP 实现,5s 连接/读取超时,支持 SCPI 二进制数据块解析 |
| `AutoReconnectChannel` | 装饰器:连接错误时自动重连并重放操作 |
| `AutoReconnectOptions` | 最大重试次数、初始/最大退避时间、自定义瞬时错误判定 |
| `MockInstrumentChannel` | 模拟仪器:预设响应、命令记录、瞬时故障注入 |
| `SpectrumAnalyzerCommands` | SCPI 命令构建器(频率/扫宽/参考电平/格式/trace) |
| `TraceDataParser` | 将 `REAL,32` trace 字节解析为峰值频率、功率与信噪比差值 |
| `TraceAnalysisResult` | 不可变的结果记录 |

## 项目结构

```text
src/
  ScpiInstrument/
    IInstrumentChannel.cs          # 通道抽象
    TcpInstrumentChannel.cs        # 真实 TCP/IP + SCPI 通道
    AutoReconnectChannel.cs        # 重连装饰器
    AutoReconnectOptions.cs        # 重连配置
    MockInstrumentChannel.cs       # 可编程模拟仪器
    SpectrumAnalyzers/
      SpectrumAnalyzerCommands.cs  # SCPI 命令构建器
      TraceDataParser.cs           # REAL,32 trace 解析
      TraceAnalysisResult.cs       # 解析结果记录
tests/
  ScpiInstrument.Tests/            # xUnit 测试套件(33 个测试)
```

## 测试与验证

- 33 个 xUnit 测试,覆盖协议解析、分包、错误路径、重连行为与模拟场景。
- 测试套件内嵌来自真实 Agilent N9020A 的现场数据固件(采集于 2026-06-11),离线测试也能贴近真实设备行为。
- 每次 push 与 pull request 都会在 CI 中执行完整构建与测试(`.github/workflows/ci.yml`)。

## 版本说明

`v0.1.x` 是从内部生产测试主机提取的孵化期快照。`v0.2.0` 新增自动重连、模拟通道与现场数据固件,并以 `ScpiInstrument` 名称作为 NuGet 包首次发布。API 尚未承诺长期稳定;待多个测试主机实际使用该库后,将开始更严格的兼容性管理。

## 参与贡献

欢迎贡献代码。提交 pull request 前请先开 issue 讨论变更方案,并确保现有测试套件通过。

## 许可协议

[MIT](LICENSE)
