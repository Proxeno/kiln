using Kiln.Internal.H264;

namespace Kiln;

/// <summary>Opt-in diagnostics for graph-residual shadow ranking in H.264 motion estimation.</summary>
public static class H264MotionGraphResidualInstrumentation
{
    /// <summary>
    /// Enables in-process graph-residual candidate ranking collection. The environment variable
    /// <c>KILN_H264_GRC_INSTRUMENT=1</c> also enables collection for the process.
    /// </summary>
    public static bool Enabled
    {
        get => H264MotionGraphResidualDiagnostics.CollectCandidateRankings;
        set => H264MotionGraphResidualDiagnostics.CollectCandidateRankings = value;
    }

    public static bool IsEnabled => H264MotionGraphResidualDiagnostics.IsCandidateRankingCollectionEnabled;

    public static string BuildReport(bool reset = false) =>
        H264MotionGraphResidualDiagnostics.BuildCandidateRankingReport(reset);

    public static void Reset() => H264MotionGraphResidualDiagnostics.ResetCandidateRankings();
}
