using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;
using Mv = Kiln.Internal.H264.H264MotionEstimator.Mv;
using McPartition = Kiln.Internal.H264.H264MotionEstimator.McPartition;

namespace Kiln.Tests;

/// <summary>
/// Parity tests for <see cref="H264MotionEstimator.SearchMbSubPartitions"/> partition-shape selection.
/// Each test synthesises a macroblock whose content can only be represented efficiently by a specific
/// partition shape and asserts that the estimator selects that shape and recovers the exact per-partition MVs.
/// </summary>
public sealed class H264SubPartitionMeTests
{
    private const int RefSize = 128;
    private const int MbX = 32;
    private const int MbY = 32;
    private const int SearchRange = 12;

    private static byte[] BuildReference(int seed = 0xABCD)
    {
        var rng = new Random(seed);
        var bytes = new byte[RefSize * RefSize];
        rng.NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Copies a region out of the reference into a <paramref name="dst"/> slice of the current MB at a
    /// given pixel offset, applying a per-region integer-pel shift in the reference.
    /// </summary>
    private static void BlitRegion(
        ReadOnlySpan<byte> reference,
        Span<byte> current,
        int mvxIntPel, int mvyIntPel,
        int dstX, int dstY,
        int w, int h)
    {
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                var srcX = MbX + dstX + mvxIntPel + col;
                var srcY = MbY + dstY + mvyIntPel + row;
                current[(dstY + row) * 16 + (dstX + col)] = reference[srcY * RefSize + srcX];
            }
        }
    }

    /// <summary>
    /// Smoke test after SATD+λ qpel scoring: sub-partition search still yields in-range MVs and a
    /// non-negative total SAD (SAD-domain invariant for slice vs intra competition).
    /// </summary>
    [Fact]
    public void SearchMbSubPartitions_with_motion_satd_and_lambda_smoke_valid_mvs_and_sad()
    {
        var reference = BuildReference();
        var current = CopyBlock(reference, RefSize, MbX + 1, MbY + 1, 16, 16);

        var result = H264MotionEstimator.SearchMbSubPartitions(
            current, currentStride: 16,
            reference, referenceStride: RefSize,
            mbX: MbX, mbY: MbY,
            mvPredictor: new Mv(0, 0),
            searchRange: SearchRange,
            useMotionSatd: true,
            kernels: H264MeTestHelpers.Kernels,
            fastSeedSearchRange: SearchRange,
            lambda: 48);

        result.TotalSad.Should().BeGreaterThanOrEqualTo(0);
        const int maxAbsQpel = 256;
        foreach (var mv in new[] { result.Mv0, result.Mv1, result.Mv2, result.Mv3 })
        {
            Math.Abs(mv.X).Should().BeLessThanOrEqualTo(maxAbsQpel);
            Math.Abs(mv.Y).Should().BeLessThanOrEqualTo(maxAbsQpel);
        }
    }

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

    /// <summary>
    /// A macroblock where the top 8 rows and bottom 8 rows require different MVs can only achieve
    /// SAD = 0 with a P_16x8 (or finer P_8x8) partition. Verifies the estimator selects at most
    /// P_16x8 or P_8x8 and recovers the exact half-MV pair.
    /// </summary>
    [Fact]
    public void SearchMbSubPartitions_horizontal_bar_selects_Mb16x8_or_finer()
    {
        var reference = BuildReference();
        var current = new byte[16 * 16];

        // Top half: shift (+3, 0); bottom half: shift (-3, 0).
        // P_16×16 and P_8×16 must merge both halves into one MV — neither achieves SAD=0.
        BlitRegion(reference, current, mvxIntPel: 3, mvyIntPel: 0, dstX: 0, dstY: 0, w: 16, h: 8);
        BlitRegion(reference, current, mvxIntPel: -3, mvyIntPel: 0, dstX: 0, dstY: 8, w: 16, h: 8);

        var result = H264MotionEstimator.SearchMbSubPartitions(
            current, currentStride: 16,
            reference, referenceStride: RefSize,
            mbX: MbX, mbY: MbY,
            mvPredictor: new Mv(0, 0),
            searchRange: SearchRange,
            useMotionSatd: false,
            kernels: H264MeTestHelpers.Kernels,
            fastSeedSearchRange: SearchRange);

        result.TotalSad.Should().Be(0,
            "both halves placed at their target MVs — the chosen partition must find an exact match");
        result.Partition.Should().BeOneOf(
            new[] { McPartition.Mb16x8, McPartition.Mb8x8 },
            "P_16x8 or finer is needed to cover two horizontally-separated motion regions");

        // Verify the top-half MV is +3 int-pels and bottom is -3 int-pels (in qpel units × 4).
        var topMv = result.Partition == McPartition.Mb16x8 ? result.Mv0 : result.Mv0;
        topMv.X.Should().Be(12, "top half target is +3 int-pels = +12 qpel");
        topMv.Y.Should().Be(0);

        if (result.Partition == McPartition.Mb16x8)
        {
            result.Mv1.X.Should().Be(-12, "bottom half target is -3 int-pels = -12 qpel");
            result.Mv1.Y.Should().Be(0);
        }
    }

    [Fact]
    public void SearchMbSubPartitions_when_subpartition_search_disabled_returns_16x16_seed()
    {
        var reference = BuildReference();
        var current = new byte[16 * 16];
        BlitRegion(reference, current, mvxIntPel: 3, mvyIntPel: 0, dstX: 0, dstY: 0, w: 16, h: 8);
        BlitRegion(reference, current, mvxIntPel: -3, mvyIntPel: 0, dstX: 0, dstY: 8, w: 16, h: 8);

        var result = H264MotionEstimator.SearchMbSubPartitions(
            current, currentStride: 16,
            reference, referenceStride: RefSize,
            mbX: MbX, mbY: MbY,
            mvPredictor: new Mv(0, 0),
            searchRange: SearchRange,
            useMotionSatd: false,
            kernels: H264MeTestHelpers.Kernels,
            fastSeedSearchRange: SearchRange,
            allowSubPartitionSearch: false);

        result.Partition.Should().Be(McPartition.Mb16x16);
        result.TotalSad.Should().BeGreaterThan(0,
            "the explicit gate must actually bypass the subpartition searches that would find the split-MV match");
    }

    /// <summary>
    /// A macroblock where the left 8 columns and right 8 columns require different MVs can only achieve
    /// SAD = 0 with a P_8×16 (or finer P_8×8) partition.
    /// </summary>
    [Fact]
    public void SearchMbSubPartitions_vertical_bar_selects_Mb8x16_or_finer()
    {
        var reference = BuildReference(seed: 0xDEAD);
        var current = new byte[16 * 16];

        // Left half: shift (0, +3); right half: shift (0, -3).
        BlitRegion(reference, current, mvxIntPel: 0, mvyIntPel: 3, dstX: 0, dstY: 0, w: 8, h: 16);
        BlitRegion(reference, current, mvxIntPel: 0, mvyIntPel: -3, dstX: 8, dstY: 0, w: 8, h: 16);

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
            "both halves placed at their target MVs — the chosen partition must find an exact match");
        result.Partition.Should().BeOneOf(
            new[] { McPartition.Mb8x16, McPartition.Mb8x8 },
            "P_8x16 or finer is needed to cover two vertically-separated motion regions");

        var leftMv = result.Partition == McPartition.Mb8x16 ? result.Mv0 : result.Mv0;
        leftMv.X.Should().Be(0);
        leftMv.Y.Should().Be(12, "left half target is +3 int-pels Y = +12 qpel");

        if (result.Partition == McPartition.Mb8x16)
        {
            result.Mv1.X.Should().Be(0);
            result.Mv1.Y.Should().Be(-12, "right half target is -3 int-pels Y = -12 qpel");
        }
    }

    [Fact]
    public void SearchMbSubPartitions_fast_search_honors_search_range_bound()
    {
        var reference = BuildReference(seed: 0xBEEF);
        var current = CopyBlock(reference, RefSize, MbX + 20, MbY, 16, 16);

        var rTight = H264MotionEstimator.SearchMbSubPartitions(
            current, currentStride: 16,
            reference, referenceStride: RefSize,
            mbX: MbX, mbY: MbY,
            mvPredictor: new Mv(0, 0),
            searchRange: 8,
            useMotionSatd: false,
            kernels: H264MeTestHelpers.Kernels,
            fastSearch: true,
            fastSeedSearchRange: 8);
        rTight.TotalSad.Should().BeGreaterThan(0, "true match is +20 pels and must be unreachable at range 8");

        var rWide = H264MotionEstimator.SearchMbSubPartitions(
            current, currentStride: 16,
            reference, referenceStride: RefSize,
            mbX: MbX, mbY: MbY,
            mvPredictor: new Mv(0, 0),
            searchRange: 32,
            useMotionSatd: false,
            kernels: H264MeTestHelpers.Kernels,
            fastSearch: false,
            fastSeedSearchRange: 32);
        rWide.TotalSad.Should().Be(0, "with range 32, +20-pel match is reachable");
        rWide.Mv0.X.Should().Be(80);
        rWide.Mv0.Y.Should().Be(0);
        rTight.Mv0.X.Should().NotBe(80, "range-8 fast search must not escape to +20-pel displacement");
    }

}
