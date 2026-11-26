using Kiln.Internal.H264;

namespace Kiln.Benchmarks;

/// <summary>
/// Measurement-only <see cref="IH264KernelSet"/> decorator: forwards every member to an inner set
/// and counts calls per member. Installed through
/// <see cref="H264KernelSetHooks.MeasurementDecorator"/> by the stage profiler, never by the
/// encoder itself.
/// <para>
/// The counters are <c>static</c> on purpose: one encoder fans its slices out over several threads,
/// and every slice shares one kernel-set instance, so per-instance counters would still need
/// cross-thread aggregation. <see cref="Interlocked.Increment(ref long)"/> keeps the count exact.
/// The cost of the increment is irrelevant here — pass C is a counting pass, never a timed one.
/// </para>
/// <para>
/// Every member forwards unconditionally, so a pass-C bitstream stays byte-identical to pass T.
/// </para>
/// </summary>
internal sealed class CountingKernelSet : IH264KernelSet
{
    private enum Member
    {
        Sad16x16,
        Sad16x8,
        Sad8x16,
        Sad8x8,
        Sad16x16x4,
        Sad8x8x4,
        SadMany4x4,
        SadIntra16x16,
        SadChromaPair,
        Ssd16x16,
        Ssd8x8,
        VarianceMb16x16,
        Satd16x16,
        Satd16x8,
        Satd8x16,
        Satd8x8,
        SatdMany4x4,
        Predict4x4,
        PredictIntra16x16,
        EncodeResidual4x4,
        ApplyDeblock,
        InterpolateLuma,
        InterpolateChroma,
        GatherSrcBlock4x4,
        GatherChroma8x8,
        ForwardDct4x4,
        Quant4x4,
    }

    /// <summary>Member names in <see cref="Member"/> order; the report is keyed by these.</summary>
    private static readonly string[] MemberNames =
    [
        nameof(IH264KernelSet.Sad16x16),
        nameof(IH264KernelSet.Sad16x8),
        nameof(IH264KernelSet.Sad8x16),
        nameof(IH264KernelSet.Sad8x8),
        nameof(IH264KernelSet.Sad16x16x4),
        nameof(IH264KernelSet.Sad8x8x4),
        nameof(IH264KernelSet.SadMany4x4),
        nameof(IH264KernelSet.SadIntra16x16),
        nameof(IH264KernelSet.SadChromaPair),
        nameof(IH264KernelSet.Ssd16x16),
        nameof(IH264KernelSet.Ssd8x8),
        nameof(IH264KernelSet.VarianceMb16x16),
        nameof(IH264KernelSet.Satd16x16),
        nameof(IH264KernelSet.Satd16x8),
        nameof(IH264KernelSet.Satd8x16),
        nameof(IH264KernelSet.Satd8x8),
        nameof(IH264KernelSet.SatdMany4x4),
        nameof(IH264KernelSet.Predict4x4),
        nameof(IH264KernelSet.PredictIntra16x16),
        nameof(IH264KernelSet.EncodeResidual4x4),
        nameof(IH264KernelSet.ApplyDeblock),
        nameof(IH264KernelSet.InterpolateLuma),
        nameof(IH264KernelSet.InterpolateChroma),
        nameof(IH264KernelSet.GatherSrcBlock4x4),
        nameof(IH264KernelSet.GatherChroma8x8),
        nameof(IH264KernelSet.ForwardDct4x4),
        nameof(IH264KernelSet.Quant4x4),
    ];

    private static readonly long[] Counts = new long[MemberNames.Length];

    /// <summary>
    /// Calls per (member, stage) for pass S/C attribution: each call is charged to the
    /// <see cref="H264PInterDiagnostics.t_profileStage"/> the issuing thread is inside, or to
    /// <see cref="H264ProfileStage.Count"/> when it is outside every timed region.
    /// </summary>
    private static readonly long[,] StageCounts =
        new long[MemberNames.Length, (int)H264ProfileStage.Count + 1];

    /// <summary>One member's call count within a single stage.</summary>
    public readonly record struct StageCall(string Member, H264ProfileStage Stage, long Calls);

