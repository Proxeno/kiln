namespace Kiln.Internal.H264;

/// <summary>
/// Experiment-only per-family ablation over <see cref="IH264KernelSet"/>, driven by the environment
/// variable <c>KILN_H264_SCALAR_ABLATE</c>. It exists to answer "which part of the vectorized kernel
/// set owns how much encoder wall", which is not answerable by reading the code: a scalar branch
/// that runs a million times in a cheap loop and one that runs a thousand times on the critical path
/// look identical in a grep.
///
/// <para><b>Unset, this is not in the dispatch path at all</b> — <see cref="Apply"/> returns its
/// argument and <see cref="H264KernelSet.CreateBest"/> is behaviourally and structurally what it was.
/// That matters, because a wrapper that is present in the timed path is itself a cost.</para>
///
/// <para>The value is a comma-separated list of family names (see <see cref="Families"/>), or
/// <c>none</c> for a wrapper that forwards every member to the vectorized set. <c>none</c> is the
/// control the packet needs: it prices the wrapper itself, so an arm's cost can be read against a
/// control that carries the same dispatch layer rather than against the unwrapped build.</para>
///
/// <para>Known null arm: <c>gather-chroma</c> swaps <c>GatherChroma8x8</c> for the identical scalar
/// method (the two kernel sets share one implementation — no vectorized variant exists in the repo),
/// so it must measure as free. An arm that cannot come out non-zero is what keeps the others honest.</para>
/// </summary>
internal static class H264ScalarAblation
{
    private const string EnvVar = "KILN_H264_SCALAR_ABLATE";

