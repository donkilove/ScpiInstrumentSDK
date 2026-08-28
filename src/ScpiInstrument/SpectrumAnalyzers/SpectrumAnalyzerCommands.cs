using System.Globalization;

namespace ScpiInstrument.SpectrumAnalyzers;

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

    /// <summary>
    /// 把多个查询链接为一条 SCPI 消息（; 分隔），一次往返取多值（响应同样以 ; 分隔）。
    /// 自动为无前导冒号的命令补 ':'——真机 N9020A 验证：链接中的后续命令不带冒号
    /// 会被当作前一命令的子命令（如 :FREQ:CENT?;DISP:... 报 -113 Undefined header）。
    /// </summary>
    public static string LinkQueries(params string[] queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        return string.Join(';', queries.Select(q => q.StartsWith(':') ? q : ":" + q));
    }

    /// <summary>
    /// 切分链接查询响应（审计 SC-12：配套 <see cref="LinkQueries"/> 的响应解析）。
    /// SCPI 数值响应不含 ';'；如响应值本身含分号需另行处理（当前仪器集无此情况）。
    /// </summary>
    public static IReadOnlyList<string> SplitLinkedResponses(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Split(';', StringSplitOptions.TrimEntries);
    }

    private static string Format(double value) => value.ToString("G", CultureInfo.InvariantCulture);
}
