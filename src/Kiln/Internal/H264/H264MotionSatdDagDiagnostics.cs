using System.Text;
using System.Threading;

namespace Kiln.Internal.H264;

/// <summary>Optional exact SATD atom-DAG/cache diagnostics for motion estimation.</summary>
internal static class H264MotionSatdDagDiagnostics
{
    private static readonly bool EnvEnabled = string.Equals(
        Environment.GetEnvironmentVariable("KILN_H264_SATD_DAG_INSTRUMENT"),
        "1",
        StringComparison.Ordinal);

    private static long s_satd4x4AtomComputes;
    private static long s_satd4x4AtomCacheHits;
    private static long s_satd4x4AtomCacheMisses;
    private static long s_satd4x4AtomCacheDisabledComputes;
    private static long s_partitionCompositionsFromAtoms;
    private static long s_partitionEarlyExits;
    private static long s_candidateCacheHits;
    private static long s_candidateCacheMisses;
    private static long s_sourceTransformComputes;
    private static long s_refTransformComputes;
    private static long s_refTransformCacheHits;
    private static long s_refTransformCacheMisses;
    private static long s_satdSadLowerBoundTests;
    private static long s_satdSadLowerBoundSkips;
    private static long s_candidateRingBreaks;
    private static long s_mbCandidateOverlapLookups;
    private static long s_mbCandidateOverlapReuses;
    private static long s_mbCandidateOverlapUnique;
    // Unified sub-partition search depth histogram (max candidateDistance per MB):
    //   [0] ≤4  [1] 5–8  [2] 9–12  [3] 13–16  [4] 17+
    private static readonly long[] s_unifiedSearchDepthBuckets = new long[5];

    private static readonly long[] s_atomComputesByShape = new long[4];
    private static readonly long[] s_atomHitsByShape = new long[4];
    private static readonly long[] s_atomMissesByShape = new long[4];
    private static readonly long[] s_atomDisabledComputesByShape = new long[4];
    private static readonly long[] s_compositionsByShape = new long[4];
    private static readonly long[] s_earlyExitsByShape = new long[4];
    private static readonly long[] s_lowerBoundTestsByShape = new long[4];
    private static readonly long[] s_lowerBoundSkipsByShape = new long[4];

    public static bool Enabled { get; set; }

    public static bool IsEnabled => Enabled || EnvEnabled;

    public readonly record struct Snapshot(
        long Satd4x4AtomComputes,
        long Satd4x4AtomCacheHits,
        long Satd4x4AtomCacheMisses,
        long Satd4x4AtomCacheDisabledComputes,
        long PartitionCompositionsFromAtoms,
        long PartitionEarlyExits,
        long CandidateCacheHits,
        long CandidateCacheMisses,
        long SourceTransformComputes,
        long RefTransformComputes,
        long RefTransformCacheHits,
        long RefTransformCacheMisses,
        long SatdSadLowerBoundTests,
        long SatdSadLowerBoundSkips,
        long CandidateRingBreaks,
        long MbCandidateOverlapLookups,
        long MbCandidateOverlapReuses,
        long MbCandidateOverlapUnique,
        long[] AtomComputesByShape,
        long[] AtomHitsByShape,
        long[] AtomMissesByShape,
        long[] AtomDisabledComputesByShape,
        long[] CompositionsByShape,
        long[] EarlyExitsByShape,
        long[] LowerBoundTestsByShape,
        long[] LowerBoundSkipsByShape,
        // Unified sub-partition search depth histogram: [0]≤4 [1]5-8 [2]9-12 [3]13-16 [4]17+
        long[] UnifiedSearchDepthBuckets);

