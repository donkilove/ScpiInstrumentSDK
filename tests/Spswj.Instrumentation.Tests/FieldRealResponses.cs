using Spswj.Instrumentation.SpectrumAnalyzers;

namespace Spswj.Instrumentation.Tests;

/// <summary>
/// 固化的真实频谱仪现场数据（来源：SPSWJ docs/现场验证/现场频谱仪验证报告.md，2026-06-11）。
/// 设备：Agilent N9020A（MY51288077），地址 192.168.1.1:5025。
/// 用途：让离线测试尽量贴近真机行为，作为协议解析与 trace 解析的权威基线。
/// </summary>
public static class FieldRealResponses
{
    // ---- 设备识别 ----
    public const string DeviceIdn = "Agilent Technologies,N9020A,MY51288077,A.14.13";

    // ---- 生产链路设置（重置后复测段） ----
    public const double CenterFreqMHz = 2460.0;
    public const double SpanMHz = 100.0;
    public const double RefLevelDbm = -30.0;

    // ---- 生产代码读回校验（真实回显格式） ----
    public const string ReadbackCenterFreq = "2.460000000E+09";
    public const string ReadbackSpan = "1.000000000E+08";
    public const string ReadbackRefLevel = "-3.000E+01";

    // ---- Trace 真实格式 ----
    // `#44004` binary block，payload 4004 字节 = 1001 个 REAL,32 点
    public const int TracePointCount = 1001;
    public const int TracePayloadBytes = 4004;
    public const string TraceBlockHeader = "#44004";

    // ---- 生产链路实测读数（2026-06-11 17:52） ----
    public const double FieldPowerDbm = -2.865;
    public const double FieldMaxFreqMHz = 2460.0;
    public const double FieldSnDeltaDb = 64.419;

    /// <summary>
    /// 按现场格式构造一条 REAL,32 trace：1001 个点、中心 2460 MHz、Span 100 MHz，
    /// 中心点功率 = 现场实测 -2.865 dBm，其余点取噪声底使信噪差复现 64.419 dB。
    /// 与真实频谱仪返回的字节布局一致（REAL,32、little-endian、4004 字节）。
    /// </summary>
    public static byte[] CreateFieldTrace()
    {
        // 精确反推噪声底：SN = max - avg，avg 含中心点本身，故
        // noise = max - SN * N / (N-1)，否则 SN 会偏差约 0.06 dB
        var noiseFloorDbm = FieldPowerDbm
            - FieldSnDeltaDb * TracePointCount / (TracePointCount - 1);
        var powers = new float[TracePointCount];

        for (var i = 0; i < powers.Length; i++)
            powers[i] = (float)noiseFloorDbm;

        var centerIndex = TracePointCount / 2; // 500 -> 恰好落在 2460 MHz 中心
        powers[centerIndex] = (float)FieldPowerDbm;

        var bytes = new byte[TracePayloadBytes];
        Buffer.BlockCopy(powers, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// 解析后的现场基线：使用与生产链路一致的中心频率/Span 调用解析器。
    /// </summary>
    public static TraceAnalysisResult ParseFieldTrace() =>
        TraceDataParser.ParseReal32Trace(
            CreateFieldTrace(),
            centerFreqHz: CenterFreqMHz * 1e6,
            spanHz: SpanMHz * 1e6);
}
