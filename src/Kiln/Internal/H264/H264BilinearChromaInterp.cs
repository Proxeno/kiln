using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>
/// Chroma 1/8-pel bilinear sub-pixel interpolation per H.264 8.4.2.2.2.
/// </summary>
/// <remarks>
/// 4:2:0 chroma sample interpolation uses bilinear weighting on the four neighbouring integer pels;
/// the rounding (+32) and &gt;&gt; 6 are normative (8.4.2.2.2).
/// </remarks>
internal static class H264BilinearChromaInterp
{
    /// <summary>True when at least one SIMD ISA (SSE4.1 or AdvSimd) is available.</summary>
    public static bool IsSimdSupported => Sse41.IsSupported || AdvSimd.IsSupported;

    /// <summary>
    /// SIMD-accelerated chroma bilinear interpolation. Byte-exact with <see cref="Interpolate"/> for all
    /// (xFrac, yFrac) combinations. Falls back to the scalar path on ISAs without SSE4.1 or AdvSimd.
    /// </summary>
    public static void InterpolateSimd(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride)
    {
        if (!IsSimdSupported)
        {
            Interpolate(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac,
                        blockWidth, blockHeight, dst, dstStride);
            return;
        }

        if (xFrac == 0 && yFrac == 0)
        {
            for (var i = 0; i < blockHeight; i++)
                src.Slice((srcOriginY + i) * srcStride + srcOriginX, blockWidth)
                   .CopyTo(dst.Slice(i * dstStride));
            return;
        }

        // Wide SIMD loads touch up to 5 bytes per row; use scalar Interpolate if padding would overrun.
        if (!SimdWindowFits(src, srcStride, srcOriginX, srcOriginY, blockWidth, blockHeight))
        {
            Interpolate(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac,
                blockWidth, blockHeight, dst, dstStride);
            return;
        }
        // Each output: (w0*A + w1*B + w2*C + w3*D + 32) >> 6
        // where A=top-left, B=top-right, C=bottom-left, D=bottom-right
        var invX = 8 - xFrac;
        var invY = 8 - yFrac;
        // Weights fit in byte (0..64); products fit in uint16 (max 64*255=16320 < 32768).
        var w0 = invX * invY;   // A
        var w1 = xFrac * invY;  // B
        var w2 = invX * yFrac;  // C
        var w3 = xFrac * yFrac; // D

        var vw0 = Vector128.Create(w0);
        var vw1 = Vector128.Create(w1);
        var vw2 = Vector128.Create(w2);
        var vw3 = Vector128.Create(w3);
        var vrnd = Vector128.Create(32);
        var vzero = Vector128<int>.Zero;
        var vmax = Vector128.Create(255);

        for (var i = 0; i < blockHeight; i++)
        {
            var topBase   = (srcOriginY + i)     * srcStride + srcOriginX;
            var botBase   = (srcOriginY + i + 1) * srcStride + srcOriginX;
            var dstRow    = dst.Slice(i * dstStride);
            var j         = 0;

            // Process 4 samples per iteration using Vector128<int> per the plan.
            for (; j + 4 <= blockWidth; j += 4)
            {
                // Load 5 consecutive bytes from top/bottom rows, form A,B,C,D vectors of 4 int32 each.
                var topVec = Load5AsInt32x4(src, topBase + j); // A[0..3] in .GetElement(0..3)
                var botVec = Load5AsInt32x4(src, botBase + j); // C[0..3]
                // B = A shifted by 1 (topVec shifted), D = C shifted by 1 (botVec shifted)
                var bVec = Load5AsInt32x4_Offset1(src, topBase + j);
                var dVec = Load5AsInt32x4_Offset1(src, botBase + j);

                var acc = vw0 * topVec + vw1 * bVec + vw2 * botVec + vw3 * dVec + vrnd;
                acc = Vector128.ShiftRightArithmetic(acc, 6);
                acc = Vector128.Min(Vector128.Max(acc, vzero), vmax);
                if (Sse2.IsSupported)
                {
                    var i16 = Sse2.PackSignedSaturate(acc, Vector128<int>.Zero);
                    var u8 = Sse2.PackUnsignedSaturate(i16, Vector128<short>.Zero);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(dstRow), j), u8.AsUInt32().ToScalar());
                }
                else
                {
                    ref var dr = ref MemoryMarshal.GetReference(dstRow);
                    Unsafe.Add(ref dr, j + 0) = (byte)acc.GetElement(0);
                    Unsafe.Add(ref dr, j + 1) = (byte)acc.GetElement(1);
                    Unsafe.Add(ref dr, j + 2) = (byte)acc.GetElement(2);
                    Unsafe.Add(ref dr, j + 3) = (byte)acc.GetElement(3);
                }
            }

