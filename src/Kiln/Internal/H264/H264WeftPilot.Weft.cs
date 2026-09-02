#if KILN_WEFT_PILOT
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Weft.Backends.Cpu;
using Weft.Ir;
using Weft.Ir.Plan;
using Weft.Kernels;
using Weft.Optimizer;

namespace Kiln.Internal.H264;

/// <summary>
/// The Weft half of the batch-SAD pilot. <b>Wholly inside <c>#if KILN_WEFT_PILOT</c></b>: a normal
/// kiln build never compiles this file, never references Weft, and behaves exactly as it does on
/// <c>main</c>. See <c>H264WeftPilot.cs</c> for the always-compiled install point.
/// </summary>
internal static partial class H264WeftPilot
{
    static partial void Install(ref IH264KernelSet kernels)
    {
        if (H264WeftBatchSadKernelSet.Enabled && kernels is not IH264BatchSadSearch)
        {
            AssertWeftMatchesOurConfiguration();
            kernels = new H264WeftBatchSadKernelSet(kernels);
        }
    }

    private static int s_weftConfigurationChecked;

    /// <summary>
    /// Runtime half of the Debug-Weft guard. <c>Kiln.csproj</c> makes the Weft references inherit
    /// this build's configuration and fails the build if they did not; this checks the assembly
    /// that was actually <em>loaded</em>, which is the one a benchmark measures — a hand-copied or
    /// stale Weft DLL never went through our build at all.
    ///
    /// <para>It fires only on a MISMATCH: a Release kiln paired with an unoptimized Weft. A Debug
    /// kiln with a Debug Weft is consistent and silent, so <c>dotnet test</c> is unaffected. Set
    /// <c>KILN_WEFT_ALLOW_DEBUG_WEFT=1</c> to proceed anyway when deliberately debugging into Weft;
    /// no measurement taken in that state should be reported.</para>
    /// </summary>
    private static void AssertWeftMatchesOurConfiguration()
    {
        if (Interlocked.Exchange(ref s_weftConfigurationChecked, 1) != 0)
        {
            return;
        }

#if !DEBUG
        var weft = typeof(Weft.Kernels.SadBatchedU8Provider).Assembly;
        var debuggable = weft.GetCustomAttribute<DebuggableAttribute>();
        if (debuggable is not null && debuggable.IsJITOptimizerDisabled &&
            Environment.GetEnvironmentVariable("KILN_WEFT_ALLOW_DEBUG_WEFT") is not ("1" or "true" or "TRUE" or "True"))
        {
            throw new InvalidOperationException(
                $"Kiln is a Release build but the loaded Weft assembly '{weft.GetName().Name}' " +
                $"({weft.Location}) was compiled with optimizations DISABLED. Any timing taken in " +
                "this state measures an unoptimized Weft and is meaningless. Rebuild the pilot so " +
                "the Weft projects inherit Kiln's configuration (see the " +
                "ShouldUnsetParentConfigurationAndPlatform comment in Directory.Build.props), or set " +
                "KILN_WEFT_ALLOW_DEBUG_WEFT=1 if you are deliberately debugging and will not report " +
                "a number from this run.");
        }
#endif
    }
}

