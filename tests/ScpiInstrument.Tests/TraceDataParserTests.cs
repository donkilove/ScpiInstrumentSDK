using ScpiInstrument.SpectrumAnalyzers;

namespace ScpiInstrument.Tests;

public class TraceDataParserTests
{
    [Fact]
    public void ParseReal32Trace_maps_max_power_index_to_frequency_and_snr()
    {
        var data = CreateTrace(-30, -10, -20);

        var result = TraceDataParser.ParseReal32Trace(
            data,
            centerFreqHz: 2400e6,
            spanHz: 100e6);

        Assert.Equal(2400, result.MaxFrequencyMHz, precision: 6);
        Assert.Equal(-10, result.PowerDbm, precision: 6);
        Assert.Equal(10, result.SignalNoiseDeltaDb, precision: 6);
    }

    [Fact]
    public void ParseReal32Trace_rejects_empty_trace()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TraceDataParser.ParseReal32Trace(Array.Empty<byte>(), 2400e6, 100e6));

        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void ParseReal32Trace_rejects_non_float_aligned_trace()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TraceDataParser.ParseReal32Trace(new byte[] { 1, 2, 3 }, 2400e6, 100e6));

        Assert.Contains("multiple of 4", ex.Message);
    }

    [Fact]
    public void ParseReal32Trace_returns_center_frequency_and_zero_snr_for_single_point()
    {
        var data = CreateTrace(-12.5);

        var result = TraceDataParser.ParseReal32Trace(
            data,
            centerFreqHz: 2460e6,
            spanHz: 100e6);

        Assert.Equal(2460, result.MaxFrequencyMHz, precision: 6);
        Assert.Equal(-12.5, result.PowerDbm, precision: 6);
        Assert.Equal(0, result.SignalNoiseDeltaDb, precision: 6);
    }

    [Fact]
    public void ParseReal32Trace_maps_first_and_last_points_to_span_edges()
    {
        var firstPointMax = CreateTrace(-10, -20, -30);
        var lastPointMax = CreateTrace(-30, -20, -10);

        var first = TraceDataParser.ParseReal32Trace(firstPointMax, 2400e6, 100e6);
        var last = TraceDataParser.ParseReal32Trace(lastPointMax, 2400e6, 100e6);

        Assert.Equal(2350, first.MaxFrequencyMHz, precision: 6);
        Assert.Equal(2450, last.MaxFrequencyMHz, precision: 6);
    }

    [Fact]
    public void ParseReal32Trace_keeps_first_index_when_multiple_points_share_max_power()
    {
        var data = CreateTrace(-10, -10, -30);

        var result = TraceDataParser.ParseReal32Trace(data, 2400e6, 100e6);

        Assert.Equal(2350, result.MaxFrequencyMHz, precision: 6);
        Assert.Equal(-10, result.PowerDbm, precision: 6);
    }

    // ---- 审计 SC-08：NaN/Infinity 数据明确失败（不静默产出 NaN 信噪比） ----

    [Fact]
    public void ParseReal32Trace_rejects_nan_samples()
    {
        var data = CreateTrace(-30, float.NaN, -20);

        var ex = Assert.Throws<ArgumentException>(() =>
            TraceDataParser.ParseReal32Trace(data, 2400e6, 100e6));

        Assert.Contains("NaN/Infinity", ex.Message);
    }

    [Fact]
    public void ParseReal32Trace_rejects_infinity_samples()
    {
        var data = CreateTrace(-30, float.PositiveInfinity, -20);

        Assert.Throws<ArgumentException>(() =>
            TraceDataParser.ParseReal32Trace(data, 2400e6, 100e6));
    }

    private static byte[] CreateTrace(params double[] powers)
    {
        var floats = powers.Select(power => (float)power).ToArray();
        var bytes = new byte[sizeof(float) * floats.Length];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
