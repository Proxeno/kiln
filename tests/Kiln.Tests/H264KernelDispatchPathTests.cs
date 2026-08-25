using FluentAssertions;
using Kiln.Internal.H264;

namespace Kiln.Tests;

public sealed class H264KernelDispatchPathTests
{
    [Fact]
    public void SearchMb16x16_fractional_refine_uses_kernel_interpolate_luma()
    {
        const int stride = 64;
        const int h = 64;
        var reference = new byte[stride * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < stride; x++)
            {
                reference[y * stride + x] = (byte)((x * 3 + y * 5) & 0xFF);
            }
        }

        const int mbX = 16;
        const int mbY = 16;
        var current = new byte[16 * 16];
        for (var row = 0; row < 16; row++)
        {
            reference.AsSpan((mbY + row) * stride + mbX, 16).CopyTo(current.AsSpan(row * 16, 16));
        }

        var tracking = new TrackingKernelSet();
        _ = H264MotionEstimator.SearchMb16x16(
            current, 16,
            reference, stride,
            mbX, mbY,
            mvPredictor: default,
            searchRange: 8,
            useMotionSatd: true,
            kernels: tracking,
            pictureWidth: 0,
            pictureHeight: 0,
            fractionalPelRefinementRounds: 1,
            lambda: 0);

        tracking.InterpolateLumaCalls.Should().BeGreaterThan(0);
    }

    private sealed class TrackingKernelSet : IH264KernelSet
    {
        private readonly ScalarKernelSet _inner = new();
        public int InterpolateLumaCalls { get; private set; }

        public int Sad16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) => _inner.Sad16x16(a, strideA, b, strideB);
        public int Sad16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) => _inner.Sad16x8(a, strideA, b, strideB);
        public int Sad8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) => _inner.Sad8x16(a, strideA, b, strideB);
        public int Sad8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) => _inner.Sad8x8(a, strideA, b, strideB);
        public void SadMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count) => _inner.SadMany4x4(src, predConcat, sads, count);
        public int SadIntra16x16(ReadOnlySpan<byte> src256, ReadOnlySpan<byte> pred256, int srcStride) => _inner.SadIntra16x16(src256, pred256, srcStride);
        public int SadChromaPair(ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV, ReadOnlySpan<byte> predU, ReadOnlySpan<byte> predV) => _inner.SadChromaPair(srcU, srcV, predU, predV);
        public int Ssd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) => _inner.Ssd16x16(a, strideA, b, strideB);
        public int Ssd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) => _inner.Ssd8x8(a, strideA, b, strideB);
        public int VarianceMb16x16(ReadOnlySpan<byte> mbTopLeft, int stride) => _inner.VarianceMb16x16(mbTopLeft, stride);
        public int Satd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) => _inner.Satd16x16(a, strideA, b, strideB);
        public int Satd16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) => _inner.Satd16x8(a, strideA, b, strideB);
        public int Satd8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) => _inner.Satd8x16(a, strideA, b, strideB);
        public int Satd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) => _inner.Satd8x8(a, strideA, b, strideB);
        public void SatdMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> satds, int count) => _inner.SatdMany4x4(src, predConcat, satds, count);
        public void Predict4x4(int mode, ReadOnlySpan<byte> topRow, ReadOnlySpan<byte> leftCol, bool topAvail, bool leftAvail, Span<byte> dst16) => _inner.Predict4x4(mode, topRow, leftCol, topAvail, leftAvail, dst16);
        public void PredictIntra16x16(int mode, ReadOnlySpan<byte> topRow, bool topAvail, ReadOnlySpan<byte> leftCol, bool leftAvail, byte topLeft, bool topLeftAvail, Span<byte> dst256) => _inner.PredictIntra16x16(mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, dst256);
        public int EncodeResidual4x4(ReadOnlySpan<byte> src16, ReadOnlySpan<byte> pred16, int qp, Span<short> zigzagCoeffOut16, Span<byte> recDst, int recStride) => _inner.EncodeResidual4x4(src16, pred16, qp, zigzagCoeffOut16, recDst, recStride);
        public void ApplyDeblock(Span<byte> y, int strideY, Span<byte> u, Span<byte> v, int strideUv, int mbWidth, int mbHeight, ReadOnlySpan<byte> bsHorizontal, ReadOnlySpan<byte> bsVertical, ReadOnlySpan<int> qpY, ReadOnlySpan<int> qpUv, int alphaOffsetDiv2, int betaOffsetDiv2) => _inner.ApplyDeblock(y, strideY, u, v, strideUv, mbWidth, mbHeight, bsHorizontal, bsVertical, qpY, qpUv, alphaOffsetDiv2, betaOffsetDiv2);

        public void InterpolateLuma(ReadOnlySpan<byte> src, int srcStride, int srcOriginX, int srcOriginY, int xFrac, int yFrac, int blockWidth, int blockHeight, Span<byte> dst, int dstStride)
        {
            InterpolateLumaCalls++;
            _inner.InterpolateLuma(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac, blockWidth, blockHeight, dst, dstStride);
        }

        public void InterpolateChroma(ReadOnlySpan<byte> src, int srcStride, int srcOriginX, int srcOriginY, int xFrac, int yFrac, int blockWidth, int blockHeight, Span<byte> dst, int dstStride) => _inner.InterpolateChroma(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac, blockWidth, blockHeight, dst, dstStride);
        public void GatherSrcBlock4x4(ReadOnlySpan<byte> srcY, int baseOff, int strideY, Span<byte> dst16) => _inner.GatherSrcBlock4x4(srcY, baseOff, strideY, dst16);
        public void GatherChroma8x8(ReadOnlySpan<byte> src, int stride, int bx, int by, Span<byte> dst64) => _inner.GatherChroma8x8(src, stride, bx, by, dst64);
        public void ForwardDct4x4(ReadOnlySpan<short> residual4x4, Span<int> outCoeff) => _inner.ForwardDct4x4(residual4x4, outCoeff);
        public void Quant4x4(Span<int> block, int qp) => _inner.Quant4x4(block, qp);
    }
}