/// <summary>
/// An <see cref="IH264KernelSet"/> that forwards every kernel to the set it wraps, and additionally
/// offers <see cref="IH264BatchSadSearch"/> backed by <b>one</b> Weft parametric
/// <c>CompiledProgram</c> per (reference-plane geometry, search range).
///
/// <para><b>The program.</b> <c>Slice</c> for the current block and <c>ExtractWindows</c> for the
/// candidate grid both take a <em>parametric</em> origin — two declared binding parameters
/// (<c>py</c>, <c>px</c>) — while the (2R+1)² relative <c>(dy, dx)</c> candidate table stays a
/// compile-time constant. That is the shape <c>docs/PARAMETRIC_PLANS.md</c> §5 rules and
/// <c>experiments/parametric-pricing/EXPERIMENT.md</c> §PP4 measured at the 720p grid: the origin
/// moves with <c>SetParameters</c>, so a whole frame is one compile rather than one compile per
/// macroblock.</para>
///
/// <para><b>Input staging, and why it is honest.</b> Weft refuses zero-copy inputs on a parametric
/// plan (temporal × parametric is refused in v1; <c>CompiledProgram.BindStreaming</c>), so the two
/// plane-shaped input tensors are owned by this provider and bound once. Rather than copy a whole
/// reference plane per call — or cache one and risk serving stale bytes when the encoder writes a
/// new reconstruction into the same buffer — each call refreshes <em>exactly</em> the bytes the
/// program is about to read: the 16×16 current block and the (2R+16)×(2R+16) reference tile around
/// the origin, at their true plane coordinates. Nothing stale can be read, no frame-boundary
/// notification is needed, and the staging cost (1.25 KB at R=8) is part of the integration
/// overhead this pilot exists to make visible.</para>
///
/// <para>The tensors carry an R-sample margin on every side so that an origin anywhere in the plane
/// has its whole ±R window inside the tensor. Margin samples, and plane samples outside the staged
/// tile, hold whatever was last written there — which is exactly the "unspecified" half of
/// <see cref="IH264BatchSadSearch"/>'s contract, and is never read, because those are precisely the
/// candidates <see cref="H264MotionEstimator"/>'s own <c>Fits</c> test rejects first.</para>
/// </summary>
internal sealed class H264WeftBatchSadKernelSet : IH264KernelSet, IH264BatchSadSearch
{
    private const int Block = 16;

    private readonly IH264KernelSet _inner;

    private static readonly ConcurrentDictionary<(int PlaneH, int PlaneW, int Range), Lazy<CompiledProgram>> Programs = new();
    private static readonly ConcurrentDictionary<(int Thread, int PlaneH, int PlaneW, int Range), Session> Sessions = new();

    [ThreadStatic]
    private static Session? t_lastSession;

    private static long s_engaged;
    private static long s_declined;
    private static long s_declinedRange;
    private static long s_compiles;
    private static double s_compileMs;

    public H264WeftBatchSadKernelSet(IH264KernelSet inner) => _inner = inner;

    /// <summary>
    /// Off unless <c>KILN_WEFT_BATCH_SAD</c> is <c>1</c>/<c>true</c>. Default: OFF.
    ///
    /// <para>Settable so the pilot harness can encode the same corpus twice in one process — once
    /// with the seam off, once on — and byte-compare the two bitstreams. Read only by
    /// <c>H264WeftPilot.Install</c>, i.e. when an encoder builds its kernel set, so flipping it
    /// affects encoders constructed afterwards and never an encoder mid-frame.</para>
    /// </summary>
    public static bool Enabled { get; set; } = ReadFlag("KILN_WEFT_BATCH_SAD");

    /// <summary>
    /// Largest search range served, from <c>KILN_WEFT_BATCH_SAD_MAX_RANGE</c> (default 8 — the
    /// K = 289 shape every prior packet in this pilot measured). Larger ranges are declined, not
    /// approximated, so the estimator falls back to its own per-candidate kernel.
    /// </summary>
    public static int MaxRange { get; set; } = ReadInt("KILN_WEFT_BATCH_SAD_MAX_RANGE", 8);

    /// <summary>Macroblock searches served by the Weft program.</summary>
    public static long Engaged => Interlocked.Read(ref s_engaged);

    /// <summary>Macroblock searches declined (fell back to the per-candidate kernel).</summary>
    public static long Declined => Interlocked.Read(ref s_declined);

    /// <summary>Of <see cref="Declined"/>, those declined only because the search range exceeded
    /// <see cref="MaxRange"/> — i.e. the ones a wider compiled set would have served.</summary>
    public static long DeclinedForRange => Interlocked.Read(ref s_declinedRange);

    /// <summary>Parametric programs compiled — one per (plane geometry, search range).</summary>
    public static long Compiles => Interlocked.Read(ref s_compiles);

    /// <summary>Total wall time spent in those compiles.</summary>
    public static double CompileMilliseconds => Volatile.Read(ref s_compileMs);

