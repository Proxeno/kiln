using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace Kiln.Internal.H264;

/// <summary>
/// Per-async-context preference for H.264 hot-path intrinsics (quant, DCT, intra SAD).
/// Null in <see cref="AsyncLocal{T}"/> means default: prefer hardware paths when supported.
/// </summary>
internal static class H264IntrinsicsPreference
{
    private static readonly AsyncLocal<bool?> PreferIntrinsicsAsync = new();

    /// <summary>Whether SIMD may be used on this flow; default true when unset.</summary>
    internal static bool PreferIntrinsics => PreferIntrinsicsAsync.Value != false;

    internal static bool UseQuantSimd => PreferIntrinsics && H264BlockTransformSimd.IsSupported;

    internal static bool UseDctSimd => PreferIntrinsics && H264Dct4x4Simd.IsSupported;

    internal static bool UseIntraSadSimd => PreferIntrinsics && H264Intra4X4Simd.IsSupported;

    internal static bool UseDequantSimd => PreferIntrinsics && H264BlockTransformDequantSimd.IsSupported;

    internal static bool UseIntraPredictSimd =>
        PreferIntrinsics
        && H264Intra4X4DirectionalSimd.IsSupported
        && H264Intra4X4FillSimd.IsSupported;

    internal static bool UseIntra16x16PredictSimd => PreferIntrinsics && H264Intra16x16PredictionSimd.IsSupported;

    internal static bool UseI16x16SadSimd => PreferIntrinsics && H264Intra16x16PredictionSimd.IsSupported;

    /// <summary>
    /// SIMD gather for <see cref="H264BaselineSliceEncoder.GatherSrcBlock4X4"/> / inter subsampled blocks.
    /// Subsumed kernels (residual / nnz / recon) live inside <see cref="H264TransformBundle"/>.
    /// Requires <see cref="Vector128.IsHardwareAccelerated"/> plus Sse41 or AdvSimd (matches legacy MbKernel gate).
    /// </summary>
    internal static bool UseMbKernelSimd =>
        PreferIntrinsics
        && Vector128.IsHardwareAccelerated
        && (Sse41.IsSupported || AdvSimd.IsSupported);

    /// <summary>
    /// SIMD fused bundle <see cref="H264TransformBundle.EncodeResidual4x4Simd"/> (DCT + quant + dequant + IDCT chain).
    /// When false but intrinsics are on, per-block residual encode uses <see cref="H264TransformBundle.EncodeResidual4x4Scalar"/>.
    /// Default SIMD-vs-scalar for this bundle is <see cref="H264TransformBundle.PreferSimdBundleByDefault"/> (defaults <c>true</c> when ISA-supported).
    /// </summary>
    internal static bool UseTransformBundleSimd =>
        PreferIntrinsics && H264TransformBundle.IsSimdBundleSupported && H264TransformBundle.PreferSimdBundleByDefault;

    /// <summary>
    /// SIMD luma deblocking filter inner loop (<see cref="H264DeblockingFilterSimd"/>).
    /// Processes 16 samples per edge in parallel; falls back to scalar for mixed-bs edges.
    /// </summary>
    internal static bool UseDeblockSimd => PreferIntrinsics && H264DeblockingFilterSimd.IsSupported;

    /// <summary>When true (default), integer-pel inter ME scores with SATD; fractional refinement still uses SAD.</summary>
    public static bool UseMotionSatd { get; set; } = true;

    /// <summary>
    /// SIMD multi-block SAD for inter motion estimation (<see cref="H264MotionSad"/>).
    /// When false, motion SAD uses the scalar reference path for parity tests.
    /// </summary>
    internal static bool UseMotionSadSimd => PreferIntrinsics && H264MotionSad.IsSupported;

    /// <summary>All encoder SIMD paths are available and preferred (for tests/guards).</summary>
    internal static bool AllEncoderSimdAvailable =>
        H264BlockTransformSimd.IsSupported && H264Dct4x4Simd.IsSupported && H264Intra4X4Simd.IsSupported
        && H264BlockTransformDequantSimd.IsSupported;

    /// <summary>
    /// Overrides <see cref="PreferIntrinsics"/> until disposed. Restores the previous async-local value.
    /// Safe for parallel tests and BenchmarkDotNet when each job uses its own async context.
    /// </summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly bool? _previous;

        public Scope(bool preferIntrinsics)
        {
            _previous = PreferIntrinsicsAsync.Value;
            PreferIntrinsicsAsync.Value = preferIntrinsics;
        }

        public void Dispose() => PreferIntrinsicsAsync.Value = _previous;
    }
}
