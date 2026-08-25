using System.Threading;

namespace Kiln.Internal.H264;

/// <summary>
/// Frame-scoped cache of 4x4 Hadamard coefficients for integer-pel reference blocks.
/// </summary>
internal sealed class H264ReferenceTransformAtlas
{
    private readonly int[] _valid;
    private readonly short[] _coefficients;

    /// <summary>
    /// Frame epoch a <see cref="_valid"/> slot must match to count as populated. Incremented by
    /// <see cref="Reset"/> instead of clearing <see cref="_valid"/>: the valid array is 4 bytes per
    /// reference sample (~8.7 MB at 1080p), so an <see cref="Array.Clear(Array)"/> per frame per DPB
    /// slot was ~17.5 MB of pure memset traffic per encoded 1080p frame. Mutated only outside the
    /// parallel slice region (same fence as the DPB rotation that calls <see cref="Reset"/>).
    /// </summary>
    private int _epoch = 1;

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

    /// <summary>
    /// Invalidate every cached 4x4 by advancing the epoch (O(1)); stale slots simply fail the
    /// epoch comparison in <see cref="GetOrCompute"/>. On the (practically unreachable) epoch
    /// wraparound, fall back to a real clear so a slot stamped epochs ago can never alias.
    /// </summary>
    public void Reset()
    {
        if (_epoch == int.MaxValue)
        {
            Array.Clear(_valid);
            _epoch = 1;
            return;
        }

        _epoch++;
    }

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

        // A slot is populated only when stamped with the current epoch; anything else is stale
        // (left over from a frame before the last Reset) and recomputed. Volatile pairs the
        // coefficient write with the stamp so concurrent slice encoders never read a half-written
        // entry: the stamp is written after the coefficients, and readers check the stamp first.
        var epoch = _epoch;
        if (Volatile.Read(ref _valid[slot]) == epoch)
        {
            if (collectDiagnostics)
                H264MotionSatdDagDiagnostics.NotifyRefTransformCacheHit();
            return coefficients;
        }

        if (collectDiagnostics)
            H264MotionSatdDagDiagnostics.NotifyRefTransformCacheMissCompute();
        H264MotionSatd.Transform4x4Strided(reference, referenceStride, x, y, coefficients);
        Volatile.Write(ref _valid[slot], epoch);
        return coefficients;
    }
}
