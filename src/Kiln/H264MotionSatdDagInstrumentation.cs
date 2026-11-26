using Kiln.Internal.H264;

namespace Kiln;

/// <summary>
/// Opt-in counters for SATD reuse and caching during motion estimation. The switch is process-wide
/// and off by default; it is meant for benchmarks and investigation, not for production use.
/// </summary>
public static class H264MotionSatdDagInstrumentation
{
    /// <summary>
    /// Turns collection on or off for the whole process. The environment variable
    /// <c>KILN_H264_SATD_DAG_INSTRUMENT=1</c> also turns it on.
    /// </summary>
    public static bool Enabled
    {
        get => H264MotionSatdDagDiagnostics.Enabled;
        set => H264MotionSatdDagDiagnostics.Enabled = value;
    }

    /// <summary>True when collection is on, through <see cref="Enabled"/> or the environment variable.</summary>
    public static bool IsEnabled => H264MotionSatdDagDiagnostics.IsEnabled;

    /// <summary>Formats the counters collected so far as text.</summary>
    /// <param name="reset">When true, clears the counters after building the report.</param>
    public static string BuildReport(bool reset = false) =>
        H264MotionSatdDagDiagnostics.BuildReport(reset);

    /// <summary>Clears the counters.</summary>
    public static void Reset() => H264MotionSatdDagDiagnostics.Reset();
}
