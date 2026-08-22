using Kiln.Internal.H264;

namespace Kiln;

public static class H264PInterInstrumentation
{
    public static bool CollectPhase2Timing
    {
        get => H264PInterDiagnostics.CollectPhase2Timing;
        set => H264PInterDiagnostics.CollectPhase2Timing = value;
    }

    public static bool IsPhase2TimingEnabled => H264PInterDiagnostics.IsPhase2TimingEnabled;

    public static string BuildPhase2TimingReport(bool reset = false) =>
        H264PInterDiagnostics.BuildPhase2TimingReport(reset);

    public static void ResetPhase2Timing() => H264PInterDiagnostics.ResetPhase2Timing();
}
