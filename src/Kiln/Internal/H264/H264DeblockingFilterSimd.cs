using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>
/// SIMD inner kernels for the H.264 in-loop deblocking filter (8.7.2.3 / 8.7.2.4).
/// Vectorizes the per-edge 16-sample luma inner loop using Vector128&lt;byte/short&gt;.
/// Alpha/beta/tC0 table lookups and per-edge boundary-strength decisions remain scalar in the caller.
/// </summary>
internal static class H264DeblockingFilterSimd
{
    /// <summary>SIMD is available when Vector128 is hardware-accelerated (x64 SSE2+ or AArch64 NEON).</summary>
    public static bool IsSupported => Vector128.IsHardwareAccelerated;

    // ── Horizontal luma edge (16 contiguous samples per row) ─────────────────────────────────────

    /// <summary>
    /// Normal filter (bS ∈ 1–3) for one horizontal luma edge: 16 samples across rows [pyEdge-4 .. pyEdge+3].
    /// <paramref name="bsMask"/> is 0xFF per lane where bS&gt;0 for that segment, 0x00 otherwise.
    /// <paramref name="vTc0"/> is the per-lane tC0 value derived from the bS and indexA lookup.
    /// </summary>
    public static void FilterHorizLumaNormal16(
        Span<byte> y, int stride, int px0, int pyEdge,
        Vector128<byte> bsMask,
        Vector128<byte> vTc0,
        byte alpha, byte beta)
    {
        ref byte yRef = ref MemoryMarshal.GetReference(y);
        int r = (pyEdge - 4) * stride + px0;
        ref byte rP2 = ref Unsafe.Add(ref yRef, r + stride);
        ref byte rP1 = ref Unsafe.Add(ref yRef, r + 2 * stride);
        ref byte rP0 = ref Unsafe.Add(ref yRef, r + 3 * stride);
        ref byte rQ0 = ref Unsafe.Add(ref yRef, r + 4 * stride);
        ref byte rQ1 = ref Unsafe.Add(ref yRef, r + 5 * stride);
        ref byte rQ2 = ref Unsafe.Add(ref yRef, r + 6 * stride);

        var vP2 = Vector128.LoadUnsafe(ref rP2);
        var vP1 = Vector128.LoadUnsafe(ref rP1);
        var vP0 = Vector128.LoadUnsafe(ref rP0);
        var vQ0 = Vector128.LoadUnsafe(ref rQ0);
        var vQ1 = Vector128.LoadUnsafe(ref rQ1);
        var vQ2 = Vector128.LoadUnsafe(ref rQ2);

        FilterNormal16Core(ref vP2, ref vP1, ref vP0, ref vQ0, ref vQ1, ref vQ2, bsMask, vTc0, alpha, beta);

        Vector128.StoreUnsafe(vP1, ref rP1);
        Vector128.StoreUnsafe(vP0, ref rP0);
        Vector128.StoreUnsafe(vQ0, ref rQ0);
        Vector128.StoreUnsafe(vQ1, ref rQ1);
    }

