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

    // ---- M65：字节序错配防线（大端数据被小端解释产出的值远超物理量程，必须拒绝而非静默通过） ----

    [Fact]
    public void ParseReal32Trace_rejects_byte_swapped_sample()
    {
        // AC-1：-20 dBm 的大端字节（0xC1A00000 反转）被小端解释为 ~41153 dBm → 必须拒绝并给出可操作提示
        var bigEndian = BitConverter.GetBytes(-20f);
        Array.Reverse(bigEndian);

        var ex = Assert.Throws<ArgumentException>(() =>
            TraceDataParser.ParseReal32Trace(bigEndian, 2400e6, 100e6));

        Assert.Contains("字节序", ex.Message);
        Assert.Contains(":FORM:BORD SWAP", ex.Message);
    }

    [Fact]
    public void ParseReal32Trace_allows_values_within_plausible_bounds()
    {
        // AC-2：量程边界语义——[-300, +200] dBm 内（含边界）正常解析
        var result = TraceDataParser.ParseReal32Trace(CreateTrace(-300, 200, -100), 2400e6, 100e6);

        Assert.Equal(200, result.PowerDbm, precision: 6);
    }

    [Theory]
    [InlineData(300.0)]    // 上越界
    [InlineData(-300.5)]   // 下越界
    public void ParseReal32Trace_rejects_out_of_range_sample(double power)
    {
        // AC-2：任一采样越界即拒绝（逐采样检查）
        var data = CreateTrace(-30, power, -20);

        var ex = Assert.Throws<ArgumentException>(() =>
            TraceDataParser.ParseReal32Trace(data, 2400e6, 100e6));

        Assert.Contains("字节序", ex.Message);
    }

    private static byte[] CreateTrace(params double[] powers)
    {
        var floats = powers.Select(power => (float)power).ToArray();
        var bytes = new byte[sizeof(float) * floats.Length];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