            for (; j < blockWidth; j++)
            {
                var x = srcOriginX + j;
                var y = srcOriginY + i;
                var a = src[y * srcStride + x];
                var b = src[y * srcStride + (x + 1)];
                var c = src[(y + 1) * srcStride + x];
                var d = src[(y + 1) * srcStride + (x + 1)];
                dstRow[j] = (byte)((w0 * a + w1 * b + w2 * c + w3 * d + 32) >> 6);
            }
        }
    }

    /// <summary>True if 4-wide SIMD bilinear loads stay inside <paramref name="src"/>.</summary>
    private static bool SimdWindowFits(ReadOnlySpan<byte> src, int srcStride, int ox, int oy, int bw, int bh)
    {
        if (ox < 0 || oy < 0 || bw <= 0 || bh <= 0)
            return false;
        var len = (long)src.Length;
        // Inner loop uses offsets topBase+j, topBase+j+4 on each row (last group j = bw-4 .. bw-1).
        // Load5AsInt32x4 reads 16 bytes on SSE / 8 bytes on NEON — guard the full SIMD load width.
        var simdLoadBytes = Sse41.IsSupported ? 16 : 8;
        var maxOffset = (long)(oy + bh) * srcStride + ox + bw - 4 + simdLoadBytes;
        return maxOffset <= len;
    }

    // ─── SIMD helpers ─────────────────────────────────────────────────────────

    /// <summary>Loads bytes at <paramref name="offset"/>..[+3] and zero-extends to 4 int32 values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Load5AsInt32x4(ReadOnlySpan<byte> src, int offset)
    {
        if (Sse41.IsSupported)
        {
            var v = Vector128.LoadUnsafe(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(src.Slice(offset)));
            return Sse41.ConvertToVector128Int32(v);
        }
        // AdvSimd: load 8 bytes as Vector64<byte>, widen to uint16, widen lower 4 to uint32
        var b8 = Vector64.LoadUnsafe(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(src.Slice(offset)));
        var w16 = AdvSimd.ShiftLeftLogicalWideningLower(b8, 0); // Vector128<ushort>
        return AdvSimd.ShiftLeftLogicalWideningLower(w16.GetLower(), 0).AsInt32(); // Vector128<uint> → int32
    }

    /// <summary>Loads bytes at <paramref name="offset"/>+1..[+4] and zero-extends to 4 int32 values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Load5AsInt32x4_Offset1(ReadOnlySpan<byte> src, int offset)
        => Load5AsInt32x4(src, offset + 1);


    /// <summary>Interpolate one chroma block at fractional position (xFrac, yFrac) in 1/8 units.</summary>
    /// <param name="src">Reference patch with the integer-pel block plus a 1-pel right/bottom halo, row-major.</param>
    /// <param name="srcStride">Row stride of <paramref name="src"/>.</param>
    /// <param name="srcOriginX">Top-left x in <paramref name="src"/> where the integer-pel block begins.</param>
    /// <param name="srcOriginY">Top-left y in <paramref name="src"/> where the integer-pel block begins.</param>
    /// <param name="xFrac">Fractional x in 1/8-pel units (0..7).</param>
    /// <param name="yFrac">Fractional y in 1/8-pel units (0..7).</param>
    /// <param name="blockWidth">Block width in samples (4 or 8).</param>
    /// <param name="blockHeight">Block height in samples (4 or 8).</param>
    /// <param name="dst">Destination buffer, row-major.</param>
    /// <param name="dstStride">Row stride of <paramref name="dst"/>.</param>
    public static void Interpolate(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride)
    {
        if (xFrac == 0 && yFrac == 0)
        {
            for (var i = 0; i < blockHeight; i++)
            {
                for (var j = 0; j < blockWidth; j++)
                {
                    dst[i * dstStride + j] = src[(srcOriginY + i) * srcStride + srcOriginX + j];
                }
            }

            return;
        }

        var invX = 8 - xFrac;
        var invY = 8 - yFrac;

        for (var i = 0; i < blockHeight; i++)
        {
            for (var j = 0; j < blockWidth; j++)
            {
                var x = srcOriginX + j;
                var y = srcOriginY + i;
                var a = src[y * srcStride + x];
                var b = src[y * srcStride + (x + 1)];
                var c = src[(y + 1) * srcStride + x];
                var d = src[(y + 1) * srcStride + (x + 1)];
                var pred = invX * invY * a
                    + xFrac * invY * b
                    + invX * yFrac * c
                    + xFrac * yFrac * d;
                dst[i * dstStride + j] = (byte)((pred + 32) >> 6);
            }
        }
    }
}
