using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>16-byte SAD for intra 4×4 mode search (SSSE3 psadbw / NEON abs-diff).</summary>
internal static class H264Intra4X4Simd
{
    public static bool IsSupported => Ssse3.IsSupported || AdvSimd.IsSupported;

    public static int SadU8x16(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length < 16 || b.Length < 16)
        {
            throw new ArgumentException("Spans must hold at least 16 bytes.");
        }

        ref var ra = ref MemoryMarshal.GetReference(a);
        ref var rb = ref MemoryMarshal.GetReference(b);
        if (Ssse3.IsSupported)
        {
            var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref ra);
            var vb = Unsafe.ReadUnaligned<Vector128<byte>>(ref rb);
            var s = Ssse3.SumAbsoluteDifferences(va, vb).AsUInt64();
            return (int)(s.GetElement(0) + s.GetElement(1));
        }

        if (AdvSimd.IsSupported)
        {
            var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref MemoryMarshal.GetReference(a));
            var vb = Unsafe.ReadUnaligned<Vector128<byte>>(ref MemoryMarshal.GetReference(b));
            if (AdvSimd.Arm64.IsSupported)
            {
                // Byte AddAcross uses ADDV; sum wraps at 256. AddAcrossWidening is vaddlvq_u8 (sums into ushort).
                var ad = AdvSimd.AbsoluteDifference(va, vb);
                return AdvSimd.Arm64.AddAcrossWidening(ad).ToScalar();
            }

            return SadScalar(ref ra, ref rb);
        }

        return SadScalar(ref ra, ref rb);
    }

    /// <summary>
    /// Batched 4×4 SAD: load the 16-byte source block into one register, then for each of
    /// <paramref name="count"/> contiguous 16-byte candidate predictions in
    /// <paramref name="predConcat"/>, compute the SAD against the shared source and write it into
    /// <paramref name="sads"/>. Hoisting the source load out of the per-candidate loop turns the
    /// 9-mode intra-4x4 RDO inner loop into 1 src load + N pred loads instead of 2N loads.
    /// </summary>
    internal static void SadManySsse3(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count)
    {
        if (count == 0)
        {
            return;
        }

        ref var rsrc = ref MemoryMarshal.GetReference(src);
        ref var rpred = ref MemoryMarshal.GetReference(predConcat);
        var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref rsrc);
        for (var i = 0; i < count; i++)
        {
            var vb = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rpred, i * 16));
            var s = Ssse3.SumAbsoluteDifferences(va, vb).AsUInt64();
            sads[i] = (int)(s.GetElement(0) + s.GetElement(1));
        }
    }

    internal static void SadManyAdvSimdArm64(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count)
    {
        if (count == 0)
        {
            return;
        }

        ref var rsrc = ref MemoryMarshal.GetReference(src);
        ref var rpred = ref MemoryMarshal.GetReference(predConcat);
        var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref rsrc);
        for (var i = 0; i < count; i++)
        {
            var vb = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rpred, i * 16));
            var diff = AdvSimd.AbsoluteDifference(va, vb);
            sads[i] = (int)(AdvSimd.Arm64.AddAcrossWidening(diff).ToScalar());
        }
    }

    internal static void SadManyAdvSimd(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count)
    {
        if (count == 0)
        {
            return;
        }

        ref var rsrc = ref MemoryMarshal.GetReference(src);
        ref var rpred = ref MemoryMarshal.GetReference(predConcat);
        for (var i = 0; i < count; i++)
        {
            sads[i] = SadScalar(ref rsrc, ref Unsafe.Add(ref rpred, i * 16));
        }
    }

    public static void SadManyU8x16(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count)
    {
        if (count == 0)
        {
            return;
        }

        if (src.Length < 16 || predConcat.Length < count * 16 || sads.Length < count)
        {
            throw new ArgumentException("Span length contract violated.");
        }

        ref var rsrc = ref MemoryMarshal.GetReference(src);
        ref var rpred = ref MemoryMarshal.GetReference(predConcat);

        if (Ssse3.IsSupported)
        {
            var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref rsrc);
            for (var i = 0; i < count; i++)
            {
                var vb = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rpred, i * 16));
                var s = Ssse3.SumAbsoluteDifferences(va, vb).AsUInt64();
                sads[i] = (int)(s.GetElement(0) + s.GetElement(1));
            }

            return;
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref rsrc);
            for (var i = 0; i < count; i++)
            {
                var vb = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rpred, i * 16));
                var ad = AdvSimd.AbsoluteDifference(va, vb);
                sads[i] = (int)AdvSimd.Arm64.AddAcrossWidening(ad).ToScalar();
            }

            return;
        }

        for (var i = 0; i < count; i++)
        {
            sads[i] = SadScalar(ref rsrc, ref Unsafe.Add(ref rpred, i * 16));
        }
    }

    private static int SadScalar(ref byte ra, ref byte rb)
    {
        var s = 0;
        for (var i = 0; i < 16; i++)
        {
            s += Math.Abs(Unsafe.Add(ref ra, i) - Unsafe.Add(ref rb, i));
        }

        return s;
    }
}
