using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Kiln.Internal.H264;

/// <summary>4×4 / 8×8 source-block gather helpers; tier kernel sets call the scalar or SIMD entry directly.</summary>
internal static class H264SrcGather
{
    internal static void GatherSrcBlock4x4Scalar(ReadOnlySpan<byte> srcY, int baseOff, int strideY, Span<byte> dst16)
    {
        var b0 = baseOff;
        var b1 = b0 + strideY;
        var b2 = b1 + strideY;
        var b3 = b2 + strideY;
        dst16[0] = srcY[b0]; dst16[1] = srcY[b0 + 1]; dst16[2] = srcY[b0 + 2]; dst16[3] = srcY[b0 + 3];
        dst16[4] = srcY[b1]; dst16[5] = srcY[b1 + 1]; dst16[6] = srcY[b1 + 2]; dst16[7] = srcY[b1 + 3];
        dst16[8] = srcY[b2]; dst16[9] = srcY[b2 + 1]; dst16[10] = srcY[b2 + 2]; dst16[11] = srcY[b2 + 3];
        dst16[12] = srcY[b3]; dst16[13] = srcY[b3 + 1]; dst16[14] = srcY[b3 + 2]; dst16[15] = srcY[b3 + 3];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void GatherSrcBlock4x4Simd(ReadOnlySpan<byte> srcY, int baseOff, int strideY, Span<byte> dst16)
    {
        ref var src = ref MemoryMarshal.GetReference(srcY);
        var r0 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, baseOff));
        var r1 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, baseOff + strideY));
        var r2 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, baseOff + 2 * strideY));
        var r3 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, baseOff + 3 * strideY));
        Vector128.Create(r0, r1, r2, r3).AsByte().StoreUnsafe(ref MemoryMarshal.GetReference(dst16));
    }

    internal static void GatherChroma8x8(ReadOnlySpan<byte> src, int stride, int bx, int by, Span<byte> dst64)
    {
        for (var y = 0; y < 8; y++)
        {
            src.Slice((by + y) * stride + bx, 8).CopyTo(dst64.Slice(y * 8, 8));
        }
    }
}
