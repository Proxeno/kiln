using System.Buffers;
using System.Runtime.CompilerServices;

namespace Kiln.Internal.H264;

/// <summary>
/// Inter motion estimation core: integer-pel search + qpel refinement + best-partition selection +
/// MV median prediction per ITU-T H.264 clause 8.4.1.3.1. Pure functions; driven by the slice encoder P-inter path.
/// </summary>
internal static class H264MotionEstimator
{
    /// <summary>
    /// Thread-local running count of motion-search candidate evaluations, in rough relative cost
    /// units (an integer-pel 16x16 score = 1, a unified sub-partition candidate = 8, a fractional
    /// qpel candidate = 2). Deterministic for a given input — it counts algorithmic work, not time —
    /// so the slice-partition balancer can feed the previous frame's per-row effort back into the
    /// row split without making bitstreams depend on wall-clock measurements. Monotonic; callers
    /// read deltas.
    /// </summary>
    [ThreadStatic]
    private static long t_searchEffort;

    /// <summary>Current thread's accumulated search effort (see <see cref="t_searchEffort"/>).</summary>
    internal static long ThreadSearchEffort => t_searchEffort;

    /// <summary>One signed motion vector in quarter-pel units.</summary>
    public readonly record struct Mv(short X, short Y);

    /// <summary>Result of a search over a single block.</summary>
    public readonly record struct SearchResult(Mv BestMv, int BestSad);
    /// <summary>Result of <see cref="SearchMbSubPartitions"/>: the best partition shape and its per-partition MVs/SADs.</summary>
    public readonly record struct PartitionResult(McPartition Partition, Mv Mv0, Mv Mv1, Mv Mv2, Mv Mv3, int TotalSad);

    /// <summary>Baseline inter partition (<c>mb_type</c>), H.264 7.4.5.</summary>
    public enum McPartition : byte { Mb16x16 = 0, Mb16x8 = 1, Mb8x16 = 2, Mb8x8 = 3 }

    /// <summary>
    /// 16×16 match cost above which the fast (hex) seed search is treated as having wholly failed to
    /// find the motion (a local-minimum lodge), triggering an exhaustive window fallback. The metric
    /// is the search's returned cost, which is SATD-domain when <c>useMotionSatd</c> (the default) and
    /// SAD-domain otherwise — so the two thresholds differ by roughly the SATD/SAD scale (~5×). They
    /// are set so well-predicted-but-detailed blocks (whose cost is high purely from texture, with the
    /// correct MV already found) stay below them, and only a wholly-mispredicted block — the abrupt /
    /// large-motion miss this fallback exists for — exceeds them. Calibrated empirically: at these
    /// values the fallback fires ~0% of the time on steady textured 640×480 content (no encode-speed
    /// cost) while still recovering an abrupt 24px/frame onset.
    /// </summary>
    private const int FastSearchFullFallbackSatd = 16384;
    private const int FastSearchFullFallbackSad = 4096;

    /// <summary>
    /// Search a 16×16 block in <paramref name="reference"/> for the best match to <paramref name="current"/>,
    /// starting from <paramref name="mvPredictor"/>. Search bounds are <paramref name="searchRange"/> integer pels
    /// in each direction. Returns the best MV in quarter-pel units (full-pel × 4) and its SAD.
    /// </summary>
    /// <param name="current">Current 16×16 block, length 256, row-major.</param>
    /// <param name="currentStride">Stride of <paramref name="current"/>.</param>
    /// <param name="reference">Reference picture luma plane (already deblock-filtered, with 16-pel halo of replicated border samples).</param>
    /// <param name="referenceStride">Stride of <paramref name="reference"/>.</param>
    /// <param name="mbX">Macroblock x in the picture (the block's pre-MV top-left x).</param>
    /// <param name="mbY">Macroblock y in the picture.</param>
    /// <param name="mvPredictor">Centre of the search window in quarter-pel units.</param>
    /// <param name="searchRange">Half-window in integer pels; total window is (2·searchRange + 1)² centred on the predictor.</param>
    /// <param name="pictureWidth">Unpadded luma width (≥16). When &gt; 0, MVs that would overread chroma during 4:2:0 inter reconstruction are rejected.</param>
    /// <param name="pictureHeight">Unpadded luma height.</param>
    public static SearchResult SearchMb16x16(
        ReadOnlySpan<byte> current, int currentStride,
        ReadOnlySpan<byte> reference, int referenceStride,
        int mbX, int mbY,
        Mv mvPredictor,
        int searchRange,
        bool useMotionSatd,
        IH264KernelSet kernels,
        int pictureWidth = 0,
        int pictureHeight = 0,
        int fractionalPelRefinementRounds = 2,
        int lambda = 0,
        H264ReferenceTransformAtlas? referenceTransformAtlas = null)
    {
        return SearchBlock(
            current, currentStride, reference, referenceStride, mbX, mbY, mvPredictor, searchRange,
            16, 16, MeBlockShape.B16x16,
            kernels,
            useMotionSatd,
            lambda,
            pictureWidth, pictureHeight, fractionalPelRefinementRounds,
            referenceTransformAtlas: referenceTransformAtlas);
    }

