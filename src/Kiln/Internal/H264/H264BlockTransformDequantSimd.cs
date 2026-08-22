using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Kiln.Internal.H264;

/// <summary>SIMD inverse quant; mirrors <see cref="H264BlockTransform.DequantApprox"/> (ITU-T H.264 §8.5.12.2).</summary>
internal static class H264BlockTransformDequantSimd
{
    public static bool IsSupported => Sse41.IsSupported || AdvSimd.IsSupported;

    /// <summary>Per-coefficient bit-exact match with <see cref="H264BlockTransform.DequantApprox"/>.</summary>
    public static void DequantApprox(ReadOnlySpan<int> quantCoeff, int qp, Span<int> fwdDomain)
    {
        if (quantCoeff.Length < 16 || fwdDomain.Length < 16)
        {
            throw new ArgumentException("Spans must hold 16 elements.");
        }

        if (quantCoeff.Contains(int.MinValue))
        {
            H264BlockTransform.DequantApprox(quantCoeff, qp, fwdDomain);
            return;
        }

        qp = Math.Clamp(qp, 0, 51);
        var qbits = 15 + (qp / 6);
        var qpRem = qp % 6;
        if (Sse41.IsSupported)
        {
            DequantSimd(quantCoeff, qpRem, qbits, fwdDomain, sse: true);
            return;
        }

        if (AdvSimd.IsSupported)
        {
            DequantSimd(quantCoeff, qpRem, qbits, fwdDomain, sse: false);
            return;
        }

        throw new InvalidOperationException("H264BlockTransformDequantSimd.DequantApprox requires SSE4.1 or AdvSimd.");
    }

    private static void DequantSimd(ReadOnlySpan<int> quantCoeff, int qpRem6, int qbits, Span<int> fwdDomain, bool sse)
    {
        var shiftV = Vector128.CreateScalar(qbits);
        var pow2 = Vector128.Create(1 << qbits);
        ref var qRef = ref MemoryMarshal.GetReference(quantCoeff);
        ref var dRef = ref MemoryMarshal.GetReference(fwdDomain);

        for (var i = 0; i < 16; i += 4)
        {
            var v = Vector128.LoadUnsafe(ref Unsafe.Add(ref qRef, i));
            Vector128<int> m;
            Vector128<int> shifted;
            if (sse)
            {
                m = Sse2.ShiftRightArithmetic(v, 31);
                var ax = Sse2.Subtract(Sse2.Xor(v, m), m);
                shifted = Sse2.ShiftLeftLogical(ax, shiftV);
            }
            else
            {
                m = AdvSimd.ShiftRightArithmetic(v, 31);
                var ax = AdvSimd.Subtract(AdvSimd.Xor(v, m), m);
                shifted = AdvSimd.Multiply(ax, pow2);
            }

            for (var k = 0; k < 4; k++)
            {
                var idx = i + k;
                var q = Unsafe.Add(ref qRef, idx);
                if (q == 0)
                {
                    Unsafe.Add(ref dRef, idx) = 0;
                    continue;
                }

                var mf = H264BlockTransform.MfHalvedForLumaRasterIndex(qpRem6, idx);
                var dq = shifted.GetElement(k) / mf;
                var maskLane = m.GetElement(k);
                Unsafe.Add(ref dRef, idx) = maskLane == 0 ? dq : -dq;
            }
        }
    }
}
