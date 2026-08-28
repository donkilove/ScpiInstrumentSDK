namespace ScpiInstrument.SpectrumAnalyzers;

public static class TraceDataParser
{
    /// <summary>
    /// 解析 REAL,32 格式的 trace 数据，返回最大功率点频率、最大功率和简单信噪差。
    /// </summary>
    public static TraceAnalysisResult ParseReal32Trace(byte[] data, double centerFreqHz, double spanHz)
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
        foreach (var value in floats)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentException($"Trace data 包含 NaN/Infinity：{value}", nameof(data));
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