    /// <summary>
    /// Strong filter (bS = 4) for one horizontal luma edge: 16 samples across rows [pyEdge-4 .. pyEdge+3].
    /// </summary>
    public static void FilterHorizLumaStrong16(
        Span<byte> y, int stride, int px0, int pyEdge,
        Vector128<byte> bsMask,
        byte alpha, byte beta)
    {
        ref byte yRef = ref MemoryMarshal.GetReference(y);
        int r = (pyEdge - 4) * stride + px0;
        ref byte rP3 = ref Unsafe.Add(ref yRef, r);
        ref byte rP2 = ref Unsafe.Add(ref yRef, r + stride);
        ref byte rP1 = ref Unsafe.Add(ref yRef, r + 2 * stride);
        ref byte rP0 = ref Unsafe.Add(ref yRef, r + 3 * stride);
        ref byte rQ0 = ref Unsafe.Add(ref yRef, r + 4 * stride);
        ref byte rQ1 = ref Unsafe.Add(ref yRef, r + 5 * stride);
        ref byte rQ2 = ref Unsafe.Add(ref yRef, r + 6 * stride);
        ref byte rQ3 = ref Unsafe.Add(ref yRef, r + 7 * stride);

        var vP3 = Vector128.LoadUnsafe(ref rP3);
        var vP2 = Vector128.LoadUnsafe(ref rP2);
        var vP1 = Vector128.LoadUnsafe(ref rP1);
        var vP0 = Vector128.LoadUnsafe(ref rP0);
        var vQ0 = Vector128.LoadUnsafe(ref rQ0);
        var vQ1 = Vector128.LoadUnsafe(ref rQ1);
        var vQ2 = Vector128.LoadUnsafe(ref rQ2);
        var vQ3 = Vector128.LoadUnsafe(ref rQ3);

        FilterStrong16Core(ref vP3, ref vP2, ref vP1, ref vP0, ref vQ0, ref vQ1, ref vQ2, ref vQ3, bsMask, alpha, beta);

        Vector128.StoreUnsafe(vP2, ref rP2);
        Vector128.StoreUnsafe(vP1, ref rP1);
        Vector128.StoreUnsafe(vP0, ref rP0);
        Vector128.StoreUnsafe(vQ0, ref rQ0);
        Vector128.StoreUnsafe(vQ1, ref rQ1);
        Vector128.StoreUnsafe(vQ2, ref rQ2);
    }

    // ── Vertical luma edge (16 stride-spaced column samples) ─────────────────────────────────────