    public static void Reset()
    {
        Interlocked.Exchange(ref s_satd4x4AtomComputes, 0);
        Interlocked.Exchange(ref s_satd4x4AtomCacheHits, 0);
        Interlocked.Exchange(ref s_satd4x4AtomCacheMisses, 0);
        Interlocked.Exchange(ref s_satd4x4AtomCacheDisabledComputes, 0);
        Interlocked.Exchange(ref s_partitionCompositionsFromAtoms, 0);
        Interlocked.Exchange(ref s_partitionEarlyExits, 0);
        Interlocked.Exchange(ref s_candidateCacheHits, 0);
        Interlocked.Exchange(ref s_candidateCacheMisses, 0);
        Interlocked.Exchange(ref s_sourceTransformComputes, 0);
        Interlocked.Exchange(ref s_refTransformComputes, 0);
        Interlocked.Exchange(ref s_refTransformCacheHits, 0);
        Interlocked.Exchange(ref s_refTransformCacheMisses, 0);
        Interlocked.Exchange(ref s_satdSadLowerBoundTests, 0);
        Interlocked.Exchange(ref s_satdSadLowerBoundSkips, 0);
        Interlocked.Exchange(ref s_candidateRingBreaks, 0);
        Interlocked.Exchange(ref s_mbCandidateOverlapLookups, 0);
        Interlocked.Exchange(ref s_mbCandidateOverlapReuses, 0);
        Interlocked.Exchange(ref s_mbCandidateOverlapUnique, 0);
        ResetArray(s_atomComputesByShape);
        ResetArray(s_atomHitsByShape);
        ResetArray(s_atomMissesByShape);
        ResetArray(s_atomDisabledComputesByShape);
        ResetArray(s_compositionsByShape);
        ResetArray(s_earlyExitsByShape);
        ResetArray(s_lowerBoundTestsByShape);
        ResetArray(s_lowerBoundSkipsByShape);
        ResetArray(s_unifiedSearchDepthBuckets);
    }

    public static Snapshot Read() => new(
        Volatile.Read(ref s_satd4x4AtomComputes),
        Volatile.Read(ref s_satd4x4AtomCacheHits),
        Volatile.Read(ref s_satd4x4AtomCacheMisses),
        Volatile.Read(ref s_satd4x4AtomCacheDisabledComputes),
        Volatile.Read(ref s_partitionCompositionsFromAtoms),
        Volatile.Read(ref s_partitionEarlyExits),
        Volatile.Read(ref s_candidateCacheHits),
        Volatile.Read(ref s_candidateCacheMisses),
        Volatile.Read(ref s_sourceTransformComputes),
        Volatile.Read(ref s_refTransformComputes),
        Volatile.Read(ref s_refTransformCacheHits),
        Volatile.Read(ref s_refTransformCacheMisses),
        Volatile.Read(ref s_satdSadLowerBoundTests),
        Volatile.Read(ref s_satdSadLowerBoundSkips),
        Volatile.Read(ref s_candidateRingBreaks),
        Volatile.Read(ref s_mbCandidateOverlapLookups),
        Volatile.Read(ref s_mbCandidateOverlapReuses),
        Volatile.Read(ref s_mbCandidateOverlapUnique),
        ReadArray(s_atomComputesByShape),
        ReadArray(s_atomHitsByShape),
        ReadArray(s_atomMissesByShape),
        ReadArray(s_atomDisabledComputesByShape),
        ReadArray(s_compositionsByShape),
        ReadArray(s_earlyExitsByShape),
        ReadArray(s_lowerBoundTestsByShape),
        ReadArray(s_lowerBoundSkipsByShape),
        ReadArray(s_unifiedSearchDepthBuckets));

