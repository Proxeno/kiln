using System.Text;
using System.Threading;

namespace Kiln.Internal.H264;

/// <summary>Optional shadow diagnostics for graph-residual motion-search ranking.</summary>
internal static class H264MotionGraphResidualDiagnostics
{
    private static readonly bool EnvCollectCandidateRankings = string.Equals(
        Environment.GetEnvironmentVariable("KILN_H264_GRC_INSTRUMENT"),
        "1",
        StringComparison.Ordinal);

    private static long s_candidateSets;
    private static long s_candidates;
    private static long s_grcTop1Winner;
    private static long s_grcTop2Winner;
    private static long s_grcTop4Winner;
    private static long s_sadTop1Winner;
    private static long s_sadTop2Winner;
    private static long s_sadTop4Winner;
    private static long s_margin110Winner;
    private static long s_margin125Winner;
    private static long s_margin150Winner;
    private static long s_margin110Rejected;
    private static long s_margin125Rejected;
    private static long s_margin150Rejected;

    public static bool CollectCandidateRankings { get; set; }

    public static bool IsCandidateRankingCollectionEnabled => CollectCandidateRankings || EnvCollectCandidateRankings;

    public static bool ShouldCollectCandidateRankings(bool useMotionSatd) =>
        useMotionSatd && IsCandidateRankingCollectionEnabled;

    public readonly record struct RankingSnapshot(
        long CandidateSets,
        long Candidates,
        long GrcTop1Winner,
        long GrcTop2Winner,
        long GrcTop4Winner,
        long SadTop1Winner,
        long SadTop2Winner,
        long SadTop4Winner,
        long Margin110Winner,
        long Margin125Winner,
        long Margin150Winner,
        long Margin110Rejected,
        long Margin125Rejected,
        long Margin150Rejected);

    public static void ResetCandidateRankings()
    {
        Interlocked.Exchange(ref s_candidateSets, 0);
        Interlocked.Exchange(ref s_candidates, 0);
        Interlocked.Exchange(ref s_grcTop1Winner, 0);
        Interlocked.Exchange(ref s_grcTop2Winner, 0);
        Interlocked.Exchange(ref s_grcTop4Winner, 0);
        Interlocked.Exchange(ref s_sadTop1Winner, 0);
        Interlocked.Exchange(ref s_sadTop2Winner, 0);
        Interlocked.Exchange(ref s_sadTop4Winner, 0);
        Interlocked.Exchange(ref s_margin110Winner, 0);
        Interlocked.Exchange(ref s_margin125Winner, 0);
        Interlocked.Exchange(ref s_margin150Winner, 0);
        Interlocked.Exchange(ref s_margin110Rejected, 0);
        Interlocked.Exchange(ref s_margin125Rejected, 0);
        Interlocked.Exchange(ref s_margin150Rejected, 0);
    }

    public static RankingSnapshot ReadCandidateRankings() => new(
        Volatile.Read(ref s_candidateSets),
        Volatile.Read(ref s_candidates),
        Volatile.Read(ref s_grcTop1Winner),
        Volatile.Read(ref s_grcTop2Winner),
        Volatile.Read(ref s_grcTop4Winner),
        Volatile.Read(ref s_sadTop1Winner),
        Volatile.Read(ref s_sadTop2Winner),
        Volatile.Read(ref s_sadTop4Winner),
        Volatile.Read(ref s_margin110Winner),
        Volatile.Read(ref s_margin125Winner),
        Volatile.Read(ref s_margin150Winner),
        Volatile.Read(ref s_margin110Rejected),
        Volatile.Read(ref s_margin125Rejected),
        Volatile.Read(ref s_margin150Rejected));

