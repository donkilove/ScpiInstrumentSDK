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