    /// <summary>Call counts per (member, stage) since the last <see cref="Reset"/>.</summary>
    public static IReadOnlyList<StageCall> SnapshotByStage()
    {
        var result = new List<StageCall>();
        for (var m = 0; m < MemberNames.Length; m++)
        {
            for (var s = 0; s <= (int)H264ProfileStage.Count; s++)
            {
                var calls = Interlocked.Read(ref StageCounts[m, s]);
                if (calls != 0)
                {
                    result.Add(new StageCall(MemberNames[m], (H264ProfileStage)s, calls));
                }
            }
        }

        return result;
    }

    private readonly IH264KernelSet _inner;
    private readonly int _sad16x16Delta;

    public CountingKernelSet(IH264KernelSet inner)
        : this(inner, sad16x16Delta: 0)
    {
    }

    /// <param name="sad16x16Delta">
    /// Added to every <see cref="Sad16x16"/> result. Zero (the production path) forwards the inner
    /// result exactly. The stage profiler's negative control passes <c>1</c> to prove the identity
    /// check notices a changed bitstream.
    /// </param>
    internal CountingKernelSet(IH264KernelSet inner, int sad16x16Delta)
    {
        _inner = inner;
        _sad16x16Delta = sad16x16Delta;
    }

    /// <summary>Per-member call counts accumulated since the last <see cref="Reset"/>.</summary>
    public static IReadOnlyDictionary<string, long> Snapshot()
    {
        var snapshot = new Dictionary<string, long>(MemberNames.Length, StringComparer.Ordinal);
        for (var i = 0; i < MemberNames.Length; i++)
        {
            snapshot[MemberNames[i]] = Interlocked.Read(ref Counts[i]);
        }

        return snapshot;
    }

    /// <summary>Zeroes every counter. Call between rows.</summary>
    public static void Reset()
    {
        Array.Clear(Counts);
        Array.Clear(StageCounts);
    }

    private static void Count(Member member)
    {
        Interlocked.Increment(ref Counts[(int)member]);
        var stage = H264PInterDiagnostics.t_profileStage;
        if ((uint)stage < (uint)H264ProfileStage.Count)
            Interlocked.Increment(ref StageCounts[(int)member, stage]);
    }

