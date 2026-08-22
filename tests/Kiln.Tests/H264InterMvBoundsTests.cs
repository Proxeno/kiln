using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

public sealed class H264InterMvBoundsTests
{
    [Fact]
    public void IsMvSafeForInter16x16AtMb_zero_MV_top_left_true()
    {
        H264InterReconstructor.IsMvSafeForInter16x16AtMb(320, 240, 0, 0, 0, 0)
            .Should().BeTrue();
    }

    [Fact]
    public void IsMvSafeForInter16x16AtMb_rejects_MV_that_exits_luma_padded_plane()
    {
        H264InterReconstructor.IsMvSafeForInter16x16AtMb(320, 240, 0, 0, short.MaxValue, 0)
            .Should().BeFalse();
    }

    [Fact]
    public void SearchMb16x16_with_picture_dims_picks_chroma_safe_MV_near_bottom_edge()
    {
        const int w = 48;
        const int h = 48;
        const int halo = H264InterReconstructor.DefaultRefHaloLuma;
        var lumaW = w + 2 * halo;
        var lumaH = h + 2 * halo;
        var reference = new byte[lumaW * lumaH];
        for (var i = 0; i < reference.Length; i++)
        {
            reference[i] = (byte)(i & 0xFF);
        }

        var mbXp = 32 + halo;
        var mbYp = 32 + halo;
        var current = new byte[16 * 16];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                current[y * 16 + x] = reference[(mbYp + y) * lumaW + (mbXp + x)];
            }
        }

        var r = H264MotionEstimator.SearchMb16x16(
            current,
            currentStride: 16,
            reference,
            referenceStride: lumaW,
            mbX: mbXp,
            mbY: mbYp,
            mvPredictor: default,
            searchRange: 8,
            useMotionSatd: false,
            kernels: H264MeTestHelpers.Kernels,
            pictureWidth: w,
            pictureHeight: h);

        H264InterReconstructor.IsMvSafeForInter16x16AtMb(w, h, 32, 32, r.BestMv.X, r.BestMv.Y)
            .Should().BeTrue();
        r.BestSad.Should().Be(0);
    }

    [Fact]
    public void SearchMbSubPartitions_with_picture_dims_rejects_chroma_unsafe_temporal_MV_near_edge()
    {
        const int w = 48;
        const int h = 48;
        const int halo = H264InterReconstructor.DefaultRefHaloLuma;
        var lumaW = w + 2 * halo;
        var lumaH = h + 2 * halo;
        var reference = new byte[lumaW * lumaH];
        for (var y = 0; y < lumaH; y++)
        {
            for (var x = 0; x < lumaW; x++)
            {
                reference[y * lumaW + x] = (byte)((x * 17 + y * 31) & 0xFF);
            }
        }

        var mbXp = 32 + halo;
        var mbYp = 32 + halo;
        var unsafeMv = new H264MotionEstimator.Mv(68, 0);
        H264InterReconstructor.IsMvSafeForInter16x16AtMb(w, h, 32, 32, unsafeMv.X, unsafeMv.Y)
            .Should().BeFalse();

        var current = new byte[16 * 16];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                current[y * 16 + x] = reference[(mbYp + y) * lumaW + (mbXp + 17 + x)];
            }
        }

        var r = H264MotionEstimator.SearchMbSubPartitions(
            current,
            currentStride: 16,
            reference,
            referenceStride: lumaW,
            mbX: mbXp,
            mbY: mbYp,
            mvPredictor: default,
            searchRange: 8,
            useMotionSatd: false,
            kernels: H264MeTestHelpers.Kernels,
            temporalMv: unsafeMv,
            fastSearch: false,
            fastSeedSearchRange: 8,
            pictureWidth: w,
            pictureHeight: h);

        AssertActiveMvsAreChromaSafe(r, w, h, 32, 32);
    }

    private static void AssertActiveMvsAreChromaSafe(
        H264MotionEstimator.PartitionResult r,
        int pictureWidth,
        int pictureHeight,
        int mbX,
        int mbY)
    {
        switch (r.Partition)
        {
            case H264MotionEstimator.McPartition.Mb16x16:
                H264InterReconstructor.IsMvSafeForInterBlockAtMb(pictureWidth, pictureHeight, mbX, mbY, 16, 16, r.Mv0.X, r.Mv0.Y)
                    .Should().BeTrue();
                break;
            case H264MotionEstimator.McPartition.Mb16x8:
                H264InterReconstructor.IsMvSafeForInterBlockAtMb(pictureWidth, pictureHeight, mbX, mbY, 16, 8, r.Mv0.X, r.Mv0.Y)
                    .Should().BeTrue();
                H264InterReconstructor.IsMvSafeForInterBlockAtMb(pictureWidth, pictureHeight, mbX, mbY + 8, 16, 8, r.Mv1.X, r.Mv1.Y)
                    .Should().BeTrue();
                break;
            case H264MotionEstimator.McPartition.Mb8x16:
                H264InterReconstructor.IsMvSafeForInterBlockAtMb(pictureWidth, pictureHeight, mbX, mbY, 8, 16, r.Mv0.X, r.Mv0.Y)
                    .Should().BeTrue();
                H264InterReconstructor.IsMvSafeForInterBlockAtMb(pictureWidth, pictureHeight, mbX + 8, mbY, 8, 16, r.Mv1.X, r.Mv1.Y)
                    .Should().BeTrue();
                break;
            case H264MotionEstimator.McPartition.Mb8x8:
                H264InterReconstructor.IsMvSafeForInterBlockAtMb(pictureWidth, pictureHeight, mbX, mbY, 8, 8, r.Mv0.X, r.Mv0.Y)
                    .Should().BeTrue();
                H264InterReconstructor.IsMvSafeForInterBlockAtMb(pictureWidth, pictureHeight, mbX + 8, mbY, 8, 8, r.Mv1.X, r.Mv1.Y)
                    .Should().BeTrue();
                H264InterReconstructor.IsMvSafeForInterBlockAtMb(pictureWidth, pictureHeight, mbX, mbY + 8, 8, 8, r.Mv2.X, r.Mv2.Y)
                    .Should().BeTrue();
                H264InterReconstructor.IsMvSafeForInterBlockAtMb(pictureWidth, pictureHeight, mbX + 8, mbY + 8, 8, 8, r.Mv3.X, r.Mv3.Y)
                    .Should().BeTrue();
                break;
        }
    }
}
