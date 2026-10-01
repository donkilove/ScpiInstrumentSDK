namespace ScpiInstrument.SpectrumAnalyzers;

public static class TraceDataParser
{
    /// <summary>
    /// 解析 REAL,32 格式的 trace 数据，返回最大功率点频率、最大功率和简单信噪差。
    /// M65 字节序错配防线（双层）：①任一采样为非规格化浮点（IsSubnormal）——真实功率谱
    /// 不会出现，是字节位型错位的典型产物；②任一采样超出
    /// [<paramref name="minPlausibleDbm"/>, <paramref name="maxPlausibleDbm"/>] 物理量程
    /// （错配后约半数样本重解释为巨大值）。命中即立即失败并给出可操作诊断，不再静默产出
    /// 错误读数；极端特殊仪器场景可参数放宽。
    /// </summary>
    /// <param name="minPlausibleDbm">物理合理功率下界（dBm）。</param>
    /// <param name="maxPlausibleDbm">物理合理功率上界（dBm）。</param>
    public static TraceAnalysisResult ParseReal32Trace(
        byte[] data, double centerFreqHz, double spanHz,
        double minPlausibleDbm = -300, double maxPlausibleDbm = 200)
    {
        if (data.Length % 4 != 0)
            throw new ArgumentException($"Trace data length ({data.Length}) must be multiple of 4", nameof(data));

        var count = data.Length / 4;
        if (count == 0)
            throw new ArgumentException("Trace data is empty", nameof(data));

        var floats = new float[count];
        Buffer.BlockCopy(data, 0, floats, 0, data.Length);

        // 审计 SC-08：任一采样为 NaN/Infinity 时明确失败（信噪比/功率计算会静默产出
        // NaN 且 maxPower 保持 MinValue 输出 -1.79e308，产线误判）
        // M65：字节序错配防线（非规格化 + 量程双层）——漏发 :FORM:BORD SWAP / 配置丢失时
        // 错值静默通过、产线读数全错无告警；命中即拒绝并给出可操作诊断
        foreach (var value in floats)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentException($"Trace data 包含 NaN/Infinity：{value}", nameof(data));
            }

            if (float.IsSubnormal(value)
                || value < minPlausibleDbm || value > maxPlausibleDbm)
            {
                throw new ArgumentException(
                    $"Trace data 疑似字节序错配：采样 {value} dBm 为非规格化浮点或超出合理范围 [{minPlausibleDbm}, {maxPlausibleDbm}]。" +
                    "请检查频谱仪 :FORM:BORD SWAP（大端/小端字节序）配置是否与数据格式配对生效。",
                    nameof(data));
            }
        }

        if (count == 1)
            return new TraceAnalysisResult(centerFreqHz / 1e6, floats[0], 0);

        var maxPower = double.MinValue;
        var maxPowerIndex = 0;
        double totalPower = 0;

        for (var i = 0; i < count; i++)
        {
            if (floats[i] > maxPower)
            {
                maxPower = floats[i];
                maxPowerIndex = i;
            }

            totalPower += floats[i];
        }

        var avgPower = totalPower / count;
        var signalNoiseDelta = maxPower - avgPower;
        var actualFreqHz = centerFreqHz - spanHz / 2.0 + (double)maxPowerIndex / (count - 1) * spanHz;
        var actualFreqMHz = actualFreqHz / 1e6;

        return new TraceAnalysisResult(actualFreqMHz, maxPower, signalNoiseDelta);
    }
}
