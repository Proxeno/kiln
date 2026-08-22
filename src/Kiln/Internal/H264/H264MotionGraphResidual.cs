using System.Runtime.CompilerServices;

namespace Kiln.Internal.H264;

/// <summary>Cheap residual-structure metric for motion-estimation diagnostics.</summary>
internal static class H264MotionGraphResidual
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeCost(
        ReadOnlySpan<byte> source,
        int sourceStride,
        ReadOnlySpan<byte> reference,
        int referenceStride,
        int width,
        int height,
        out int sad,
        out int residualGradient,
        out int verticalMidBoundary,
        out int horizontalMidBoundary)
    {
        sad = 0;
        residualGradient = 0;
        verticalMidBoundary = 0;
        horizontalMidBoundary = 0;

        Span<int> prevRowResiduals = stackalloc int[16];
        var midX = width >> 1;
        var midY = height >> 1;

        for (var y = 0; y < height; y++)
        {
            var srcRow = y * sourceStride;
            var refRow = y * referenceStride;
            var prevResidual = 0;
            for (var x = 0; x < width; x++)
            {
                var residual = source[srcRow + x] - reference[refRow + x];
                sad += Math.Abs(residual);

                if (x > 0)
                {
                    var horizontalDelta = Math.Abs(residual - prevResidual);
                    residualGradient += horizontalDelta;
                    if (x == midX)
                        verticalMidBoundary += horizontalDelta;
                }

                if (y > 0)
                {
                    var verticalDelta = Math.Abs(residual - prevRowResiduals[x]);
                    residualGradient += verticalDelta;
                    if (y == midY)
                        horizontalMidBoundary += verticalDelta;
                }

                prevResidual = residual;
                prevRowResiduals[x] = residual;
            }
        }

        return sad + (residualGradient >> 2);
    }
}
