// Phase-1 acceptance test for Composer2 task F2a (H264MotionEstimator core).
//
// Authoring strategy: STRATEGY A from the orchestration playbook — the real test fixtures live
// inside `#if HAS_MOTION_ESTIMATOR` blocks that reference internal types the F2a worker will add
// in `src/Kiln/Internal/H264/H264MotionEstimator.cs`. With that symbol UNDEFINED
// (the state at file commit time), only the `MotionEstimator_must_be_delivered_before_test_runs`
// fact is compiled, and it fails with a Composer2-actionable error. After the F2a worker lands
// the production module, the senior's phase-3 integration step adds
// `<DefineConstants>$(DefineConstants);HAS_MOTION_ESTIMATOR</DefineConstants>` to
// `tests/Kiln.Tests/Kiln.Tests.csproj`; the gate test then short-circuits
// to a no-op pass and the real fixture suite below activates.
//
// Why Strategy A and not Strategy B (reflection-only stub):
//   * `H264MotionEstimator`'s public API takes `ReadOnlySpan<byte>` parameters, which CANNOT be
//     passed through `MethodInfo.Invoke` (spans are ref structs). Reflection would force the test
//     to copy spans into byte[] surrogates and use `MakeGenericMethod` gymnastics, which would be
//     fragile and obscure the actual assertions.
//   * The internal `Mv`, `SearchResult`, `PartitionResult`, `McPartition` types are also reached
//     more cleanly as direct symbols once they exist; reflection would either round-trip through
//     `dynamic` (bypassing record-struct equality) or wrap each in a homegrown adapter.
//   * Strategy A's only cost is a one-line csproj edit by the senior at integration time, which
//     is already in the senior's phase-3 playbook.
//
// Drift-trap context (see h264_encoder_composer2_delegation_orchestration.md "The drift trap"):
//   H.264 sub-pel motion estimation can decode through `ffmpeg` cleanly with a slightly wrong MV —
//   the bitstream is structurally valid, the decoder follows the encoder's MV exactly, and PSNR
//   silently drops ~0.5 dB. The fixtures below therefore assert EXACT MV recovery (full-pel and
//   sub-pel) on hand-crafted reference patches, not "PSNR within tolerance". A wrong median in
//   `PredictMv`, an off-by-one in qpel refinement, or a partition-selection wired backwards will
//   all surface here as a definite mismatch instead of a pass-with-quality-loss.

using Xunit;

#if HAS_MOTION_ESTIMATOR
using FluentAssertions;
using Kiln.Internal.H264;
using Mv = Kiln.Internal.H264.H264MotionEstimator.Mv;
using McPartition = Kiln.Internal.H264.H264MotionEstimator.McPartition;
#endif

namespace Kiln.Tests;