    private static readonly string[] Requested = Parse(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>True when the wrapper is installed at all (including for <c>none</c>).</summary>
    internal static bool Active { get; } = Requested.Length > 0;

    internal static IReadOnlyList<string> Families { get; } = new[]
    {
        "sad", "satd", "ssd", "variance", "intrapred", "residual",
        "deblock", "interp", "gather-luma", "gather-chroma", "dct", "quant",
    };

    private static string[] Parse(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Wants(string family) => Array.IndexOf(Requested, family) >= 0;

    /// <summary>
    /// A member is ablated when its own subgroup is named or its parent family is. The subgroups
    /// exist because the two families that carry encoder wall (SAD, interpolation) each bundle
    /// several distinct call sites, and a family-level number cannot say which part of it is the
    /// vectorized fast path and which part is a scalar residue inside the same family.
    /// </summary>
    private static bool Off(string subgroup, string parent) => Wants(subgroup) || Wants(parent);

    /// <summary>Wraps <paramref name="simd"/> when <c>KILN_H264_SCALAR_ABLATE</c> is set.</summary>
    internal static IH264KernelSet Apply(IH264KernelSet simd) =>
        Active ? new AblatingKernelSet(simd, new ScalarKernelSet()) : simd;

    /// <summary>
    /// Forwards every member to the vectorized set except those in the named families, which go to
    /// the scalar set. A member routed to scalar produces the same bitstream (kiln's own parity tests
    /// assert this), so an arm changes speed and nothing else.
    /// </summary>
    private sealed class AblatingKernelSet(IH264KernelSet simd, IH264KernelSet scalar) : IH264KernelSet
    {
        private readonly IH264KernelSet _simd = simd;
        private readonly IH264KernelSet _scalar = scalar;

        private IH264KernelSet SadMotion => Off("sad-motion", "sad") ? _scalar : _simd;
        private IH264KernelSet SadIntra => Off("sad-intra", "sad") ? _scalar : _simd;
        private IH264KernelSet SadChroma => Off("sad-chroma", "sad") ? _scalar : _simd;
        private IH264KernelSet InterpLuma => Off("interp-luma", "interp") ? _scalar : _simd;
        private IH264KernelSet InterpChroma => Off("interp-chroma", "interp") ? _scalar : _simd;
        private IH264KernelSet Satd => Off("satd", "satd") ? _scalar : _simd;
        private IH264KernelSet Ssd => Off("ssd", "ssd") ? _scalar : _simd;
        private IH264KernelSet Variance => Off("variance", "variance") ? _scalar : _simd;
        private IH264KernelSet IntraPred => Off("intrapred", "intrapred") ? _scalar : _simd;
        private IH264KernelSet Residual => Off("residual", "residual") ? _scalar : _simd;
        private IH264KernelSet Deblock => Off("deblock", "deblock") ? _scalar : _simd;
        private IH264KernelSet GatherLuma => Off("gather-luma", "gather-luma") ? _scalar : _simd;
        private IH264KernelSet GatherChroma => Off("gather-chroma", "gather-chroma") ? _scalar : _simd;
        private IH264KernelSet Dct => Off("dct", "dct") ? _scalar : _simd;
        private IH264KernelSet Quant => Off("quant", "quant") ? _scalar : _simd;

        public int Sad16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
            SadMotion.Sad16x16(a, strideA, b, strideB);

        public int Sad16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
            SadMotion.Sad16x8(a, strideA, b, strideB);

        public int Sad8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
            SadMotion.Sad8x16(a, strideA, b, strideB);

        public int Sad8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
            SadMotion.Sad8x8(a, strideA, b, strideB);

        public void SadMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count) =>
            SadIntra.SadMany4x4(src, predConcat, sads, count);

        public int SadIntra16x16(ReadOnlySpan<byte> src256, ReadOnlySpan<byte> pred256, int srcStride) =>
            SadIntra.SadIntra16x16(src256, pred256, srcStride);

        public int SadChromaPair(ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV, ReadOnlySpan<byte> predU, ReadOnlySpan<byte> predV) =>
            SadChroma.SadChromaPair(srcU, srcV, predU, predV);

        public int Ssd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
            Ssd.Ssd16x16(a, strideA, b, strideB);

        public int Ssd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
            Ssd.Ssd8x8(a, strideA, b, strideB);

        public int VarianceMb16x16(ReadOnlySpan<byte> mbTopLeft, int stride) =>
            Variance.VarianceMb16x16(mbTopLeft, stride);

        public int Satd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
            Satd.Satd16x16(a, strideA, b, strideB);

        public int Satd16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
            Satd.Satd16x8(a, strideA, b, strideB);

        public int Satd8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
            Satd.Satd8x16(a, strideA, b, strideB);

        public int Satd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
            Satd.Satd8x8(a, strideA, b, strideB);

        public void SatdMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> satds, int count) =>
            Satd.SatdMany4x4(src, predConcat, satds, count);

        public void Predict4x4(int mode, ReadOnlySpan<byte> topRow, ReadOnlySpan<byte> leftCol, bool topAvail, bool leftAvail, Span<byte> dst16) =>
            IntraPred.Predict4x4(mode, topRow, leftCol, topAvail, leftAvail, dst16);

        public void PredictIntra16x16(int mode, ReadOnlySpan<byte> topRow, bool topAvail, ReadOnlySpan<byte> leftCol, bool leftAvail, byte topLeft, bool topLeftAvail, Span<byte> dst256) =>
            IntraPred.PredictIntra16x16(mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, dst256);

        public int EncodeResidual4x4(ReadOnlySpan<byte> src16, ReadOnlySpan<byte> pred16, int qp, Span<short> zigzagCoeffOut16, Span<byte> recDst, int recStride) =>
            Residual.EncodeResidual4x4(src16, pred16, qp, zigzagCoeffOut16, recDst, recStride);

        public void ApplyDeblock(
            Span<byte> y, int strideY,
            Span<byte> u, Span<byte> v, int strideUv,
            int mbWidth, int mbHeight,
            ReadOnlySpan<byte> bsHorizontal,
            ReadOnlySpan<byte> bsVertical,
            ReadOnlySpan<int> qpY,
            ReadOnlySpan<int> qpUv,
            int alphaOffsetDiv2,
            int betaOffsetDiv2) =>
            Deblock.ApplyDeblock(y, strideY, u, v, strideUv, mbWidth, mbHeight, bsHorizontal, bsVertical, qpY, qpUv, alphaOffsetDiv2, betaOffsetDiv2);

        public void InterpolateLuma(
            ReadOnlySpan<byte> src, int srcStride,
            int srcOriginX, int srcOriginY,
            int xFrac, int yFrac,
            int blockWidth, int blockHeight,
            Span<byte> dst, int dstStride) =>
            InterpLuma.InterpolateLuma(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac, blockWidth, blockHeight, dst, dstStride);

        public void InterpolateChroma(
            ReadOnlySpan<byte> src, int srcStride,
            int srcOriginX, int srcOriginY,
            int xFrac, int yFrac,
            int blockWidth, int blockHeight,
            Span<byte> dst, int dstStride) =>
            InterpChroma.InterpolateChroma(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac, blockWidth, blockHeight, dst, dstStride);

        public void GatherSrcBlock4x4(ReadOnlySpan<byte> srcY, int baseOff, int strideY, Span<byte> dst16) =>
            GatherLuma.GatherSrcBlock4x4(srcY, baseOff, strideY, dst16);

        public void GatherChroma8x8(ReadOnlySpan<byte> src, int stride, int bx, int by, Span<byte> dst64) =>
            GatherChroma.GatherChroma8x8(src, stride, bx, by, dst64);

        public void ForwardDct4x4(ReadOnlySpan<short> residual4x4, Span<int> outCoeff) =>
            Dct.ForwardDct4x4(residual4x4, outCoeff);

        public void Quant4x4(Span<int> block, int qp) =>
            Quant.Quant4x4(block, qp);
    }
}
