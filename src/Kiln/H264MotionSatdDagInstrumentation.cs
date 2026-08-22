using Kiln.Internal.H264;

namespace Kiln;

/// <summary>Optional exact SATD atom-DAG/cache diagnostics for H.264 motion estimation.</summary>
public static class H264MotionSatdDagInstrumentation
{
    public static bool Enabled
    {
        get => H264MotionSatdDagDiagnostics.Enabled;
        set => H264MotionSatdDagDiagnostics.Enabled = value;
    }

    public static bool IsEnabled => H264MotionSatdDagDiagnostics.IsEnabled;

    public static string BuildReport(bool reset = false) =>
        H264MotionSatdDagDiagnostics.BuildReport(reset);

    public static void Reset() => H264MotionSatdDagDiagnostics.Reset();
}
