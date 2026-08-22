namespace Kiln.Tests;

/// <summary>Shared reference helpers for motion SAD parity tests.</summary>
internal static class MotionSadTestHelpers
{
    internal static int NaiveSad(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b, int strideB,
        int width, int height)
    {
        var s = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                s += Math.Abs(a[y * strideA + x] - b[y * strideB + x]);
            }
        }

        return s;
    }

    internal static (byte[] a, byte[] b) RandomPair(Random rng, int strideA, int rowsA, int strideB, int rowsB)
    {
        var a = new byte[strideA * rowsA];
        var b = new byte[strideB * rowsB];
        rng.NextBytes(a);
        rng.NextBytes(b);
        return (a, b);
    }
}