    /// <summary>
    /// Same as <see cref="SearchMb16x16"/> but evaluates 16×16, 16×8, 8×16, and 8×8 partition shapes and returns the
    /// best by total partition SAD. The returned per-partition MVs (Mv0..Mv3) are populated only for the partitions
    /// the chosen shape uses; unused slots are zero-initialised.
    /// </summary>
    public static PartitionResult SearchMbSubPartitions(
        ReadOnlySpan<byte> current, int currentStride,
        ReadOnlySpan<byte> reference, int referenceStride,
        int mbX, int mbY,
        Mv mvPredictor,
        int searchRange,
        bool useMotionSatd,
        IH264KernelSet kernels,
        Mv? temporalMv = null,
        bool fastSearch = true,
        int lambda = 0,
        int pictureWidth = 0,
        int pictureHeight = 0,
        int fastSeedSearchRange = 0,
        bool allowSubPartitionSearch = true,
        H264ReferenceTransformAtlas? referenceTransformAtlas = null,
        int subPartitionRangeCap = 8)
    {
        var best = new PartitionResult(McPartition.Mb16x16, default, default, default, default, int.MaxValue);

        void Try(McPartition part, int totalSad, Mv m0, Mv m1, Mv m2, Mv m3)
        {
            if (IsPartitionBetter(totalSad, part, best.TotalSad, best.Partition))
                best = new PartitionResult(part, m0, m1, m2, m3, totalSad);
        }

        if (temporalMv.HasValue)
        {
            var tmv = temporalMv.Value;
            if (tmv.X != 0 || tmv.Y != 0)
            {
                var picW = referenceStride;
                var picH = reference.Length / referenceStride;
                var rx = mbX + (tmv.X >> 2);
                var ry = mbY + (tmv.Y >> 2);
                if (Fits(rx, ry, 16, 16, picW, picH))
                {
                    var mux = mbX - H264InterReconstructor.DefaultRefHaloLuma;
                    var muy = mbY - H264InterReconstructor.DefaultRefHaloLuma;
                    var chromaOk = pictureWidth <= 0 || pictureHeight <= 0 ||
                                   H264InterReconstructor.IsMvSafeForInterBlockAtMb(
                                       pictureWidth, pictureHeight, mux, muy, 16, 16, tmv.X, tmv.Y);
                    if (chromaOk)
                    {
                        var refWin = OffsetWindow(reference, referenceStride, rx, ry);
                        var tSad = kernels.Sad16x16(current, currentStride, refWin, referenceStride);
                        Try(McPartition.Mb16x16, tSad, tmv, default, default, default);
                    }
                }
            }
        }

        // Seed widening is explicit policy from caller; default (0) keeps strict range semantics.
        var seedSearchRange = Math.Max(searchRange, fastSeedSearchRange);
        var r0 = fastSearch
            ? SearchBlockHex(
                current, currentStride, reference, referenceStride, mbX, mbY, mvPredictor,
                seedSearchRange,
                16, 16, MeBlockShape.B16x16,
                kernels,
                useMotionSatd,
                lambda,
                pictureWidth,
                pictureHeight,
                referenceTransformAtlas: referenceTransformAtlas)
            : SearchMb16x16(current, currentStride, reference, referenceStride, mbX, mbY, mvPredictor, searchRange,
                useMotionSatd, kernels,
                pictureWidth: pictureWidth, pictureHeight: pictureHeight, fractionalPelRefinementRounds: 2, lambda: lambda,
                referenceTransformAtlas: referenceTransformAtlas);

        // Hex (fast) search descends from a single seed and can lodge in a local minimum, missing
        // large/abrupt motion entirely — fatal for fast camera pans where no spatial or temporal
        // predictor points anywhere near the true MV. When the fast result is a clearly poor match,
        // fall back to the exhaustive window search (the same one used when FastSearch is off) and
        // keep whichever is better. This only fires on macroblocks the fast search failed on, so the
        // well-predicted common case keeps its speed.
        var fallbackThreshold = useMotionSatd ? FastSearchFullFallbackSatd : FastSearchFullFallbackSad;
        if (fastSearch && r0.BestSad > fallbackThreshold)
        {
            H264PInterDiagnostics.NotifyMeExhaustiveFallback();
            var full = SearchMb16x16(
                current, currentStride, reference, referenceStride, mbX, mbY, mvPredictor, seedSearchRange,
                useMotionSatd, kernels,
                pictureWidth: pictureWidth, pictureHeight: pictureHeight, fractionalPelRefinementRounds: 2, lambda: lambda,
                referenceTransformAtlas: referenceTransformAtlas);
            // Adopt the exhaustive result only on a SUBSTANTIAL win (< half the hex SAD). A big drop
            // means the hex lodged in a local minimum and the full search found the true (large,
            // ~uniform) motion — exactly the case we want, and its MV is also the right centre to seed
            // the sub-partition search from. A marginal drop means there simply is no good 16×16 match
            // (e.g. genuinely divergent per-quadrant motion); adopting it there would only shift the
            // sub-partition seed off the quadrants' centroid and hurt 8×8 selection.
            if (full.BestSad * 2 < r0.BestSad)
                r0 = full;
        }
        Try(McPartition.Mb16x16, r0.BestSad, r0.BestMv, default, default, default);
        var best16x16Cost = r0.BestSad;
        const int skipThreshold = 4 * 16 * 16;
        if (best16x16Cost <= skipThreshold || !allowSubPartitionSearch)
            return best;
        var seed16x16 = r0.BestMv;
        // Cap sub-partition range at subPartitionRangeCap (default 8) for speed; the 16x16 hex seed
        // already searched the full window, so ±8 captures the residual per-partition delta.
        var subSearchRange = Math.Min(subPartitionRangeCap, Math.Max(searchRange / 2, Math.Min(8, searchRange)));
        // NOTE: there is deliberately no per-(source,reference) SATD 4x4 "atom" cache and no MV-keyed
        // 8x8 SAD candidate cache here. Both were measured (KILN_H264_SATD_DAG_INSTRUMENT=1) at a 0.0%
        // hit rate on every workload, because neither can ever hit by construction: the Manhattan ring
        // in SearchMbSubPartitionsUnified visits each candidate MV exactly once, and an atom key
        // (sourceIndex, refX, refY) is unique per (source block, candidate) pair. The reuse that does
        // exist is purely reference-side — the same reference 4x4 is transformed for many candidates —
        // and that is already captured by the reference transform atlas / refTransformCache below.
        byte[]? rentedSatd4x4RefTransformValid = null;
        short[]? rentedSatd4x4RefTransformCacheValues = null;
        Span<short> satd4x4SourceCoefficients = stackalloc short[16 * H264MotionSatd.Transform4x4CoefficientCount];
        var satd4x4RefTransformCache = default(Satd4x4RefTransformGrid);
        if (useMotionSatd)
        {
            if (referenceTransformAtlas is null)
            {
                var refTransformOriginX = mbX + (seed16x16.X >> 2) - subSearchRange;
                var refTransformOriginY = mbY + (seed16x16.Y >> 2) - subSearchRange;
                var refTransformWidth = (subSearchRange << 1) + 13;
                var refTransformHeight = refTransformWidth;
                var refTransformSlotCount = checked(refTransformWidth * refTransformHeight);
                rentedSatd4x4RefTransformValid = ArrayPool<byte>.Shared.Rent(refTransformSlotCount);
                rentedSatd4x4RefTransformCacheValues = ArrayPool<short>.Shared.Rent(
                    refTransformSlotCount * H264MotionSatd.Transform4x4CoefficientCount);
                var refTransformValid = rentedSatd4x4RefTransformValid.AsSpan(0, refTransformSlotCount);
                refTransformValid.Clear();
                satd4x4RefTransformCache = new Satd4x4RefTransformGrid
                {
                    Valid = refTransformValid,
                    Coefficients = rentedSatd4x4RefTransformCacheValues.AsSpan(
                        0, refTransformSlotCount * H264MotionSatd.Transform4x4CoefficientCount),
                    OriginX = refTransformOriginX,
                    OriginY = refTransformOriginY,
                    Width = refTransformWidth,
                    Height = refTransformHeight,
                };
            }

            PrecomputeMb4x4Transforms(current, currentStride, satd4x4SourceCoefficients);
        }

        try
        {
            if (useMotionSatd)
            {
                SearchMbSubPartitionsUnified(
                    current, currentStride, reference, referenceStride,
                    mbX, mbY, seed16x16, subSearchRange,
                    kernels, lambda, pictureWidth, pictureHeight,
                    satd4x4SourceCoefficients,
                    satd4x4RefTransformCache, referenceTransformAtlas,
                    best.TotalSad,
                    out var uTop, out var uBot,
                    out var uLeft, out var uRight,
                    out var uQ00, out var uQ10, out var uQ01, out var uQ11);

                if (uTop.BestSad != int.MaxValue && uBot.BestSad != int.MaxValue)
                    Try(McPartition.Mb16x8, uTop.BestSad + uBot.BestSad, uTop.BestMv, uBot.BestMv, default, default);

                if (uLeft.BestSad != int.MaxValue && uRight.BestSad != int.MaxValue)
                    Try(McPartition.Mb8x16, uLeft.BestSad + uRight.BestSad, uLeft.BestMv, uRight.BestMv, default, default);

                if (uQ00.BestSad != int.MaxValue && uQ10.BestSad != int.MaxValue &&
                    uQ01.BestSad != int.MaxValue && uQ11.BestSad != int.MaxValue)
                    Try(McPartition.Mb8x8, uQ00.BestSad + uQ10.BestSad + uQ01.BestSad + uQ11.BestSad,
                        uQ00.BestMv, uQ10.BestMv, uQ01.BestMv, uQ11.BestMv);
            }
            else
            {
                var top = current[..(8 * currentStride)];
                var bot = current[(8 * currentStride)..];
                var t = SearchBlock(top, currentStride, reference, referenceStride, mbX, mbY, seed16x16, subSearchRange, 16, 8, MeBlockShape.B16x8, kernels, useMotionSatd, lambda, pictureWidth, pictureHeight,
                    fractionalPelRefinementRounds: 2, currentMb: current, currentMbStride: currentStride,
                    currentBlockX: 0, currentBlockY: 0,
                    sourceTransformCoefficients: satd4x4SourceCoefficients,
                    refTransformCache: satd4x4RefTransformCache,
                    referenceTransformAtlas: referenceTransformAtlas,
                    scoreCeilingExclusive: best.TotalSad);
                if (t.BestSad < best.TotalSad)
                {
                    var bCeiling = best.TotalSad - t.BestSad;
                    var b = SearchBlock(bot, currentStride, reference, referenceStride, mbX, mbY + 8, seed16x16, subSearchRange, 16, 8, MeBlockShape.B16x8, kernels, useMotionSatd, lambda, pictureWidth, pictureHeight,
                        fractionalPelRefinementRounds: 2, currentMb: current, currentMbStride: currentStride,
                        currentBlockX: 0, currentBlockY: 8,
                        sourceTransformCoefficients: satd4x4SourceCoefficients,
                        refTransformCache: satd4x4RefTransformCache,
                        referenceTransformAtlas: referenceTransformAtlas,
                        scoreCeilingExclusive: bCeiling);
                    if (b.BestSad < bCeiling)
                        Try(McPartition.Mb16x8, checked(t.BestSad + b.BestSad), t.BestMv, b.BestMv, default, default);
                }

                var l = SearchBlock(current, currentStride, reference, referenceStride, mbX, mbY, seed16x16, subSearchRange, 8, 16, MeBlockShape.B8x16, kernels, useMotionSatd, lambda, pictureWidth, pictureHeight,
                    fractionalPelRefinementRounds: 2, currentMb: current, currentMbStride: currentStride,
                    currentBlockX: 0, currentBlockY: 0,
                    sourceTransformCoefficients: satd4x4SourceCoefficients,
                    refTransformCache: satd4x4RefTransformCache,
                    referenceTransformAtlas: referenceTransformAtlas,
                    scoreCeilingExclusive: best.TotalSad);
                if (l.BestSad < best.TotalSad)
                {
                    var rCeiling = best.TotalSad - l.BestSad;
                    var r = SearchBlock(current.Slice(8), currentStride, reference, referenceStride, mbX + 8, mbY, seed16x16, subSearchRange, 8, 16, MeBlockShape.B8x16, kernels, useMotionSatd, lambda, pictureWidth, pictureHeight,
                        fractionalPelRefinementRounds: 2, currentMb: current, currentMbStride: currentStride,
                        currentBlockX: 8, currentBlockY: 0,
                        sourceTransformCoefficients: satd4x4SourceCoefficients,
                        refTransformCache: satd4x4RefTransformCache,
                        referenceTransformAtlas: referenceTransformAtlas,
                        scoreCeilingExclusive: rCeiling);
                    if (r.BestSad < rCeiling)
                        Try(McPartition.Mb8x16, checked(l.BestSad + r.BestSad), l.BestMv, r.BestMv, default, default);
                }

                var q00 = SearchBlock(current, currentStride, reference, referenceStride, mbX, mbY, seed16x16, subSearchRange, 8, 8, MeBlockShape.B8x8, kernels, useMotionSatd, lambda, pictureWidth, pictureHeight,
                    fractionalPelRefinementRounds: 2, currentMb: current, currentMbStride: currentStride,
                    currentBlockX: 0, currentBlockY: 0,
                    sourceTransformCoefficients: satd4x4SourceCoefficients,
                    refTransformCache: satd4x4RefTransformCache,
                    referenceTransformAtlas: referenceTransformAtlas,
                    scoreCeilingExclusive: best.TotalSad);
                if (q00.BestSad < best.TotalSad)
                {
                    var q10Ceiling = best.TotalSad - q00.BestSad;
                    var q10 = SearchBlock(current.Slice(8), currentStride, reference, referenceStride, mbX + 8, mbY, seed16x16, subSearchRange, 8, 8, MeBlockShape.B8x8, kernels, useMotionSatd, lambda, pictureWidth, pictureHeight,
                        fractionalPelRefinementRounds: 2, currentMb: current, currentMbStride: currentStride,
                        currentBlockX: 8, currentBlockY: 0,
                        sourceTransformCoefficients: satd4x4SourceCoefficients,
                        refTransformCache: satd4x4RefTransformCache,
                        referenceTransformAtlas: referenceTransformAtlas,
                        scoreCeilingExclusive: q10Ceiling);
                    if (q10.BestSad < q10Ceiling)
                    {
                        var q0 = checked(q00.BestSad + q10.BestSad);
                        if (q0 < best.TotalSad)
                        {
                            var q01Ceiling = best.TotalSad - q0;
                            var q01 = SearchBlock(current.Slice(8 * currentStride), currentStride, reference, referenceStride, mbX, mbY + 8, seed16x16, subSearchRange, 8, 8, MeBlockShape.B8x8, kernels, useMotionSatd, lambda, pictureWidth, pictureHeight,
                                fractionalPelRefinementRounds: 2, currentMb: current, currentMbStride: currentStride,
                                currentBlockX: 0, currentBlockY: 8,
                                sourceTransformCoefficients: satd4x4SourceCoefficients,
                                refTransformCache: satd4x4RefTransformCache,
                                referenceTransformAtlas: referenceTransformAtlas,
                                scoreCeilingExclusive: q01Ceiling);
                            if (q01.BestSad < q01Ceiling)
                            {
                                var q01Sum = checked(q0 + q01.BestSad);
                                if (q01Sum < best.TotalSad)
                                {
                                    var q11Ceiling = best.TotalSad - q01Sum;
                                    var q11 = SearchBlock(current.Slice(8 * currentStride + 8), currentStride, reference, referenceStride, mbX + 8, mbY + 8, seed16x16, subSearchRange, 8, 8, MeBlockShape.B8x8, kernels, useMotionSatd, lambda, pictureWidth, pictureHeight,
                                        fractionalPelRefinementRounds: 2, currentMb: current, currentMbStride: currentStride,
                                        currentBlockX: 8, currentBlockY: 8,
                                        sourceTransformCoefficients: satd4x4SourceCoefficients,
                                        refTransformCache: satd4x4RefTransformCache,
                                        referenceTransformAtlas: referenceTransformAtlas,
                                        scoreCeilingExclusive: q11Ceiling);
                                    if (q11.BestSad < q11Ceiling)
                                        Try(McPartition.Mb8x8, checked(q01Sum + q11.BestSad), q00.BestMv, q10.BestMv, q01.BestMv, q11.BestMv);
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            if (rentedSatd4x4RefTransformValid is not null)
                ArrayPool<byte>.Shared.Return(rentedSatd4x4RefTransformValid);
            if (rentedSatd4x4RefTransformCacheValues is not null)
                ArrayPool<short>.Shared.Return(rentedSatd4x4RefTransformCacheValues);
        }

        return best;
    }

    /// <summary>
    /// Median predictor per H.264 8.4.1.3.1: component-wise median of
    /// <paramref name="mvA"/> (block to the left), <paramref name="mvB"/> (above), and the effective
    /// C neighbour (<paramref name="mvC"/>, or <paramref name="mvD"/> when <paramref name="cAvail"/> is
    /// false and <paramref name="dAvail"/> is true). For partitions without a prediction, the spec
    /// uses refIdx −1 and MV (0,0); we represent that by substituting 0 per component when a neighbour
    /// is unavailable, then taking <c>Median3</c> over the three values — including the cnt=2 case
    /// (exactly two neighbours inter-coded), which must not collapse to copying one neighbour's MV.
    /// </summary>
    public static Mv PredictMv(
        Mv mvA, bool aAvail,
        Mv mvB, bool bAvail,
        Mv mvC, bool cAvail,
        Mv mvD, bool dAvail)
    {
        var cEff = mvC;
        var cEffAvail = cAvail;
        if (!cAvail && dAvail) { cEff = mvD; cEffAvail = true; }
        var na = aAvail;
        var nb = bAvail;
        var nc = cEffAvail;
        var cnt = (na ? 1 : 0) + (nb ? 1 : 0) + (nc ? 1 : 0);

        return cnt switch
        {
            0 => default,
            1 => na ? mvA : nb ? mvB : cEff,
            _ => new Mv(
                Median3(na ? mvA.X : (short)0, nb ? mvB.X : (short)0, nc ? cEff.X : (short)0),
                Median3(na ? mvA.Y : (short)0, nb ? mvB.Y : (short)0, nc ? cEff.Y : (short)0)),
        };
    }

    /// <summary>
    /// Luma motion vector predictor per H.264 §8.4.1.3 including the §8.4.1.3.2 directional rule:
    /// when exactly one of the three neighbours A/B/C has the same reference index as the current
    /// partition (<paramref name="currentRefIdx"/>), the predictor is that neighbour's MV — not the
    /// component-wise median. This matters once a P slice mixes reference indices (e.g. row-0 MBs
    /// referencing the older DPB slot while row-1 MBs reference the newer one): the availability-only
    /// <see cref="PredictMv"/> would Median3 across mismatched-ref neighbours and diverge from the
    /// decoder. A neighbour's refIdx of <c>-1</c> denotes "not available / not inter".
    /// </summary>
    /// <summary>
    /// Median MV predictor (H.264 §8.4.1.3). This overload derives neighbour "absence" from
    /// <c>refIdx == -1</c>, which conflates a truly-absent neighbour (decoder
    /// <c>PART_NOT_AVAILABLE</c>) with an intra-coded one (decoder <c>LIST_NOT_USED</c>) — both
    /// arrive as refIdx -1. That is correct only for callers that never present an intra neighbour
    /// distinctly. Any P-slice path whose neighbour may be an intra-in-P macroblock must use the
    /// <c>…, bool aAbsent, …</c> overload, so the C←D substitution (§8.4.1.3.1) and the B&amp;C
    /// inheritance rule fire only for genuinely absent neighbours (an intra neighbour is present and
    /// contributes MV (0,0) with refIdx -1, matching the decoder).
    /// </summary>
    public static Mv PredictMvWithRefIdx(
        Mv mvA, int refIdxA,
        Mv mvB, int refIdxB,
        Mv mvC, int refIdxC,
        Mv mvD, int refIdxD,
        int currentRefIdx) =>
        PredictMvWithRefIdx(
            mvA, refIdxA, mvB, refIdxB, mvC, refIdxC, mvD, refIdxD, currentRefIdx,
            aAbsent: refIdxA == -1, bAbsent: refIdxB == -1, cAbsent: refIdxC == -1, dAbsent: refIdxD == -1);

    /// <summary>
    /// Median MV predictor (H.264 §8.4.1.3) with explicit positional absence. A neighbour is
    /// <em>absent</em> only when its macroblock lies outside the picture or current slice (decoder
    /// <c>PART_NOT_AVAILABLE</c>); an intra-coded neighbour is <em>present</em> and contributes MV
    /// (0,0) with refIdx -1 (decoder <c>LIST_NOT_USED</c>). Only absent neighbours drive the C←D
    /// substitution and the "B and C both absent → inherit A" rule — an intra neighbour does neither.
    /// Callers pass MV (0,0) / refIdx -1 for both intra and absent neighbours; the absence flags
    /// disambiguate. The median itself takes a neighbour's MV only when it is inter (refIdx ≥ 0);
    /// intra/absent neighbours contribute (0,0).
    /// </summary>
    public static Mv PredictMvWithRefIdx(
        Mv mvA, int refIdxA,
        Mv mvB, int refIdxB,
        Mv mvC, int refIdxC,
        Mv mvD, int refIdxD,
        int currentRefIdx,
        bool aAbsent, bool bAbsent, bool cAbsent, bool dAbsent)
    {
        // §8.4.1.3.1: substitute D for C only when the above-right neighbour is genuinely absent.
        if (cAbsent) { mvC = mvD; refIdxC = refIdxD; cAbsent = dAbsent; }

        // §8.4.1.3.1: when B and C are both absent but A is present, B and C inherit A.
        if (bAbsent && cAbsent && !aAbsent)
        {
            mvB = mvA; refIdxB = refIdxA;
            mvC = mvA; refIdxC = refIdxA;
        }

        // Inter neighbours contribute their MV to the median; intra/absent (refIdx -1) contribute (0,0).
        var vA = refIdxA >= 0 ? mvA : default;
        var vB = refIdxB >= 0 ? mvB : default;
        var vC = refIdxC >= 0 ? mvC : default;

        // §8.4.1.3.2: single matching reference index short-circuits the median. A real currentRefIdx
        // (≥ 0) never matches an intra/absent neighbour's refIdx -1.
        var matchA = refIdxA == currentRefIdx;
        var matchB = refIdxB == currentRefIdx;
        var matchC = refIdxC == currentRefIdx;
        if ((matchA ? 1 : 0) + (matchB ? 1 : 0) + (matchC ? 1 : 0) == 1)
            return matchA ? vA : matchB ? vB : vC;

        return new Mv(
            Median3(vA.X, vB.X, vC.X),
            Median3(vA.Y, vB.Y, vC.Y));
    }

    private static readonly int[] MvBitCostTable = CreateMvBitCostTable();

    private static int[] CreateMvBitCostTable()
    {
        var t = new int[128];
        for (var i = 0; i < 128; i++)
            t[i] = (int)Math.Round(Math.Log2(i + 1) * 256);
        return t;
    }

    private static int MvBitCost(int delta) => MvBitCostTable[Math.Min(Math.Abs(delta), 127)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MotionVectorCost(int lambda, int dx, int dy) =>
        lambda == 0 ? 0 : (lambda * (MvBitCost(dx) + MvBitCost(dy))) >> 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MotionVectorCostFromSearchOffset(
        int lambda, int dx, int dy, int predIx, int predIy, short mvpX, short mvpY)
    {
        var mvX = (short)(((predIx + dx) * 4));
        var mvY = (short)(((predIy + dy) * 4));
        return MotionVectorCost(lambda, mvX - mvpX, mvY - mvpY);
    }

    private enum MeBlockShape : byte
    {
        B16x16,
        B16x8,
        B8x16,
        B8x8
    }

    private ref struct Satd4x4RefTransformGrid
    {
        public Span<byte> Valid;
        public Span<short> Coefficients;
        public int OriginX;
        public int OriginY;
        public int Width;
        public int Height;

        public readonly bool IsEmpty => Valid.IsEmpty || Coefficients.IsEmpty || Width <= 0 || Height <= 0;
    }

    private readonly ref struct IntegerCandidateEvaluation
    {
        public readonly bool Valid;
        public readonly int ReferenceX;
        public readonly int ReferenceY;
        public readonly Mv Mv;
        public readonly int MvCost;
        public readonly int SatdStopAfter;
        public readonly bool StopOnEqual;

        public IntegerCandidateEvaluation(
            bool valid,
            int referenceX,
            int referenceY,
            Mv mv,
            int mvCost,
            int satdStopAfter,
            bool stopOnEqual)
        {
            Valid = valid;
            ReferenceX = referenceX;
            ReferenceY = referenceY;
            Mv = mv;
            MvCost = mvCost;
            SatdStopAfter = satdStopAfter;
            StopOnEqual = stopOnEqual;
        }
    }

    private static IntegerCandidateEvaluation EvaluateIntegerCandidate(
        int mbX,
        int mbY,
        int predIx,
        int predIy,
        int dx,
        int dy,
        int bw,
        int bh,
        int picW,
        int picH,
        int lambda,
        int searchRange,
        ReadOnlySpan<int> dxMvBitCosts,
        ReadOnlySpan<int> dyMvBitCosts,
        bool checkChroma,
        int pictureWidth,
        int pictureHeight,
        int mbUnpaddedX,
        int mbUnpaddedY,
        int bestScore,
        int bestSad,
        Mv bestMv,
        int scoreCeilingExclusive)
    {
        var rx = mbX + predIx + dx;
        var ry = mbY + predIy + dy;
        if (!Fits(rx, ry, bw, bh, picW, picH))
            return default;

        var mvInt = ToMv(dx, dy, predIx, predIy, 0, 0);
        if (checkChroma &&
            !H264InterReconstructor.IsMvSafeForInterBlockAtMb(
                pictureWidth, pictureHeight, mbUnpaddedX, mbUnpaddedY,
                bw, bh,
                mvInt.X, mvInt.Y))
            return default;

        var mvCost = lambda == 0
            ? 0
            : (lambda * (dxMvBitCosts[dx + searchRange] + dyMvBitCosts[dy + searchRange])) >> 8;
        if (mvCost > bestScore)
            return default;
        if (mvCost >= scoreCeilingExclusive)
            return default;

        var bestStopAfter = bestScore == int.MaxValue ? int.MaxValue : bestScore - mvCost;
        var ceilingStopAfter = scoreCeilingExclusive == int.MaxValue ? int.MaxValue : scoreCeilingExclusive - mvCost;
        var satdStopAfter = Math.Min(bestStopAfter, ceilingStopAfter);
        var stopOnEqual =
            satdStopAfter == ceilingStopAfter ||
            (bestScore != int.MaxValue &&
             !IsBetterScoreCandidate(bestScore, satdStopAfter, mvInt, bestScore, bestSad, bestMv));

        return new IntegerCandidateEvaluation(
            valid: true,
            referenceX: rx,
            referenceY: ry,
            mv: mvInt,
            mvCost: mvCost,
            satdStopAfter: satdStopAfter,
            stopOnEqual: stopOnEqual);
    }

    // One Manhattan-ordered loop over all sub-partition shapes for a single MB.
    // Computes all 16 SATD 4x4 atoms per candidate once and derives all 8 shape costs
    // from subset sums, eliminating 6x redundant bounds/chroma/MV-cost overhead.
    //
    // Atom layout (sourceIndex = (sourceY/4)*4 + sourceX/4, row-major):
    //   TL quadrant: atoms  0, 1, 4, 5   (sourceX=0,4  sourceY=0,4)
    //   TR quadrant: atoms  2, 3, 6, 7   (sourceX=8,12 sourceY=0,4)
    //   BL quadrant: atoms  8, 9,12,13   (sourceX=0,4  sourceY=8,12)
    //   BR quadrant: atoms 10,11,14,15   (sourceX=8,12 sourceY=8,12)
    //
    //   16x8 top = TL+TR, 16x8 bot = BL+BR, 8x16 left = TL+BL, 8x16 right = TR+BR
    private static void SearchMbSubPartitionsUnified(
        ReadOnlySpan<byte> currentMb,
        int currentMbStride,
        ReadOnlySpan<byte> reference,
        int referenceStride,
        int mbX, int mbY,
        Mv seed,
        int subSearchRange,
        IH264KernelSet kernels,
        int lambda,
        int pictureWidth, int pictureHeight,
        Span<short> sourceTransformCoefficients,
        Satd4x4RefTransformGrid refTransformCache,
        H264ReferenceTransformAtlas? referenceTransformAtlas,
        int globalCeiling,
        out SearchResult top, out SearchResult bot,
        out SearchResult left, out SearchResult right,
        out SearchResult q00, out SearchResult q10,
        out SearchResult q01, out SearchResult q11)
    {
        var picW = referenceStride;
        var picH = reference.Length / referenceStride;
        var predIx = seed.X >> 2;
        var predIy = seed.Y >> 2;
        var checkChroma = pictureWidth > 0 && pictureHeight > 0;
        const int halo = H264InterReconstructor.DefaultRefHaloLuma;

        var rangeLen = subSearchRange * 2 + 1;
        Span<int> dxBits = lambda != 0 ? stackalloc int[rangeLen] : default;
        Span<int> dyBits = lambda != 0 ? stackalloc int[rangeLen] : default;
        if (lambda != 0)
        {
            for (var i = 0; i < rangeLen; i++)
                dxBits[i] = dyBits[i] = MvBitCost(i - subSearchRange);
        }

        Span<int> atoms = stackalloc int[16]; // reused per candidate; moved outside loop (CA2014)

        var topBestSad = int.MaxValue; var topBestScore = int.MaxValue; var topBestMv = default(Mv);
        var botBestSad = int.MaxValue; var botBestScore = int.MaxValue; var botBestMv = default(Mv);
        var leftBestSad = int.MaxValue; var leftBestScore = int.MaxValue; var leftBestMv = default(Mv);
        var rightBestSad = int.MaxValue; var rightBestScore = int.MaxValue; var rightBestMv = default(Mv);
        var q00BestSad = int.MaxValue; var q00BestScore = int.MaxValue; var q00BestMv = default(Mv);
        var q10BestSad = int.MaxValue; var q10BestScore = int.MaxValue; var q10BestMv = default(Mv);
        var q01BestSad = int.MaxValue; var q01BestScore = int.MaxValue; var q01BestMv = default(Mv);
        var q11BestSad = int.MaxValue; var q11BestScore = int.MaxValue; var q11BestMv = default(Mv);

        // Per-shape convergence flags: set once minMvCost exceeds a shape's own best score.
        // Shapes that are geometrically impossible (bestScore stays int.MaxValue after candidateDistance=0)
        // are pre-marked done after that first ring pass so they don't block global ring-break.
        var topDone = false; var botDone = false; var leftDone = false; var rightDone = false;
        var q00Done = false; var q10Done = false; var q01Done = false; var q11Done = false;

        var collectDiag = H264MotionSatdDagDiagnostics.IsEnabled;
        var maxManhattanDistance = subSearchRange << 1;
        var lastCandidateDistance = 0;

        for (var candidateDistance = 0; candidateDistance <= maxManhattanDistance; candidateDistance++)
        {
            if (lambda != 0)
            {
                var minMvCost = MinimumMvCostForManhattanDistance(lambda, candidateDistance, subSearchRange);
                if (minMvCost >= globalCeiling)
                {
                    H264MotionSatdDagDiagnostics.NotifyCandidateRingBreak();
                    break;
                }
                // After distance=0 any shape still at MaxValue is geometrically impossible; mark done
                // so it doesn't block convergence. For subsequent rings, mark done when minMvCost
                // exceeds the shape's own best — equivalent to per-shape ring-break in SearchBlock.
                if (candidateDistance == 1)
                {
                    if (topBestScore   == int.MaxValue) topDone   = true;
                    if (botBestScore   == int.MaxValue) botDone   = true;
                    if (leftBestScore  == int.MaxValue) leftDone  = true;
                    if (rightBestScore == int.MaxValue) rightDone = true;
                    if (q00BestScore   == int.MaxValue) q00Done   = true;
                    if (q10BestScore   == int.MaxValue) q10Done   = true;
                    if (q01BestScore   == int.MaxValue) q01Done   = true;
                    if (q11BestScore   == int.MaxValue) q11Done   = true;
                }
                topDone   |= !topDone   && topBestScore   != int.MaxValue && minMvCost > topBestScore;
                botDone   |= !botDone   && botBestScore   != int.MaxValue && minMvCost > botBestScore;
                leftDone  |= !leftDone  && leftBestScore  != int.MaxValue && minMvCost > leftBestScore;
                rightDone |= !rightDone && rightBestScore != int.MaxValue && minMvCost > rightBestScore;
                q00Done   |= !q00Done   && q00BestScore   != int.MaxValue && minMvCost > q00BestScore;
                q10Done   |= !q10Done   && q10BestScore   != int.MaxValue && minMvCost > q10BestScore;
                q01Done   |= !q01Done   && q01BestScore   != int.MaxValue && minMvCost > q01BestScore;
                q11Done   |= !q11Done   && q11BestScore   != int.MaxValue && minMvCost > q11BestScore;
                if (topDone && botDone && leftDone && rightDone && q00Done && q10Done && q01Done && q11Done)
                {
                    H264MotionSatdDagDiagnostics.NotifyCandidateRingBreak();
                    break;
                }
            }

            lastCandidateDistance = candidateDistance;
            var dxStart = Math.Max(-subSearchRange, -candidateDistance);
            var dxEnd = Math.Min(subSearchRange, candidateDistance);
            for (var dx = dxStart; dx <= dxEnd; dx++)
            {
                var dyAbs = candidateDistance - Math.Abs(dx);
                if (dyAbs > subSearchRange)
                    continue;
                for (var dySign = -1; dySign <= 1; dySign += 2)
                {
                    if (dyAbs == 0 && dySign > 0)
                        break;
                    var dy = dyAbs * dySign;

                    var rx = mbX + predIx + dx;
                    var ry = mbY + predIy + dy;

                    // Bounds: derive quadrant validity from 4 flags
                    var xFit8  = rx >= 0 && rx + 8 <= picW;
                    if (!xFit8) continue;
                    var yFit8  = ry >= 0 && ry + 8 <= picH;
                    if (!yFit8) continue;
                    var xFit16 = rx + 16 <= picW;
                    var yFit16 = ry + 16 <= picH;

                    // trFits = xFit16 (yFit8 already checked); shape guards:
                    //   8x8 TR, 16x8 top           → trFits
                    //   8x8 BL, 8x16 left          → blFits
                    //   8x8 BR, 16x8 bot, 8x16 right → brFits
                    var trFits = xFit16;
                    var blFits = yFit16;
                    var brFits = xFit16 && yFit16;

                    var mv = ToMv(dx, dy, predIx, predIy, 0, 0);
                    var mvCost = lambda == 0 ? 0
                        : (lambda * (dxBits[dx + subSearchRange] + dyBits[dy + subSearchRange])) >> 8;

                    // Per-quadrant 8x8 chroma checks — 8x16 right uses mbX+8 as its block origin
                    // so a single 16x16 check is too conservative and would over-reject.
                    var chromaTL = !checkChroma || H264InterReconstructor.IsMvSafeForInterBlockAtMb(
                        pictureWidth, pictureHeight, mbX - halo, mbY - halo, 8, 8, mv.X, mv.Y);
                    var chromaTR = !trFits || !checkChroma || H264InterReconstructor.IsMvSafeForInterBlockAtMb(
                        pictureWidth, pictureHeight, mbX + 8 - halo, mbY - halo, 8, 8, mv.X, mv.Y);
                    var chromaBL = !blFits || !checkChroma || H264InterReconstructor.IsMvSafeForInterBlockAtMb(
                        pictureWidth, pictureHeight, mbX - halo, mbY + 8 - halo, 8, 8, mv.X, mv.Y);
                    var chromaBR = !brFits || !checkChroma || H264InterReconstructor.IsMvSafeForInterBlockAtMb(
                        pictureWidth, pictureHeight, mbX + 8 - halo, mbY + 8 - halo, 8, 8, mv.X, mv.Y);
                    if (!chromaTL && !chromaTR && !chromaBL && !chromaBR)
                        continue;

                    // MV-cost pre-filter: skip if mvCost already exceeds every non-converged shape's
                    // best. Converged shapes (shapeXDone) no longer contribute to the ceiling.
                    // Using min(all bests) including already-converged shapes would incorrectly prune
                    // candidates that could still improve a shape with a high current best (e.g. 8x16
                    // right at MV=(4,0) when TL has a near-zero score from a static background).
                    var minActiveBest = globalCeiling;
                    if (!topDone   && topBestScore   < minActiveBest) minActiveBest = topBestScore;
                    if (!botDone   && botBestScore   < minActiveBest) minActiveBest = botBestScore;
                    if (!leftDone  && leftBestScore  < minActiveBest) minActiveBest = leftBestScore;
                    if (!rightDone && rightBestScore < minActiveBest) minActiveBest = rightBestScore;
                    if (!q00Done   && q00BestScore   < minActiveBest) minActiveBest = q00BestScore;
                    if (!q10Done   && q10BestScore   < minActiveBest) minActiveBest = q10BestScore;
                    if (!q01Done   && q01BestScore   < minActiveBest) minActiveBest = q01BestScore;
                    if (!q11Done   && q11BestScore   < minActiveBest) minActiveBest = q11BestScore;
                    if (mvCost >= minActiveBest)
                        continue;

                    // Per-shape SAD lower-bound pre-filter.
                    // We cannot use a single full-MB sum: a MV that's bad for the left half but great
                    // for the right half would have high sadTL+sadBL yet a valid 8x16-right candidate.
                    // So we compute each quadrant's SAD atom once and derive per-shape lower bounds,
                    // skipping SATD only when NO shape can possibly improve on its current best.
                    {
                        var sTl = Sad8x8Atom(kernels, currentMb, currentMbStride, reference, referenceStride, 0, rx, ry);
                        var lb00 = ((sTl + 1) >> 1) + mvCost;
                        if (lb00 >= q00BestScore)
                        {
                            // TL alone can't improve q00; check if any other shape might still benefit.
                            var canImprove = false;
                            if (trFits)
                            {
                                var sTr = Sad8x8Atom(kernels, currentMb, currentMbStride, reference, referenceStride, 1, rx, ry);
                                canImprove = ((sTr + 1) >> 1) + mvCost < q10BestScore ||
                                             ((sTl + sTr + 1) >> 1) + mvCost < topBestScore;
                                if (!canImprove && blFits) // brFits ⊆ trFits && blFits
                                {
                                    var sBl = Sad8x8Atom(kernels, currentMb, currentMbStride, reference, referenceStride, 2, rx, ry);
                                    var sBr = Sad8x8Atom(kernels, currentMb, currentMbStride, reference, referenceStride, 3, rx, ry);
                                    canImprove =
                                        ((sBl + 1) >> 1) + mvCost < q01BestScore ||
                                        ((sBr + 1) >> 1) + mvCost < q11BestScore ||
                                        ((sBl + sBr + 1) >> 1) + mvCost < botBestScore ||
                                        ((sTl + sBl + 1) >> 1) + mvCost < leftBestScore ||
                                        ((sTr + sBr + 1) >> 1) + mvCost < rightBestScore;
                                }
                            }
                            else if (blFits)
                            {
                                var sBl = Sad8x8Atom(kernels, currentMb, currentMbStride, reference, referenceStride, 2, rx, ry);
                                canImprove = ((sBl + 1) >> 1) + mvCost < q01BestScore ||
                                             ((sTl + sBl + 1) >> 1) + mvCost < leftBestScore;
                            }
                            if (!canImprove)
                                continue;
                        }
                    }

                    // Compute SATD 4x4 atoms; skip columns/rows that fall outside the picture
                    var bxLim = xFit16 ? 4 : 2;
                    var byLim = yFit16 ? 4 : 2;
                    for (var by = 0; by < byLim; by++)
                    {
                        var sy = by * 4;
                        var ry4 = ry + sy;
                        for (var bx = 0; bx < bxLim; bx++)
                        {
                            var sx = bx * 4;
                            atoms[by * 4 + bx] = Satd4x4Direct(
                                currentMb, currentMbStride, sx, sy,
                                reference, referenceStride, rx + sx, ry4,
                                sourceTransformCoefficients,
                                refTransformCache, referenceTransformAtlas,
                                collectDiag, 0 /* B16x16 atom shape */);
                        }
                    }

                    t_searchEffort += byLim * bxLim;

                    // Quadrant sums → shape scores
                    var sumTl = atoms[0] + atoms[1] + atoms[4] + atoms[5];
                    var sumTr = trFits ? atoms[2] + atoms[3] + atoms[6] + atoms[7] : int.MaxValue;
                    var sumBl = blFits ? atoms[8] + atoms[9] + atoms[12] + atoms[13] : int.MaxValue;
                    var sumBr = brFits ? atoms[10] + atoms[11] + atoms[14] + atoms[15] : int.MaxValue;

                    // 8x8 quadrants
                    if (!q00Done && chromaTL && IsBetterScoreCandidate(sumTl + mvCost, sumTl, mv, q00BestScore, q00BestSad, q00BestMv))
                    { q00BestSad = sumTl; q00BestScore = sumTl + mvCost; q00BestMv = mv; }

                    if (!q10Done && trFits && chromaTR && IsBetterScoreCandidate(sumTr + mvCost, sumTr, mv, q10BestScore, q10BestSad, q10BestMv))
                    { q10BestSad = sumTr; q10BestScore = sumTr + mvCost; q10BestMv = mv; }

                    if (!q01Done && blFits && chromaBL && IsBetterScoreCandidate(sumBl + mvCost, sumBl, mv, q01BestScore, q01BestSad, q01BestMv))
                    { q01BestSad = sumBl; q01BestScore = sumBl + mvCost; q01BestMv = mv; }

                    if (!q11Done && brFits && chromaBR && IsBetterScoreCandidate(sumBr + mvCost, sumBr, mv, q11BestScore, q11BestSad, q11BestMv))
                    { q11BestSad = sumBr; q11BestScore = sumBr + mvCost; q11BestMv = mv; }

                    // 16x8 shapes
                    if (!topDone && trFits && chromaTL && chromaTR)
                    {
                        var sadTop = sumTl + sumTr;
                        if (IsBetterScoreCandidate(sadTop + mvCost, sadTop, mv, topBestScore, topBestSad, topBestMv))
                        { topBestSad = sadTop; topBestScore = sadTop + mvCost; topBestMv = mv; }
                    }
                    if (!botDone && brFits && chromaBL && chromaBR)
                    {
                        var sadBot = sumBl + sumBr;
                        if (IsBetterScoreCandidate(sadBot + mvCost, sadBot, mv, botBestScore, botBestSad, botBestMv))
                        { botBestSad = sadBot; botBestScore = sadBot + mvCost; botBestMv = mv; }
                    }

                    // 8x16 shapes
                    if (!leftDone && blFits && chromaTL && chromaBL)
                    {
                        var sadLeft = sumTl + sumBl;
                        if (IsBetterScoreCandidate(sadLeft + mvCost, sadLeft, mv, leftBestScore, leftBestSad, leftBestMv))
                        { leftBestSad = sadLeft; leftBestScore = sadLeft + mvCost; leftBestMv = mv; }
                    }
                    if (!rightDone && brFits && chromaTR && chromaBR)
                    {
                        var sadRight = sumTr + sumBr;
                        if (IsBetterScoreCandidate(sadRight + mvCost, sadRight, mv, rightBestScore, rightBestSad, rightBestMv))
                        { rightBestSad = sadRight; rightBestScore = sadRight + mvCost; rightBestMv = mv; }
                    }
                }
            }
        }

        H264MotionSatdDagDiagnostics.NotifyUnifiedSubPartitionDepth(lastCandidateDistance);

        // Fractional-pel refinement per shape
        top = topBestSad != int.MaxValue
            ? RefineFrac(currentMb[..(8 * currentMbStride)], currentMbStride,
                reference, referenceStride, mbX, mbY, 16, 8,
                kernels, MeBlockShape.B16x8, topBestMv, topBestSad, picW, picH,
                checkChroma, pictureWidth, pictureHeight, mbX - halo, mbY - halo, 2, true, lambda)
            : new SearchResult(default, int.MaxValue);

        bot = botBestSad != int.MaxValue
            ? RefineFrac(currentMb[(8 * currentMbStride)..], currentMbStride,
                reference, referenceStride, mbX, mbY + 8, 16, 8,
                kernels, MeBlockShape.B16x8, botBestMv, botBestSad, picW, picH,
                checkChroma, pictureWidth, pictureHeight, mbX - halo, mbY + 8 - halo, 2, true, lambda)
            : new SearchResult(default, int.MaxValue);

        left = leftBestSad != int.MaxValue
            ? RefineFrac(currentMb, currentMbStride,
                reference, referenceStride, mbX, mbY, 8, 16,
                kernels, MeBlockShape.B8x16, leftBestMv, leftBestSad, picW, picH,
                checkChroma, pictureWidth, pictureHeight, mbX - halo, mbY - halo, 2, true, lambda)
            : new SearchResult(default, int.MaxValue);

        right = rightBestSad != int.MaxValue
            ? RefineFrac(currentMb[8..], currentMbStride,
                reference, referenceStride, mbX + 8, mbY, 8, 16,
                kernels, MeBlockShape.B8x16, rightBestMv, rightBestSad, picW, picH,
                checkChroma, pictureWidth, pictureHeight, mbX + 8 - halo, mbY - halo, 2, true, lambda)
            : new SearchResult(default, int.MaxValue);

        q00 = q00BestSad != int.MaxValue
            ? RefineFrac(currentMb, currentMbStride,
                reference, referenceStride, mbX, mbY, 8, 8,
                kernels, MeBlockShape.B8x8, q00BestMv, q00BestSad, picW, picH,
                checkChroma, pictureWidth, pictureHeight, mbX - halo, mbY - halo, 2, true, lambda)
            : new SearchResult(default, int.MaxValue);

        q10 = q10BestSad != int.MaxValue
            ? RefineFrac(currentMb[8..], currentMbStride,
                reference, referenceStride, mbX + 8, mbY, 8, 8,
                kernels, MeBlockShape.B8x8, q10BestMv, q10BestSad, picW, picH,
                checkChroma, pictureWidth, pictureHeight, mbX + 8 - halo, mbY - halo, 2, true, lambda)
            : new SearchResult(default, int.MaxValue);

        q01 = q01BestSad != int.MaxValue
            ? RefineFrac(currentMb[(8 * currentMbStride)..], currentMbStride,
                reference, referenceStride, mbX, mbY + 8, 8, 8,
                kernels, MeBlockShape.B8x8, q01BestMv, q01BestSad, picW, picH,
                checkChroma, pictureWidth, pictureHeight, mbX - halo, mbY + 8 - halo, 2, true, lambda)
            : new SearchResult(default, int.MaxValue);

        q11 = q11BestSad != int.MaxValue
            ? RefineFrac(currentMb[(8 * currentMbStride + 8)..], currentMbStride,
                reference, referenceStride, mbX + 8, mbY + 8, 8, 8,
                kernels, MeBlockShape.B8x8, q11BestMv, q11BestSad, picW, picH,
                checkChroma, pictureWidth, pictureHeight, mbX + 8 - halo, mbY + 8 - halo, 2, true, lambda)
            : new SearchResult(default, int.MaxValue);
    }

    private static SearchResult SearchBlock(
        ReadOnlySpan<byte> current, int currentStride,
        ReadOnlySpan<byte> reference, int referenceStride,
        int mbX, int mbY,
        Mv mvPredictor,
        int searchRange,
        int bw, int bh,
        MeBlockShape blockShape,
        IH264KernelSet kernels,
        bool useMotionSatd,
        int lambda = 0,
        int pictureWidth = 0,
        int pictureHeight = 0,
        int fractionalPelRefinementRounds = 2,
        H264ReferenceTransformAtlas? referenceTransformAtlas = null)
    {
        var noRefTransformCache = default(Satd4x4RefTransformGrid);
        return SearchBlock(
            current, currentStride, reference, referenceStride, mbX, mbY, mvPredictor, searchRange,
            bw, bh, blockShape, kernels, useMotionSatd, lambda, pictureWidth, pictureHeight,
            fractionalPelRefinementRounds, current, currentStride, 0, 0,
            sourceTransformCoefficients: default, refTransformCache: noRefTransformCache,
            referenceTransformAtlas: referenceTransformAtlas);
    }

    private static SearchResult SearchBlock(
        ReadOnlySpan<byte> current, int currentStride,
        ReadOnlySpan<byte> reference, int referenceStride,
        int mbX, int mbY,
        Mv mvPredictor,
        int searchRange,
        int bw, int bh,
        MeBlockShape blockShape,
        IH264KernelSet kernels,
        bool useMotionSatd,
        int lambda,
        int pictureWidth,
        int pictureHeight,
        int fractionalPelRefinementRounds,
        ReadOnlySpan<byte> currentMb,
        int currentMbStride,
        int currentBlockX,
        int currentBlockY,
        Span<short> sourceTransformCoefficients = default,
        Satd4x4RefTransformGrid refTransformCache = default,
        H264ReferenceTransformAtlas? referenceTransformAtlas = null,
        int scoreCeilingExclusive = int.MaxValue)
    {
        var picW = referenceStride;
        var picH = reference.Length / referenceStride;
        var predIx = mvPredictor.X >> 2;
        var predIy = mvPredictor.Y >> 2;

        var checkChroma = pictureWidth > 0 && pictureHeight > 0;
        var mbUnpaddedX = checkChroma ? mbX - H264InterReconstructor.DefaultRefHaloLuma : 0;
        var mbUnpaddedY = checkChroma ? mbY - H264InterReconstructor.DefaultRefHaloLuma : 0;

        var bestSad = int.MaxValue;
        var bestScore = int.MaxValue;
        var bestMv = default(Mv);
        var rangeLen = checked(searchRange * 2 + 1);
        Span<int> dxMvBitCosts = lambda != 0 ? stackalloc int[rangeLen] : default;
        Span<int> dyMvBitCosts = lambda != 0 ? stackalloc int[rangeLen] : default;
        if (lambda != 0)
        {
            for (var i = 0; i < rangeLen; i++)
            {
                var delta = i - searchRange;
                dxMvBitCosts[i] = MvBitCost(delta);
                dyMvBitCosts[i] = MvBitCost(delta);
            }
        }

        var collectGraphResidual = H264MotionGraphResidualDiagnostics.ShouldCollectCandidateRankings(useMotionSatd);
        var candidateCapacity = checked(rangeLen * rangeLen);
        Span<int> graphResidualCosts = collectGraphResidual ? stackalloc int[candidateCapacity] : default;
        Span<int> topGraphResidualCosts = collectGraphResidual ? stackalloc int[4] : default;
        Span<int> topGraphResidualIndexes = collectGraphResidual ? stackalloc int[4] : default;
        Span<int> topSadCosts = collectGraphResidual ? stackalloc int[4] : default;
        Span<int> topSadIndexes = collectGraphResidual ? stackalloc int[4] : default;
        var graphResidualCandidateCount = 0;
        var graphResidualWinnerIndex = -1;
        var graphResidualWinnerCost = 0;
        if (collectGraphResidual)
        {
            topGraphResidualCosts.Fill(int.MaxValue);
            topGraphResidualIndexes.Fill(-1);
            topSadCosts.Fill(int.MaxValue);
            topSadIndexes.Fill(-1);
        }

        var maxManhattanDistance = searchRange << 1;
        for (var candidateDistance = 0; candidateDistance <= maxManhattanDistance; candidateDistance++)
        {
            if (lambda != 0)
            {
                var minRemainingMvCost = MinimumMvCostForManhattanDistance(lambda, candidateDistance, searchRange);
                if ((bestScore != int.MaxValue && minRemainingMvCost > bestScore) ||
                    minRemainingMvCost >= scoreCeilingExclusive)
                {
                    H264MotionSatdDagDiagnostics.NotifyCandidateRingBreak();
                    break;
                }
            }

            var dxStart = Math.Max(-searchRange, -candidateDistance);
            var dxEnd = Math.Min(searchRange, candidateDistance);
            for (var dx = dxStart; dx <= dxEnd; dx++)
            {
                var dyAbs = candidateDistance - Math.Abs(dx);
                if (dyAbs > searchRange)
                    continue;

                for (var dySign = -1; dySign <= 1; dySign += 2)
                {
                    if (dyAbs == 0 && dySign > 0)
                        break;

                    var candidate = EvaluateIntegerCandidate(
                        mbX, mbY, predIx, predIy,
                        dx, dyAbs * dySign,
                        bw, bh, picW, picH,
                        lambda, searchRange, dxMvBitCosts, dyMvBitCosts,
                        checkChroma, pictureWidth, pictureHeight, mbUnpaddedX, mbUnpaddedY,
                        bestScore, bestSad, bestMv, scoreCeilingExclusive);
                    if (!candidate.Valid)
                        continue;

                    var graphResidualCandidateIndex = -1;
                    var graphResidualCost = 0;
                    ReadOnlySpan<byte> refWin = default;
                    if (collectGraphResidual)
                    {
                        refWin = OffsetWindow(reference, referenceStride, candidate.ReferenceX, candidate.ReferenceY);
                        graphResidualCost = H264MotionGraphResidual.ComputeCost(
                            current, currentStride, refWin, referenceStride, bw, bh,
                            out var graphResidualSad, out _, out _, out _);
                        graphResidualCandidateIndex = graphResidualCandidateCount;
                        graphResidualCosts[graphResidualCandidateCount] = graphResidualCost;
                        InsertTop4(graphResidualCost, graphResidualCandidateIndex, topGraphResidualCosts, topGraphResidualIndexes);
                        InsertTop4(graphResidualSad, graphResidualCandidateIndex, topSadCosts, topSadIndexes);
                        graphResidualCandidateCount++;
                    }

                    if (useMotionSatd && candidate.SatdStopAfter != int.MaxValue)
                    {
                        if (refWin.IsEmpty)
                            refWin = OffsetWindow(reference, referenceStride, candidate.ReferenceX, candidate.ReferenceY);
                        var sadLowerBoundSource = SadBlock(
                            kernels, blockShape, current, currentStride, refWin, referenceStride);
                        if (SatdSadLowerBoundRejects(sadLowerBoundSource, candidate.SatdStopAfter, candidate.StopOnEqual, (int)blockShape))
                            continue;
                    }

                    int s;
                    if (useMotionSatd &&
                        CanUseTransformDomainSatd4x4(sourceTransformCoefficients, refTransformCache, referenceTransformAtlas))
                    {
                        s = SatdBlockTransformDomain(blockShape, currentMb, currentMbStride, reference, referenceStride,
                            currentBlockX, currentBlockY, candidate.ReferenceX, candidate.ReferenceY,
                            sourceTransformCoefficients, refTransformCache, referenceTransformAtlas,
                            candidate.SatdStopAfter, candidate.StopOnEqual);
                    }
                    else
                    {
                        if (refWin.IsEmpty)
                            refWin = OffsetWindow(reference, referenceStride, candidate.ReferenceX, candidate.ReferenceY);
                        s = ScoreBlock(kernels, blockShape, useMotionSatd, current, currentStride, refWin, referenceStride);
                    }
                    var candScore = s + candidate.MvCost;
                    if (candScore >= scoreCeilingExclusive)
                        continue;
                    if (IsBetterScoreCandidate(candScore, s, candidate.Mv, bestScore, bestSad, bestMv))
                    {
                        bestScore = candScore;
                        bestSad = s;
                        bestMv = candidate.Mv;
                        if (collectGraphResidual)
                        {
                            graphResidualWinnerIndex = graphResidualCandidateIndex;
                            graphResidualWinnerCost = graphResidualCost;
                        }
                    }
                }
            }
        }

        if (bestSad == int.MaxValue)
            return new SearchResult(default, int.MaxValue);

        if (collectGraphResidual && graphResidualCandidateCount > 0)
        {
            H264MotionGraphResidualDiagnostics.NotifyCandidateSet(
                graphResidualCandidateCount,
                graphResidualWinnerIndex,
                graphResidualWinnerCost,
                graphResidualCosts[..graphResidualCandidateCount],
                topGraphResidualIndexes,
                topSadIndexes);
        }

        return RefineFrac(
            current, currentStride, reference, referenceStride, mbX, mbY, bw, bh,
            kernels,
            blockShape, bestMv, bestSad, picW, picH,
            checkChroma, pictureWidth, pictureHeight, mbUnpaddedX, mbUnpaddedY,
            fractionalPelRefinementRounds,
            useMotionSatd, lambda);
    }

    private static SearchResult SearchBlockHex(
        ReadOnlySpan<byte> current, int currentStride,
        ReadOnlySpan<byte> reference, int referenceStride,
        int mbX, int mbY,
        Mv mvPredictor,
        int maxRange,
        int bw, int bh,
        MeBlockShape blockShape,
        IH264KernelSet kernels,
        bool useMotionSatd,
        int lambda = 0,
        int pictureWidth = 0,
        int pictureHeight = 0,
        int fractionalPelRefinementRounds = 2,
        H264ReferenceTransformAtlas? referenceTransformAtlas = null)
    {
        H264PInterDiagnostics.NotifyMeHexSearch();
        var picW = referenceStride;
        var picH = reference.Length / referenceStride;
        var predIx = mvPredictor.X >> 2;
        var predIy = mvPredictor.Y >> 2;
        var checkChroma = pictureWidth > 0 && pictureHeight > 0;
        var mbUnpaddedX = checkChroma ? mbX - H264InterReconstructor.DefaultRefHaloLuma : 0;
        var mbUnpaddedY = checkChroma ? mbY - H264InterReconstructor.DefaultRefHaloLuma : 0;

        ReadOnlySpan<int> hexOx = [-4, 4, 0, 0, -2, 2, -2, 2];
        ReadOnlySpan<int> hexOy = [0, 0, -4, 4, -3, -3, 3, 3];
        ReadOnlySpan<int> diaOx = [-1, 1, 0, 0];
        ReadOnlySpan<int> diaOy = [0, 0, -1, 1];
        Span<int> cacheKey = stackalloc int[256];
        Span<int> cacheSad = stackalloc int[256];
        Span<int> cacheScore = stackalloc int[256];
        Span<Mv> cacheMv = stackalloc Mv[256];
        Span<short> sourceTransformCoefficients =
            useMotionSatd && referenceTransformAtlas is not null
                ? stackalloc short[16 * H264MotionSatd.Transform4x4CoefficientCount]
                : default;
        if (!sourceTransformCoefficients.IsEmpty)
            PrecomputeMb4x4Transforms(current, currentStride, sourceTransformCoefficients);

        var curDx = 0;
        var curDy = 0;
        ScoreInterIntegerPelCached(
            current, currentStride, reference, referenceStride,
            mbX, mbY, predIx, predIy, mvPredictor.X, mvPredictor.Y, bw, bh, picW, picH, maxRange,
            curDx, curDy, lambda, kernels, blockShape, useMotionSatd,
            checkChroma, pictureWidth, pictureHeight, mbUnpaddedX, mbUnpaddedY,
            scoreStopAfter: int.MaxValue,
            sourceTransformCoefficients: sourceTransformCoefficients,
            referenceTransformAtlas: referenceTransformAtlas,
            cacheKey: cacheKey,
            cacheSad: cacheSad,
            cacheScore: cacheScore,
            cacheMv: cacheMv,
            s: out var curSad,
            score: out var curScore,
            mv: out var curMv);

        if (curSad == int.MaxValue)
        {
            return RefineFrac(
                current, currentStride, reference, referenceStride, mbX, mbY, bw, bh,
                kernels,
                blockShape, default, int.MaxValue, picW, picH,
                checkChroma, pictureWidth, pictureHeight, mbUnpaddedX, mbUnpaddedY,
                fractionalPelRefinementRounds,
                useMotionSatd, lambda);
        }

        while (true)
        {
            var bestSad = curSad;
            var bestScore = curScore;
            var bestDx = curDx;
            var bestDy = curDy;
            var bestMvLocal = curMv;
            for (var i = 0; i < 8; i++)
            {
                var ndx = curDx + hexOx[i];
                var ndy = curDy + hexOy[i];
                if (MotionVectorCostFromSearchOffset(lambda, ndx, ndy, predIx, predIy, mvPredictor.X, mvPredictor.Y) > bestScore)
                    continue;
                ScoreInterIntegerPelCached(
                    current, currentStride, reference, referenceStride,
                    mbX, mbY, predIx, predIy, mvPredictor.X, mvPredictor.Y, bw, bh, picW, picH, maxRange,
                    ndx, ndy, lambda, kernels, blockShape, useMotionSatd,
                    checkChroma, pictureWidth, pictureHeight, mbUnpaddedX, mbUnpaddedY,
                    scoreStopAfter: bestScore,
                    sourceTransformCoefficients: sourceTransformCoefficients,
                    referenceTransformAtlas: referenceTransformAtlas,
                    cacheKey: cacheKey,
                    cacheSad: cacheSad,
                    cacheScore: cacheScore,
                    cacheMv: cacheMv,
                    s: out var s,
                    score: out var sc,
                    mv: out var mv);
                if (s == int.MaxValue)
                    continue;
                if (IsBetterScoreCandidate(sc, s, mv, bestScore, bestSad, bestMvLocal))
                {
                    bestSad = s;
                    bestScore = sc;
                    bestDx = ndx;
                    bestDy = ndy;
                    bestMvLocal = mv;
                }
            }
            if (bestDx == curDx && bestDy == curDy)
                break;
            curDx = bestDx;
            curDy = bestDy;
            curSad = bestSad;
            curScore = bestScore;
            curMv = bestMvLocal;
        }

        for (var it = 0; it < 3; it++)
        {
            var bestSad = curSad;
            var bestScore = curScore;
            var bestDx = curDx;
            var bestDy = curDy;
            var bestMvLocal = curMv;
            for (var i = 0; i < 4; i++)
            {
                var ndx = curDx + diaOx[i];
                var ndy = curDy + diaOy[i];
                if (MotionVectorCostFromSearchOffset(lambda, ndx, ndy, predIx, predIy, mvPredictor.X, mvPredictor.Y) > bestScore)
                    continue;
                ScoreInterIntegerPelCached(
                    current, currentStride, reference, referenceStride,
                    mbX, mbY, predIx, predIy, mvPredictor.X, mvPredictor.Y, bw, bh, picW, picH, maxRange,
                    ndx, ndy, lambda, kernels, blockShape, useMotionSatd,
                    checkChroma, pictureWidth, pictureHeight, mbUnpaddedX, mbUnpaddedY,
                    scoreStopAfter: bestScore,
                    sourceTransformCoefficients: sourceTransformCoefficients,
                    referenceTransformAtlas: referenceTransformAtlas,
                    cacheKey: cacheKey,
                    cacheSad: cacheSad,
                    cacheScore: cacheScore,
                    cacheMv: cacheMv,
                    s: out var s,
                    score: out var sc,
                    mv: out var mv);
                if (s == int.MaxValue)
                    continue;
                if (IsBetterScoreCandidate(sc, s, mv, bestScore, bestSad, bestMvLocal))
                {
                    bestSad = s;
                    bestScore = sc;
                    bestDx = ndx;
                    bestDy = ndy;
                    bestMvLocal = mv;
                }
            }
            if (bestDx == curDx && bestDy == curDy)
                break;
            curDx = bestDx;
            curDy = bestDy;
            curSad = bestSad;
            curScore = bestScore;
            curMv = bestMvLocal;
        }

        return RefineFrac(
            current, currentStride, reference, referenceStride, mbX, mbY, bw, bh,
            kernels,
            blockShape, curMv, curSad, picW, picH,
            checkChroma, pictureWidth, pictureHeight, mbUnpaddedX, mbUnpaddedY,
            fractionalPelRefinementRounds,
            useMotionSatd, lambda);
    }

    private static void ScoreInterIntegerPel(
        ReadOnlySpan<byte> current, int currentStride,
        ReadOnlySpan<byte> reference, int referenceStride,
        int mbX, int mbY,
        int predIx, int predIy,
        short mvpX, short mvpY,
        int bw, int bh,
        int picW, int picH,
        int maxRange,
        int dx, int dy,
        int lambda,
        IH264KernelSet kernels,
        MeBlockShape blockShape,
        bool useMotionSatd,
        bool checkChroma,
        int pictureWidth,
        int pictureHeight,
        int mbUnpaddedX,
        int mbUnpaddedY,
        int scoreStopAfter,
        ReadOnlySpan<short> sourceTransformCoefficients,
        H264ReferenceTransformAtlas? referenceTransformAtlas,
        out int s,
        out int score,
        out Mv mv)
    {
        mv = ToMv(dx, dy, predIx, predIy, 0, 0);
        if (Math.Abs(dx) > maxRange || Math.Abs(dy) > maxRange)
        {
            s = int.MaxValue;
            score = int.MaxValue;
            return;
        }
        var rx = mbX + predIx + dx;
        var ry = mbY + predIy + dy;
        if (!Fits(rx, ry, bw, bh, picW, picH))
        {
            s = int.MaxValue;
            score = int.MaxValue;
            return;
        }
        if (checkChroma &&
            !H264InterReconstructor.IsMvSafeForInterBlockAtMb(
                pictureWidth, pictureHeight, mbUnpaddedX, mbUnpaddedY,
                bw, bh,
                mv.X, mv.Y))
        {
            s = int.MaxValue;
            score = int.MaxValue;
            return;
        }
        var mvCost = MotionVectorCost(lambda, mv.X - mvpX, mv.Y - mvpY);
        if (useMotionSatd && !sourceTransformCoefficients.IsEmpty && referenceTransformAtlas is not null)
        {
            var satdStopAfter = scoreStopAfter == int.MaxValue ? int.MaxValue : scoreStopAfter - mvCost;
            if (satdStopAfter < 0)
            {
                s = int.MaxValue;
                score = int.MaxValue;
                return;
            }

            if (satdStopAfter != int.MaxValue)
            {
                var refWin = OffsetWindow(reference, referenceStride, rx, ry);
                var sadLowerBoundSource = SadBlock(kernels, blockShape, current, currentStride, refWin, referenceStride);
                if (SatdSadLowerBoundRejects(sadLowerBoundSource, satdStopAfter, stopOnEqual: false, (int)blockShape))
                {
                    s = int.MaxValue;
                    score = int.MaxValue;
                    return;
                }
            }

            s = SatdBlockFromReferenceAtlas(
                blockShape,
                sourceTransformCoefficients,
                reference,
                referenceStride,
                rx,
                ry,
                referenceTransformAtlas,
                satdStopAfter);
        }
        else
        {
            var refWin = OffsetWindow(reference, referenceStride, rx, ry);
            if (useMotionSatd && scoreStopAfter != int.MaxValue)
            {
                var satdStopAfter = scoreStopAfter - mvCost;
                if (satdStopAfter < 0)
                {
                    s = int.MaxValue;
                    score = int.MaxValue;
                    return;
                }

                var sadLowerBoundSource = SadBlock(kernels, blockShape, current, currentStride, refWin, referenceStride);
                if (SatdSadLowerBoundRejects(sadLowerBoundSource, satdStopAfter, stopOnEqual: false, (int)blockShape))
                {
                    s = int.MaxValue;
                    score = int.MaxValue;
                    return;
                }

                s = SatdBlockBounded(blockShape, current, currentStride, refWin, referenceStride, satdStopAfter);
            }
            else
            {
                s = ScoreBlock(kernels, blockShape, useMotionSatd, current, currentStride, refWin, referenceStride);
            }
        }
        score = s + mvCost;
    }

    private static void ScoreInterIntegerPelCached(
        ReadOnlySpan<byte> current, int currentStride,
        ReadOnlySpan<byte> reference, int referenceStride,
        int mbX, int mbY,
        int predIx, int predIy,
        short mvpX, short mvpY,
        int bw, int bh,
        int picW, int picH,
        int maxRange,
        int dx, int dy,
        int lambda,
        IH264KernelSet kernels,
        MeBlockShape blockShape,
        bool useMotionSatd,
        bool checkChroma,
        int pictureWidth,
        int pictureHeight,
        int mbUnpaddedX,
        int mbUnpaddedY,
        int scoreStopAfter,
        ReadOnlySpan<short> sourceTransformCoefficients,
        H264ReferenceTransformAtlas? referenceTransformAtlas,
        Span<int> cacheKey,
        Span<int> cacheSad,
        Span<int> cacheScore,
        Span<Mv> cacheMv,
        out int s,
        out int score,
        out Mv mv)
    {
        var collectDagDiagnostics = H264MotionSatdDagDiagnostics.IsEnabled;
        var key = ((dx + 1024) << 16) | (dy + 1024);
        var mask = cacheKey.Length - 1;
        var slot = (int)(((uint)key * 2654435761u) & (uint)mask);
        for (var probe = 0; probe < cacheKey.Length; probe++)
        {
            var cachedKey = cacheKey[slot];
            if (cachedKey == key)
            {
                if (collectDagDiagnostics)
                    H264MotionSatdDagDiagnostics.NotifyCandidateCacheHit();
                s = cacheSad[slot];
                score = cacheScore[slot];
                mv = cacheMv[slot];
                return;
            }

            if (cachedKey == 0)
            {
                if (collectDagDiagnostics)
                    H264MotionSatdDagDiagnostics.NotifyCandidateCacheMiss();
                ScoreInterIntegerPel(
                    current, currentStride, reference, referenceStride,
                    mbX, mbY, predIx, predIy, mvpX, mvpY, bw, bh, picW, picH, maxRange,
                    dx, dy, lambda, kernels, blockShape, useMotionSatd,
                    checkChroma, pictureWidth, pictureHeight, mbUnpaddedX, mbUnpaddedY,
                    scoreStopAfter,
                    sourceTransformCoefficients, referenceTransformAtlas,
                    out s, out score, out mv);

                if (score <= scoreStopAfter || s == int.MaxValue || scoreStopAfter == int.MaxValue)
                {
                    cacheKey[slot] = key;
                    cacheSad[slot] = s;
                    cacheScore[slot] = score;
                    cacheMv[slot] = mv;
                }
                return;
            }

            slot = (slot + 1) & mask;
        }

        if (collectDagDiagnostics)
            H264MotionSatdDagDiagnostics.NotifyCandidateCacheMiss();
        ScoreInterIntegerPel(
            current, currentStride, reference, referenceStride,
            mbX, mbY, predIx, predIy, mvpX, mvpY, bw, bh, picW, picH, maxRange,
            dx, dy, lambda, kernels, blockShape, useMotionSatd,
            checkChroma, pictureWidth, pictureHeight, mbUnpaddedX, mbUnpaddedY,
            scoreStopAfter,
            sourceTransformCoefficients, referenceTransformAtlas,
            out s, out score, out mv);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScoreBlock(
        IH264KernelSet kernels,
        MeBlockShape blockShape,
        bool useMotionSatd,
        ReadOnlySpan<byte> current,
        int currentStride,
        ReadOnlySpan<byte> reference,
        int referenceStride)
    {
        // Effort in 4x4-SATD-atom equivalents: a WxH SATD is ~area/16 atoms, SAD ~half that.
        var area = blockShape switch
        {
            MeBlockShape.B16x16 => 256,
            MeBlockShape.B16x8 or MeBlockShape.B8x16 => 128,
            _ => 64,
        };
        t_searchEffort += useMotionSatd ? area >> 4 : area >> 5;
        return useMotionSatd
            ? SatdBlock(kernels, blockShape, current, currentStride, reference, referenceStride)
            : SadBlock(kernels, blockShape, current, currentStride, reference, referenceStride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SadBlock(
        IH264KernelSet kernels,
        MeBlockShape blockShape,
        ReadOnlySpan<byte> current,
        int currentStride,
        ReadOnlySpan<byte> reference,
        int referenceStride) =>
        blockShape switch
        {
            MeBlockShape.B16x16 => kernels.Sad16x16(current, currentStride, reference, referenceStride),
            MeBlockShape.B16x8 => kernels.Sad16x8(current, currentStride, reference, referenceStride),
            MeBlockShape.B8x16 => kernels.Sad8x16(current, currentStride, reference, referenceStride),
            _ => kernels.Sad8x8(current, currentStride, reference, referenceStride),
        };

    /// <summary>
    /// SAD of one 8x8 quadrant of the current MB against the reference at (referenceBlockX, referenceBlockY).
    /// <paramref name="atomIndex"/> is the quadrant in raster order (0=TL, 1=TR, 2=BL, 3=BR).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sad8x8Atom(
        IH264KernelSet kernels,
        ReadOnlySpan<byte> currentMb,
        int currentMbStride,
        ReadOnlySpan<byte> reference,
        int referenceStride,
        int atomIndex,
        int referenceBlockX,
        int referenceBlockY)
    {
        t_searchEffort++;
        var sourceX = (atomIndex & 1) << 3;
        var sourceY = (atomIndex >> 1) << 3;
        return kernels.Sad8x8(
            currentMb[(sourceY * currentMbStride + sourceX)..],
            currentMbStride,
            OffsetWindow(reference, referenceStride, referenceBlockX + sourceX, referenceBlockY + sourceY),
            referenceStride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SatdBlock(
        IH264KernelSet kernels,
        MeBlockShape blockShape,
        ReadOnlySpan<byte> current,
        int currentStride,
        ReadOnlySpan<byte> reference,
        int referenceStride) =>
        blockShape switch
        {
            MeBlockShape.B16x16 => kernels.Satd16x16(current, currentStride, reference, referenceStride),
            MeBlockShape.B16x8 => kernels.Satd16x8(current, currentStride, reference, referenceStride),
            MeBlockShape.B8x16 => kernels.Satd8x16(current, currentStride, reference, referenceStride),
            _ => kernels.Satd8x8(current, currentStride, reference, referenceStride),
        };

    /// <summary>
    /// Transform-domain SATD for a whole partition: sums the 4x4 atoms built from the precomputed source
    /// coefficients and the (cached) reference coefficients, with early exit once <paramref name="stopAfter"/>
    /// is exceeded. Requires <see cref="CanUseTransformDomainSatd4x4"/>.
    /// </summary>
    private static int SatdBlockTransformDomain(
        MeBlockShape blockShape,
        ReadOnlySpan<byte> currentMb,
        int currentMbStride,
        ReadOnlySpan<byte> reference,
        int referenceStride,
        int currentBlockX,
        int currentBlockY,
        int referenceBlockX,
        int referenceBlockY,
        ReadOnlySpan<short> sourceTransformCoefficients,
        Satd4x4RefTransformGrid refTransformCache,
        H264ReferenceTransformAtlas? referenceTransformAtlas,
        int stopAfter = int.MaxValue,
        bool stopOnEqual = false)
    {
        var collectDagDiagnostics = H264MotionSatdDagDiagnostics.IsEnabled;
        var shapeIndex = (int)blockShape;
        if (collectDagDiagnostics)
            H264MotionSatdDagDiagnostics.NotifyPartitionComposition(shapeIndex);

        var blocksX = blockShape is MeBlockShape.B16x16 or MeBlockShape.B16x8 ? 4 : 2;
        var blocksY = blockShape is MeBlockShape.B16x16 or MeBlockShape.B8x16 ? 4 : 2;
        var sum = 0;
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                sum += Satd4x4Direct(
                    currentMb, currentMbStride, currentBlockX + bx * 4, currentBlockY + by * 4,
                    reference, referenceStride, referenceBlockX + bx * 4, referenceBlockY + by * 4,
                    sourceTransformCoefficients, refTransformCache, referenceTransformAtlas,
                    collectDagDiagnostics, shapeIndex);
                if (sum > stopAfter || (stopOnEqual && sum == stopAfter))
                {
                    if (collectDagDiagnostics)
                        H264MotionSatdDagDiagnostics.NotifyPartitionEarlyExit(shapeIndex);
                    return sum;
                }
            }
        }

        return sum;
    }

    /// <summary>
    /// One SATD 4x4 atom. Computed in the transform domain (source coefficients precomputed once per MB,
    /// reference coefficients served by the atlas / per-MB reference transform cache) when available, else
    /// straight from pixels. There is intentionally no per-atom result cache: an atom key is
    /// (sourceIndex, refX, refY), which is unique for every (source block, candidate MV) pair a single MB
    /// search visits, so such a cache can never hit — see the note in <see cref="SearchMbSubPartitions"/>.
    /// </summary>
    private static int Satd4x4Direct(
        ReadOnlySpan<byte> currentMb,
        int currentMbStride,
        int sourceX,
        int sourceY,
        ReadOnlySpan<byte> reference,
        int referenceStride,
        int refX,
        int refY,
        ReadOnlySpan<short> sourceTransformCoefficients,
        Satd4x4RefTransformGrid refTransformCache,
        H264ReferenceTransformAtlas? referenceTransformAtlas,
        bool collectDagDiagnostics,
        int shapeIndex)
    {
        if (collectDagDiagnostics)
            H264MotionSatdDagDiagnostics.NotifyAtomCacheDisabledCompute(shapeIndex);

        if (!CanUseTransformDomainSatd4x4(sourceTransformCoefficients, refTransformCache, referenceTransformAtlas))
        {
            return H264MotionSatd.Satd4x4Strided(
                currentMb, currentMbStride, sourceX, sourceY,
                reference, referenceStride, refX, refY);
        }

        var sourceIndex = ((sourceY >> 2) << 2) + (sourceX >> 2);
        return Satd4x4FromTransformCache(
            sourceTransformCoefficients, sourceIndex,
            reference, referenceStride, refX, refY,
            refTransformCache, referenceTransformAtlas,
            collectDagDiagnostics);
    }

    private static void PrecomputeMb4x4Transforms(
        ReadOnlySpan<byte> currentMb,
        int currentMbStride,
        Span<short> sourceTransformCoefficients)
    {
        var sourceIndex = 0;
        for (var by = 0; by < 4; by++)
        {
            for (var bx = 0; bx < 4; bx++)
            {
                H264MotionSatd.Transform4x4Strided(
                    currentMb, currentMbStride, bx * 4, by * 4,
                    sourceTransformCoefficients.Slice(
                        sourceIndex * H264MotionSatd.Transform4x4CoefficientCount,
                        H264MotionSatd.Transform4x4CoefficientCount));
                sourceIndex++;
            }
        }

        H264MotionSatdDagDiagnostics.NotifySourceTransformComputes(16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanUseTransformDomainSatd4x4(
        ReadOnlySpan<short> sourceTransformCoefficients,
        Satd4x4RefTransformGrid refTransformCache,
        H264ReferenceTransformAtlas? referenceTransformAtlas) =>
        !sourceTransformCoefficients.IsEmpty &&
        (referenceTransformAtlas is not null || !refTransformCache.IsEmpty);

    private static int SatdBlockFromReferenceAtlas(
        MeBlockShape blockShape,
        ReadOnlySpan<short> sourceTransformCoefficients,
        ReadOnlySpan<byte> reference,
        int referenceStride,
        int referenceBlockX,
        int referenceBlockY,
        H264ReferenceTransformAtlas referenceTransformAtlas,
        int stopAfter = int.MaxValue)
    {
        var collectDagDiagnostics = H264MotionSatdDagDiagnostics.IsEnabled;
        var shapeIndex = (int)blockShape;
        if (collectDagDiagnostics)
            H264MotionSatdDagDiagnostics.NotifyPartitionComposition(shapeIndex);

        var blocksX = blockShape is MeBlockShape.B16x16 or MeBlockShape.B16x8 ? 4 : 2;
        var blocksY = blockShape is MeBlockShape.B16x16 or MeBlockShape.B8x16 ? 4 : 2;
        var sum = 0;
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                var sourceIndex = (by << 2) + bx;
                var refX = referenceBlockX + bx * 4;
                var refY = referenceBlockY + by * 4;
                sum += Satd4x4FromReferenceAtlas(
                    sourceTransformCoefficients, sourceIndex,
                    reference, referenceStride, refX, refY,
                    referenceTransformAtlas,
                    collectDagDiagnostics, shapeIndex);
                if (sum > stopAfter)
                {
                    if (collectDagDiagnostics)
                        H264MotionSatdDagDiagnostics.NotifyPartitionEarlyExit(shapeIndex);
                    return sum;
                }
            }
        }

        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Satd4x4FromReferenceAtlas(
        ReadOnlySpan<short> sourceTransformCoefficients,
        int sourceIndex,
        ReadOnlySpan<byte> reference,
        int referenceStride,
        int refX,
        int refY,
        H264ReferenceTransformAtlas referenceTransformAtlas,
        bool collectDagDiagnostics,
        int shapeIndex)
    {
        if (collectDagDiagnostics)
            H264MotionSatdDagDiagnostics.NotifyAtomCacheDisabledCompute(shapeIndex);

        var sourceCoefficients = sourceTransformCoefficients.Slice(
            sourceIndex * H264MotionSatd.Transform4x4CoefficientCount,
            H264MotionSatd.Transform4x4CoefficientCount);
        var refCoefficients = referenceTransformAtlas.GetOrCompute(
            reference, referenceStride, refX, refY, collectDagDiagnostics);
        return H264MotionSatd.Satd4x4FromTransformed(sourceCoefficients, refCoefficients);
    }

    private static int Satd4x4FromTransformCache(
        ReadOnlySpan<short> sourceTransformCoefficients,
        int sourceIndex,
        ReadOnlySpan<byte> reference,
        int referenceStride,
        int refX,
        int refY,
        Satd4x4RefTransformGrid refTransformCache,
        H264ReferenceTransformAtlas? referenceTransformAtlas,
        bool collectDagDiagnostics)
    {
        var sourceCoefficients = sourceTransformCoefficients.Slice(
            sourceIndex * H264MotionSatd.Transform4x4CoefficientCount,
            H264MotionSatd.Transform4x4CoefficientCount);
        if (referenceTransformAtlas is not null && referenceTransformAtlas.Contains4x4(refX, refY))
        {
            var atlasRefCoefficients = referenceTransformAtlas.GetOrCompute(
                reference, referenceStride, refX, refY, collectDagDiagnostics);
            return H264MotionSatd.Satd4x4FromTransformed(sourceCoefficients, atlasRefCoefficients);
        }

        if (refTransformCache.IsEmpty)
        {
            Span<short> uncachedRefCoefficients = stackalloc short[H264MotionSatd.Transform4x4CoefficientCount];
            if (collectDagDiagnostics)
                H264MotionSatdDagDiagnostics.NotifyRefTransformCacheMissCompute();
            H264MotionSatd.Transform4x4Strided(reference, referenceStride, refX, refY, uncachedRefCoefficients);
            return H264MotionSatd.Satd4x4FromTransformed(sourceCoefficients, uncachedRefCoefficients);
        }

        var gridX = refX - refTransformCache.OriginX;
        var gridY = refY - refTransformCache.OriginY;
        if ((uint)gridX >= (uint)refTransformCache.Width || (uint)gridY >= (uint)refTransformCache.Height)
        {
            Span<short> uncachedRefCoefficients = stackalloc short[H264MotionSatd.Transform4x4CoefficientCount];
            if (collectDagDiagnostics)
                H264MotionSatdDagDiagnostics.NotifyRefTransformCacheMissCompute();
            H264MotionSatd.Transform4x4Strided(reference, referenceStride, refX, refY, uncachedRefCoefficients);
            return H264MotionSatd.Satd4x4FromTransformed(sourceCoefficients, uncachedRefCoefficients);
        }

        var slot = gridY * refTransformCache.Width + gridX;
        var coefficientOffset = slot * H264MotionSatd.Transform4x4CoefficientCount;
        var refCoefficients = refTransformCache.Coefficients.Slice(
            coefficientOffset,
            H264MotionSatd.Transform4x4CoefficientCount);

        if (refTransformCache.Valid[slot] != 0)
        {
            if (collectDagDiagnostics)
                H264MotionSatdDagDiagnostics.NotifyRefTransformCacheHit();
        }
        else
        {
            if (collectDagDiagnostics)
                H264MotionSatdDagDiagnostics.NotifyRefTransformCacheMissCompute();
            H264MotionSatd.Transform4x4Strided(reference, referenceStride, refX, refY, refCoefficients);
            refTransformCache.Valid[slot] = 1;
        }

        return H264MotionSatd.Satd4x4FromTransformed(sourceCoefficients, refCoefficients);
    }

    private static SearchResult RefineFrac(
        ReadOnlySpan<byte> current, int currentStride,
        ReadOnlySpan<byte> reference, int referenceStride,
        int mbX, int mbY,
        int bw, int bh,
        IH264KernelSet kernels,
        MeBlockShape blockShape,
        Mv seedMv,
        int seedSad,
        int picW,
        int picH,
        bool checkChroma,
        int pictureWidth,
        int pictureHeight,
        int mbUnpaddedX,
        int mbUnpaddedY,
        int refinementRounds,
        bool useMotionSatd,
        int lambda = 0)
    {
        if (refinementRounds <= 0)
        {
            return new SearchResult(seedMv, seedSad);
        }

        refinementRounds = Math.Clamp(refinementRounds, 1, 2);

        var stride = bw;
        var blockSize = bh * stride;
        Span<byte> qpelBatch = stackalloc byte[16 * blockSize];
        var qpelBatchValidMask = 0;
        var qpelBatchOriginX = int.MinValue;
        var qpelBatchOriginY = int.MinValue;
        Span<int> qpelMvCosts = useMotionSatd && lambda != 0 ? stackalloc int[16] : default;
        if (useMotionSatd && lambda != 0)
        {
            for (var fy = 0; fy < 4; fy++)
            {
                for (var fx = 0; fx < 4; fx++)
                {
                    qpelMvCosts[(fy << 2) + fx] = (lambda * (MvBitCost(fx) + MvBitCost(fy))) >> 8;
                }
            }
        }

        var seedIx = (int)seedMv.X >> 2;
        var seedIy = (int)seedMv.Y >> 2;
        var seedFx = (int)seedMv.X & 3;
        var seedFy = (int)seedMv.Y & 3;

        int bestSad;
        int bestScore;
        if (seedSad == int.MaxValue)
        {
            bestSad = int.MaxValue;
            bestScore = int.MaxValue;
        }
        else if (useMotionSatd)
        {
            if (!Fits(mbX + seedIx, mbY + seedIy, bw, bh, picW, picH))
            {
                bestSad = int.MaxValue;
                bestScore = int.MaxValue;
            }
            else if (seedFx == 0 && seedFy == 0)
            {
                var seedRefWin = OffsetWindow(reference, referenceStride, mbX + seedIx, mbY + seedIy);
                bestSad = SadBlock(kernels, blockShape, current, currentStride, seedRefWin, referenceStride);
                bestScore = seedSad;
            }
            else if (!QpelFits(mbX + seedIx, mbY + seedIy, bw, bh, picW, picH))
            {
                bestSad = int.MaxValue;
                bestScore = int.MaxValue;
            }
            else
            {
                var originX = mbX + seedIx;
                var originY = mbY + seedIy;
                EnsureQpelBlock(kernels, reference, referenceStride, originX, originY, seedFx, seedFy,
                    bw, bh, stride, qpelBatch, blockSize,
                    ref qpelBatchValidMask, ref qpelBatchOriginX, ref qpelBatchOriginY);
                var block = qpelBatch.Slice(((seedFy << 2) + seedFx) * blockSize, blockSize);
                bestSad = SadBlock(kernels, blockShape, current, currentStride, block, stride);
                var seedSatd = SatdBlock(kernels, blockShape, current, currentStride, block, stride);
                var seedMvCost = lambda == 0 ? 0 : qpelMvCosts[(seedFy << 2) + seedFx];
                bestScore = seedSatd + seedMvCost;
            }
        }
        else
        {
            bestSad = seedSad;
            bestScore = seedSad;
        }

        var bestMv = seedMv;

        for (var round = 0; round < refinementRounds; round++)
        {
            var ix = ((int)bestMv.X >> 2);
            var iy = ((int)bestMv.Y >> 2);
            var originX = mbX + ix;
            var originY = mbY + iy;
            if (!QpelFits(originX, originY, bw, bh, picW, picH))
                continue;

            for (var fy = 0; fy < 4; fy++)
            {
                for (var fx = 0; fx < 4; fx++)
                {
                    var mv = ToMv(0, 0, ix, iy, fx, fy);
                    if (bestScore != int.MaxValue && mv.X == bestMv.X && mv.Y == bestMv.Y)
                        continue;

                    if (checkChroma &&
                        !H264InterReconstructor.IsMvSafeForInterBlockAtMb(
                            pictureWidth, pictureHeight, mbUnpaddedX, mbUnpaddedY,
                            bw, bh,
                            mv.X, mv.Y))
                        continue;

                    // Fractional candidate: qpel interpolation plus SAD/SATD over the block.
                    t_searchEffort += (bw * bh) >> 4;
                    if (useMotionSatd)
                    {
                        var mvCost = lambda == 0 ? 0 : qpelMvCosts[(fy << 2) + fx];
                        if (mvCost > bestScore)
                            continue;

                        EnsureQpelBlock(kernels, reference, referenceStride, originX, originY, fx, fy,
                            bw, bh, stride, qpelBatch, blockSize,
                            ref qpelBatchValidMask, ref qpelBatchOriginX, ref qpelBatchOriginY);
                        var block = qpelBatch.Slice(((fy << 2) + fx) * blockSize, blockSize);
                        var satdStopAfter = bestScore == int.MaxValue ? int.MaxValue : bestScore - mvCost;
                        var candSad = SadBlock(kernels, blockShape, current, currentStride, block, stride);
                        if (SatdSadLowerBoundRejects(candSad, satdStopAfter, stopOnEqual: false, (int)blockShape))
                            continue;

                        var candSatd = satdStopAfter == int.MaxValue
                            ? SatdBlock(kernels, blockShape, current, currentStride, block, stride)
                            : SatdBlockBounded(blockShape, current, currentStride, block, stride, satdStopAfter);
                        var candScore = candSatd + mvCost;
                        if (candScore > bestScore)
                            continue;

                        if (IsBetterScoreCandidate(candScore, candSad, mv, bestScore, bestSad, bestMv))
                        {
                            bestScore = candScore;
                            bestSad = candSad;
                            bestMv = mv;
                        }
                    }
                    else
                    {
                        EnsureQpelBlock(kernels, reference, referenceStride, originX, originY, fx, fy,
                            bw, bh, stride, qpelBatch, blockSize,
                            ref qpelBatchValidMask, ref qpelBatchOriginX, ref qpelBatchOriginY);
                        var block = qpelBatch.Slice(((fy << 2) + fx) * blockSize, blockSize);
                        var candSad = SadBlock(kernels, blockShape, current, currentStride, block, stride);
                        if (IsBetterSad(candSad, mv, bestSad, bestMv))
                        {
                            bestScore = candSad;
                            bestSad = candSad;
                            bestMv = mv;
                        }
                    }
                }
            }
        }

        return new SearchResult(bestMv, bestSad);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SatdSadLowerBoundRejects(int sad, int stopAfter, bool stopOnEqual, int shapeIndex)
    {
        if (stopAfter == int.MaxValue)
            return false;
        var lowerBound = (sad + 1) >> 1;
        var rejects = lowerBound > stopAfter || (stopOnEqual && lowerBound == stopAfter);
        H264MotionSatdDagDiagnostics.NotifySatdSadLowerBoundTest(shapeIndex, rejects);
        return rejects;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MinimumMvCostForManhattanDistance(int lambda, int distance, int searchRange)
    {
        if (distance <= searchRange)
            return MotionVectorCost(lambda, distance, 0);

        return MotionVectorCost(lambda, searchRange, distance - searchRange);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SatdBlockBounded(
        MeBlockShape blockShape,
        ReadOnlySpan<byte> current,
        int currentStride,
        ReadOnlySpan<byte> reference,
        int referenceStride,
        int stopAfter)
    {
        var blocksX = blockShape is MeBlockShape.B16x16 or MeBlockShape.B16x8 ? 4 : 2;
        var blocksY = blockShape is MeBlockShape.B16x16 or MeBlockShape.B8x16 ? 4 : 2;
        var sum = 0;

        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                var x = bx * 4;
                var y = by * 4;
                sum += H264MotionSatd.Satd4x4Strided(
                    current, currentStride, x, y,
                    reference, referenceStride, x, y);
                if (sum > stopAfter)
                    return sum;
            }
        }

        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureQpelBlock(
        IH264KernelSet kernels,
        ReadOnlySpan<byte> reference,
        int referenceStride,
        int originX,
        int originY,
        int fx,
        int fy,
        int bw,
        int bh,
        int stride,
        Span<byte> qpelBatch,
        int blockSize,
        ref int validMask,
        ref int batchOriginX,
        ref int batchOriginY)
    {
        if (batchOriginX != originX || batchOriginY != originY)
        {
            validMask = 0;
            batchOriginX = originX;
            batchOriginY = originY;
        }

        var idx = (fy << 2) + fx;
        var bit = 1 << idx;
        if ((validMask & bit) != 0)
            return;

        kernels.InterpolateLuma(
            reference, referenceStride,
            originX, originY,
            fx, fy,
            bw, bh,
            qpelBatch.Slice(idx * blockSize, blockSize), stride);
        validMask |= bit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBetterScoreCandidate(
        int candScore, int candSad, Mv candMv,
        int bestScore, int bestSad, Mv bestMv)
    {
        if (candScore != bestScore)
            return candScore < bestScore;
        if (candSad != bestSad)
            return candSad < bestSad;
        return MvMagnitudeBetter(candMv, bestMv);
    }

    private static void InsertTop4(int cost, int index, Span<int> topCosts, Span<int> topIndexes)
    {
        for (var i = 0; i < 4; i++)
        {
            if (cost >= topCosts[i])
                continue;

            for (var j = 3; j > i; j--)
            {
                topCosts[j] = topCosts[j - 1];
                topIndexes[j] = topIndexes[j - 1];
            }

            topCosts[i] = cost;
            topIndexes[i] = index;
            return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Mv ToMv(int dx, int dy, int predIx, int predIy, int fx, int fy) =>
        new((short)(((predIx + dx) * 4) + fx), (short)(((predIy + dy) * 4) + fy));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> OffsetWindow(ReadOnlySpan<byte> pic, int stride, int ox, int oy) =>
        pic[(oy * stride + ox)..];

    private static bool Fits(int rx, int ry, int bw, int bh, int picW, int picH) =>
        rx >= 0 && ry >= 0 && rx + bw <= picW && ry + bh <= picH;

    private static bool QpelFits(int originX, int originY, int bw, int bh, int picW, int picH) =>
        originX >= 2 && originY >= 2 && originX + bw + 3 <= picW && originY + bh + 3 <= picH;

    private static bool MvMagnitudeBetter(Mv a, Mv b)
    {
        var ma = Math.Abs((int)a.X) + Math.Abs((int)a.Y);
        var mb = Math.Abs((int)b.X) + Math.Abs((int)b.Y);
        if (ma != mb)
            return ma < mb;
        if (a.X != b.X)
            return a.X < b.X;
        return a.Y < b.Y;
    }

    private static bool IsBetterSad(int sadNew, Mv mvNew, int sadBest, Mv mvBest) =>
        sadNew < sadBest || (sadNew == sadBest && MvMagnitudeBetter(mvNew, mvBest));

    private static bool IsPartitionBetter(int sadNew, McPartition partNew, int sadBest, McPartition partBest) =>
        sadNew < sadBest || (sadNew == sadBest && (byte)partNew < (byte)partBest);

    private static short Median3(short a, short b, short c) =>
        (short)(a + b + c - Math.Max(a, Math.Max(b, c)) - Math.Min(a, Math.Min(b, c)));
}