/// <summary>
/// Phase-1 acceptance test class for the F2a deliverable
/// (<c>Kiln.Internal.H264.H264MotionEstimator</c>). The class is the Composer2 worker's
/// contract: it must be green by the end of F2a's diff. The set of <see cref="FactAttribute"/>s
/// changes depending on the <c>HAS_MOTION_ESTIMATOR</c> compile-time symbol — see the file-level
/// comment for the strategy explanation.
/// </summary>
public sealed class H264MotionEstimatorTests
{
#if !HAS_MOTION_ESTIMATOR
    /// <summary>
    /// Pre-delivery gate: as long as the F2a production module is missing, this test fails with a
    /// clear, actionable message so a Composer2 worker reading the failure log knows exactly what
    /// to add. The full fixture suite (full-pel/qpel recovery, median predictor, partition
    /// selection) lives below this method inside <c>#if HAS_MOTION_ESTIMATOR</c> and activates
    /// after the senior toggles the build symbol in phase-3 integration.
    /// </summary>
    [Fact]
    public void MotionEstimator_must_be_delivered_before_test_runs()
    {
        var t = Type.GetType("Kiln.Internal.H264.H264MotionEstimator, Kiln");
        if (t is null)
        {
            Assert.Fail(
                "F2a has not been delivered: type Kiln.Internal.H264.H264MotionEstimator " +
                "is missing. Implement the module per src/Kiln/Internal/H264/H264MotionEstimator.cs " +
                "with the public API given in the F2a Composer2 prompt " +
                "(`SearchMb16x16`, `SearchMbSubPartitions`, `PredictMv`, plus `Mv`, `SearchResult`, " +
                "`PartitionResult`, `McPartition`).");
        }

        Assert.Fail(
            "F2a appears to be delivered (Kiln.Internal.H264.H264MotionEstimator exists), " +
            "but the test project's `HAS_MOTION_ESTIMATOR` build symbol is not enabled. The senior must " +
            "add `<DefineConstants>$(DefineConstants);HAS_MOTION_ESTIMATOR</DefineConstants>` to a " +
            "`<PropertyGroup>` in `tests/Kiln.Tests/Kiln.Tests.csproj` to activate " +
            "the real H264MotionEstimatorTests fixtures (see file header comment).");
    }
#else
    // -----------------------------------------------------------------------------------------
    // Real fixture suite (active when HAS_MOTION_ESTIMATOR is defined).
    // -----------------------------------------------------------------------------------------

    private const int RefSize = 128;
    private const int MbX = 32;
    private const int MbY = 32;
    private const int SearchRange = 8;
    private const int RngSeed = 0xDEC0DE;

    /// <summary>
    /// Builds a deterministic random luma plane large enough to contain a 16×16 macroblock at
    /// (<see cref="MbX"/>, <see cref="MbY"/>) plus the <see cref="SearchRange"/> integer-pel
    /// search window AND the H.264 6-tap qpel halo (3 samples on each side beyond the integer
    /// search range). 128×128 with the macroblock at (32, 32) and `searchRange=8` leaves 21
    /// samples of headroom on each side, which comfortably exceeds the 6-tap requirement.
    /// </summary>
    private static byte[] BuildReference(int seed = RngSeed)
    {
        var rng = new Random(seed);
        var bytes = new byte[RefSize * RefSize];
        rng.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Copies a sub-block out of a row-major plane into a tightly-packed buffer.</summary>
    private static byte[] CopyBlock(ReadOnlySpan<byte> src, int srcStride, int x, int y, int w, int h)
    {
        var dst = new byte[w * h];
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                dst[row * w + col] = src[(y + row) * srcStride + (x + col)];
            }
        }

