namespace ScpiInstrument.SpectrumAnalyzers;

public sealed record TraceAnalysisResult(double MaxFrequencyMHz, double PowerDbm, double SignalNoiseDeltaDb);