    public static void ResetCounters()
    {
        Interlocked.Exchange(ref s_engaged, 0);
        Interlocked.Exchange(ref s_declined, 0);
        Interlocked.Exchange(ref s_declinedRange, 0);
    }

    private static bool ReadFlag(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return v is "1" or "true" or "TRUE" or "True";
    }

    private static int ReadInt(string name, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    public bool TryScoreSad16x16Grid(
        ReadOnlySpan<byte> current, int currentStride,
        ReadOnlySpan<byte> reference, int referenceStride, int referenceHeight,
        int originX, int originY,
        int searchRange,
        out ReadOnlySpan<int> sadGrid)
    {
        sadGrid = default;

        var planeW = referenceStride;
        var planeH = referenceHeight;
        var m = searchRange;

        // Everything this provider cannot answer EXACTLY, it declines. Each clause is a real case:
        //  - range outside the compiled set (one program is baked per range, so an unserved range
        //    would need another compile; the estimator's per-candidate path is the honest fallback);
        //  - a plane too small to hold a whole ±R window (the parameter box would be empty);
        //  - a grid centre outside the plane — the declared parameter box refuses it, and those
        //    macroblocks have most of their candidates rejected by Fits anyway;
        //  - a current block whose 16 rows are not all readable (the estimator would not read them
        //    either unless some candidate passes Fits, so staging them first must not fault).
        if (searchRange < 1 || searchRange > MaxRange ||
            planeW < Block + (2 * m) || planeH < Block + (2 * m) ||
            (uint)originX > (uint)(planeW - Block) || (uint)originY > (uint)(planeH - Block) ||
            currentStride < Block || current.Length < ((Block - 1) * currentStride) + Block ||
            reference.Length < (long)planeH * planeW)
        {
            Interlocked.Increment(ref s_declined);
            if (searchRange > MaxRange)
            {
                Interlocked.Increment(ref s_declinedRange);
            }

            return false;
        }

        var session = GetSession(planeH, planeW, searchRange);
        var tw = planeW + (2 * m);

        // Reference tile: rows [originY - R, originY + R + 16), cols [originX - R, originX + R + 16),
        // clipped to the plane, written at the plane coordinates the program reads from (plane
        // sample (r, c) lives at tensor (r + R, c + R)).
        var refDst = session.Reference;
        var r0 = Math.Max(0, originY - m);
        var r1 = Math.Min(planeH, originY + m + Block);
        var c0 = Math.Max(0, originX - m);
        var c1 = Math.Min(planeW, originX + m + Block);
        var tileWidth = c1 - c0;
        for (var r = r0; r < r1; r++)
        {
            reference.Slice((r * referenceStride) + c0, tileWidth)
                .CopyTo(refDst.Slice(((r + m) * tw) + c0 + m, tileWidth));
        }

        // Current block, at the same origin the parametric Slice reads.
        var curDst = session.Current;
        for (var row = 0; row < Block; row++)
        {
            current.Slice(row * currentStride, Block)
                .CopyTo(curDst.Slice(((originY + m + row) * tw) + originX + m, Block));
        }

        session.Binding.SetParameters(("py", originY + m), ("px", originX + m));
        session.Binding.Run();

        sadGrid = session.Sads.AsReadOnlySpan<int>();
        Interlocked.Increment(ref s_engaged);
        return true;
    }

    private static Session GetSession(int planeH, int planeW, int range)
    {
        var cached = t_lastSession;
        if (cached is not null && cached.Matches(planeH, planeW, range))
        {
            return cached;
        }

        var key = (Environment.CurrentManagedThreadId, planeH, planeW, range);
        var session = Sessions.GetOrAdd(key, static k => new Session(k.PlaneH, k.PlaneW, k.Range));
        t_lastSession = session;
        return session;
    }

    /// <summary>Per-thread execution state for one geometry: the two staged input tensors and the
    /// binding that reads them. The <see cref="CompiledProgram"/> behind it is shared.</summary>
    private sealed class Session
    {
        private readonly int _planeH;
        private readonly int _planeW;
        private readonly int _range;

        public Session(int planeH, int planeW, int range)
        {
            _planeH = planeH;
            _planeW = planeW;
            _range = range;

            var program = Programs.GetOrAdd(
                (planeH, planeW, range),
                static k => new Lazy<CompiledProgram>(
                    () =>
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var p = CompileParametric(k.PlaneH, k.PlaneW, k.Range);
                        sw.Stop();
                        Interlocked.Increment(ref s_compiles);
                        lock (Programs)
                        {
                            s_compileMs += sw.Elapsed.TotalMilliseconds;
                        }

                        return p;
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;

            var type = TensorTypeFor(planeH, planeW, range);
            var cur = Tensor.Allocate(type);
            var refp = Tensor.Allocate(type);
            CurrentTensor = cur;
            ReferenceTensor = refp;
            Binding = program.Bind(
                new Dictionary<string, Tensor> { ["cur"] = cur, ["ref"] = refp },
                parameters: new Dictionary<string, int> { ["py"] = range, ["px"] = range });
            Sads = Binding.Output("sads");
        }

        public ExecutionBinding Binding { get; }

        public Tensor Sads { get; }

        private Tensor CurrentTensor { get; }

        private Tensor ReferenceTensor { get; }

        public Span<byte> Current => CurrentTensor.AsSpan<byte>();

        public Span<byte> Reference => ReferenceTensor.AsSpan<byte>();

        public bool Matches(int planeH, int planeW, int range) =>
            _planeH == planeH && _planeW == planeW && _range == range;
    }

    private static TensorType TensorTypeFor(int planeH, int planeW, int range) =>
        new(new Shape(planeH + (2 * range), planeW + (2 * range)), ElementType.U8);

    /// <summary>
    /// The one parametric program. Real production pipeline — <c>GraphBuilder</c> →
    /// <c>ElementwiseFusionOptimizer.Compile</c> → <c>RegionMergePass.Run(<see cref="SadArms"/>)</c>
    /// → <c>PlanVerifier.Verify().ThrowIfInvalid()</c> → <c>IlRenderer.Render</c> — the same one
    /// PP4's <c>ParametricKilnGraph.CompileParametric</c> uses.
    /// </summary>
    private static CompiledProgram CompileParametric(int planeH, int planeW, int range)
    {
        var type = TensorTypeFor(planeH, planeW, range);
        var b = new GraphBuilder("kiln-weft-batch-sad");
        var cur = b.Input("cur", type);
        var refp = b.Input("ref", type);

        // Origins are tensor coordinates: plane sample (r, c) sits at (r + range, c + range), so an
        // origin anywhere in the plane keeps its whole ±range window inside the tensor.
        var py = b.Parameter("py", range, planeH - Block + range);
        var px = b.Parameter("px", range, planeW - Block + range);

        var side = (2 * range) + 1;
        var offsets = new int[side * side][];
        for (var k = 0; k < offsets.Length; k++)
        {
            // (dy + range) * side + (dx + range) — the index H264MotionEstimator's ring loop reads.
            offsets[k] = [(k / side) - range, (k % side) - range];
        }

        var curBlock = b.Slice(cur, [(ParamOffset)py, (ParamOffset)px], [Block, Block]);
        var windows = b.ExtractWindows(refp, offsets, [Block, Block], origin: [(ParamOffset)py, (ParamOffset)px]);
        var broadcast = b.Broadcast(curBlock, [offsets.Length, Block, Block]);
        var diff = b.Sub(b.Cast(broadcast, ElementType.I32), b.Cast(windows, ElementType.I32));
        b.Sink("sads", b.Reduce(b.Abs(diff), ReduceKind.Sum, [1, 2], "motion"));

        var graph = b.Build();
        var plan0 = new ElementwiseFusionOptimizer().Compile(graph, new CompileOptions());
        var plan = RegionMergePass.Run(plan0, SadArms());
        PlanVerifier.Verify(plan).ThrowIfInvalid();
        return new IlRenderer(providers: [.. SadArms().Cast<IKernelProvider>()]).Render(plan);
    }

    /// <summary>
    /// Every Weft arm that can serve this region, in one place, so the region pass and the renderer
    /// cannot disagree about what is available.
    ///
    /// <para><b>Why both.</b> The graph above reduces a rank-3 batched domain, so its idiom is
    /// <c>sad.u8.sum.batched</c>. Weft ships two arms for that idiom on the two ISAs kiln runs on:
    /// <c>SadBatchedU8Provider</c> (<c>RequiredIsa = arm64-advsimd</c>) and
    /// <c>SadBatchedU8Sse2Provider</c> (<c>RequiredIsa = x64-sse2</c>). Registering only the first —
    /// which is what this pilot did until now — left the x86 path with <em>no</em> SIMD SAD arm
    /// registered at all: the region fell to Weft's generic lowering while still paying the pilot's
    /// per-call staging and <c>SetParameters</c> cost. That is a defect in the pilot on its own
    /// terms; it advertises a batched SIMD SAD seam and on x86 did not have one.</para>
    ///
    /// <para>Each provider gates on its own <c>RequiredIsa</c>, so exactly one can claim on a given
    /// machine and the arm64 behaviour is unchanged by the second entry.</para>
    ///
    /// <para><b>Why the SHARED arms come first, and why that ordering is the whole point.</b>
    /// <c>RegionMergePass</c> takes the <em>first interested</em> provider, so this order is the
    /// selection. Weft ships a second pair for the same idiom — <c>SadSharedU8Provider</c>
    /// (<c>arm64-advsimd</c>) and <c>SadSharedU8SseProvider</c> (<c>x64-ssse3</c>) — which slide one
    /// register file across neighbouring candidates instead of reloading every window. That is
    /// exactly the load-traffic deficit <c>experiments/kiln-pilot</c> Packet 2 measured as the
    /// largest single gap between Weft and an expert kernel (9,248 loads/MB against 816).</para>
    ///
    /// <para>It matters more than it sounds, because <b>registering only the batched pair does not
    /// pay.</b> Priced at kiln's own geometry (R=8, K=289, 1080p plane) on an AVX2 host, with kiln's
    /// own <c>H264MotionSad.Sad16x16Avx2</c> composition as the control — the kernel the seam
    /// actually replaces, not Weft's generic lowering:</para>
    /// <code>
    ///   generic lowering (no arm registered)  450.5 us/MB   0.026x   <- what the pilot did before
    ///   sad-u8-batched-psadbw                  11.8 us/MB   0.983x   <- a WASH against kiln's own
    ///   sad-u8-shared-palignr                   6.6 us/MB   1.761x
    ///   kiln's own AVX2 loop (CONTROL)         11.6 us/MB   1.000x
    /// </code>
    /// <para>All four arms were verified to agree elementwise over 200 macroblocks x 289 candidates
    /// before any timing was taken. Receipts and method:
    /// <c>experiments/kiln-surface/</c> in the Weft repo (§4), whose end-to-end run also shows this
    /// ordering erasing the 0-2.7% x86 regression the re-baselining packet measured.</para>
    ///
    /// <para><b>NOT MEASURED:</b> the arm64 half. This list now offers <c>SadSharedU8Provider</c>
    /// ahead of <c>SadBatchedU8Provider</c> on arm64 too, and that reorder has not been benchmarked
    /// there. It is safe by construction — each arm gates on its own <c>RequiredIsa</c> and the seam
    /// is default-off — but the x86 ratios above must not be read as an arm64 claim.</para>
    /// </summary>
    private static IRegionProvider[] SadArms() =>
    [
        new SadSharedU8Provider(),
        new SadSharedU8SseProvider(),
        new SadBatchedU8Provider(),
        new SadBatchedU8Sse2Provider(),
    ];

    // ---- IH264KernelSet: everything else is the wrapped set, unchanged --------------------------

    public int Sad16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        _inner.Sad16x16(a, strideA, b, strideB);

    public int Sad16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        _inner.Sad16x8(a, strideA, b, strideB);

    public int Sad8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        _inner.Sad8x16(a, strideA, b, strideB);

    public int Sad8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        _inner.Sad8x8(a, strideA, b, strideB);

    public void SadMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> sads, int count) =>
        _inner.SadMany4x4(src, predConcat, sads, count);

    public int SadIntra16x16(ReadOnlySpan<byte> src256, ReadOnlySpan<byte> pred256, int srcStride) =>
        _inner.SadIntra16x16(src256, pred256, srcStride);

    public int SadChromaPair(ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV, ReadOnlySpan<byte> predU, ReadOnlySpan<byte> predV) =>
        _inner.SadChromaPair(srcU, srcV, predU, predV);

    public int Ssd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        _inner.Ssd16x16(a, strideA, b, strideB);

    public int Ssd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        _inner.Ssd8x8(a, strideA, b, strideB);

    public int VarianceMb16x16(ReadOnlySpan<byte> mbTopLeft, int stride) =>
        _inner.VarianceMb16x16(mbTopLeft, stride);

    public int Satd16x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        _inner.Satd16x16(a, strideA, b, strideB);

    public int Satd16x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        _inner.Satd16x8(a, strideA, b, strideB);

    public int Satd8x16(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        _inner.Satd8x16(a, strideA, b, strideB);

    public int Satd8x8(ReadOnlySpan<byte> a, int strideA, ReadOnlySpan<byte> b, int strideB) =>
        _inner.Satd8x8(a, strideA, b, strideB);

    public void SatdMany4x4(ReadOnlySpan<byte> src, ReadOnlySpan<byte> predConcat, Span<int> satds, int count) =>
        _inner.SatdMany4x4(src, predConcat, satds, count);

    public void Predict4x4(int mode, ReadOnlySpan<byte> topRow, ReadOnlySpan<byte> leftCol, bool topAvail, bool leftAvail, Span<byte> dst16) =>
        _inner.Predict4x4(mode, topRow, leftCol, topAvail, leftAvail, dst16);

    public void PredictIntra16x16(int mode, ReadOnlySpan<byte> topRow, bool topAvail, ReadOnlySpan<byte> leftCol, bool leftAvail, byte topLeft, bool topLeftAvail, Span<byte> dst256) =>
        _inner.PredictIntra16x16(mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, dst256);

    public int EncodeResidual4x4(ReadOnlySpan<byte> src16, ReadOnlySpan<byte> pred16, int qp, Span<short> zigzagCoeffOut16, Span<byte> recDst, int recStride) =>
        _inner.EncodeResidual4x4(src16, pred16, qp, zigzagCoeffOut16, recDst, recStride);

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
        _inner.ApplyDeblock(y, strideY, u, v, strideUv, mbWidth, mbHeight, bsHorizontal, bsVertical, qpY, qpUv, alphaOffsetDiv2, betaOffsetDiv2);

    public void InterpolateLuma(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride) =>
        _inner.InterpolateLuma(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac, blockWidth, blockHeight, dst, dstStride);

    public void InterpolateChroma(
        ReadOnlySpan<byte> src, int srcStride,
        int srcOriginX, int srcOriginY,
        int xFrac, int yFrac,
        int blockWidth, int blockHeight,
        Span<byte> dst, int dstStride) =>
        _inner.InterpolateChroma(src, srcStride, srcOriginX, srcOriginY, xFrac, yFrac, blockWidth, blockHeight, dst, dstStride);

    public void GatherSrcBlock4x4(ReadOnlySpan<byte> srcY, int baseOff, int strideY, Span<byte> dst16) =>
        _inner.GatherSrcBlock4x4(srcY, baseOff, strideY, dst16);

    public void GatherChroma8x8(ReadOnlySpan<byte> src, int stride, int bx, int by, Span<byte> dst64) =>
        _inner.GatherChroma8x8(src, stride, bx, by, dst64);

    public void ForwardDct4x4(ReadOnlySpan<short> residual4x4, Span<int> outCoeff) =>
        _inner.ForwardDct4x4(residual4x4, outCoeff);

    public void Quant4x4(Span<int> block, int qp) => _inner.Quant4x4(block, qp);
}
#endif