    public static void NotifyCandidateSet(
        int candidateCount,
        int winnerIndex,
        int winnerGrc,
        ReadOnlySpan<int> grcCosts,
        ReadOnlySpan<int> topGrcIndexes,
        ReadOnlySpan<int> topSadIndexes)
    {
        if (candidateCount <= 0 || winnerIndex < 0)
            return;

        var bestGrc = int.MaxValue;
        for (var i = 0; i < candidateCount; i++)
        {
            if (grcCosts[i] < bestGrc)
                bestGrc = grcCosts[i];
        }

        var rejected110 = 0;
        var rejected125 = 0;
        var rejected150 = 0;
        for (var i = 0; i < candidateCount; i++)
        {
            var grc = grcCosts[i];
            if ((long)grc * 10 > (long)bestGrc * 11)
                rejected110++;
            if ((long)grc * 4 > (long)bestGrc * 5)
                rejected125++;
            if ((long)grc * 2 > (long)bestGrc * 3)
                rejected150++;
        }

        Interlocked.Increment(ref s_candidateSets);
        Interlocked.Add(ref s_candidates, candidateCount);
        if (ContainsTop(topGrcIndexes, winnerIndex, 1))
            Interlocked.Increment(ref s_grcTop1Winner);
        if (ContainsTop(topGrcIndexes, winnerIndex, 2))
            Interlocked.Increment(ref s_grcTop2Winner);
        if (ContainsTop(topGrcIndexes, winnerIndex, 4))
            Interlocked.Increment(ref s_grcTop4Winner);
        if (ContainsTop(topSadIndexes, winnerIndex, 1))
            Interlocked.Increment(ref s_sadTop1Winner);
        if (ContainsTop(topSadIndexes, winnerIndex, 2))
            Interlocked.Increment(ref s_sadTop2Winner);
        if (ContainsTop(topSadIndexes, winnerIndex, 4))
            Interlocked.Increment(ref s_sadTop4Winner);
        if ((long)winnerGrc * 10 <= (long)bestGrc * 11)
            Interlocked.Increment(ref s_margin110Winner);
        if ((long)winnerGrc * 4 <= (long)bestGrc * 5)
            Interlocked.Increment(ref s_margin125Winner);
        if ((long)winnerGrc * 2 <= (long)bestGrc * 3)
            Interlocked.Increment(ref s_margin150Winner);
        Interlocked.Add(ref s_margin110Rejected, rejected110);
        Interlocked.Add(ref s_margin125Rejected, rejected125);
        Interlocked.Add(ref s_margin150Rejected, rejected150);
    }

    public static string BuildCandidateRankingReport(bool reset = false)
    {
        var s = ReadCandidateRankings();
        if (s.CandidateSets <= 0)
            return "GRC shadow ranking: no candidate sets collected.";

        static string Pct(long value, long total) =>
            total <= 0 ? "0.0%" : ((double)value * 100.0 / total).ToString("F1") + "%";

        var sb = new StringBuilder(512);
        sb.Append("GRC shadow ranking: sets=").Append(s.CandidateSets)
            .Append(" candidates=").Append(s.Candidates).AppendLine();
        sb.Append("  GRC winner recall: top1=").Append(Pct(s.GrcTop1Winner, s.CandidateSets))
            .Append(" top2=").Append(Pct(s.GrcTop2Winner, s.CandidateSets))
            .Append(" top4=").Append(Pct(s.GrcTop4Winner, s.CandidateSets)).AppendLine();
        sb.Append("  SAD winner recall: top1=").Append(Pct(s.SadTop1Winner, s.CandidateSets))
            .Append(" top2=").Append(Pct(s.SadTop2Winner, s.CandidateSets))
            .Append(" top4=").Append(Pct(s.SadTop4Winner, s.CandidateSets)).AppendLine();
        sb.Append("  Margin winner recall: 1.10=").Append(Pct(s.Margin110Winner, s.CandidateSets))
            .Append(" 1.25=").Append(Pct(s.Margin125Winner, s.CandidateSets))
            .Append(" 1.50=").Append(Pct(s.Margin150Winner, s.CandidateSets)).AppendLine();
        sb.Append("  Candidate reject rate: 1.10=").Append(Pct(s.Margin110Rejected, s.Candidates))
            .Append(" 1.25=").Append(Pct(s.Margin125Rejected, s.Candidates))
            .Append(" 1.50=").Append(Pct(s.Margin150Rejected, s.Candidates));

        if (reset)
            ResetCandidateRankings();
        return sb.ToString();
    }

    private static bool ContainsTop(ReadOnlySpan<int> indexes, int winnerIndex, int count)
    {
        count = Math.Min(count, indexes.Length);
        for (var i = 0; i < count; i++)
        {
            if (indexes[i] == winnerIndex)
                return true;
        }

        return false;
    }
}
