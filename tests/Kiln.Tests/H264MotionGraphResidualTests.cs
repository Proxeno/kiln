using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

public sealed class H264MotionGraphResidualTests
{
    [Fact]
    public void Zero_residual_has_zero_components()
    {
        var a = new byte[16 * 16];
        var b = new byte[16 * 16];

        var cost = H264MotionGraphResidual.ComputeCost(
            a, 16, b, 16, width: 16, height: 16,
            out var sad, out var gradient, out var verticalMid, out var horizontalMid);

        cost.Should().Be(0);
        sad.Should().Be(0);
        gradient.Should().Be(0);
        verticalMid.Should().Be(0);
        horizontalMid.Should().Be(0);
    }

    [Fact]
    public void Constant_residual_has_sad_without_gradient()
    {
        var a = Enumerable.Repeat((byte)10, 16 * 16).ToArray();
        var b = Enumerable.Repeat((byte)5, 16 * 16).ToArray();

        var cost = H264MotionGraphResidual.ComputeCost(
            a, 16, b, 16, width: 16, height: 16,
            out var sad, out var gradient, out var verticalMid, out var horizontalMid);

        sad.Should().Be(16 * 16 * 5);
        gradient.Should().Be(0);
        verticalMid.Should().Be(0);
        horizontalMid.Should().Be(0);
        cost.Should().Be(sad);
    }

    [Fact]
    public void Checkerboard_residual_has_high_gradient()
    {
        var a = new byte[16 * 16];
        var b = new byte[16 * 16];
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            a[y * 16 + x] = ((x + y) & 1) == 0 ? (byte)255 : (byte)0;

        var cost = H264MotionGraphResidual.ComputeCost(
            a, 16, b, 16, width: 16, height: 16,
            out var sad, out var gradient, out var verticalMid, out var horizontalMid);

        sad.Should().Be(128 * 255);
        gradient.Should().Be(480 * 255);
        verticalMid.Should().Be(16 * 255);
        horizontalMid.Should().Be(16 * 255);
        cost.Should().Be(sad + (gradient >> 2));
    }

    [Fact]
    public void Single_impulse_has_localized_gradient()
    {
        var a = new byte[16 * 16];
        var b = new byte[16 * 16];
        a[6 * 16 + 5] = 255;

        var cost = H264MotionGraphResidual.ComputeCost(
            a, 16, b, 16, width: 16, height: 16,
            out var sad, out var gradient, out var verticalMid, out var horizontalMid);

        sad.Should().Be(255);
        gradient.Should().Be(4 * 255);
        verticalMid.Should().Be(0);
        horizontalMid.Should().Be(0);
        cost.Should().Be(sad + (gradient >> 2));
    }

    [Fact]
    public void Random_strided_block_matches_reference_implementation()
    {
        var rng = new Random(0x6772);
        const int strideA = 23;
        const int strideB = 29;
        var a = new byte[strideA * 20];
        var b = new byte[strideB * 20];
        rng.NextBytes(a);
        rng.NextBytes(b);

        var cost = H264MotionGraphResidual.ComputeCost(
            a.AsSpan(3 * strideA + 2), strideA,
            b.AsSpan(2 * strideB + 4), strideB,
            width: 16, height: 16,
            out var sad, out var gradient, out var verticalMid, out var horizontalMid);

        var expected = Reference(
            a.AsSpan(3 * strideA + 2), strideA,
            b.AsSpan(2 * strideB + 4), strideB,
            width: 16, height: 16);

        sad.Should().Be(expected.Sad);
        gradient.Should().Be(expected.Gradient);
        verticalMid.Should().Be(expected.VerticalMid);
        horizontalMid.Should().Be(expected.HorizontalMid);
        cost.Should().Be(expected.Cost);
    }

    private static (int Sad, int Gradient, int VerticalMid, int HorizontalMid, int Cost) Reference(
        ReadOnlySpan<byte> source,
        int sourceStride,
        ReadOnlySpan<byte> reference,
        int referenceStride,
        int width,
        int height)
    {
        var residuals = new int[width * height];
        var sad = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var r = source[y * sourceStride + x] - reference[y * referenceStride + x];
            residuals[y * width + x] = r;
            sad += Math.Abs(r);
        }

        var gradient = 0;
        var verticalMid = 0;
        var horizontalMid = 0;
        var midX = width >> 1;
        var midY = height >> 1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var r = residuals[y * width + x];
            if (x > 0)
            {
                var d = Math.Abs(r - residuals[y * width + x - 1]);
                gradient += d;
                if (x == midX)
                    verticalMid += d;
            }

            if (y > 0)
            {
                var d = Math.Abs(r - residuals[(y - 1) * width + x]);
                gradient += d;
                if (y == midY)
                    horizontalMid += d;
            }
        }

        return (sad, gradient, verticalMid, horizontalMid, sad + (gradient >> 2));
    }
}
