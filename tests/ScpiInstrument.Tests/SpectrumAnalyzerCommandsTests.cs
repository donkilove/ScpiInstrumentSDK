using System.Globalization;
using ScpiInstrument.SpectrumAnalyzers;

namespace ScpiInstrument.Tests;

public class SpectrumAnalyzerCommandsTests
{
    [Fact]
    public void Numeric_commands_use_invariant_decimal_separator()
    {
        using var culture = new TemporaryCulture("de-DE");

        Assert.Equal(":FREQ:CENT 2400.5 MHz", SpectrumAnalyzerCommands.SetCenterFrequency(2400.5));
        Assert.Equal(":FREQ:SPAN 12.75 MHz", SpectrumAnalyzerCommands.SetSpan(12.75));
        Assert.Equal("DISP:WIND:TRAC:Y:RLEV -30.25", SpectrumAnalyzerCommands.SetRefLevel(-30.25));
    }

    [Fact]
    public void Query_commands_match_expected_scpi_literals()
    {
        Assert.Equal("SYST:IDN?", SpectrumAnalyzerCommands.QueryIdn());
        Assert.Equal(":FREQ:CENT?", SpectrumAnalyzerCommands.QueryCenterFrequency());
        Assert.Equal(":FREQ:SPAN?", SpectrumAnalyzerCommands.QuerySpan());
        Assert.Equal("DISP:WIND:TRAC:Y:RLEV?", SpectrumAnalyzerCommands.QueryRefLevel());
        Assert.Equal(":FORM:DATA REAL,32", SpectrumAnalyzerCommands.SetDataFormatReal32());
        Assert.Equal(":FORM:BORD SWAP", SpectrumAnalyzerCommands.SetDataByteOrderSwap());
        Assert.Equal("TRAC1:TYPE MAXH", SpectrumAnalyzerCommands.SetTraceMaxHold());
        Assert.Equal(":TRAC? TRACE1", SpectrumAnalyzerCommands.QueryTrace1());
    }

    [Fact]
    public void LinkQueries_joins_with_semicolons_and_adds_leading_colons()
    {
        // 链接查询：; 分隔；后续命令必须带前导冒号（真机 N9020A 验证：
        // DISP:... 不带冒号会被当作前一命令的子命令报 -113 Undefined header）
        var linked = SpectrumAnalyzerCommands.LinkQueries(
            SpectrumAnalyzerCommands.QueryCenterFrequency(),
            SpectrumAnalyzerCommands.QuerySpan(),
            SpectrumAnalyzerCommands.QueryRefLevel());

        Assert.Equal(":FREQ:CENT?;:FREQ:SPAN?;:DISP:WIND:TRAC:Y:RLEV?", linked);
    }

    [Fact]
    public void LinkQueries_single_query_returns_as_is()
    {
        Assert.Equal(":FREQ:CENT?", SpectrumAnalyzerCommands.LinkQueries(":FREQ:CENT?"));
    }

    private sealed class TemporaryCulture : IDisposable
    {
        private readonly CultureInfo _previousCulture;
        private readonly CultureInfo _previousUiCulture;

        public TemporaryCulture(string cultureName)
        {
            _previousCulture = CultureInfo.CurrentCulture;
            _previousUiCulture = CultureInfo.CurrentUICulture;
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _previousCulture;
            CultureInfo.CurrentUICulture = _previousUiCulture;
        }
    }
}
