namespace Kiln.RateControl;

/// <summary>
/// Shared baseline tracker for network timing signals (RTT, jitter): the baseline snaps down to
/// the fastest sample seen and drifts up by 1/256 of the gap per update, so a genuinely changed
/// route becomes the new baseline over a few seconds of decisions while a transient spike barely
/// moves it. Introduced when the fixed 50 ms congestion RTT threshold was replaced with the
/// baseline-relative test (see <see cref="RateControlConfig.CongestionRttMultiplier"/>); the
/// severe-congestion tier and the jitter early-warning use the identical treatment, so the math
/// lives here once. Inputs are caller-supplied feedback only — no wall clock — keeping every
/// consumer deterministic.
/// </summary>
internal static class NetworkSignalBaseline
{
    /// <summary>
    /// Fold one sample (milliseconds) into the tracked baseline. Non-positive samples (callers
    /// without a measurement) leave the baseline untouched; until the first positive sample the
    /// baseline stays 0, meaning "unknown" — baseline-relative tests must not fire off it.
    /// </summary>
    /// <param name="baselineMs">Tracked baseline in milliseconds (0 = no sample seen yet).</param>
    /// <param name="sampleMs">Latest sample in milliseconds.</param>
    /// <returns>The updated baseline.</returns>
    public static double Update(ref double baselineMs, double sampleMs)
    {
        if (sampleMs <= 0)
        {
            return baselineMs;
        }

        if (baselineMs <= 0 || sampleMs < baselineMs)
        {
            baselineMs = sampleMs;
        }
        else
        {
            baselineMs += (sampleMs - baselineMs) / 256.0;
        }

        return baselineMs;
    }
}
