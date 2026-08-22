using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>
/// Luma quarter-pel sub-pixel interpolation per H.264 8.4.2.2.1 (six-tap (1, -5, 20, 20, -5, 1) +
/// bilinear quarter-pel blend).
/// </summary>
/// <remarks>
/// Caller fills <paramref name="src"/> with a properly-padded reference patch (border replication per
/// 8.4.2.1 already applied); the interpolator does no border handling itself. For the centre half-pel
/// (2,2), the second 6-tap pass must consume unclipped 16-bit horizontal intermediates (8.4.2.2.1).
/// </remarks>
internal static class H264QpelLumaInterp
{
    private static readonly Vector128<short> TapMinus5 = Vector128.Create((short)(-5));
    private static readonly Vector128<short> Tap20 = Vector128.Create((short)20);

    /// <summary>True when at least one SIMD ISA (SSE4.1 or AdvSimd) is available.</summary>
    public static bool IsSimdSupported => Sse41.IsSupported || AdvSimd.IsSupported;

    /// <summary>
    /// SIMD-accelerated luma quarter-pel interpolation. Byte-exact with <see cref="Interpolate"/>
    /// for all fractional positions. Falls back to the scalar path on ISAs without SSE4.1 or AdvSimd.
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

        switch ((xFrac, yFrac))
        {
            case (0, 0):
                SimdCopyBlock(src, srcStride, srcOriginX, srcOriginY, blockWidth, blockHeight, dst, dstStride);
                break;
            case (2, 0):
                SimdH1Block(src, srcStride, srcOriginX, srcOriginY, blockWidth, blockHeight, dst, dstStride);
                break;
            case (0, 2):
                SimdV1Block(src, srcStride, srcOriginX, srcOriginY, blockWidth, blockHeight, dst, dstStride);
                break;
            case (2, 2):
                SimdHvBlock(src, srcStride, srcOriginX, srcOriginY, blockWidth, blockHeight, dst, dstStride);
                break;
            default:
                SimdBlendBlock(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac,
                               blockWidth, blockHeight, dst, dstStride);
                break;
        }
    }

    // ─── SIMD kernels ─────────────────────────────────────────────────────────

    private static void SimdCopyBlock(
        ReadOnlySpan<byte> src, int srcStride,
        int ox, int oy, int bw, int bh,
        Span<byte> dst, int dstStride)
    {
        for (var i = 0; i < bh; i++)
            src.Slice((oy + i) * srcStride + ox, bw).CopyTo(dst.Slice(i * dstStride));
    }

    /// <summary>Horizontal half-pel: H1 = clip((tap6_h + 16) >> 5).</summary>
    private static void SimdH1Block(
        ReadOnlySpan<byte> src, int srcStride,
        int ox, int oy, int bw, int bh,
        Span<byte> dst, int dstStride)
    {
        for (var i = 0; i < bh; i++)
        {
            var rowBase = (oy + i) * srcStride;
            var dstRow = dst.Slice(i * dstStride);
            var j = 0;
            for (; j + 8 <= bw; j += 8)
            {
                var tap = HorizRawFilter8(src, rowBase, ox + j);
                // (tap + 16) >> 5, clip 0..255
                tap = Vector128.ShiftRightArithmetic(Vector128.Add(tap, Vector128.Create((short)16)), 5);
                PackAndStore8(tap, dstRow, j);
            }
            for (; j < bw; j++)
                dstRow[j] = (byte)Clip255((LumaH(src, srcStride, ox + j, oy + i) + 16) >> 5);
        }
    }

    /// <summary>Vertical half-pel: V1 = clip((tap6_v + 16) >> 5).</summary>
    private static void SimdV1Block(
        ReadOnlySpan<byte> src, int srcStride,
        int ox, int oy, int bw, int bh,
        Span<byte> dst, int dstStride)
    {
        for (var i = 0; i < bh; i++)
        {
            var dstRow = dst.Slice(i * dstStride);
            var y = oy + i;
            var j = 0;
            for (; j + 8 <= bw; j += 8)
            {
                var tap = VertRawFilter8(src, srcStride, ox + j, y);
                tap = Vector128.ShiftRightArithmetic(Vector128.Add(tap, Vector128.Create((short)16)), 5);
                PackAndStore8(tap, dstRow, j);
            }
            for (; j < bw; j++)
                dstRow[j] = (byte)Clip255((LumaV(src, srcStride, ox + j, y) + 16) >> 5);
        }
    }

    /// <summary>
    /// Diagonal half-pel: Hv — horizontal 6-tap (raw int16), then vertical 6-tap (int32), (+512)>>10, clip.
    /// </summary>
    private static void SimdHvBlock(
        ReadOnlySpan<byte> src, int srcStride,
        int ox, int oy, int bw, int bh,
        Span<byte> dst, int dstStride)
    {
        // Pre-compute horizontal raw intermediates for rows (oy-2)..(oy+bh+2), width = bw
        var horizRows = bh + 5; // rows y-2..y+bh-1+3  →  5 extra rows (2 above + 3 below)
        Span<short> horizBuf = horizRows * bw <= 1024
            ? stackalloc short[1024]
            : new short[horizRows * bw];
        horizBuf = horizBuf[..(horizRows * bw)];

        for (var r = 0; r < horizRows; r++)
        {
            var srcY = oy - 2 + r;
            var rowBase = srcY * srcStride;
            var outRow = horizBuf.Slice(r * bw);
            var j = 0;
            for (; j + 8 <= bw; j += 8)
            {
                var tap = HorizRawFilter8(src, rowBase, ox + j);
                // Store raw 16-bit taps (no clip/shift yet)
                tap.StoreUnsafe(ref outRow[j]);
            }
            for (; j < bw; j++)
                outRow[j] = (short)LumaH(src, srcStride, ox + j, srcY);
        }

        // Vertical 6-tap pass on int16 intermediates → int32 result, (+512)>>10, clip
        var vz = Vector128<int>.Zero;
        var vhi = Vector128.Create(255);
        for (var i = 0; i < bh; i++)
        {
            var dstRow = dst.Slice(i * dstStride);
            // Row index within horizBuf: (y-2) is at r=0, so output row i corresponds to r=i+2
            var j = 0;
            for (; j + 4 <= bw; j += 4)
            {
                // Load 6 rows of 4 int16 intermediates, widen to int32, apply 6-tap
                var h0 = WidenLow4(horizBuf, (i + 0) * bw + j);
                var h1 = WidenLow4(horizBuf, (i + 1) * bw + j);
                var h2 = WidenLow4(horizBuf, (i + 2) * bw + j);
                var h3 = WidenLow4(horizBuf, (i + 3) * bw + j);
                var h4 = WidenLow4(horizBuf, (i + 4) * bw + j);
                var h5 = WidenLow4(horizBuf, (i + 5) * bw + j);
                var acc = Tap6Int32(h0, h1, h2, h3, h4, h5);
                acc = Vector128.ShiftRightArithmetic(Vector128.Add(acc, Vector128.Create(512)), 10);
                acc = Vector128.Min(Vector128.Max(acc, vz), vhi);
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
            for (; j < bw; j++)
            {
                var v = Tap6(horizBuf[(i + 0) * bw + j], horizBuf[(i + 1) * bw + j],
                             horizBuf[(i + 2) * bw + j], horizBuf[(i + 3) * bw + j],
                             horizBuf[(i + 4) * bw + j], horizBuf[(i + 5) * bw + j]);
                dstRow[j] = (byte)Clip255((v + 512) >> 10);
            }
        }
    }

    /// <summary>
    /// Quarter-pel blend: compute the two contributing half-pel (or integer-pel) buffers and blend
    /// byte-wise via (A + B + 1) >> 1.
    /// </summary>
    private static void SimdBlendBlock(
        ReadOnlySpan<byte> src, int srcStride,
        int ox, int oy, int xFrac, int yFrac,
        int bw, int bh,
        Span<byte> dst, int dstStride)
    {
        var tempSize = bw * bh;
        Span<byte> bufA = tempSize <= 512 ? stackalloc byte[512] : new byte[tempSize];
        Span<byte> bufB = tempSize <= 512 ? stackalloc byte[512] : new byte[tempSize];
        bufA = bufA[..tempSize];
        bufB = bufB[..tempSize];

        // Determine sources A and B per H.264 8.4.2.2.1 switch table
        switch ((xFrac, yFrac))
        {
            case (1, 0): // (G(x,y)      + H1(x,y)) >> 1
                SimdCopyBlock(src, srcStride, ox,     oy,     bw, bh, bufA, bw);
                SimdH1Block  (src, srcStride, ox,     oy,     bw, bh, bufB, bw); break;
            case (3, 0): // (G(x+1,y)    + H1(x,y)) >> 1
                SimdCopyBlock(src, srcStride, ox + 1, oy,     bw, bh, bufA, bw);
                SimdH1Block  (src, srcStride, ox,     oy,     bw, bh, bufB, bw); break;
            case (0, 1): // (G(x,y)      + V1(x,y)) >> 1
                SimdCopyBlock(src, srcStride, ox,     oy,     bw, bh, bufA, bw);
                SimdV1Block  (src, srcStride, ox,     oy,     bw, bh, bufB, bw); break;
            case (0, 3): // (G(x,y+1)    + V1(x,y)) >> 1
                SimdCopyBlock(src, srcStride, ox,     oy + 1, bw, bh, bufA, bw);
                SimdV1Block  (src, srcStride, ox,     oy,     bw, bh, bufB, bw); break;
            case (1, 1): // (H1(x,y)     + V1(x,y)) >> 1
                SimdH1Block  (src, srcStride, ox,     oy,     bw, bh, bufA, bw);
                SimdV1Block  (src, srcStride, ox,     oy,     bw, bh, bufB, bw); break;
            case (2, 1): // (H1(x,y)     + Hv(x,y)) >> 1
                SimdH1Block  (src, srcStride, ox,     oy,     bw, bh, bufA, bw);
                SimdHvBlock  (src, srcStride, ox,     oy,     bw, bh, bufB, bw); break;
            case (3, 1): // (H1(x,y)     + V1(x+1,y)) >> 1
                SimdH1Block  (src, srcStride, ox,     oy,     bw, bh, bufA, bw);
                SimdV1Block  (src, srcStride, ox + 1, oy,     bw, bh, bufB, bw); break;
            case (1, 2): // (V1(x,y)     + Hv(x,y)) >> 1
                SimdV1Block  (src, srcStride, ox,     oy,     bw, bh, bufA, bw);
                SimdHvBlock  (src, srcStride, ox,     oy,     bw, bh, bufB, bw); break;
            case (3, 2): // (V1(x+1,y)   + Hv(x,y)) >> 1
                SimdV1Block  (src, srcStride, ox + 1, oy,     bw, bh, bufA, bw);
                SimdHvBlock  (src, srcStride, ox,     oy,     bw, bh, bufB, bw); break;
            case (1, 3): // (H1(x,y+1)   + V1(x,y)) >> 1
                SimdH1Block  (src, srcStride, ox,     oy + 1, bw, bh, bufA, bw);
                SimdV1Block  (src, srcStride, ox,     oy,     bw, bh, bufB, bw); break;
            case (2, 3): // (H1(x,y+1)   + Hv(x,y)) >> 1
                SimdH1Block  (src, srcStride, ox,     oy + 1, bw, bh, bufA, bw);
                SimdHvBlock  (src, srcStride, ox,     oy,     bw, bh, bufB, bw); break;
            case (3, 3): // (H1(x,y+1)   + V1(x+1,y)) >> 1
                SimdH1Block  (src, srcStride, ox,     oy + 1, bw, bh, bufA, bw);
                SimdV1Block  (src, srcStride, ox + 1, oy,     bw, bh, bufB, bw); break;
            default:
                throw new ArgumentOutOfRangeException(nameof(xFrac), $"Unsupported ({xFrac},{yFrac})");
        }

        // (A + B + 1) >> 1 per byte using PAVGB / VRHADD
        for (var i = 0; i < bh; i++)
        {
            var aRow = bufA.Slice(i * bw);
            var bRow = bufB.Slice(i * bw);
            var dRow = dst.Slice(i * dstStride);
            var j = 0;
            for (; j + 16 <= bw; j += 16)
            {
                var va = Vector128.LoadUnsafe(ref aRow[j]);
                var vb = Vector128.LoadUnsafe(ref bRow[j]);
                SimdAvgByte16(va, vb).StoreUnsafe(ref dRow[j]);
            }
            for (; j < bw; j++)
                dRow[j] = (byte)((aRow[j] + bRow[j] + 1) >> 1);
        }
    }

    // ─── SIMD vector helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Applies horizontal 6-tap filter `tap6(src[ox-2..ox+5])` to 8 consecutive columns starting
    /// at <paramref name="ox"/>. Returns raw (unclipped, unshifted) tap6 values as <see cref="Vector128{Int16}"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> HorizRawFilter8(ReadOnlySpan<byte> src, int rowBase, int ox)
    {
        // Load 16 source bytes at ox-2; bytes 0..5 used for 8 consecutive tap windows.
        Debug.Assert(rowBase + ox + 14 <= src.Length, "HorizRawFilter8 requires 16-byte read slack in src.");
        var raw = Vector128.LoadUnsafe(ref Unsafe.AsRef(in src[rowBase + ox - 2]));
        // p[k] = bytes k..k+7 of raw, widened to int16 → 8 values for 8 output columns
        var p0 = Widen8LowerToShort(raw);
        var p1 = Widen8LowerToShort(ByteShiftRight(raw, 1));
        var p2 = Widen8LowerToShort(ByteShiftRight(raw, 2));
        var p3 = Widen8LowerToShort(ByteShiftRight(raw, 3));
        var p4 = Widen8LowerToShort(ByteShiftRight(raw, 4));
        var p5 = Widen8LowerToShort(ByteShiftRight(raw, 5));
        // tap6 = p0 - 5*p1 + 20*p2 + 20*p3 - 5*p4 + p5
        return p0
            + Vector128.Multiply(TapMinus5, p1)
            + Vector128.Multiply(Tap20, p2)
            + Vector128.Multiply(Tap20, p3)
            + Vector128.Multiply(TapMinus5, p4)
            + p5;
    }

    /// <summary>
    /// Applies vertical 6-tap filter to 8 columns at x starting at <paramref name="ox"/>, row <paramref name="oy"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> VertRawFilter8(ReadOnlySpan<byte> src, int stride, int ox, int oy)
    {
        var p0 = LoadRow8AsShort(src, stride, ox, oy - 2);
        var p1 = LoadRow8AsShort(src, stride, ox, oy - 1);
        var p2 = LoadRow8AsShort(src, stride, ox, oy);
        var p3 = LoadRow8AsShort(src, stride, ox, oy + 1);
        var p4 = LoadRow8AsShort(src, stride, ox, oy + 2);
        var p5 = LoadRow8AsShort(src, stride, ox, oy + 3);
        return p0
            + Vector128.Multiply(TapMinus5, p1)
            + Vector128.Multiply(Tap20, p2)
            + Vector128.Multiply(Tap20, p3)
            + Vector128.Multiply(TapMinus5, p4)
            + p5;
    }

    /// <summary>Loads 8 source bytes at (ox, oy) and zero-extends to 8 int16 values.</summary>
    /// <remarks>Reads 16 bytes from <paramref name="src"/>; callers must provide ≥16 bytes of slack past the last used column (padded reference planes satisfy this).</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadRow8AsShort(ReadOnlySpan<byte> src, int stride, int ox, int oy)
    {
        var byteOff = oy * stride + ox;
        Debug.Assert(byteOff + 16 <= src.Length, "LoadRow8AsShort requires 16-byte read slack in src.");
        var raw = Vector128.LoadUnsafe(ref Unsafe.AsRef(in src[byteOff]));
        return Widen8LowerToShort(raw);
    }

    /// <summary>Widens lower 8 bytes of a 128-bit vector to 8 int16 values (zero-extension).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Widen8LowerToShort(Vector128<byte> v)
    {
        if (Sse41.IsSupported)
            return Sse41.ConvertToVector128Int16(v);
        // AdvSimd: widen lower 8 bytes (as Vector64<byte>) to 8 uint16, reinterpret as int16
        return AdvSimd.ShiftLeftLogicalWideningLower(v.GetLower(), 0).AsInt16();
    }

    /// <summary>
    /// Shifts a 128-bit byte vector right by <paramref name="n"/> bytes, filling vacated high bytes with 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ByteShiftRight(Vector128<byte> v, byte n)
    {
        if (Sse2.IsSupported)
        {
#pragma warning disable CA1857
            return Sse2.ShiftRightLogical128BitLane(v, n);
#pragma warning restore CA1857
        }
        // NEON: vextq_u8(v, zero, n) = [v[n..15], zero[0..n-1]]
#pragma warning disable CA1857
        return AdvSimd.ExtractVector128(v, Vector128<byte>.Zero, n);
#pragma warning restore CA1857
    }

    /// <summary>Clips 8 int16 values to 0..255 and packs them into 8 bytes at <paramref name="dst"/>[<paramref name="offset"/>].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PackAndStore8(Vector128<short> v, Span<byte> dst, int offset)
    {
        if (Sse2.IsSupported)
        {
            // PACKUSWB: clips int16 to [0,255] and packs lower 8 values into bytes
            var packed = Sse2.PackUnsignedSaturate(v, v);
            Vector64.StoreUnsafe(packed.GetLower(), ref dst[offset]);
        }
        else
        {
            // NEON: clamp to [0,255] then narrow without saturation (safe because clamped)
            var clamped = AdvSimd.Max(AdvSimd.Min(v, Vector128.Create((short)255)), Vector128<short>.Zero);
            AdvSimd.ExtractNarrowingLower(clamped.AsUInt16()).StoreUnsafe(ref dst[offset]);
        }
    }

    /// <summary>Widens 4 int16 values starting at <paramref name="offset"/> to int32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WidenLow4(ReadOnlySpan<short> buf, int offset)
    {
        var v64 = Vector64.LoadUnsafe(ref Unsafe.AsRef(in buf[offset]));
        if (Sse41.IsSupported)
            return Sse41.ConvertToVector128Int32(Vector128.Create(v64, Vector64<short>.Zero));
        return AdvSimd.ShiftLeftLogicalWideningLower(v64, 0).AsInt32();
    }

    /// <summary>Horizontal 6-tap on four int32 vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Tap6Int32(
        Vector128<int> p0, Vector128<int> p1, Vector128<int> p2,
        Vector128<int> p3, Vector128<int> p4, Vector128<int> p5)
    {
        var m5  = Vector128.Create(-5);
        var p20 = Vector128.Create(20);
        return p0
            + Vector128.Multiply(m5,  p1)
            + Vector128.Multiply(p20, p2)
            + Vector128.Multiply(p20, p3)
            + Vector128.Multiply(m5,  p4)
            + p5;
    }

    /// <summary>Computes (A + B + 1) >> 1 per byte using PAVGB/VRHADD.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> SimdAvgByte16(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported) return Sse2.Average(a, b);
        return AdvSimd.FusedAddRoundedHalving(a, b);
    }


    /// <summary>Interpolate one luma block at fractional position (xFrac, yFrac) in 1/4 units.</summary>
    /// <param name="src">Reference patch covering the block plus the 6-tap halo on every side, row-major.</param>
    /// <param name="srcStride">Row stride of <paramref name="src"/>.</param>
    /// <param name="srcOriginX">Top-left x in <paramref name="src"/> where the integer-pel block begins (≥ 2 to leave room for the 6-tap filter).</param>
    /// <param name="srcOriginY">Top-left y in <paramref name="src"/> where the integer-pel block begins (≥ 2).</param>
    /// <param name="xFrac">Fractional x in 1/4-pel units (0..3).</param>
    /// <param name="yFrac">Fractional y in 1/4-pel units (0..3).</param>
    /// <param name="blockWidth">Block width in samples (4, 8, or 16).</param>
    /// <param name="blockHeight">Block height in samples (4, 8, or 16).</param>
    /// <param name="dst">Destination buffer of size blockWidth · blockHeight, row-major.</param>
    /// <param name="dstStride">Row stride of <paramref name="dst"/>.</param>
    public static void Interpolate(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride)
    {
        for (var i = 0; i < blockHeight; i++)
        {
            for (var j = 0; j < blockWidth; j++)
            {
                var x = srcOriginX + j;
                var y = srcOriginY + i;
                dst[i * dstStride + j] = LumaSampleAt(src, srcStride, x, y, xFrac, yFrac);
            }
        }
    }

    /// <summary>One output sample per H.264 8.4.2.2.1 (same decomposition as the acceptance reference).</summary>
    private static byte LumaSampleAt(
        ReadOnlySpan<byte> src, int stride, int x, int y, int xFrac, int yFrac)
    {
        switch ((xFrac, yFrac))
        {
            case (0, 0):
                return G(src, stride, x, y);
            case (1, 0):
                return (byte)((G(src, stride, x, y) + H1(src, stride, x, y) + 1) >> 1);
            case (2, 0):
                return (byte)H1(src, stride, x, y);
            case (3, 0):
                return (byte)((G(src, stride, x + 1, y) + H1(src, stride, x, y) + 1) >> 1);
            case (0, 1):
                return (byte)((G(src, stride, x, y) + V1(src, stride, x, y) + 1) >> 1);
            case (0, 2):
                return (byte)V1(src, stride, x, y);
            case (0, 3):
                return (byte)((G(src, stride, x, y + 1) + V1(src, stride, x, y) + 1) >> 1);
            case (1, 1):
                return (byte)((H1(src, stride, x, y) + V1(src, stride, x, y) + 1) >> 1);
            case (2, 1):
                return (byte)((H1(src, stride, x, y) + Hv(src, stride, x, y) + 1) >> 1);
            case (3, 1):
                return (byte)((H1(src, stride, x, y) + V1(src, stride, x + 1, y) + 1) >> 1);
            case (1, 2):
                return (byte)((V1(src, stride, x, y) + Hv(src, stride, x, y) + 1) >> 1);
            case (2, 2):
                return (byte)Hv(src, stride, x, y);
            case (3, 2):
                return (byte)((V1(src, stride, x + 1, y) + Hv(src, stride, x, y) + 1) >> 1);
            case (1, 3):
                return (byte)((H1(src, stride, x, y + 1) + V1(src, stride, x, y) + 1) >> 1);
            case (2, 3):
                return (byte)((H1(src, stride, x, y + 1) + Hv(src, stride, x, y) + 1) >> 1);
            case (3, 3):
                return (byte)((H1(src, stride, x, y + 1) + V1(src, stride, x + 1, y) + 1) >> 1);
            default:
                throw new ArgumentOutOfRangeException(nameof(xFrac));
        }
    }

    private static byte G(ReadOnlySpan<byte> src, int stride, int x, int y) =>
        src[y * stride + x];

    private static int Tap6(int p0, int p1, int p2, int p3, int p4, int p5) =>
        p0 - 5 * p1 + 20 * p2 + 20 * p3 - 5 * p4 + p5;

    private static int LumaH(ReadOnlySpan<byte> src, int stride, int x, int y) =>
        Tap6(
            src[y * stride + (x - 2)],
            src[y * stride + (x - 1)],
            src[y * stride + x],
            src[y * stride + (x + 1)],
            src[y * stride + (x + 2)],
            src[y * stride + (x + 3)]);

    private static int LumaV(ReadOnlySpan<byte> src, int stride, int x, int y) =>
        Tap6(
            src[(y - 2) * stride + x],
            src[(y - 1) * stride + x],
            src[y * stride + x],
            src[(y + 1) * stride + x],
            src[(y + 2) * stride + x],
            src[(y + 3) * stride + x]);

    /// <summary>
    /// Centre half-pel: horizontal 6-tap at full precision, then vertical 6-tap with (+512) &gt;&gt; 10
    /// (H.264 8.4.2.2.1). Horizontal outputs are kept as 16-bit intermediates — not clipped to 8-bit
    /// before the vertical pass.
    /// </summary>
    private static int Hv(ReadOnlySpan<byte> src, int stride, int x, int y)
    {
        Span<short> horiz = stackalloc short[6];
        horiz[0] = (short)LumaH(src, stride, x, y - 2);
        horiz[1] = (short)LumaH(src, stride, x, y - 1);
        horiz[2] = (short)LumaH(src, stride, x, y);
        horiz[3] = (short)LumaH(src, stride, x, y + 1);
        horiz[4] = (short)LumaH(src, stride, x, y + 2);
        horiz[5] = (short)LumaH(src, stride, x, y + 3);
        var v = Tap6(horiz[0], horiz[1], horiz[2], horiz[3], horiz[4], horiz[5]);
        return Clip255((v + 512) >> 10);
    }

    private static int H1(ReadOnlySpan<byte> src, int stride, int x, int y) =>
        Clip255((LumaH(src, stride, x, y) + 16) >> 5);

    private static int V1(ReadOnlySpan<byte> src, int stride, int x, int y) =>
        Clip255((LumaV(src, stride, x, y) + 16) >> 5);

    private static int Clip255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
}