        return dst;
    }

    public static IEnumerable<object[]> FullPelMvCases()
    {
        // Test cases enumerated in the orchestration doc's "Senior phase 1: test inventory" /
        // "Test 2" section. Each tuple is an integer-pel MV (mvX, mvY); the expected qpel return
        // is (mvX*4, mvY*4) since each integer pel = 4 qpel.
        yield return new object[] { 0, 0 };
        yield return new object[] { 3, 0 };
        yield return new object[] { 0, 3 };
        yield return new object[] { 3, 3 };
        yield return new object[] { -2, 1 };
        yield return new object[] { 1, -2 };
    }

    /// <summary>
    /// Hand-crafted reference patches: copy a 16×16 sub-block out of a known reference at a
    /// specific integer-pel offset, then assert the estimator recovers that exact MV (in qpel
    /// units) and reports SAD = 0 (the current block is bit-identical to the matched reference
    /// position by construction).
    /// </summary>
    [Theory]
    [MemberData(nameof(FullPelMvCases))]
    public void SearchMb16x16_recovers_exact_full_pel_MV(int mvxIntPel, int mvyIntPel)
    {
        var reference = BuildReference();
        var current = CopyBlock(reference, RefSize, MbX + mvxIntPel, MbY + mvyIntPel, 16, 16);

        var result = H264MotionEstimator.SearchMb16x16(
            current, currentStride: 16,
            reference, referenceStride: RefSize,
            mbX: MbX, mbY: MbY,
            mvPredictor: new Mv(0, 0),
            searchRange: SearchRange,
            useMotionSatd: false,
            kernels: H264MeTestHelpers.Kernels);

        result.BestMv.X.Should().Be((short)(mvxIntPel * 4),
            $"integer MV ({mvxIntPel}, {mvyIntPel}) should round-trip to qpel ({mvxIntPel * 4}, {mvyIntPel * 4})");
        result.BestMv.Y.Should().Be((short)(mvyIntPel * 4));
        result.BestSad.Should().Be(0,
            "the current block was copied verbatim from the reference at the target MV — SAD must be exactly zero");
    }

    /// <summary>
    /// Constructs a "current" block by interpolating the reference at qpel offset (1/4, 1/4)
    /// using the SAME interpolator the estimator must use during refinement
    /// (<see cref="H264QpelLumaInterp"/>). The estimator must therefore find an exact qpel match
    /// at MV (1, 1) in qpel units (integer (0, 0) + fractional (1, 1)) with SAD = 0.
    /// </summary>
    [Fact]
    public void SearchMb16x16_recovers_qpel_quarter_quarter_offset()
    {
        var reference = BuildReference();
        var current = new byte[16 * 16];
        H264QpelLumaInterp.Interpolate(
            reference, srcStride: RefSize,
            srcOriginX: MbX, srcOriginY: MbY,
            xFrac: 1, yFrac: 1,
            blockWidth: 16, blockHeight: 16,
            current, dstStride: 16);

        var result = H264MotionEstimator.SearchMb16x16(
            current, currentStride: 16,
            reference, referenceStride: RefSize,
            mbX: MbX, mbY: MbY,
            mvPredictor: new Mv(0, 0),
            searchRange: SearchRange,
            useMotionSatd: false,
            kernels: H264MeTestHelpers.Kernels);

        result.BestMv.X.Should().Be((short)1,
            "current block is the reference interpolated at qpel xFrac=1, so the recovered qpel MV.X must be 1");
        result.BestMv.Y.Should().Be((short)1,
            "current block is the reference interpolated at qpel yFrac=1, so the recovered qpel MV.Y must be 1");
        result.BestSad.Should().Be(0,
            "current is bit-identical to qpel-(1,1) interpolation of reference — SAD must be exactly zero");
    }

    /// <summary>
    /// Qpel refinement with SATD + λ·MvBitCost (Phase 1): must run without throwing and still report
    /// SAD-domain <see cref="SearchResult.BestSad"/> — zero residual yields zero SAD.
    /// </summary>
    [Fact]
    public void SearchMb16x16_with_motion_satd_and_lambda_qpel_refinement_zero_residual_has_zero_best_sad()
    {
        var reference = BuildReference();
        var current = CopyBlock(reference, RefSize, MbX, MbY, 16, 16);

        var result = H264MotionEstimator.SearchMb16x16(
            current, currentStride: 16,
            reference, referenceStride: RefSize,
            mbX: MbX, mbY: MbY,
            mvPredictor: new Mv(0, 0),
            searchRange: SearchRange,
            useMotionSatd: true,
            kernels: H264MeTestHelpers.Kernels,
            lambda: 64);

        result.BestSad.Should().Be(0,
            "BestSad stays SAD-domain; identical current and reference at MV (0,0) ⇒ SAD is zero");
    }

    public static IEnumerable<object[]> PredictMv_AllAvailableCases()
    {
        // ITU-T H.264 8.4.1.3.1 median predictor: component-wise median of neighbours A (left),
        // B (above), C (above-right). Test the canonical orchestration-doc case plus a few
        // permutations to catch axis-swap or component-mix-up bugs.
        // Format: { aX, aY, bX, bY, cX, cY, expectedX, expectedY }.
        yield return new object[] { 4, 0, 0, 4, 4, 4, 4, 4 };
        yield return new object[] { -3, 0, 0, -3, -3, -3, -3, -3 };
        yield return new object[] { 1, 7, 7, 1, 4, 4, 4, 4 };
        yield return new object[] { 0, 0, 0, 0, 0, 0, 0, 0 };
    }

    [Theory]
    [MemberData(nameof(PredictMv_AllAvailableCases))]
    public void PredictMv_returns_component_wise_median_when_A_B_C_all_available(
        int aX, int aY, int bX, int bY, int cX, int cY, int expectedX, int expectedY)
    {
        var result = H264MotionEstimator.PredictMv(
            new Mv((short)aX, (short)aY), aAvail: true,
            new Mv((short)bX, (short)bY), bAvail: true,
            new Mv((short)cX, (short)cY), cAvail: true,
            mvD: default, dAvail: false);

        result.X.Should().Be((short)expectedX);
        result.Y.Should().Be((short)expectedY);
    }

    [Fact]
    public void PredictMv_returns_A_when_only_A_is_available()
    {
        var result = H264MotionEstimator.PredictMv(
            new Mv(7, -3), aAvail: true,
            mvB: default, bAvail: false,
            mvC: default, cAvail: false,
            mvD: default, dAvail: false);

        result.Should().Be(new Mv(7, -3),
            "H.264 8.4.1.3.1 special case: when exactly one of A/B/C is available, the predictor is that neighbour's MV");
    }

    [Fact]
    public void PredictMv_returns_B_when_only_B_is_available()
    {
        var result = H264MotionEstimator.PredictMv(
            mvA: default, aAvail: false,
            new Mv(2, 9), bAvail: true,
            mvC: default, cAvail: false,
            mvD: default, dAvail: false);

        result.Should().Be(new Mv(2, 9));
    }

    [Fact]
    public void PredictMv_returns_C_when_only_C_is_available()
    {
        var result = H264MotionEstimator.PredictMv(
            mvA: default, aAvail: false,
            mvB: default, bAvail: false,
            new Mv(-5, 4), cAvail: true,
            mvD: default, dAvail: false);

        result.Should().Be(new Mv(-5, 4));
    }

    [Fact]
    public void PredictMv_substitutes_D_for_C_when_C_is_unavailable_per_8_4_1_3_1()
    {
        // 8.4.1.3.1 C-fallback: when C is unavailable but D (above-left) is available, the median
        // is taken over (A, B, D) instead of (A, B, C). Setting A=(2,2), B=(4,4), D=(6,6) makes
        // the median (4, 4); a buggy implementation that passes mvC=(0,0) through unchanged would
        // produce median(2, 4, 0)=2 instead.
        var result = H264MotionEstimator.PredictMv(
            new Mv(2, 2), aAvail: true,
            new Mv(4, 4), bAvail: true,
            mvC: default, cAvail: false,
            new Mv(6, 6), dAvail: true);

        result.Should().Be(new Mv(4, 4),
            "C unavailable + D available ⇒ predictor = median(A, B, D) per H.264 8.4.1.3.1; got the wrong median, " +
            "likely the C-fallback substitution is missing or applied conditionally on the wrong flag");
    }

    [Fact]
    public void PredictMv_returns_zero_when_all_neighbours_are_unavailable()
    {
        var result = H264MotionEstimator.PredictMv(
            mvA: default, aAvail: false,
            mvB: default, bAvail: false,
            mvC: default, cAvail: false,
            mvD: default, dAvail: false);

        result.Should().Be(new Mv(0, 0),
            "with no neighbours available (e.g. top-left MB of the slice) the predictor must default to (0, 0)");
    }

    [Fact]
    public void PredictMv_substitutes_zero_when_only_B_and_C_available_per_8_4_1_3_1()
    {
        // Left-column non-corner MBs: A unavailable, B and C inter-coded → median(0, B, C) per spec.
        var result = H264MotionEstimator.PredictMv(
            mvA: default, aAvail: false,
            new Mv(2, 4), bAvail: true,
            new Mv(6, 8), cAvail: true,
            mvD: default, dAvail: false);

        result.Should().Be(new Mv(2, 4),
            "Median(0,2,6)=2 and Median(0,4,8)=4; wrong code returns (6,8) from duplicating C");
    }

    [Fact]
    public void PredictMv_substitutes_zero_when_only_A_and_C_available_per_8_4_1_3_1()
    {
        var result = H264MotionEstimator.PredictMv(
            new Mv(2, 4), aAvail: true,
            mvB: default, bAvail: false,
            new Mv(6, 8), cAvail: true,
            mvD: default, dAvail: false);

        result.Should().Be(new Mv(2, 4),
            "Median(2,0,6)=2 and Median(4,0,8)=4");
    }

    [Fact]
    public void PredictMv_substitutes_zero_when_only_A_and_B_available_per_8_4_1_3_1()
    {
        var result = H264MotionEstimator.PredictMv(
            new Mv(2, 4), aAvail: true,
            new Mv(6, 8), bAvail: true,
            mvC: default, cAvail: false,
            mvD: default, dAvail: false);

        result.Should().Be(new Mv(2, 4),
            "Median(2,6,0)=2 and Median(4,8,0)=4 when C and D unavailable");
    }

    [Fact]
    public void PredictMv_cnt2_straddle_zero_yields_zero_components()
    {
        var result = H264MotionEstimator.PredictMv(
            mvA: default, aAvail: false,
            new Mv(-3, 5), bAvail: true,
            new Mv(4, -2), cAvail: true,
            mvD: default, dAvail: false);

        result.Should().Be(new Mv(0, 0),
            "Median(0,-3,4)=(0) and Median(0,5,-2)=(0); wrong cnt=2 would pick one neighbour");
    }

    /// <summary>
    /// Uniform translation case: every 16×16 sample of the current block is the reference shifted
    /// by (2, 2) integer pels. Every partition shape can achieve total SAD = 0, so the
    /// implementation is free to pick any shape — but whichever shape it chooses, the active MV
    /// slots must all carry the (8, 8) qpel MV and total SAD must be exactly zero.
    /// </summary>
    [Fact]
    public void SearchMbSubPartitions_uniform_translation_achieves_zero_sad()
    {
        const int mvxIntPel = 2;
        const int mvyIntPel = 2;
        var reference = BuildReference();
        var current = CopyBlock(reference, RefSize, MbX + mvxIntPel, MbY + mvyIntPel, 16, 16);

        var result = H264MotionEstimator.SearchMbSubPartitions(
            current, currentStride: 16,
            reference, referenceStride: RefSize,
            mbX: MbX, mbY: MbY,
            mvPredictor: new Mv(0, 0),
            searchRange: SearchRange,
            useMotionSatd: false,
            kernels: H264MeTestHelpers.Kernels,
            fastSearch: false,
            fastSeedSearchRange: SearchRange);

        result.TotalSad.Should().Be(0,
            "uniform translation across the macroblock means at least one partition shape achieves SAD=0; " +
            "anything non-zero indicates the search did not converge");

        var expected = new Mv((short)(mvxIntPel * 4), (short)(mvyIntPel * 4));
        switch (result.Partition)
        {
            case McPartition.Mb16x16:
                result.Mv0.Should().Be(expected, "Mb16x16: Mv0 carries the single MV");
                break;
            case McPartition.Mb16x8:
                result.Mv0.Should().Be(expected, "Mb16x8: Mv0 = top half MV");
                result.Mv1.Should().Be(expected, "Mb16x8: Mv1 = bottom half MV");
                break;
            case McPartition.Mb8x16:
                result.Mv0.Should().Be(expected, "Mb8x16: Mv0 = left half MV");
                result.Mv1.Should().Be(expected, "Mb8x16: Mv1 = right half MV");
                break;
            case McPartition.Mb8x8:
                result.Mv0.Should().Be(expected, "Mb8x8: Mv0 = TL quadrant");
                result.Mv1.Should().Be(expected, "Mb8x8: Mv1 = TR quadrant");
                result.Mv2.Should().Be(expected, "Mb8x8: Mv2 = BL quadrant");
                result.Mv3.Should().Be(expected, "Mb8x8: Mv3 = BR quadrant");
                break;
            default:
                Assert.Fail($"Unexpected partition shape: {result.Partition}");
                break;
        }
    }

    /// <summary>
    /// Per-quadrant divergent-MV case: each 8×8 quadrant of the current macroblock is copied from
    /// a different position in the reference. Only <see cref="McPartition.Mb8x8"/> can achieve
    /// total SAD = 0 — every other shape must merge two quadrants with conflicting optimal MVs
    /// into a single partition, accumulating large residual SAD against the random pattern.
    /// </summary>
    [Fact]
    public void SearchMbSubPartitions_divergent_quadrant_MVs_select_Mb8x8()
    {
        var reference = BuildReference();

        // Per-quadrant target MVs (integer pel); chosen so that no two quadrants share an MV (so
        // 16×8 and 8×16 partitions cannot achieve SAD=0 by lucky uniformity) and all are inside
        // the [-SearchRange, +SearchRange] window so the estimator can locate them.
        var (qTLx, qTLy) = (0, 0);
        var (qTRx, qTRy) = (5, 0);
        var (qBLx, qBLy) = (0, 5);
        var (qBRx, qBRy) = (-5, -5);

        var current = new byte[16 * 16];
        BlitQuadrant(reference, current, qTLx, qTLy, dstX: 0, dstY: 0);
        BlitQuadrant(reference, current, qTRx, qTRy, dstX: 8, dstY: 0);
        BlitQuadrant(reference, current, qBLx, qBLy, dstX: 0, dstY: 8);
        BlitQuadrant(reference, current, qBRx, qBRy, dstX: 8, dstY: 8);

        var result = H264MotionEstimator.SearchMbSubPartitions(
            current, currentStride: 16,
            reference, referenceStride: RefSize,
            mbX: MbX, mbY: MbY,
            mvPredictor: new Mv(0, 0),
            searchRange: SearchRange,
            useMotionSatd: false,
            kernels: H264MeTestHelpers.Kernels,
            fastSeedSearchRange: SearchRange);

        result.Partition.Should().Be(McPartition.Mb8x8,
            "only an 8×8 partition can drive total SAD to 0 when all four 8×8 quadrants want different MVs; " +
            "if a coarser partition wins, partition-shape SAD competition is wired backwards");
        result.TotalSad.Should().Be(0,
            "with each quadrant placed at its target MV, the 8×8 partition has an exact match per quadrant");
        result.Mv0.Should().Be(new Mv((short)(qTLx * 4), (short)(qTLy * 4)), "Mv0 = top-left 8×8 quadrant MV");
        result.Mv1.Should().Be(new Mv((short)(qTRx * 4), (short)(qTRy * 4)), "Mv1 = top-right 8×8 quadrant MV");
        result.Mv2.Should().Be(new Mv((short)(qBLx * 4), (short)(qBLy * 4)), "Mv2 = bottom-left 8×8 quadrant MV");
        result.Mv3.Should().Be(new Mv((short)(qBRx * 4), (short)(qBRy * 4)), "Mv3 = bottom-right 8×8 quadrant MV");
    }

    private static void BlitQuadrant(
        ReadOnlySpan<byte> reference,
        Span<byte> current,
        int mvxIntPel, int mvyIntPel,
        int dstX, int dstY)
    {
        for (var row = 0; row < 8; row++)
        {
            for (var col = 0; col < 8; col++)
            {
                var srcX = MbX + dstX + mvxIntPel + col;
                var srcY = MbY + dstY + mvyIntPel + row;
                current[(dstY + row) * 16 + (dstX + col)] = reference[srcY * RefSize + srcX];
            }
        }
    }
#endif
}
