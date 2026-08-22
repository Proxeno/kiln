using System.Threading;

namespace Kiln.Internal.H264;

/// <summary>
/// Frame-scoped cache of 4x4 Hadamard coefficients for integer-pel reference blocks.
/// </summary>
internal sealed class H264ReferenceTransformAtlas
{
    private readonly int[] _valid;
    private readonly short[] _coefficients;

    public H264ReferenceTransformAtlas(int stride, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 4);

        Stride = stride;
        Height = height;
        _valid = new int[checked(stride * height)];
        _coefficients = new short[checked(stride * height * H264MotionSatd.Transform4x4CoefficientCount)];
    }

    public int Stride { get; }

    public int Height { get; }

    public void Reset() => Array.Clear(_valid);

    public bool Contains4x4(int x, int y) =>
        (uint)x <= (uint)(Stride - 4) &&
        (uint)y <= (uint)(Height - 4);

    public ReadOnlySpan<short> GetOrCompute(
        ReadOnlySpan<byte> reference,
        int referenceStride,
        int x,
        int y,
        bool collectDiagnostics)
    {
        if (referenceStride != Stride)
        {
            throw new ArgumentException("Reference stride does not match the transform atlas stride.", nameof(referenceStride));
        }

        if (!Contains4x4(x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Reference 4x4 top-left is outside the transform atlas.");
        }

        var slot = y * Stride + x;
        var coefficientOffset = slot * H264MotionSatd.Transform4x4CoefficientCount;
        var coefficients = _coefficients.AsSpan(
            coefficientOffset,
            H264MotionSatd.Transform4x4CoefficientCount);

        if (Volatile.Read(ref _valid[slot]) != 0)
        {
            if (collectDiagnostics)
                H264MotionSatdDagDiagnostics.NotifyRefTransformCacheHit();
            return coefficients;
        }

        if (collectDiagnostics)
            H264MotionSatdDagDiagnostics.NotifyRefTransformCacheMissCompute();
        H264MotionSatd.Transform4x4Strided(reference, referenceStride, x, y, coefficients);
        Volatile.Write(ref _valid[slot], 1);
        return coefficients;
    }
}