    /// <summary>
    /// Normal filter (bS ∈ 1–3) for one vertical luma edge: 16 samples in column <paramref name="px"/>,
    /// rows [mbY .. mbY+15]. Gathers into temp buffers, applies SIMD core, scatters back.
    /// </summary>
    public static void FilterVertLumaNormal16(
        Span<byte> y, int stride, int px, int mbY,
        Vector128<byte> bsMask,
        Vector128<byte> vTc0,
        byte alpha, byte beta)
    {
        ref byte yRef = ref MemoryMarshal.GetReference(y);
        Span<ulong> stripRows = stackalloc ulong[16];
        Span<byte> p2Buf = stackalloc byte[16];
        Span<byte> p1Buf = stackalloc byte[16];
        Span<byte> p0Buf = stackalloc byte[16];
        Span<byte> q0Buf = stackalloc byte[16];
        Span<byte> q1Buf = stackalloc byte[16];
        Span<byte> q2Buf = stackalloc byte[16];

        for (var row = 0; row < 16; row++)
        {
            var off = (mbY + row) * stride + px;
            var strip = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref yRef, off - 4));
            stripRows[row] = strip;
            p2Buf[row] = (byte)(strip >> 8);
            p1Buf[row] = (byte)(strip >> 16);
            p0Buf[row] = (byte)(strip >> 24);
            q0Buf[row] = (byte)(strip >> 32);
            q1Buf[row] = (byte)(strip >> 40);
            q2Buf[row] = (byte)(strip >> 48);
        }

        var vP2 = Vector128.LoadUnsafe(ref p2Buf[0]);
        var vP1 = Vector128.LoadUnsafe(ref p1Buf[0]);
        var vP0 = Vector128.LoadUnsafe(ref p0Buf[0]);
        var vQ0 = Vector128.LoadUnsafe(ref q0Buf[0]);
        var vQ1 = Vector128.LoadUnsafe(ref q1Buf[0]);
        var vQ2 = Vector128.LoadUnsafe(ref q2Buf[0]);

        FilterNormal16Core(ref vP2, ref vP1, ref vP0, ref vQ0, ref vQ1, ref vQ2, bsMask, vTc0, alpha, beta);

        Vector128.StoreUnsafe(vP1, ref p1Buf[0]);
        Vector128.StoreUnsafe(vP0, ref p0Buf[0]);
        Vector128.StoreUnsafe(vQ0, ref q0Buf[0]);
        Vector128.StoreUnsafe(vQ1, ref q1Buf[0]);

        const ulong updateMask = 0x0000FFFFFFFF0000UL; // bytes [2..5] => p1, p0, q0, q1
        for (var row = 0; row < 16; row++)
        {
            var off = (mbY + row) * stride + px;
            var strip = stripRows[row] & ~updateMask;
            strip |= (ulong)p1Buf[row] << 16;
            strip |= (ulong)p0Buf[row] << 24;
            strip |= (ulong)q0Buf[row] << 32;
            strip |= (ulong)q1Buf[row] << 40;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref yRef, off - 4), strip);
        }
    }

    /// <summary>
    /// Strong filter (bS = 4) for one vertical luma edge: 16 samples in column <paramref name="px"/>,
    /// rows [mbY .. mbY+15].
    /// </summary>
    public static void FilterVertLumaStrong16(
        Span<byte> y, int stride, int px, int mbY,
        Vector128<byte> bsMask,
        byte alpha, byte beta)
    {
        ref byte yRef = ref MemoryMarshal.GetReference(y);
        Span<ulong> stripRows = stackalloc ulong[16];
        Span<byte> p3Buf = stackalloc byte[16];
        Span<byte> p2Buf = stackalloc byte[16];
        Span<byte> p1Buf = stackalloc byte[16];
        Span<byte> p0Buf = stackalloc byte[16];
        Span<byte> q0Buf = stackalloc byte[16];
        Span<byte> q1Buf = stackalloc byte[16];
        Span<byte> q2Buf = stackalloc byte[16];
        Span<byte> q3Buf = stackalloc byte[16];

        for (var row = 0; row < 16; row++)
        {
            var off = (mbY + row) * stride + px;
            var strip = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref yRef, off - 4));
            stripRows[row] = strip;
            p3Buf[row] = (byte)strip;
            p2Buf[row] = (byte)(strip >> 8);
            p1Buf[row] = (byte)(strip >> 16);
            p0Buf[row] = (byte)(strip >> 24);
            q0Buf[row] = (byte)(strip >> 32);
            q1Buf[row] = (byte)(strip >> 40);
            q2Buf[row] = (byte)(strip >> 48);
            q3Buf[row] = (byte)(strip >> 56);
        }

        var vP3 = Vector128.LoadUnsafe(ref p3Buf[0]);
        var vP2 = Vector128.LoadUnsafe(ref p2Buf[0]);
        var vP1 = Vector128.LoadUnsafe(ref p1Buf[0]);
        var vP0 = Vector128.LoadUnsafe(ref p0Buf[0]);
        var vQ0 = Vector128.LoadUnsafe(ref q0Buf[0]);
        var vQ1 = Vector128.LoadUnsafe(ref q1Buf[0]);
        var vQ2 = Vector128.LoadUnsafe(ref q2Buf[0]);
        var vQ3 = Vector128.LoadUnsafe(ref q3Buf[0]);

        FilterStrong16Core(ref vP3, ref vP2, ref vP1, ref vP0, ref vQ0, ref vQ1, ref vQ2, ref vQ3, bsMask, alpha, beta);

        Vector128.StoreUnsafe(vP2, ref p2Buf[0]);
        Vector128.StoreUnsafe(vP1, ref p1Buf[0]);
        Vector128.StoreUnsafe(vP0, ref p0Buf[0]);
        Vector128.StoreUnsafe(vQ0, ref q0Buf[0]);
        Vector128.StoreUnsafe(vQ1, ref q1Buf[0]);
        Vector128.StoreUnsafe(vQ2, ref q2Buf[0]);

        const ulong updateMask = 0x00FFFFFFFFFFFF00UL; // bytes [1..6] => p2,p1,p0,q0,q1,q2
        for (var row = 0; row < 16; row++)
        {
            var off = (mbY + row) * stride + px;
            var strip = stripRows[row] & ~updateMask;
            strip |= (ulong)p2Buf[row] << 8;
            strip |= (ulong)p1Buf[row] << 16;
            strip |= (ulong)p0Buf[row] << 24;
            strip |= (ulong)q0Buf[row] << 32;
            strip |= (ulong)q1Buf[row] << 40;
            strip |= (ulong)q2Buf[row] << 48;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref yRef, off - 4), strip);
        }
    }

    // ── SIMD compute cores ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Normal filter core (8.7.2.3, bS ∈ 1–3). Operates on 16 samples simultaneously.
    /// vP0/vQ0/vP1/vQ1 are modified in-place; vP2/vQ2 are read-only.
    /// Uses ORIGINAL vP0/vQ0 for computing the p1/q1 update (matching spec order).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterNormal16Core(
        ref Vector128<byte> vP2, ref Vector128<byte> vP1, ref Vector128<byte> vP0,
        ref Vector128<byte> vQ0, ref Vector128<byte> vQ1, ref Vector128<byte> vQ2,
        Vector128<byte> bsMask, Vector128<byte> vTc0, byte alpha, byte beta)
    {
        var vAlpha = Vector128.Create(alpha);
        var vBeta = Vector128.Create(beta);

        // activeMask: |p0−q0| < alpha AND |p1−p0| < beta AND |q1−q0| < beta
        var activeMask = Vector128.BitwiseAnd(
            Vector128.BitwiseAnd(
                Vector128.LessThan(AbsDiff(vP0, vQ0), vAlpha),
                Vector128.LessThan(AbsDiff(vP1, vP0), vBeta)),
            Vector128.LessThan(AbsDiff(vQ1, vQ0), vBeta));

        var finalMask = Vector128.BitwiseAnd(bsMask, activeMask);
        if (finalMask == Vector128<byte>.Zero)
        {
            return;
        }

        var apMask = Vector128.LessThan(AbsDiff(vP2, vP0), vBeta);
        var aqMask = Vector128.LessThan(AbsDiff(vQ2, vQ0), vBeta);

        // tC = tC0 + (|p2−p0|<β ? 1 : 0) + (|q2−q0|<β ? 1 : 0) — per lane
        var vOne = Vector128.Create((byte)1);
        var vTc = Vector128.Add(vTc0,
            Vector128.Add(
                Vector128.BitwiseAnd(apMask, vOne),
                Vector128.BitwiseAnd(aqMask, vOne)));

        // Δ = clip3(−tC, tC, ((q0−p0)<<2 + (p1−q1) + 4) >> 3)
        // Process lower and upper 8 lanes in int16.
        var zeroS = Vector128<short>.Zero;
        var v255S = Vector128.Create((short)255);

        var sP0L = Vector128.WidenLower(vP0).AsInt16();
        var sQ0L = Vector128.WidenLower(vQ0).AsInt16();
        var sP1L = Vector128.WidenLower(vP1).AsInt16();
        var sQ1L = Vector128.WidenLower(vQ1).AsInt16();
        var sTcL = Vector128.WidenLower(vTc).AsInt16();

        var deltaL = ClipDelta(
            Vector128.ShiftRightArithmetic(
                Vector128.Add(
                    Vector128.Add(Vector128.ShiftLeft(Vector128.Subtract(sQ0L, sP0L), 2), Vector128.Subtract(sP1L, sQ1L)),
                    Vector128.Create((short)4)),
                3),
            sTcL);

        var sP0H = Vector128.WidenUpper(vP0).AsInt16();
        var sQ0H = Vector128.WidenUpper(vQ0).AsInt16();
        var sP1H = Vector128.WidenUpper(vP1).AsInt16();
        var sQ1H = Vector128.WidenUpper(vQ1).AsInt16();
        var sTcH = Vector128.WidenUpper(vTc).AsInt16();

        var deltaH = ClipDelta(
            Vector128.ShiftRightArithmetic(
                Vector128.Add(
                    Vector128.Add(Vector128.ShiftLeft(Vector128.Subtract(sQ0H, sP0H), 2), Vector128.Subtract(sP1H, sQ1H)),
                    Vector128.Create((short)4)),
                3),
            sTcH);

        // p0' = clip1(p0 + Δ),  q0' = clip1(q0 − Δ)
        var newP0 = NarrowSat(
            Vector128.Min(v255S, Vector128.Max(zeroS, Vector128.Add(sP0L, deltaL))),
            Vector128.Min(v255S, Vector128.Max(zeroS, Vector128.Add(sP0H, deltaH))));
        var newQ0 = NarrowSat(
            Vector128.Min(v255S, Vector128.Max(zeroS, Vector128.Subtract(sQ0L, deltaL))),
            Vector128.Min(v255S, Vector128.Max(zeroS, Vector128.Subtract(sQ0H, deltaH))));

        // Save original p0/q0 for the p1/q1 update (spec uses pre-modification values).
        var origP0L = sP0L;
        var origP0H = sP0H;
        var origQ0L = sQ0L;
        var origQ0H = sQ0H;

        // p1' = p1 + clip3(−tC0, tC0, (p2 + ((p0+q0+1)>>1) − p1*2) >> 1)
        var sTc0L = Vector128.WidenLower(vTc0).AsInt16();
        var sTc0H = Vector128.WidenUpper(vTc0).AsInt16();
        var sP2L = Vector128.WidenLower(vP2).AsInt16();
        var sP2H = Vector128.WidenUpper(vP2).AsInt16();

        var halfL = Vector128.ShiftRightArithmetic(Vector128.Add(Vector128.Add(origP0L, origQ0L), Vector128.Create((short)1)), 1);
        var halfH = Vector128.ShiftRightArithmetic(Vector128.Add(Vector128.Add(origP0H, origQ0H), Vector128.Create((short)1)), 1);

        var newP1 = NarrowSat(
            Vector128.Min(v255S, Vector128.Max(zeroS, Vector128.Add(sP1L,
                ClipDelta(Vector128.ShiftRightArithmetic(Vector128.Subtract(Vector128.Add(sP2L, halfL), Vector128.ShiftLeft(sP1L, 1)), 1), sTc0L)))),
            Vector128.Min(v255S, Vector128.Max(zeroS, Vector128.Add(sP1H,
                ClipDelta(Vector128.ShiftRightArithmetic(Vector128.Subtract(Vector128.Add(sP2H, halfH), Vector128.ShiftLeft(sP1H, 1)), 1), sTc0H)))));

        // q1' = q1 + clip3(−tC0, tC0, (q2 + ((p0+q0+1)>>1) − q1*2) >> 1)
        var sQ2L = Vector128.WidenLower(vQ2).AsInt16();
        var sQ2H = Vector128.WidenUpper(vQ2).AsInt16();

        var newQ1 = NarrowSat(
            Vector128.Min(v255S, Vector128.Max(zeroS, Vector128.Add(sQ1L,
                ClipDelta(Vector128.ShiftRightArithmetic(Vector128.Subtract(Vector128.Add(sQ2L, halfL), Vector128.ShiftLeft(sQ1L, 1)), 1), sTc0L)))),
            Vector128.Min(v255S, Vector128.Max(zeroS, Vector128.Add(sQ1H,
                ClipDelta(Vector128.ShiftRightArithmetic(Vector128.Subtract(Vector128.Add(sQ2H, halfH), Vector128.ShiftLeft(sQ1H, 1)), 1), sTc0H)))));

        // Apply masks: p1/q1 only updated where apMask/aqMask AND finalMask
        vP0 = Vector128.ConditionalSelect(finalMask, newP0, vP0);
        vQ0 = Vector128.ConditionalSelect(finalMask, newQ0, vQ0);
        vP1 = Vector128.ConditionalSelect(Vector128.BitwiseAnd(finalMask, apMask), newP1, vP1);
        vQ1 = Vector128.ConditionalSelect(Vector128.BitwiseAnd(finalMask, aqMask), newQ1, vQ1);
    }

    /// <summary>
    /// Strong filter core (8.7.2.4, bS = 4). Operates on 16 samples simultaneously.
    /// vP3/vQ3 are read-only; vP0/vP1/vP2/vQ0/vQ1/vQ2 are modified in-place.
    /// All new values computed from original inputs before any write-back (spec requires this).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterStrong16Core(
        ref Vector128<byte> vP3, ref Vector128<byte> vP2, ref Vector128<byte> vP1, ref Vector128<byte> vP0,
        ref Vector128<byte> vQ0, ref Vector128<byte> vQ1, ref Vector128<byte> vQ2, ref Vector128<byte> vQ3,
        Vector128<byte> bsMask, byte alpha, byte beta)
    {
        var vAlpha = Vector128.Create(alpha);
        var vBeta = Vector128.Create(beta);

        // activeMask: |p0−q0| < alpha AND |p1−p0| < beta AND |q1−q0| < beta
        var activeMask = Vector128.BitwiseAnd(
            Vector128.BitwiseAnd(
                Vector128.LessThan(AbsDiff(vP0, vQ0), vAlpha),
                Vector128.LessThan(AbsDiff(vP1, vP0), vBeta)),
            Vector128.LessThan(AbsDiff(vQ1, vQ0), vBeta));

        var finalMask = Vector128.BitwiseAnd(bsMask, activeMask);
        if (finalMask == Vector128<byte>.Zero)
        {
            return;
        }

        var apMask = Vector128.LessThan(AbsDiff(vP2, vP0), vBeta);
        var aqMask = Vector128.LessThan(AbsDiff(vQ2, vQ0), vBeta);

        // smallGap = |p0−q0| < (alpha>>2)+2
        var vSmallGapThresh = Vector128.Create((byte)((alpha >> 2) + 2));
        var smallGapMask = Vector128.LessThan(AbsDiff(vP0, vQ0), vSmallGapThresh);

        var pStrongMask = Vector128.BitwiseAnd(apMask, smallGapMask);
        var qStrongMask = Vector128.BitwiseAnd(aqMask, smallGapMask);

        // All intermediate arithmetic in ushort (values 0–255, sums ≤ 2044, always non-negative)
        var uP3L = Vector128.WidenLower(vP3);
        var uP2L = Vector128.WidenLower(vP2);
        var uP1L = Vector128.WidenLower(vP1);
        var uP0L = Vector128.WidenLower(vP0);
        var uQ0L = Vector128.WidenLower(vQ0);
        var uQ1L = Vector128.WidenLower(vQ1);
        var uQ2L = Vector128.WidenLower(vQ2);
        var uQ3L = Vector128.WidenLower(vQ3);

        var uP3H = Vector128.WidenUpper(vP3);
        var uP2H = Vector128.WidenUpper(vP2);
        var uP1H = Vector128.WidenUpper(vP1);
        var uP0H = Vector128.WidenUpper(vP0);
        var uQ0H = Vector128.WidenUpper(vQ0);
        var uQ1H = Vector128.WidenUpper(vQ1);
        var uQ2H = Vector128.WidenUpper(vQ2);
        var uQ3H = Vector128.WidenUpper(vQ3);

        // p-side: strong → p0' = (p2 + 2p1 + 2p0 + 2q0 + q1 + 4) >> 3
        var pStrongP0L = StrongP0(uP2L, uP1L, uP0L, uQ0L, uQ1L);
        var pStrongP0H = StrongP0(uP2H, uP1H, uP0H, uQ0H, uQ1H);
        // p-side: weak  → p0' = (2p1 + p0 + q1 + 2) >> 2
        var pWeakP0L = WeakP0(uP1L, uP0L, uQ1L);
        var pWeakP0H = WeakP0(uP1H, uP0H, uQ1H);

        // q-side: strong → q0' = (q2 + 2q1 + 2q0 + 2p0 + p1 + 4) >> 3
        var qStrongQ0L = StrongP0(uQ2L, uQ1L, uQ0L, uP0L, uP1L);
        var qStrongQ0H = StrongP0(uQ2H, uQ1H, uQ0H, uP0H, uP1H);
        // q-side: weak  → q0' = (2q1 + q0 + p1 + 2) >> 2
        var qWeakQ0L = WeakP0(uQ1L, uQ0L, uP1L);
        var qWeakQ0H = WeakP0(uQ1H, uQ0H, uP1H);

        // Merge strong/weak using pStrongMask/qStrongMask (byte-level select after narrowing)
        var newP0Raw = Vector128.ConditionalSelect(pStrongMask,
            NarrowTrunc(pStrongP0L, pStrongP0H),
            NarrowTrunc(pWeakP0L, pWeakP0H));
        var newQ0Raw = Vector128.ConditionalSelect(qStrongMask,
            NarrowTrunc(qStrongQ0L, qStrongQ0H),
            NarrowTrunc(qWeakQ0L, qWeakQ0H));

        // p1' (strong only) = (p2 + p1 + p0 + q0 + 2) >> 2
        var pStrongP1L = StrongP1(uP2L, uP1L, uP0L, uQ0L);
        var pStrongP1H = StrongP1(uP2H, uP1H, uP0H, uQ0H);
        var newP1Raw = NarrowTrunc(pStrongP1L, pStrongP1H);

        // q1' (strong only) = (q2 + q1 + q0 + p0 + 2) >> 2
        var qStrongQ1L = StrongP1(uQ2L, uQ1L, uQ0L, uP0L);
        var qStrongQ1H = StrongP1(uQ2H, uQ1H, uQ0H, uP0H);
        var newQ1Raw = NarrowTrunc(qStrongQ1L, qStrongQ1H);

        // p2' (strong only) = (2p3 + 3p2 + p1 + p0 + q0 + 4) >> 3
        var pStrongP2L = StrongP2(uP3L, uP2L, uP1L, uP0L, uQ0L);
        var pStrongP2H = StrongP2(uP3H, uP2H, uP1H, uP0H, uQ0H);
        var newP2Raw = NarrowTrunc(pStrongP2L, pStrongP2H);

        // q2' (strong only) = (2q3 + 3q2 + q1 + q0 + p0 + 4) >> 3
        var qStrongQ2L = StrongP2(uQ3L, uQ2L, uQ1L, uQ0L, uP0L);
        var qStrongQ2H = StrongP2(uQ3H, uQ2H, uQ1H, uQ0H, uP0H);
        var newQ2Raw = NarrowTrunc(qStrongQ2L, qStrongQ2H);

        // Apply finalMask (overrides bsMask && activeMask); p1/p2/q1/q2 also need pStrong/qStrong
        vP0 = Vector128.ConditionalSelect(finalMask, newP0Raw, vP0);
        vQ0 = Vector128.ConditionalSelect(finalMask, newQ0Raw, vQ0);
        vP1 = Vector128.ConditionalSelect(Vector128.BitwiseAnd(finalMask, pStrongMask), newP1Raw, vP1);
        vQ1 = Vector128.ConditionalSelect(Vector128.BitwiseAnd(finalMask, qStrongMask), newQ1Raw, vQ1);
        vP2 = Vector128.ConditionalSelect(Vector128.BitwiseAnd(finalMask, pStrongMask), newP2Raw, vP2);
        vQ2 = Vector128.ConditionalSelect(Vector128.BitwiseAnd(finalMask, qStrongMask), newQ2Raw, vQ2);
    }

    // ── Arithmetic helpers ────────────────────────────────────────────────────────────────────────

    // |a − b| for unsigned bytes: max(a,b) − min(a,b) (no wraparound since max≥min).
    // Uses hardware abs-diff when available, otherwise falls back to generic Vector128 ops.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> AbsDiff(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported)
        {
            // psubusb(a,b)|psubusb(b,a): saturating unsigned subtract gives max(a−b,0)
            return Sse2.SubtractSaturate(a, b) | Sse2.SubtractSaturate(b, a);
        }

        if (AdvSimd.IsSupported)
        {
            return AdvSimd.AbsoluteDifference(a, b);
        }

        // Generic: max(a,b) − min(a,b) — safe because result is non-negative
        return Vector128.Subtract(Vector128.Max(a, b), Vector128.Min(a, b));
    }

    // clip3(−tc, tc, v)  (all in int16 lanes)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> ClipDelta(Vector128<short> v, Vector128<short> tc)
        => Vector128.Min(tc, Vector128.Max(Vector128.Negate(tc), v));

    // Pack two int16 vectors to unsigned byte with saturation (for normal filter clamp-to-[0,255]).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> NarrowSat(Vector128<short> lo, Vector128<short> hi)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.PackUnsignedSaturate(lo, hi);
        }

        if (AdvSimd.IsSupported)
        {
            var loU8 = AdvSimd.ExtractNarrowingSaturateUnsignedLower(lo);
            return AdvSimd.ExtractNarrowingSaturateUnsignedUpper(loU8, hi);
        }

        // Generic fallback
        Span<byte> buf = stackalloc byte[16];
        for (int i = 0; i < 8; i++)
        {
            buf[i] = (byte)Math.Clamp((int)lo.GetElement(i), 0, 255);
            buf[i + 8] = (byte)Math.Clamp((int)hi.GetElement(i), 0, 255);
        }

        return Vector128.LoadUnsafe(ref buf[0]);
    }

    // Truncating narrow ushort→byte (safe for strong filter where values are always 0–255).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> NarrowTrunc(Vector128<ushort> lo, Vector128<ushort> hi)
        => Vector128.Narrow(lo, hi);

    // Strong-filter p0 formula: (a + 2b + 2c + 2d + e + 4) >> 3
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> StrongP0(
        Vector128<ushort> a, Vector128<ushort> b, Vector128<ushort> c,
        Vector128<ushort> d, Vector128<ushort> e)
        => Vector128.ShiftRightLogical(
            Vector128.Add(
                Vector128.Add(
                    Vector128.Add(a, Vector128.ShiftLeft(b, 1)),
                    Vector128.Add(Vector128.ShiftLeft(c, 1), Vector128.ShiftLeft(d, 1))),
                Vector128.Add(e, Vector128.Create((ushort)4))),
            3);

    // Strong-filter p1 formula: (a + b + c + d + 2) >> 2
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> StrongP1(
        Vector128<ushort> a, Vector128<ushort> b, Vector128<ushort> c, Vector128<ushort> d)
        => Vector128.ShiftRightLogical(
            Vector128.Add(Vector128.Add(Vector128.Add(a, b), Vector128.Add(c, d)), Vector128.Create((ushort)2)),
            2);

    // Strong-filter p2 formula: (2a + 3b + c + d + e + 4) >> 3
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> StrongP2(
        Vector128<ushort> a, Vector128<ushort> b, Vector128<ushort> c,
        Vector128<ushort> d, Vector128<ushort> e)
        => Vector128.ShiftRightLogical(
            Vector128.Add(
                Vector128.Add(
                    Vector128.Add(Vector128.ShiftLeft(a, 1), Vector128.Add(b, Vector128.ShiftLeft(b, 1))),
                    Vector128.Add(c, d)),
                Vector128.Add(e, Vector128.Create((ushort)4))),
            3);

    // Weak-filter p0 formula: (2a + b + c + 2) >> 2
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> WeakP0(Vector128<ushort> a, Vector128<ushort> b, Vector128<ushort> c)
        => Vector128.ShiftRightLogical(
            Vector128.Add(Vector128.Add(Vector128.ShiftLeft(a, 1), b), Vector128.Add(c, Vector128.Create((ushort)2))),
            2);
}
