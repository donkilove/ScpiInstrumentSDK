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
