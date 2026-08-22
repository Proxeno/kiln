using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>
/// SIMD 8×8 SAD over a (U, V) chroma plane pair, used by intra chroma mode RDO (H.264 8.3.4).
/// Replaces the per-pixel scalar reduction in <c>H264BaselineSliceEncoder.ChooseChromaIntraMode</c>
/// with a single accumulating-vector pass + one horizontal reduce — i.e. the senior parent's
/// "accumulate vector partials across rows, reduce once at end" pattern, applied to the chroma
/// kernel that's the next-most-frequent SAD callsite after J1's <see cref="H264Intra4X4Simd.SadU8x16"/>.
/// </summary>
internal static class H264ChromaSadSimd
{
    public static bool IsSupported => Sse2.IsSupported || AdvSimd.IsSupported;

    /// <summary>
    /// Sum of |srcU[i] − predU[i]| + |srcV[i] − predV[i]| across the 8×8 region. Bit-exact against
    /// the per-pixel scalar reduction in <c>ChooseChromaIntraMode</c>: each input is 64 contiguous
    /// bytes laid out row-major 8×8 (matching the slice encoder's stack-allocated pred8x8 / per-row
    /// src slice). Pack U and V from the same row into one Vector128&lt;byte&gt; (lower 8 lanes = U,
    /// upper 8 lanes = V) so a single SAD-class instruction (PSADBW on x86, UABD on NEON) covers
    /// both planes per iteration.
    /// </summary>
    internal static int SadU8x8PairSsse2(
        ReadOnlySpan<byte> srcU,
        ReadOnlySpan<byte> srcV,
        ReadOnlySpan<byte> predU,
        ReadOnlySpan<byte> predV) =>
        SadSse2(srcU, srcV, predU, predV);

    internal static int SadU8x8PairAdvSimd(
        ReadOnlySpan<byte> srcU,
        ReadOnlySpan<byte> srcV,
        ReadOnlySpan<byte> predU,
        ReadOnlySpan<byte> predV) =>
        SadNeon(srcU, srcV, predU, predV);

    public static int SadU8x8Pair(
        ReadOnlySpan<byte> srcU,
        ReadOnlySpan<byte> srcV,
        ReadOnlySpan<byte> predU,
        ReadOnlySpan<byte> predV)
    {
        if (srcU.Length < 64 || srcV.Length < 64 || predU.Length < 64 || predV.Length < 64)
        {
            throw new ArgumentException("Chroma 8×8 SAD requires 64-byte spans for each plane.");
        }

        if (Sse2.IsSupported)
        {
            return SadSse2(srcU, srcV, predU, predV);
        }

        if (AdvSimd.IsSupported)
        {
            return SadNeon(srcU, srcV, predU, predV);
        }

        return SadScalar(srcU, srcV, predU, predV);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SadSse2(
        ReadOnlySpan<byte> srcU,
        ReadOnlySpan<byte> srcV,
        ReadOnlySpan<byte> predU,
        ReadOnlySpan<byte> predV)
    {
        ref var su = ref MemoryMarshal.GetReference(srcU);
        ref var sv = ref MemoryMarshal.GetReference(srcV);
        ref var pu = ref MemoryMarshal.GetReference(predU);
        ref var pv = ref MemoryMarshal.GetReference(predV);

        // Sse2.SumAbsoluteDifferences returns Vector128<ushort> with two valid partials in lanes 0
        // (lower 8 bytes) and 4 (upper 8 bytes); adds across rows accumulate into the same lanes.
        var acc = Vector128<ushort>.Zero;
        for (var y = 0; y < 8; y++)
        {
            var off = y * 8;
            var s = Vector128.Create(
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref su, off)),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref sv, off))).AsByte();
            var p = Vector128.Create(
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pu, off)),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pv, off))).AsByte();
            acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(s, p));
        }

        // Per-row partial ≤ 8·255 = 2040; 8 rows ≤ 16320 — fits in ushort with headroom.
        return acc.GetElement(0) + acc.GetElement(4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SadNeon(
        ReadOnlySpan<byte> srcU,
        ReadOnlySpan<byte> srcV,
        ReadOnlySpan<byte> predU,
        ReadOnlySpan<byte> predV)
    {
        ref var su = ref MemoryMarshal.GetReference(srcU);
        ref var sv = ref MemoryMarshal.GetReference(srcV);
        ref var pu = ref MemoryMarshal.GetReference(predU);
        ref var pv = ref MemoryMarshal.GetReference(predV);

        // 8 ushort lanes accumulate one (lower-half + upper-half) byte-diff per row across 8 rows;
        // each lane max = 16·255 = 4080; total max = 8·4080 = 32640 — within ushort.
        var acc = Vector128<ushort>.Zero;
        for (var y = 0; y < 8; y++)
        {
            var off = y * 8;
            var s = Vector128.Create(
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref su, off)),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref sv, off))).AsByte();
            var p = Vector128.Create(
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pu, off)),
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pv, off))).AsByte();
            var diff = AdvSimd.AbsoluteDifference(s, p);
            acc = Vector128.Add(acc, Vector128.WidenLower(diff));
            acc = Vector128.Add(acc, Vector128.WidenUpper(diff));
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.Arm64.AddAcross(acc).ToScalar();
        }

        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            sum += acc.GetElement(i);
        }

        return sum;
    }

    private static int SadScalar(
        ReadOnlySpan<byte> srcU,
        ReadOnlySpan<byte> srcV,
        ReadOnlySpan<byte> predU,
        ReadOnlySpan<byte> predV)
    {
        var sad = 0;
        for (var y = 0; y < 8; y++)
        {
            var off = y * 8;
            for (var x = 0; x < 8; x++)
            {
                sad += Math.Abs(srcU[off + x] - predU[off + x]);
                sad += Math.Abs(srcV[off + x] - predV[off + x]);
            }
        }

        return sad;
    }
}
