using System.Globalization;

namespace Spswj.Instrumentation.SpectrumAnalyzers;

public static class SpectrumAnalyzerCommands
{
    public static string SetCenterFrequency(double freqMHz) => $":FREQ:CENT {Format(freqMHz)} MHz";
    public static string SetSpan(double spanMHz) => $":FREQ:SPAN {Format(spanMHz)} MHz";
    public static string SetRefLevel(double dbm) => $"DISP:WIND:TRAC:Y:RLEV {Format(dbm)}";
    public static string QueryIdn() => "SYST:IDN?";
    public static string QueryCenterFrequency() => ":FREQ:CENT?";
    public static string QuerySpan() => ":FREQ:SPAN?";
    public static string QueryRefLevel() => "DISP:WIND:TRAC:Y:RLEV?";
    public static string SetDataFormatReal32() => ":FORM:DATA REAL,32";
    public static string SetDataByteOrderSwap() => ":FORM:BORD SWAP";
    public static string SetTraceMaxHold() => "TRAC1:TYPE MAXH";
    public static string QueryTrace1() => ":TRAC? TRACE1";

    private static string Format(double value) => value.ToString("G", CultureInfo.InvariantCulture);
}
