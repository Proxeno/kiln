using Kiln.Internal.H264;

namespace Kiln;

/// <summary>
/// Opt-in timing diagnostics for P-macroblock encoding (motion search, prediction, luma, chroma
/// and bitstream writing). The switch is process-wide and off by default; it is meant for
/// benchmarks and investigation, not for production use.
/// </summary>
public static class H264PInterInstrumentation
{
    /// <summary>
    /// Turns timing collection on or off for the whole process. The environment variable
    /// <c>KILN_H264_P_INTER_TIMING=1</c> also turns it on.
    /// </summary>
    public static bool CollectPhase2Timing
    {
        get => H264PInterDiagnostics.CollectPhase2Timing;
        set => H264PInterDiagnostics.CollectPhase2Timing = value;
    }

    /// <summary>
    /// True when timing is being collected: through <see cref="CollectPhase2Timing"/>, through the
    /// environment variable, or because <see cref="H264MotionSatdDagInstrumentation"/> is enabled.
    /// </summary>
    public static bool IsPhase2TimingEnabled => H264PInterDiagnostics.IsPhase2TimingEnabled;

    /// <summary>Formats the timings collected so far as text.</summary>
    /// <param name="reset">When true, clears the collected timings after building the report.</param>
    public static string BuildPhase2TimingReport(bool reset = false) =>
        H264PInterDiagnostics.BuildPhase2TimingReport(reset);

    /// <summary>Clears the collected timings.</summary>
    public static void ResetPhase2Timing() => H264PInterDiagnostics.ResetPhase2Timing();
}
