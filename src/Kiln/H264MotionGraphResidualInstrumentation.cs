using Kiln.Internal.H264;

namespace Kiln;

/// <summary>
/// Opt-in diagnostics that compare an experimental graph-residual ranking of motion-search
/// candidates with their SAD ranking. Collection applies when the encoder scores candidates with
/// SATD. The switch is process-wide and off by default; it is meant for
/// investigation, not for production use.
/// </summary>
public static class H264MotionGraphResidualInstrumentation
{
    /// <summary>
    /// Turns candidate-ranking collection on or off for the whole process. The environment variable
    /// <c>KILN_H264_GRC_INSTRUMENT=1</c> also turns it on.
    /// </summary>
    public static bool Enabled
    {
        get => H264MotionGraphResidualDiagnostics.CollectCandidateRankings;
        set => H264MotionGraphResidualDiagnostics.CollectCandidateRankings = value;
    }

    /// <summary>True when collection is on, through <see cref="Enabled"/> or the environment variable.</summary>
    public static bool IsEnabled => H264MotionGraphResidualDiagnostics.IsCandidateRankingCollectionEnabled;

    /// <summary>Formats the rankings collected so far as text.</summary>
    /// <param name="reset">When true, clears the collected rankings after building the report.</param>
    public static string BuildReport(bool reset = false) =>
        H264MotionGraphResidualDiagnostics.BuildCandidateRankingReport(reset);

    /// <summary>Clears the collected rankings.</summary>
    public static void Reset() => H264MotionGraphResidualDiagnostics.ResetCandidateRankings();
}