    public static string BuildReport(bool reset = false)
    {
        var s = Read();
        var atomRequests = s.Satd4x4AtomCacheHits + s.Satd4x4AtomCacheMisses + s.Satd4x4AtomCacheDisabledComputes;
        var candidateLookups = s.CandidateCacheHits + s.CandidateCacheMisses;
        var refTransformLookups = s.RefTransformCacheHits + s.RefTransformCacheMisses;

        static string Pct(long numerator, long denominator) =>
            denominator <= 0 ? "0.0%" : ((double)numerator * 100.0 / denominator).ToString("F1") + "%";

        var sb = new StringBuilder(768);
        sb.Append("SATD DAG: atomRequests=").Append(atomRequests)
            .Append(" atomComputes=").Append(s.Satd4x4AtomComputes)
            .Append(" savedByCache=").Append(s.Satd4x4AtomCacheHits)
            .Append(" atomCacheHits=").Append(s.Satd4x4AtomCacheHits)
            .Append(" atomCacheMisses=").Append(s.Satd4x4AtomCacheMisses)
            .Append(" atomCacheHitRate=").Append(Pct(s.Satd4x4AtomCacheHits, atomRequests))
            .Append(" atomCacheDisabledComputes=").Append(s.Satd4x4AtomCacheDisabledComputes).AppendLine();
        sb.Append("  partitionCompositionsFromAtoms=").Append(s.PartitionCompositionsFromAtoms)
            .Append(" partitionEarlyExits=").Append(s.PartitionEarlyExits)
            .Append(" partitionEarlyExitRate=").Append(Pct(s.PartitionEarlyExits, s.PartitionCompositionsFromAtoms)).AppendLine();
        sb.Append("  candidateCacheHits=").Append(s.CandidateCacheHits)
            .Append(" candidateCacheMisses=").Append(s.CandidateCacheMisses)
            .Append(" candidateCacheHitRate=").Append(Pct(s.CandidateCacheHits, candidateLookups)).AppendLine();
        sb.Append("  sourceTransformComputes=").Append(s.SourceTransformComputes)
            .Append(" refTransformComputes=").Append(s.RefTransformComputes)
            .Append(" refTransformCacheHits=").Append(s.RefTransformCacheHits)
            .Append(" refTransformCacheMisses=").Append(s.RefTransformCacheMisses)
            .Append(" refTransformCacheHitRate=").Append(Pct(s.RefTransformCacheHits, refTransformLookups)).AppendLine();
        sb.Append("  satdSadLowerBoundTests=").Append(s.SatdSadLowerBoundTests)
            .Append(" satdSadLowerBoundSkips=").Append(s.SatdSadLowerBoundSkips)
            .Append(" satdSadLowerBoundSkipRate=").Append(Pct(s.SatdSadLowerBoundSkips, s.SatdSadLowerBoundTests))
            .Append(" candidateRingBreaks=").Append(s.CandidateRingBreaks).AppendLine();
        sb.Append("  mbCandidateOverlapLookups=").Append(s.MbCandidateOverlapLookups)
            .Append(" mbCandidateOverlapReuses=").Append(s.MbCandidateOverlapReuses)
            .Append(" mbCandidateOverlapUnique=").Append(s.MbCandidateOverlapUnique)
            .Append(" mbCandidateOverlapReuseRate=").Append(Pct(s.MbCandidateOverlapReuses, s.MbCandidateOverlapLookups)).AppendLine();
        var totalDepth = s.UnifiedSearchDepthBuckets.Sum();
        if (totalDepth > 0)
        {
            sb.Append("  unifiedSearchDepth(MBs): ");
            string[] labels = ["≤4", "5-8", "9-12", "13-16", "17+"];
            for (var i = 0; i < s.UnifiedSearchDepthBuckets.Length; i++)
            {
                if (i != 0) sb.Append(' ');
                sb.Append(labels[i]).Append('=').Append(s.UnifiedSearchDepthBuckets[i])
                  .Append('(').Append(Pct(s.UnifiedSearchDepthBuckets[i], totalDepth)).Append(')');
            }
            sb.AppendLine();
        }
        sb.Append("  byShape: ");
        for (var i = 0; i < 4; i++)
        {
            if (i != 0)
                sb.Append(" | ");
            var lookups = s.AtomHitsByShape[i] + s.AtomMissesByShape[i] + s.AtomDisabledComputesByShape[i];
            sb.Append(ShapeName(i))
                .Append(" comps=").Append(s.CompositionsByShape[i])
                .Append(" early=").Append(s.EarlyExitsByShape[i])
                .Append(" requests=").Append(lookups)
                .Append(" atomComputes=").Append(s.AtomComputesByShape[i])
                .Append(" hitRate=").Append(Pct(s.AtomHitsByShape[i], lookups))
                .Append(" lbSkips=").Append(s.LowerBoundSkipsByShape[i])
                .Append('/').Append(s.LowerBoundTestsByShape[i]);
        }

        if (reset)
            Reset();

        return sb.ToString();
    }

    public static void NotifyPartitionComposition(int shape)
    {
        if (!IsEnabled)
            return;
        Interlocked.Increment(ref s_partitionCompositionsFromAtoms);
        Interlocked.Increment(ref s_compositionsByShape[shape]);
    }