    public int Sad16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        Count(Member.Sad16x16);
        return _inner.Sad16x16(a, strideA, b, strideB) + _sad16x16Delta;
    }

    public int Sad16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        Count(Member.Sad16x8);
        return _inner.Sad16x8(a, strideA, b, strideB);
    }

    public int Sad8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        Count(Member.Sad8x16);
        return _inner.Sad8x16(a, strideA, b, strideB);
    }

    public int Sad8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        Count(Member.Sad8x8);
        return _inner.Sad8x8(a, strideA, b, strideB);
    }

    public void Sad16x16x4(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b0, ReadOnlySpan<byte> b1,
        ReadOnlySpan<byte> b2, ReadOnlySpan<byte> b3,
        int strideB, Span<int> sads)
    {
        Count(Member.Sad16x16x4);
        _inner.Sad16x16x4(a, strideA, b0, b1, b2, b3, strideB, sads);
    }

    public void Sad8x8x4(
        ReadOnlySpan<byte> a, int strideA,
        ReadOnlySpan<byte> b0, ReadOnlySpan<byte> b1,
        ReadOnlySpan<byte> b2, ReadOnlySpan<byte> b3,
        int strideB, Span<int> sads)
    {
        Count(Member.Sad8x8x4);
        _inner.Sad8x8x4(a, strideA, b0, b1, b2, b3, strideB, sads);
    }

    public void SadMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count)
    {
        Count(Member.SadMany4x4);
        _inner.SadMany4x4(src, predConcat, sads, count);
    }

    public int SadIntra16x16(ReadOnlySpan<byte> src256, ReadOnlySpan<byte> pred256, int srcStride)
    {
        Count(Member.SadIntra16x16);
        return _inner.SadIntra16x16(src256, pred256, srcStride);
    }

    public int SadChromaPair(ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV, ReadOnlySpan<byte> predU, ReadOnlySpan<byte> predV)
    {
        Count(Member.SadChromaPair);
        return _inner.SadChromaPair(srcU, srcV, predU, predV);
    }

    public int Ssd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        Count(Member.Ssd16x16);
        return _inner.Ssd16x16(a, strideA, b, strideB);
    }

    public int Ssd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        Count(Member.Ssd8x8);
        return _inner.Ssd8x8(a, strideA, b, strideB);
    }

    public int VarianceMb16x16(ReadOnlySpan<byte> mbTopLeft, int stride)
    {
        Count(Member.VarianceMb16x16);
        return _inner.VarianceMb16x16(mbTopLeft, stride);
    }

    public int Satd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        Count(Member.Satd16x16);
        return _inner.Satd16x16(a, strideA, b, strideB);
    }

    public int Satd16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        Count(Member.Satd16x8);
        return _inner.Satd16x8(a, strideA, b, strideB);
    }

    public int Satd8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        Count(Member.Satd8x16);
        return _inner.Satd8x16(a, strideA, b, strideB);
    }

    public int Satd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB)
    {
        Count(Member.Satd8x8);
        return _inner.Satd8x8(a, strideA, b, strideB);
    }

    public void SatdMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> satds, int count)
    {
        Count(Member.SatdMany4x4);
        _inner.SatdMany4x4(src, predConcat, satds, count);
    }

    public void Predict4x4(int mode, ReadOnlySpan<byte> topRow, ReadOnlySpan<byte> leftCol, bool topAvail, bool leftAvail, Span<byte> dst16)
    {
        Count(Member.Predict4x4);
        _inner.Predict4x4(mode, topRow, leftCol, topAvail, leftAvail, dst16);
    }

    public void PredictIntra16x16(int mode, ReadOnlySpan<byte> topRow, bool topAvail, ReadOnlySpan<byte> leftCol, bool leftAvail, byte topLeft, bool topLeftAvail, Span<byte> dst256)
    {
        Count(Member.PredictIntra16x16);
        _inner.PredictIntra16x16(mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, dst256);
    }

    public int EncodeResidual4x4(ReadOnlySpan<byte> src16, ReadOnlySpan<byte> pred16, int qp, Span<short> zigzagCoeffOut16, Span<byte> recDst, int recStride)
    {
        Count(Member.EncodeResidual4x4);
        return _inner.EncodeResidual4x4(src16, pred16, qp, zigzagCoeffOut16, recDst, recStride);
    }

    public void ApplyDeblock(
        Span<byte> y, int strideY,
        Span<byte> u, Span<byte> v, int strideUv,
        int mbWidth, int mbHeight,
        ReadOnlySpan<byte> bsHorizontal,
        ReadOnlySpan<byte> bsVertical,
        ReadOnlySpan<int> qpY,
        ReadOnlySpan<int> qpUv,
        int alphaOffsetDiv2,
        int betaOffsetDiv2)
    {
        Count(Member.ApplyDeblock);
        _inner.ApplyDeblock(
            y, strideY, u, v, strideUv, mbWidth, mbHeight,
            bsHorizontal, bsVertical, qpY, qpUv, alphaOffsetDiv2, betaOffsetDiv2);
    }

    public void InterpolateLuma(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride)
    {
        Count(Member.InterpolateLuma);
        _inner.InterpolateLuma(
            src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac, blockWidth, blockHeight, dst, dstStride);
    }

    public void InterpolateChroma(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride)
    {
        Count(Member.InterpolateChroma);
        _inner.InterpolateChroma(
            src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac, blockWidth, blockHeight, dst, dstStride);
    }

    public void GatherSrcBlock4x4(ReadOnlySpan<byte> srcY, int baseOff, int strideY, Span<byte> dst16)
    {
        Count(Member.GatherSrcBlock4x4);
        _inner.GatherSrcBlock4x4(srcY, baseOff, strideY, dst16);
    }

    public void GatherChroma8x8(ReadOnlySpan<byte> src, int stride, int bx, int by, Span<byte> dst64)
    {
        Count(Member.GatherChroma8x8);
        _inner.GatherChroma8x8(src, stride, bx, by, dst64);
    }

    public void ForwardDct4x4(ReadOnlySpan<short> residual4x4, Span<int> outCoeff)
    {
        Count(Member.ForwardDct4x4);
        _inner.ForwardDct4x4(residual4x4, outCoeff);
    }

    public void Quant4x4(Span<int> block, int qp)
    {
        Count(Member.Quant4x4);
        _inner.Quant4x4(block, qp);
    }
}