    public static void NotifyPartitionEarlyExit(int shape)
    {
        if (!IsEnabled)
            return;
        Interlocked.Increment(ref s_partitionEarlyExits);
        Interlocked.Increment(ref s_earlyExitsByShape[shape]);
    }

    public static void NotifyAtomCacheHit(int shape)
    {
        if (!IsEnabled)
            return;
        Interlocked.Increment(ref s_satd4x4AtomCacheHits);
        Interlocked.Increment(ref s_atomHitsByShape[shape]);
    }

    public static void NotifyAtomCacheMissCompute(int shape)
    {
        if (!IsEnabled)
            return;
        Interlocked.Increment(ref s_satd4x4AtomCacheMisses);
        Interlocked.Increment(ref s_atomMissesByShape[shape]);
        NotifyAtomCompute(shape);
    }

    public static void NotifyAtomCacheDisabledCompute(int shape)
    {
        if (!IsEnabled)
            return;
        Interlocked.Increment(ref s_satd4x4AtomCacheDisabledComputes);
        Interlocked.Increment(ref s_atomDisabledComputesByShape[shape]);
        NotifyAtomCompute(shape);
    }

    public static void NotifyCandidateCacheHit()
    {
        if (IsEnabled)
            Interlocked.Increment(ref s_candidateCacheHits);
    }

    public static void NotifyCandidateCacheMiss()
    {
        if (IsEnabled)
            Interlocked.Increment(ref s_candidateCacheMisses);
    }

    public static void NotifySourceTransformComputes(int count)
    {
        if (IsEnabled)
            Interlocked.Add(ref s_sourceTransformComputes, count);
    }

    public static void NotifyRefTransformCacheHit()
    {
        if (IsEnabled)
            Interlocked.Increment(ref s_refTransformCacheHits);
    }

    public static void NotifyRefTransformCacheMissCompute()
    {
        if (!IsEnabled)
            return;
        Interlocked.Increment(ref s_refTransformCacheMisses);
        Interlocked.Increment(ref s_refTransformComputes);
    }

    public static void NotifySatdSadLowerBoundTest(int shape, bool skipped)
    {
        if (!IsEnabled)
            return;
        Interlocked.Increment(ref s_satdSadLowerBoundTests);
        Interlocked.Increment(ref s_lowerBoundTestsByShape[shape]);
        if (!skipped)
            return;
        Interlocked.Increment(ref s_satdSadLowerBoundSkips);
        Interlocked.Increment(ref s_lowerBoundSkipsByShape[shape]);
    }

    public static void NotifyCandidateRingBreak()
    {
        if (!IsEnabled)
            return;
        Interlocked.Increment(ref s_candidateRingBreaks);
    }

    public static void NotifyUnifiedSubPartitionDepth(int maxCandidateDistance)
    {
        if (!IsEnabled)
            return;
        var bucket = maxCandidateDistance <= 4 ? 0
            : maxCandidateDistance <= 8  ? 1
            : maxCandidateDistance <= 12 ? 2
            : maxCandidateDistance <= 16 ? 3
            : 4;
        Interlocked.Increment(ref s_unifiedSearchDepthBuckets[bucket]);
    }

    public static void NotifyMbCandidateOverlap(bool reused)
    {
        if (!IsEnabled)
            return;
        Interlocked.Increment(ref s_mbCandidateOverlapLookups);
        if (reused)
            Interlocked.Increment(ref s_mbCandidateOverlapReuses);
        else
            Interlocked.Increment(ref s_mbCandidateOverlapUnique);
    }

    private static void NotifyAtomCompute(int shape)
    {
        Interlocked.Increment(ref s_satd4x4AtomComputes);
        Interlocked.Increment(ref s_atomComputesByShape[shape]);
    }

    private static long[] ReadArray(long[] values)
    {
        var copy = new long[values.Length];
        for (var i = 0; i < values.Length; i++)
            copy[i] = Volatile.Read(ref values[i]);
        return copy;
    }

    private static void ResetArray(long[] values)
    {
        for (var i = 0; i < values.Length; i++)
            Interlocked.Exchange(ref values[i], 0);
    }

    private static string ShapeName(int shape) => shape switch
    {
        0 => "16x16",
        1 => "16x8",
        2 => "8x16",
        _ => "8x8",
    };
}
