using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
namespace Kiln.Internal.H264;

/// <summary>Builds one slice RBSP (header + macroblock layers) for an entire frame.</summary>
internal sealed class H264BaselineSliceEncoder
{
    // ── Neighbour-context grids ────────────────────────────────────────────────────────────────
    //
    // Both the CAVLC nC derivation (ITU-T H.264 §9.2.1) and the Intra_4×4 most-probable-mode
    // derivation (§8.3.1.1) need, for a block at geometric position (row, col) inside the current
    // macroblock, the value belonging to neighbour A (immediately to the left) and neighbour B
    // (immediately above), as located by §6.4.11.4. Those neighbours are inside the current
    // macroblock for interior blocks and inside the left / above macroblock for edge blocks.
    //
    // Kiln represents that with a plain 2-D grid holding the current macroblock's blocks in
    // geometric row-major order, padded by a one-entry halo along the top and left edges that is
    // pre-loaded from the neighbouring macroblocks. With the halo in place, A and B are always at
    // (row, col-1) and (row-1, col), i.e. `slot - 1` and `slot - stride`, with no per-block
    // neighbour lookup and no availability branch in the inner loop: unavailable neighbours are
    // pre-seeded with a sentinel by the fill routines.
    //
    // Luma: 4×4 blocks per §6.4.3, so a 4×4 body plus halo → stride 5, 25 slots.
    // Chroma (4:2:0): 2×2 blocks per component per §6.4.7, so a 2×2 body plus halo → stride 3,
    // 9 slots per component; the two components use consecutive 9-slot windows.

    /// <summary>Row stride of the luma 4×4 neighbour-context grid (4 blocks + one halo column).</summary>
    private const int LumaCtxStride = 5;

    /// <summary>Total slots in the luma 4×4 neighbour-context grid.</summary>
    private const int LumaCtxSlots = LumaCtxStride * 5;

    /// <summary>Row stride of one component's chroma 4×4 neighbour-context grid (2 blocks + one halo column).</summary>
    private const int ChromaCtxStride = 3;

    /// <summary>Slots per component in the chroma neighbour-context grid.</summary>
    private const int ChromaCtxSlotsPerComponent = ChromaCtxStride * 3;

    /// <summary>Total slots in the two-component chroma neighbour-context grid (Cb window then Cr window).</summary>
    private const int ChromaCtxSlots = ChromaCtxSlotsPerComponent * 2;

    /// <summary>
    /// Slot of the luma 4×4 block at geometric (<paramref name="row"/>, <paramref name="col"/>) inside the
    /// macroblock. Accepts −1 for the halo row/column that carries the above / left macroblock's edge blocks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int LumaCtxSlot(int row, int col) => (row + 1) * LumaCtxStride + col + 1;

    /// <summary>
    /// Slot of the chroma 4×4 block at geometric (<paramref name="row"/>, <paramref name="col"/>) of component
    /// <paramref name="component"/> (0 = Cb, 1 = Cr). Accepts −1 for the halo row/column.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ChromaCtxSlot(int component, int row, int col) =>
        component * ChromaCtxSlotsPerComponent + (row + 1) * ChromaCtxStride + col + 1;

    /// <summary>
    /// Inter-prediction SATD16×16 above which the P-slice intra fallback also evaluates an Intra_4×4
    /// candidate (in addition to Intra_16×16). The I_4×4 mode search is the expensive part, so it is
    /// reserved for macroblocks whose inter match already failed — ~16/pixel average Hadamard residual.
    /// Below this, the inter prediction is good enough that intra cannot win and the search is skipped.
    /// </summary>
    private const int I4x4InPInterSatdGate = 3000;

    /// <summary>
    /// Rough extra bit cost charged to the I_4×4 candidate over the I_16×16 estimate in the P-slice
    /// intra RD comparison: I_NxN signals up to 16 per-block prediction-mode flags vs I_16×16's compact
    /// header. Used only for candidate ranking, so an approximate constant suffices.
    /// </summary>
    private const int I4x4InPModeBitsEstimate = 16;

    /// <summary>
    /// Phase F SAD-domain lambda for intra mode RDO. Returned at the per-QP sweep optimum.
    /// </summary>
    /// <remarks>
    /// Picked by the dense sweep <c>KILN_LAMBDA_SWEEP=1 dotnet test … H264LambdaSweepTests</c>
    /// over λ ∈ {0..6, 8, 12} at QPs {22, 28, 34} on both committed fixtures (320×240 + 256×224).
    /// Selection rule: highest **average pairwise PSNR vs the committed golden reference fixtures**
    /// across both resolutions,
    /// with a worst-case (min) tiebreaker for picks within ~0.2 dB of each other.
    ///
    /// Per-QP measurements (320×240 / 256×224 pairwise dB → average):
    ///   QP=22 → λ=3 picked: 23.61 / 23.24 → 23.43 (λ=4 averaged 23.47 but lost worst-case 22.67 vs 23.24)
    ///   QP=28 → λ=2 kept:    22.54 / 23.14 → 22.84 (λ=12 averaged 22.99 but only by 0.15 dB,
    ///                                                lost worst-case, and shifts encoder behaviour
    ///                                                from the validated state — preserve stability)
    ///   QP=34 → λ=6 picked: 24.49 / 24.00 → 24.25 (next-best λ=8 trails by ~1.4 dB)
    ///
    /// Net quality gain vs the previous {0, 1, 2, 4, 5, 7} table: QP=22 +0.51 dB, QP=28 ±0,
    /// QP=34 +1.16 dB on average pairwise PSNR.
    ///
    /// The non-monotonic 1→3→2→6 trajectory across QP ranges is real (sweep noise on its own does
    /// not flip the optima) and likely reflects how the SAD-only RDO interacts with the widened
    /// Intra4x4 mode set: at low QP residual cost dominates so a higher λ filters directional modes
    /// whose residuals quantize worse; at QP=28 there is a stable local minimum at λ=2; at high QP
    /// the rate term matters more again as residuals collapse, so λ rises sharply. Re-sweep after
    /// Intra16x16 / inter RD competition lands — the optima will shift.
    ///
    /// Off-sweep buckets (≤18 and ≥37): conservative extrapolation. The standard H.264 SAD λ formula
    /// 0.85·2^((QP-12)/6) climbs to ~27 at QP=42 but the encoder's measured optima track much lower,
    /// so we extrapolate by adding a single tick (6 → 8 → 10) per 6 QP steps rather than following
    /// the formula. Re-sweep into these buckets if a deployment exercises them in earnest.
    /// </remarks>
    private static int LambdaSadForQp(int qp)
    {
        if (qp <= 18) return 1;
        if (qp <= 24) return 3;
        if (qp <= 30) return 2;
        if (qp <= 36) return 6;
        if (qp <= 42) return 8;
        return 10;
    }

    /// <summary>
    /// SATD-domain lambda for the two-stage I4×4 mode decision.
    /// Formula: (int)(0.85 × 2^((QP−12)/3) + 0.5) — the standard H.264 mode-decision lambda.
    /// Unlike the SAD lambda above (which is empirically swept), this follows the standard formula
    /// directly; QP=28 gives ~34. Full per-QP sweep is a TODO once golden fixtures are locked.
    /// </summary>
    /// <remarks>
    /// Values below were computed as Math.Max(1, (int)(0.85 * Math.Pow(2.0, (qp - 12) / 3.0) + 0.5)).
    /// QP=28 smoke-tested at ~34 (acceptance gate); remaining entries are formula-derived.
    /// TODO: run H264LambdaSweepTests with KILN_LAMBDA_SWEEP=1 to tune per-QP entries.
    /// </remarks>
    private static readonly int[] LambdaSatdTable =
    [
        // QP= 0   1   2   3   4   5   6   7   8   9  10  11  12  13  14
               1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
        // QP=15  16  17  18  19  20  21  22  23  24
               2,  2,  3,  3,  4,  5,  7,  9, 11, 14,
        // QP=25  26  27  28  29  30  31  32  33  34
              17, 22, 27, 34, 43, 54, 69, 86,109,137,
        // QP=35  36  37  38  39  40  41  42  43  44
             173,218,274,345,435,548,691,870,1097,1382,
        // QP=45  46  47  48  49  50  51
            1741,2193,2763,3482,4387,5527,6963,
    ];

    private static int LambdaSatdForQp(int qp) => LambdaSatdTable[Math.Clamp(qp, 0, 51)];

    /// <summary>
    /// H.264 4×4 luma block order within the MB (Z-order within each 8×8): scan index → 4×4 row in MB
    /// (<c>n / 4</c> in raster block coords). Matches residual / prev_intra4x4 order (see Rec. ITU-T H.264 7.3.5).
    /// </summary>
    private static ReadOnlySpan<byte> ScanIdxToBr =>
    [
        0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 3, 3, 2, 2, 3, 3,
    ];

    /// <summary>Scan index → 4×4 column in MB (<c>n % 4</c> in raster block coords).</summary>
    private static ReadOnlySpan<byte> ScanIdxToBc =>
    [
        0, 1, 0, 1, 2, 3, 2, 3, 0, 1, 0, 1, 2, 3, 2, 3,
    ];

    private readonly int _width;
    private readonly int _height;
    private readonly int _mbW;
    private readonly int _mbH;
    private readonly int _mbCount;
    private readonly int _qp;
    /// <summary>
    /// RD λ for chroma-DC refinement in <c>PrepareChroma8x8</c>. When the host sets
    /// <see cref="H264BaselineEncoderOptions.ChromaDcRdLambda"/>, this fixed value is used; otherwise λ is
    /// derived from <c>qpThisMb</c> each macroblock so rate control / AQ stay consistent with luma RD.
    /// </summary>
    private readonly double _chromaDcRdLambda;
    /// <summary>True when <see cref="_chromaDcRdLambda"/> was explicitly provided (not derived per-MB from luma QP).</summary>
    private readonly bool _chromaDcRdLambdaUserOverride;
    /// <summary>SAD-domain lambda for intra mode RDO; J = SAD + lambda*R. Standard H.264 lambda for SAD: lambda_motion = 0.85 * 2^((QP-12)/6).</summary>
    private readonly int _intra4x4LambdaSad;
    /// <summary>SATD-domain lambda for two-stage I4×4 mode decision; J = SATD + lambda*R. Standard H.264 lambda for SATD: 0.85 * 2^((QP-12)/3).</summary>
    private readonly int _intra4x4LambdaSatd;
    /// <summary>Non-null when the host passed <see cref="H264BaselineEncoderOptions.Intra4x4SadLambda"/> — chroma intra RDO then uses this fixed λ instead of <see cref="LambdaSadForQp"/>.</summary>
    private readonly int? _intra4x4SadLambdaUserOverride;
    /// <summary>
    /// Shared per-frame state owned by the orchestrator and reused by every slice encoder coding
    /// the same access unit. Buffers below alias arrays inside <see cref="_shared"/>; the fields
    /// keep their original names so the hot encode path is untouched. See
    /// <see cref="H264FrameSharedState"/> for the safety contract.
    /// </summary>
    private readonly H264FrameSharedState _shared;
    private readonly byte[] _recY;
    private readonly byte[] _recU;
    private readonly byte[] _recV;
    private readonly byte[] _nonZeros;
    /// <summary>
    /// Per-macroblock Intra_4×4 boundary prediction modes, <see cref="IntraModeBoundaryStride"/> bytes per MB.
    /// </summary>
    /// <remarks>
    /// A later macroblock only ever reads its neighbours' <em>edge</em> blocks: per H.264 §6.4.11.4 the MB to
    /// the right reads this MB's rightmost 4×4 column (blocks (0..3, 3)) as its A neighbours, and the MB
    /// below reads this MB's bottom 4×4 row (blocks (3, 0..3)) as its B neighbours. The interior twelve
    /// modes are never referenced again, so only those seven distinct blocks are retained:
    /// <list type="bullet">
    ///   <item><description>bytes 0..3 — right column, top to bottom: modes of blocks (0,3), (1,3), (2,3), (3,3).</description></item>
    ///   <item><description>bytes 4..6 — bottom row, left to right: modes of blocks (3,0), (3,1), (3,2); block
    ///   (3,3) is the shared corner and already occupies byte 3.</description></item>
    /// </list>
    /// <c>0xFF</c> marks a macroblock that was not coded as Intra_4×4 (see <see cref="ResetForFrame"/>).
    /// </remarks>
    private readonly byte[] _intraModes;
    /// <summary>Per-MB chroma 4×4 non-zero counts: U then V (4 + 4 per macroblock).</summary>
    private readonly byte[] _chromaNonZeros;
    /// <summary>Per-MB horizontal-edge boundary strength array (H.264 8.7.2.1) — 16 entries per MB (4 edges × 4 segments).</summary>
    private readonly byte[] _bsHorizontal;
    /// <summary>Per-MB vertical-edge boundary strength array (H.264 8.7.2.1) — 16 entries per MB (4 edges × 4 segments).</summary>
    private readonly byte[] _bsVertical;
    /// <summary>Per-MB luma QP (H.264 8.7.2 alpha/beta indexing); written each MB from rate control ± AQ.</summary>
    private readonly int[] _qpY;
    /// <summary>Per-MB chroma QP (H.264 8.7.2 chroma deblock alpha/beta indexing).</summary>
    private readonly int[] _qpUv;

    /// <summary>Halo width on every side of the luma reference picture (H.264 8.4.2.1) — sized for the 16-pel MB body plus the 6-tap qpel filter window. Constant across the encoder lifetime.</summary>
    private const int HaloLuma = H264FrameSharedState.HaloLuma;
    /// <summary>Halo width on every side of each chroma reference plane — sized for the 8-pel chroma block plus the bilinear filter halo (H.264 8.4.1.4).</summary>
    private const int HaloChroma = H264FrameSharedState.HaloChroma;

    private readonly byte[] _paddedRefY;
    private readonly byte[] _paddedRefU;
    private readonly byte[] _paddedRefV;
    private readonly int _paddedStrideY;
    private readonly int _paddedStrideUv;
    /// <summary>
    /// Whether the reference picture buffers hold a usable reconstruction from the previous coded frame.
    /// Backed by <see cref="H264FrameSharedState.DpbCount"/> so every slice encoder coding the same
    /// access unit observes the same flag. Mutated only outside the parallel slice region.
    /// </summary>
    private bool _paddedRefValid => _shared.DpbCount > 0;

    /// <summary>Per-MB committed luma motion vector (qpel units) for the current slice's MV predictor cache (H.264 8.4.1.3.1). Stores partition-0 MV for sub-partition MBs. Zero for intra MBs / unset for not-yet-coded MBs.</summary>
    private readonly H264MotionEstimator.Mv[] _mbMvs;
    /// <summary>Per-MB sub-partition MVs in partition-index order (0..3). Only slots 0..N-1 are valid where N is the partition count for the MB's shape. Always 4 entries per MB; unused slots are zero.</summary>
    private readonly H264MotionEstimator.Mv[] _mbSubPartMvs;
    /// <summary>Per-MB partition shape, populated for inter MBs (P_16×16, P_16×8, P_8×16, P_8×8).</summary>
    private readonly H264MotionEstimator.McPartition[] _mbPartitions;
    /// <summary>True for MBs encoded as inter (skip or P_L0_16x16) in the current slice; false for intra MBs. Drives BS computation and MV-cache reads.</summary>
    private readonly bool[] _mbIsInter;
    /// <summary>True for MBs emitted as P_Skip; used during slice-data writing to choose between counting toward mb_skip_run and emitting a full mb layer.</summary>
    private readonly bool[] _mbIsSkip;

    private readonly H264RbspBitBuffer _rbspBuffer = new(initialCapacity: 64 * 1024);

    /// <summary>
    /// Test-only hook: invoked after each MB is written (not set in production).
    /// Callback receives (sequentialSliceIndex, mbIndex, bitsBeforeMb, bitsAfterMb).
    /// sequentialSliceIndex is determined by test callers via the SliceStartHook counter.
    /// </summary>
    internal static Action<int, int, int, int>? MbBitTraceHook;

    /// <summary>
    /// Test-only hook: invoked at the start of <see cref="EncodeSliceRbsp"/> (before any bits are written).
    /// Callback receives the sequential slice index (incremented by the caller's wrapper).
    /// Provides the slice header end bit offset after the call returns via a companion dictionary keyed
    /// by sequential slice index.
    /// </summary>
    internal static Action<int, int, bool>? SliceHeaderBitsHook;  // (sliceSeq, headerBits, isPslice)


    private int _sliceSeq = -1;
    private int _currentFrameNum;
    private int _currentCodedFrameIndex = -1;

    // Count of MBs in the current slice that ran (or will run) sub-partition search.
    // Once this exceeds _subPartBudget, the sub-partition range cap drops to 4 for remaining MBs.
    private int _subPartMbCount;
    // Per-slice sub-partition search budget, recomputed at each slice start as SliceMbCount /
    // SubPartBudgetMbDivisor (see EncodeSliceRbsp). Slices partition the frame, so the per-frame
    // total is the same fraction of the frame's MB count regardless of SliceCount — the budget no
    // longer couples output quality to the slice configuration (the old fixed 32 per
    // slice encoder multiplied the frame total by the slice count, and was additionally reset only
    // by slice 0's encoder, leaving encoders 1..N-1 permanently over budget after the first frame).
    private int _subPartBudget;
    // Divisor 4 measured on 640x480 (sweep of budgets {32/slice-encoder, mb/8, mb/4, mb/2,
    // unlimited} x contents x QP {23,28,35} x slices {1,4,8}): it recovers +2.2 dB of the +3.2 dB
    // the legacy fixed budget cost on sustained-motion content at QP 23, is within noise of
    // unlimited on typical content (where any proportional budget also costs the same time as
    // unlimited), and still bounds pathological-content encode time to ~1.7x the legacy budget
    // (unlimited: ~2.1x). Raising it further trades ~10% more worst-case time for ~+0.8 dB.
    private const int SubPartBudgetMbDivisor = 4;
    private readonly int _subPartitionRangeCap;

    // Deterministic per-slice motion-search effort budget (see
    // H264BaselineEncoderOptions.MotionSearchEffortCapPerMb): an equal share of the frame budget,
    // fixed at construction (capPerMb x frame MB count / slice count — equal, not proportional to
    // the slice's MB count, because the effort-balanced partition deliberately gives high-motion
    // bands fewer rows; a proportional budget would bind on exactly the slice the balancer already
    // equalised). Consumption is read from the thread-local
    // H264MotionEstimator.ThreadSearchEffort as a delta from the slice start — each slice encodes
    // sequentially on a single worker thread, so the delta contains exactly this slice's work and
    // the resulting decisions are independent of thread scheduling.
    private readonly long _meEffortBudget;
    private long _meEffortAtSliceStart;

    /// <summary>First MB row of the current slice — used to scope deblocking to the slice range.</summary>
    private int _firstMbRowInSlice;
    /// <summary>When true, emits <c>disable_deblocking_filter_idc=2</c> (within-slice only) in the slice header.</summary>
    private bool _filterAcrossSlicesDisabled;

    /// <summary>
    /// Per-slice rate control (see <see cref="H264BaselineEncoderOptions.TargetBitsPerFrame"/>).
    /// When the effective per-frame bit budget is 0, <see cref="H264RateControl.NextMbQp"/> returns the slice
    /// base QP for every MB; otherwise QP tracks accumulated bit spend against the target.
    /// </summary>
    private readonly H264RateControl _rateControl;
    /// <summary>Previous MB's coded QP — used to compute mb_qp_delta = qpThisMb - prevMbQp per H.264 7.4.5.</summary>
    private int _lastMbQp;

    /// <summary>When true: one fractional-ME refinement pass (vs two) + skip chroma-DC coordinate refinement on **inter** chroma.</summary>
    private readonly bool _preferRealtimeLatencyTuning;
    /// <summary>When true: slice disables in-loop filtering (IDC=1); encoder skips deblock apply.</summary>
    private readonly bool _lightweightDeblocking;
    private readonly bool _fastSearch;
    private readonly bool _useMotionSatd;
    private readonly bool _enableIntraInPFallback;
    private readonly int? _experimentalZeroMvSkipSadThreshold;
    private readonly bool _useTrellis;
    private readonly double _aqStrength;
    private readonly int[] _mbAqQpOffset;
    private readonly IH264KernelSet _kernels;

    public H264BaselineSliceEncoder(
        int width,
        int height,
        int qp,
        double chromaDcRdLambda = double.NaN,
        int? intra4x4SadLambdaOverride = null,
        bool preferRealtimeLatencyTuning = false,
        bool lightweightDeblocking = false,
        bool fastSearch = true,
        int trellisLevel = 0,
        H264FrameSharedState? sharedState = null,
        double adaptiveQuantStrength = 0,
        int targetBitsPerFrame = 0,
        IH264KernelSet? kernels = null,
        bool useMotionSatd = true,
        bool enableIntraInPFallback = true,
        int? experimentalZeroMvSkipSadThreshold = null,
        int subPartitionRangeCap = 16,
        long motionSearchEffortSliceBudget = 0)
    {
        if (width <= 0 || height <= 0 || (width & 15) != 0 || (height & 15) != 0)
        {
            throw new ArgumentException("Picture size must be positive multiples of 16.");
        }

        _width = width;
        _height = height;
        _mbW = width / 16;
        _mbH = height / 16;
        _mbCount = _mbW * _mbH;
        _qp = Math.Clamp(qp, 0, 51);
        _chromaDcRdLambdaUserOverride = !double.IsNaN(chromaDcRdLambda);
        _chromaDcRdLambda = _chromaDcRdLambdaUserOverride
            ? chromaDcRdLambda
            : H264ChromaDcScale.DefaultChromaDcRdLambdaFromLumaQp(_qp);
        // Lambda for SAD-domain mode RDO. With the full Intra4x4 9-mode set (Junior-C) live, the
        // bit-cost penalty becomes meaningful — SAD-pure selection over-fits to directional modes
        // whose residuals quantize worse than DC/V/H. See LambdaSadForQp for the QP-indexed table
        // tuned against the committed golden fixtures.
        _intra4x4LambdaSad = intra4x4SadLambdaOverride ?? LambdaSadForQp(_qp);
        _intra4x4LambdaSatd = LambdaSatdForQp(_qp);
        _intra4x4SadLambdaUserOverride = intra4x4SadLambdaOverride;

        // Slice encoders share their reconstruction, reference-picture buffers, and per-MB neighbour
        // caches so multiple parallel slice encoders coding the same access unit operate on a single
        // logical frame. Writes from each slice land in disjoint MB indices / row ranges; the existing
        // slice-aware neighbour guards (_firstMbRowInSlice) prevent cross-slice reads.
        _shared = sharedState ?? new H264FrameSharedState(width, height, useMotionSatd: useMotionSatd);
        _recY = _shared.RecY;
        _recU = _shared.RecU;
        _recV = _shared.RecV;
        _nonZeros = _shared.NonZeros;
        _intraModes = _shared.IntraModes;
        _chromaNonZeros = _shared.ChromaNonZeros;
        _bsHorizontal = _shared.BsHorizontal;
        _bsVertical = _shared.BsVertical;
        _qpY = _shared.QpY;
        _qpUv = _shared.QpUv;

        _paddedStrideY = _shared.PaddedStrideY;
        _paddedStrideUv = _shared.PaddedStrideUv;
        _paddedRefY = _shared.PaddedRefY;
        _paddedRefU = _shared.PaddedRefU;
        _paddedRefV = _shared.PaddedRefV;
        _mbMvs = _shared.MbMvs;
        _mbSubPartMvs = _shared.MbSubPartMvs;
        _mbPartitions = _shared.MbPartitions;
        _mbIsInter = _shared.MbIsInter;
        _mbIsSkip = _shared.MbIsSkip;

        // Per-slice rate control: targetBitsPerFrame == 0 gives constant QP (H264RateControl keeps
        // _frameTargetBits at 0; StartFrame(0) preserves the constructor default each slice).
        _rateControl = new H264RateControl(initialQp: _qp, targetBitsPerFrame: targetBitsPerFrame, mbsPerFrame: _mbCount);
        _lastMbQp = _qp;
        _preferRealtimeLatencyTuning = preferRealtimeLatencyTuning;
        _lightweightDeblocking = lightweightDeblocking;
        _fastSearch = fastSearch;
        _useMotionSatd = useMotionSatd;
        _enableIntraInPFallback = enableIntraInPFallback;
        _experimentalZeroMvSkipSadThreshold = experimentalZeroMvSkipSadThreshold.HasValue
            ? Math.Max(0, experimentalZeroMvSkipSadThreshold.Value)
            : null;
        _useTrellis = trellisLevel >= 1;
        _aqStrength = adaptiveQuantStrength;
        _mbAqQpOffset = adaptiveQuantStrength > 0 ? new int[_mbCount] : Array.Empty<int>();
        _kernels = kernels ?? H264KernelSet.CreateBest();
        _subPartitionRangeCap = Math.Max(1, subPartitionRangeCap);
        _meEffortBudget = motionSearchEffortSliceBudget > 0 ? motionSearchEffortSliceBudget : long.MaxValue;
    }

    private void ComputeAqOffsets(ReadOnlySpan<byte> srcY, int strideY)
    {
        var logActs = new double[_mbCount];
        double sumLog = 0;
        for (var mb = 0; mb < _mbCount; mb++)
        {
            var mbx = mb % _mbW;
            var mby = mb / _mbW;
            var rowOff = mby * 16 * strideY + mbx * 16;
            var variance = H264VarianceFastPath.VarianceMb16x16(srcY.Slice(rowOff), strideY);
            var act = Math.Max(variance, 1);
            var la = Math.Log2(act);
            logActs[mb] = la;
            sumLog += la;
        }

        var meanLog = sumLog / _mbCount;
        for (var mb = 0; mb < _mbCount; mb++)
        {
            var logRatio = logActs[mb] - meanLog;
            var offset = (int)Math.Round(_aqStrength * logRatio);
            _mbAqQpOffset[mb] = Math.Clamp(offset, -6, 6);
        }
    }

    internal int MacroblockCount => _mbCount;

    /// <summary>Diagnostic-only: encoder-side reconstructed Y plane (after the most recent <see cref="EncodeSliceRbsp"/>).</summary>
    internal ReadOnlySpan<byte> ReconstructedYPlane => _recY;

    /// <summary>Diagnostic-only: encoder-side reconstructed U plane (after the most recent <see cref="EncodeSliceRbsp"/>).</summary>
    internal ReadOnlySpan<byte> ReconstructedUPlane => _recU;

    /// <summary>Diagnostic-only: encoder-side reconstructed V plane (after the most recent <see cref="EncodeSliceRbsp"/>).</summary>
    internal ReadOnlySpan<byte> ReconstructedVPlane => _recV;

    /// <summary>
    /// Encode one slice RBSP into a reusable internal buffer. The returned span is valid only until the next
    /// call to this method on the same encoder instance.
    /// </summary>
    /// <param name="sliceLumaQp">
    /// Slice luma QP for this coded picture (&lt; 0 omits adjustment: derive from ctor base <see cref="_qp"/>).
    /// Must stay compatible with emitted PPS <c>pic_init_qp_minus26</c>: delta is signaled via slice <c>slice_qp_delta</c>.
    /// </param>
    public ReadOnlySpan<byte> EncodeSliceRbsp(
        ReadOnlySpan<byte> y,
        int strideY,
        ReadOnlySpan<byte> u,
        ReadOnlySpan<byte> v,
        int strideUv,
        bool isIdr,
        bool isPslice,
        int frameNum,
        int idrPicId,
        int sliceLumaQp = -1,
        int firstMbInSlice = 0,
        int mbCountInSlice = -1,
        bool filterAcrossSlicesDisabled = false,
        bool isFirstSliceInFrame = true,
        int codedFrameIndex = -1)
    {
        var collectFramePhases = H264PInterDiagnostics.CollectFramePhases;
        var sliceStartTicks = collectFramePhases ? Stopwatch.GetTimestamp() : 0;
        var effectiveMbCount = mbCountInSlice < 0 ? _mbCount : mbCountInSlice;
        if (firstMbInSlice % _mbW != 0)
        {
            throw new ArgumentException(
                $"firstMbInSlice must be row-aligned (multiple of {_mbW}); got {firstMbInSlice}. " +
                "Non-row-aligned slices violate §6.4.4 neighbour availability for left-edge MBs and " +
                "produce wrong mvp/mvd chains on compliant decoders.",
                nameof(firstMbInSlice));
        }
        _firstMbRowInSlice = firstMbInSlice / _mbW;
        _filterAcrossSlicesDisabled = filterAcrossSlicesDisabled;

        if (isFirstSliceInFrame)
        {
            ResetForFrame();
        }
        // Per-slice sub-partition budget: a fixed fraction of this slice's MB count, so the
        // per-frame total is invariant to how the frame is divided into slices. Reset here (not in
        // ResetForFrame) because in multi-slice frames only slice 0's encoder runs the frame reset;
        // the old frame-reset-only counter left encoders 1..N-1 permanently over budget.
        _subPartMbCount = 0;
        var subPartDivisor = H264PInterDiagnostics.SubPartBudgetDivisorOverride ?? SubPartBudgetMbDivisor;
        _subPartBudget = subPartDivisor switch
        {
            0 => int.MaxValue,
            < 0 => 32, // legacy fixed per-slice-encoder budget (measurement baseline)
            _ => Math.Max(8, effectiveMbCount / subPartDivisor),
        };
        // ME effort budget: snapshot the thread counter before any macroblock encodes, so the
        // ladder in TryEncodePInterMacroblock depends only on this slice's own prior work.
        _meEffortAtSliceStart = H264MotionEstimator.ThreadSearchEffort;
        _currentFrameNum = frameNum;
        _currentCodedFrameIndex = codedFrameIndex;

        // Copy this slice's source luma into the reconstruction buffer so intra prediction
        // can read correct top/left neighbors across the slice boundary before encoding overwrites them.
        var copyStartTicks = collectFramePhases ? Stopwatch.GetTimestamp() : 0;
        CopyPlane2d(
            y.Slice(_firstMbRowInSlice * 16 * strideY), strideY,
            _recY.AsSpan(_firstMbRowInSlice * 16 * _width), _width,
            _width, effectiveMbCount / _mbW * 16);
        var copyTicks = collectFramePhases ? Stopwatch.GetTimestamp() - copyStartTicks : 0;

        // Reference-picture lifecycle: an IDR resets the reference cache (decoder will too via
        // memory_management_control_operation 5 implied by IDR). For a P-slice the cache is
        // populated from the previous slice's deblocked reconstruction (see end-of-method below).
        if (isFirstSliceInFrame && isIdr)
        {
            _shared.DpbCount = 0;
        }

        // Inter is wired only for P-slices that have a prior reference picture available. The very
        // first non-IDR after start-up (or if rate control ever forces back-to-back IDRs) falls back
        // to the existing intra-only path.
        var canUseInter = isPslice && _paddedRefValid;

        if (_aqStrength > 0)
            ComputeAqOffsets(y, strideY);

        _rbspBuffer.Reset();
        var sliceBaseQp = sliceLumaQp < 0 ? _qp : Math.Clamp(sliceLumaQp, 0, 51);
        var sliceQpDeltaRaw = sliceBaseQp - _qp;
        var sliceQpDelta = Math.Clamp(sliceQpDeltaRaw, -52, 51);
        var sliceQpApplied = Math.Clamp(_qp + sliceQpDelta, 0, 51);

        _sliceSeq++;
        WriteSliceHeader(_rbspBuffer, isIdr, isPslice, frameNum, idrPicId, sliceQpDelta, firstMbInSlice);
        var bitLengthAfterHeader = _rbspBuffer.BitLength;
        SliceHeaderBitsHook?.Invoke(_sliceSeq, bitLengthAfterHeader, isPslice);

        // Rate control: single-slice uses full-picture budget + global MB index. Multi-slice + TargetBitsPerFrame
        // uses this slice's proportional budget and slice-local MB indices so cumTarget matches _cumSpentThisFrame.
        if (effectiveMbCount < _mbCount && _rateControl.PictureTargetBits > 0)
        {
            var sliceBudget = (int)((long)_rateControl.PictureTargetBits * effectiveMbCount / _mbCount);
            _rateControl.StartFrame(
                targetBitsThisFrame: 0,
                constantSliceLumaQp: sliceQpApplied,
                rateScheduleMbs: effectiveMbCount,
                sliceTargetBits: sliceBudget);
        }
        else
        {
            _rateControl.StartFrame(targetBitsThisFrame: 0, constantSliceLumaQp: sliceQpApplied);
        }

        _lastMbQp = sliceQpApplied;

        var useTrellis = _useTrellis;
        // For P-slices we accumulate mb_skip_run and emit it once before the next non-skip MB
        // (H.264 7.3.4) — flushing inside the MB loop and then once more after the loop if the
        // slice ends in a skip tail (an all-skip slice ends here with a single ue(_mbCount)).
        var pendingSkipRun = 0;
        var rowStartTicks = collectFramePhases ? Stopwatch.GetTimestamp() : 0;
        var rowStartEffort = H264MotionEstimator.ThreadSearchEffort;
        var rowFlushMb = firstMbInSlice + _mbW - 1;
        for (var mb = firstMbInSlice; mb < firstMbInSlice + effectiveMbCount; mb++)
        {
            var mbLocal = mb - firstMbInSlice;
            var bitsBefore = _rbspBuffer.BitLength;
            var aqOffset = _aqStrength > 0 ? _mbAqQpOffset[mb] : 0;
            var qpThisMb = Math.Clamp(_rateControl.NextMbQp(mbLocal, complexity: 0) - aqOffset, 0, 51);

            var didSkip = false;
            if (canUseInter)
            {
                didSkip = TryEncodePInterMacroblock(_rbspBuffer, mb, y, strideY, u, v, strideUv, qpThisMb, useTrellis, ref pendingSkipRun);
            }

            if (!didSkip && !_mbIsInter[mb])
            {
                // Intra path (I-slice MB or intra-fallback in P-slice). Flush any pending skip run
                // first so its mb_skip_run prefix appears before this non-skip MB layer.
                if (isPslice)
                {
                    _rbspBuffer.WriteUe((uint)pendingSkipRun);
                    pendingSkipRun = 0;
                }
                WriteMacroblock(_rbspBuffer, mb, y, strideY, u, v, strideUv, isPslice, qpThisMb, useTrellis);
            }

            var bitsAfterMb = _rbspBuffer.BitLength;
            var bitsSpent = bitsAfterMb - bitsBefore;
            _rateControl.Update(mbLocal, bitsSpent);
            MbBitTraceHook?.Invoke(_sliceSeq, mb, bitsBefore, bitsAfterMb);
            // _lastMbQp is updated only when mb_qp_delta is emitted (§7.3.5.1), tracking
            // decoder QPY,PREV. Skips and zero-CBP inter MBs do NOT advance _lastMbQp.
            _qpY[mb] = _lastMbQp;
            _qpUv[mb] = H264ChromaDcScale.ChromaQpFromLuma(_lastMbQp, 0);
            if (mb == rowFlushMb)
            {
                // Row boundary: publish this row's deterministic motion-search effort for the
                // orchestrator's slice-partition balancer. Rows are owned by exactly one slice, so
                // this is a plain single-writer store.
                var rowEndEffort = H264MotionEstimator.ThreadSearchEffort;
                _shared.RowMeEffort[mb / _mbW] = rowEndEffort - rowStartEffort;
                rowStartEffort = rowEndEffort;
                rowFlushMb += _mbW;
                if (collectFramePhases)
                {
                    var rowEndTicks = Stopwatch.GetTimestamp();
                    NotifyRowStats(mb / _mbW, rowEndTicks - rowStartTicks);
                    rowStartTicks = rowEndTicks;
                }
            }
        }

        // Flush a trailing skip run for an all-skip or skip-tail slice (H.264 7.3.4 mb_skip_run
        // is read once per loop iteration even when no MB layer follows it).
        if (isPslice && pendingSkipRun > 0)
        {
            _rbspBuffer.WriteUe((uint)pendingSkipRun);
            pendingSkipRun = 0;
        }

        if (effectiveMbCount > 0 && _rbspBuffer.BitLength == bitLengthAfterHeader)
        {
            throw new InvalidOperationException(
                "Slice macroblock loop wrote no bits after the slice header; RBSP would decode as an empty slice_data.");
        }

        var deblockStartTicks = collectFramePhases ? Stopwatch.GetTimestamp() : 0;
        if (firstMbInSlice == 0 && mbCountInSlice < 0)
        {
            // Single-slice legacy path: deblock full picture then rotate DPB and pad reference.
            ApplyInLoopDeblock();
            var uvW = _width / 2;
            var uvH = _height / 2;
            RotateDpbAndPad(_recY, _width, _recU, _recV, uvW, uvH);
        }
        else
        {
            // Multi-slice path: deblock only this slice's row range.
            // Reference padding is handled by the orchestrator after all slices complete.
            ApplyInLoopDeblockScoped(firstMbInSlice, effectiveMbCount);
        }

        _rbspBuffer.WriteRbspTrailingBits();
        if (collectFramePhases)
        {
            var endTicks = Stopwatch.GetTimestamp();
            LastSliceElapsedTicks = endTicks - sliceStartTicks;
            H264PInterDiagnostics.NotifySlicePhases(copyTicks, endTicks - deblockStartTicks);
        }
        return _rbspBuffer.WrittenSpan();
    }

    /// <summary>
    /// Reset every per-frame neighbour cache so a new access unit starts with no carry-over state.
    /// Called once at the start of slice 0; subsequent slices in the same frame keep the in-progress
    /// caches so within-frame neighbours remain visible. Slice-boundary neighbours are still hidden
    /// via the <c>_firstMbRowInSlice</c> guards in the prediction/CAVLC helpers (H.264 6.4.4).
    /// </summary>
    /// <summary>
    /// Per-MB-row diagnostics flush for <see cref="H264PInterDiagnostics.CollectFramePhases"/> runs:
    /// row wall ticks plus the row's MB outcome mix (P_Skip / plain 16x16 inter / sub-partitioned
    /// inter / intra) so the partition cost model can be calibrated against measured row times.
    /// </summary>
    private void NotifyRowStats(int mbRow, long ticks)
    {
        var effort = _shared.RowMeEffort[mbRow];
        var skip = 0;
        var inter16 = 0;
        var interSub = 0;
        var intra = 0;
        for (var mb = mbRow * _mbW; mb < (mbRow + 1) * _mbW; mb++)
        {
            if (_mbIsSkip[mb])
                skip++;
            else if (!_mbIsInter[mb])
                intra++;
            else if (_mbPartitions[mb] == H264MotionEstimator.McPartition.Mb16x16)
                inter16++;
            else
                interSub++;
        }

        H264PInterDiagnostics.NotifyRowStats(mbRow, ticks, effort, skip, inter16, interSub, intra);
    }

    private void ResetForFrame()
    {
        Array.Clear(_nonZeros);
        Array.Clear(_chromaNonZeros);
        Array.Fill(_intraModes, (byte)0xFF);
        Array.Clear(_mbIsInter);
        Array.Clear(_mbIsSkip);
        Array.Clear(_mbMvs);
        Array.Clear(_mbSubPartMvs);
        Array.Clear(_mbPartitions);
        Array.Clear(_shared.MbRefIdx);
        Array.Clear(_shared.MbSubPartRefIdx);
        _recU.AsSpan().Clear();
        _recV.AsSpan().Clear();
    }

    /// <summary>
    /// Orchestrator-driven equivalent of the slice-0-only reset inside <see cref="EncodeSliceRbsp"/>.
    /// Run once before launching the parallel slice loop so the shared per-MB caches and chroma
    /// reconstruction start the access unit clean; concurrent slice writes are then disjoint and safe.
    /// </summary>
    internal void BeginFrame(bool isIdr)
    {
        if (isIdr)
        {
            _shared.DpbCount = 0;
        }

        ResetForFrame();
    }

    /// <summary>
    /// View of the last RBSP this slice encoder wrote. Valid until the next
    /// <see cref="EncodeSliceRbsp"/> call on this instance. Used by the multi-slice orchestrator
    /// to gather NALs in raster order after a <c>Parallel.For</c> launch.
    /// </summary>
    internal ReadOnlySpan<byte> LastSliceRbsp => _rbspBuffer.WrittenSpan();

    /// <summary>
    /// Wall-clock ticks of the most recent <see cref="EncodeSliceRbsp"/> call on this instance.
    /// Only recorded while <see cref="H264PInterDiagnostics.CollectFramePhases"/> is set; the
    /// multi-slice orchestrator reads it after the parallel region to measure slice imbalance.
    /// </summary>
    internal long LastSliceElapsedTicks { get; private set; }

    /// <summary>
    /// Pad the fully-reconstructed frame into the internal padded reference buffer so inter
    /// prediction is available for the next P-frame. Called by the orchestrator after all slices
    /// of a multi-slice frame have been encoded and deblocked.
    /// </summary>
    internal void PadReconstructedReference()
    {
        var uvW = _width / 2;
        var uvH = _height / 2;
        RotateDpbAndPad(_recY, _width, _recU, _recV, uvW, uvH);
    }

    private void RotateDpbAndPad(byte[] recY, int width, byte[] recU, byte[] recV, int uvW, int uvH)
    {
        var effectiveMaxRefs = Math.Clamp(_shared.MaxReferenceFrames, 1, H264FrameSharedState.MaxDpbSize);

        // Copy slot 0 → slot 1 whenever slot 0 holds a valid frame (DpbCount ≥ 1).
        // Previously only copied at capacity, leaving slot 1 zeroed during the warm-up
        // frame and causing ME to occasionally search against a black reference.
        // Skipped entirely in single-reference mode: slot 1 is never read (DpbCount never reaches 2).
        if (effectiveMaxRefs >= 2 && _shared.DpbCount >= 1)
        {
            Array.Copy(_shared.DpbPaddedY[0], _shared.DpbPaddedY[1], _shared.DpbPaddedY[0].Length);
            Array.Copy(_shared.DpbPaddedU[0], _shared.DpbPaddedU[1], _shared.DpbPaddedU[0].Length);
            Array.Copy(_shared.DpbPaddedV[0], _shared.DpbPaddedV[1], _shared.DpbPaddedV[0].Length);
        }
        if (_shared.DpbCount < effectiveMaxRefs)
            _shared.DpbCount++;
        H264ReferencePicturePadder.Pad(recY, width, width, _height, HaloLuma, _shared.DpbPaddedY[0], _paddedStrideY);
        H264ReferencePicturePadder.Pad(recU, uvW, uvW, uvH, HaloChroma, _shared.DpbPaddedU[0], _paddedStrideUv);
        H264ReferencePicturePadder.Pad(recV, uvW, uvW, uvH, HaloChroma, _shared.DpbPaddedV[0], _paddedStrideUv);
    }

    /// <summary>
    /// Apply the H.264 8.7 in-loop deblocking filter to <see cref="_recY"/>/<see cref="_recU"/>/
    /// <see cref="_recV"/> at slice end, so the encoder's reconstructed picture matches the decoder's
    /// when the slice declares <c>disable_deblocking_filter_idc = 0</c>.
    /// </summary>
    /// <remarks>
    /// Boundary-strength rules (8.7.2.1) for an all-intra slice: bS = 4 at every MB edge (between two
    /// intra MBs), bS = 3 at every internal 4×4 sub-block edge inside an intra MB. Picture-boundary
    /// edges are skipped inside the filter itself (it checks <c>mx == 0</c> / <c>my == 0</c>).
    /// </remarks>
    private void ApplyInLoopDeblock()
    {
        if (_lightweightDeblocking)
        {
            return;
        }

        Span<H264InterBoundaryStrength.InterEdgeNeighbour> thisMbBlocks =
            stackalloc H264InterBoundaryStrength.InterEdgeNeighbour[16];
        Span<H264InterBoundaryStrength.InterEdgeNeighbour> aboveBottomRow =
            stackalloc H264InterBoundaryStrength.InterEdgeNeighbour[4];
        Span<H264InterBoundaryStrength.InterEdgeNeighbour> leftRightCol =
            stackalloc H264InterBoundaryStrength.InterEdgeNeighbour[4];

        for (var mb = 0; mb < _mbCount; mb++)
        {
            var bsBase = mb * 16;

            if (_mbIsInter[mb])
            {
                // H.264 8.7.2.1 inter rules: coded coeff → bs=2; else ref/MV → bs 0–1 when RefIdx matches.
                // Intra neighbours use RefIdx=-1 (bs=1 here); inter↔intra outer MB edges get bs=4 below.
                FillInterMbBlocks(mb, thisMbBlocks);
                var mbx = mb % _mbW;
                var mby = mb / _mbW;
                var hasAbove = mby > 0;
                var hasLeft = mbx > 0;
                if (hasAbove)
                {
                    FillInterMbBlocks(mb - _mbW, aboveBottomRow, bottomRowOnly: true);
                }
                if (hasLeft)
                {
                    FillInterMbBlocks(mb - 1, leftRightCol, rightColOnly: true);
                }

                H264InterBoundaryStrength.Compute(
                    thisMbBlocks,
                    hasAbove ? (ReadOnlySpan<H264InterBoundaryStrength.InterEdgeNeighbour>)aboveBottomRow : default,
                    hasLeft ? (ReadOnlySpan<H264InterBoundaryStrength.InterEdgeNeighbour>)leftRightCol : default,
                    _bsHorizontal.AsSpan(bsBase, 16),
                    _bsVertical.AsSpan(bsBase, 16));
            }
            else
            {
                // Intra MB: bs=4 on outer MB edges, bs=3 on internal edges (8.7.2.1 intra rule).
                for (var ev = 0; ev < 4; ev++)
                {
                    var bs = ev == 0 ? (byte)4 : (byte)3;
                    _bsVertical[bsBase + ev * 4 + 0] = bs;
                    _bsVertical[bsBase + ev * 4 + 1] = bs;
                    _bsVertical[bsBase + ev * 4 + 2] = bs;
                    _bsVertical[bsBase + ev * 4 + 3] = bs;
                }

                for (var eh = 0; eh < 4; eh++)
                {
                    var bs = eh == 0 ? (byte)4 : (byte)3;
                    _bsHorizontal[bsBase + eh * 4 + 0] = bs;
                    _bsHorizontal[bsBase + eh * 4 + 1] = bs;
                    _bsHorizontal[bsBase + eh * 4 + 2] = bs;
                    _bsHorizontal[bsBase + eh * 4 + 3] = bs;
                }
            }
        }

        for (var mb = 0; mb < _mbCount; mb++)
        {
            if (!_mbIsInter[mb])
            {
                continue;
            }

            var bsBase = mb * 16;
            var mbx = mb % _mbW;
            var mby = mb / _mbW;
            if (mby > 0 && !_mbIsInter[mb - _mbW])
            {
                for (var seg = 0; seg < 4; seg++)
                {
                    _bsHorizontal[bsBase + seg] = 4;
                }
            }

            if (mbx > 0 && !_mbIsInter[mb - 1])
            {
                for (var seg = 0; seg < 4; seg++)
                {
                    _bsVertical[bsBase + seg] = 4;
                }
            }
        }

        var uvW = _width / 2;
        _kernels.ApplyDeblock(
            _recY.AsSpan(), _width,
            _recU.AsSpan(), _recV.AsSpan(), uvW,
            _mbW, _mbH,
            _bsHorizontal, _bsVertical,
            _qpY, _qpUv,
            alphaOffsetDiv2: 0,
            betaOffsetDiv2: 0);
    }

    /// <summary>
    /// Apply the in-loop deblocking filter scoped to one slice's MB row range.
    /// The BS computation skips the top horizontal edge of the slice's first row (<c>idc=2</c>
    /// — no cross-slice boundary filtering). Pass 2 starts at <paramref name="firstMbInSlice"/>
    /// and the <see cref="H264DeblockingFilter.Apply"/> span starts at the same offset so the
    /// filter's built-in <c>my == 0</c> guard naturally skips the boundary edge.
    /// </summary>
    private void ApplyInLoopDeblockScoped(int firstMbInSlice, int mbCountInSlice)
    {
        if (_lightweightDeblocking)
        {
            return;
        }

        Span<H264InterBoundaryStrength.InterEdgeNeighbour> thisMbBlocks =
            stackalloc H264InterBoundaryStrength.InterEdgeNeighbour[16];
        Span<H264InterBoundaryStrength.InterEdgeNeighbour> aboveBottomRow =
            stackalloc H264InterBoundaryStrength.InterEdgeNeighbour[4];
        Span<H264InterBoundaryStrength.InterEdgeNeighbour> leftRightCol =
            stackalloc H264InterBoundaryStrength.InterEdgeNeighbour[4];

        var lastMb = firstMbInSlice + mbCountInSlice;

        for (var mb = firstMbInSlice; mb < lastMb; mb++)
        {
            var bsBase = mb * 16;
            if (_mbIsInter[mb])
            {
                FillInterMbBlocks(mb, thisMbBlocks);
                var mbx = mb % _mbW;
                var mby = mb / _mbW;
                var hasAbove = mby > _firstMbRowInSlice;
                var hasLeft = mbx > 0;
                if (hasAbove)
                    FillInterMbBlocks(mb - _mbW, aboveBottomRow, bottomRowOnly: true);
                if (hasLeft)
                    FillInterMbBlocks(mb - 1, leftRightCol, rightColOnly: true);
                H264InterBoundaryStrength.Compute(
                    thisMbBlocks,
                    hasAbove ? (ReadOnlySpan<H264InterBoundaryStrength.InterEdgeNeighbour>)aboveBottomRow : default,
                    hasLeft ? (ReadOnlySpan<H264InterBoundaryStrength.InterEdgeNeighbour>)leftRightCol : default,
                    _bsHorizontal.AsSpan(bsBase, 16),
                    _bsVertical.AsSpan(bsBase, 16));
            }
            else
            {
                for (var ev = 0; ev < 4; ev++)
                {
                    var bs = ev == 0 ? (byte)4 : (byte)3;
                    _bsVertical[bsBase + ev * 4 + 0] = bs;
                    _bsVertical[bsBase + ev * 4 + 1] = bs;
                    _bsVertical[bsBase + ev * 4 + 2] = bs;
                    _bsVertical[bsBase + ev * 4 + 3] = bs;
                }
                for (var eh = 0; eh < 4; eh++)
                {
                    var bs = eh == 0 ? (byte)4 : (byte)3;
                    _bsHorizontal[bsBase + eh * 4 + 0] = bs;
                    _bsHorizontal[bsBase + eh * 4 + 1] = bs;
                    _bsHorizontal[bsBase + eh * 4 + 2] = bs;
                    _bsHorizontal[bsBase + eh * 4 + 3] = bs;
                }
            }
        }

        for (var mb = firstMbInSlice; mb < lastMb; mb++)
        {
            if (!_mbIsInter[mb]) continue;
            var bsBase = mb * 16;
            var mbx = mb % _mbW;
            var mby = mb / _mbW;
            if (mby > _firstMbRowInSlice && !_mbIsInter[mb - _mbW])
            {
                for (var seg = 0; seg < 4; seg++)
                    _bsHorizontal[bsBase + seg] = 4;
            }
            if (mbx > 0 && !_mbIsInter[mb - 1])
            {
                for (var seg = 0; seg < 4; seg++)
                    _bsVertical[bsBase + seg] = 4;
            }
        }

        var uvW = _width / 2;
        var sliceRowCount = mbCountInSlice / _mbW;
        _kernels.ApplyDeblock(
            _recY.AsSpan(_firstMbRowInSlice * 16 * _width), _width,
            _recU.AsSpan(_firstMbRowInSlice * 8 * uvW), _recV.AsSpan(_firstMbRowInSlice * 8 * uvW), uvW,
            _mbW, sliceRowCount,
            _bsHorizontal.AsSpan(firstMbInSlice * 16),
            _bsVertical.AsSpan(firstMbInSlice * 16),
            _qpY.AsSpan(firstMbInSlice),
            _qpUv.AsSpan(firstMbInSlice),
            alphaOffsetDiv2: 0,
            betaOffsetDiv2: 0);
    }

    /// <summary>
    /// Materialise the 16 4×4-block (refIdx, MV, luma-nonzero flags) neighbours for one MB so
    /// <see cref="H264InterBoundaryStrength.Compute"/> can compare them. With our 16×16-only inter
    /// path every 4×4 block in an inter MB shares the MB's MV and refIdx 0; an intra MB
    /// contributes refIdx=-1 and zero MV. The bottom row (<paramref name="bottomRowOnly"/>)
    /// and right column slices use rasters 12–15 and 3,7,11,15 respectively to match BS edge layout.
    /// </summary>
    private void FillInterMbBlocks(
        int mbIdx,
        Span<H264InterBoundaryStrength.InterEdgeNeighbour> dst,
        bool bottomRowOnly = false,
        bool rightColOnly = false)
    {
        var inter = _mbIsInter[mbIdx];
        var refIdx = inter ? (int)_shared.MbRefIdx[mbIdx] : -1;

        var baseNz = mbIdx * 16;
        if (bottomRowOnly)
        {
            for (var seg = 0; seg < 4; seg++)
            {
                var raster = 12 + seg;
                var nz = _nonZeros[baseNz + raster] != 0;
                var mv = inter ? GetBlk4x4Mv(mbIdx, raster) : default;
                dst[seg] = new H264InterBoundaryStrength.InterEdgeNeighbour(refIdx, mv.X, mv.Y, nz);
            }

            return;
        }

        if (rightColOnly)
        {
            for (var seg = 0; seg < 4; seg++)
            {
                var raster = 3 + seg * 4;
                var nz = _nonZeros[baseNz + raster] != 0;
                var mv = inter ? GetBlk4x4Mv(mbIdx, raster) : default;
                dst[seg] = new H264InterBoundaryStrength.InterEdgeNeighbour(refIdx, mv.X, mv.Y, nz);
            }

            return;
        }

        for (var i = 0; i < 16; i++)
        {
            var nz = inter && _nonZeros[baseNz + i] != 0;
            var mv = inter ? GetBlk4x4Mv(mbIdx, i) : default;
            dst[i] = new H264InterBoundaryStrength.InterEdgeNeighbour(refIdx, mv.X, mv.Y, nz);
        }
    }

    /// <summary>
    /// Returns the motion vector for the 4×4 block at raster index <paramref name="raster4x4"/> (0..15,
    /// row-major within the macroblock) given the MB's partition shape. Used for deblocking BS computation.
    /// </summary>
    private H264MotionEstimator.Mv GetBlk4x4Mv(int mbIdx, int raster4x4)
    {
        var part = _mbPartitions[mbIdx];
        var row = raster4x4 / 4;
        var col = raster4x4 % 4;
        return part switch
        {
            H264MotionEstimator.McPartition.Mb16x16 => _mbMvs[mbIdx],
            H264MotionEstimator.McPartition.Mb16x8 =>
                row < 2 ? _mbSubPartMvs[mbIdx * 4] : _mbSubPartMvs[mbIdx * 4 + 1],
            H264MotionEstimator.McPartition.Mb8x16 =>
                col < 2 ? _mbSubPartMvs[mbIdx * 4] : _mbSubPartMvs[mbIdx * 4 + 1],
            H264MotionEstimator.McPartition.Mb8x8 =>
                _mbSubPartMvs[mbIdx * 4 + (row >= 2 ? 2 : 0) + (col >= 2 ? 1 : 0)],
            _ => _mbMvs[mbIdx],
        };
    }

    private static void CopyPlane2d(
        ReadOnlySpan<byte> src,
        int srcStride,
        Span<byte> dst,
        int dstStride,
        int w,
        int h)
    {
        for (var row = 0; row < h; row++)
        {
            src.Slice(row * srcStride, w).CopyTo(dst.Slice(row * dstStride, w));
        }
    }

    /// <summary>
    /// Gather chroma 8x8 plane neighbor samples used by all intra chroma prediction modes (H.264 8.3.4):
    /// <paramref name="topRow"/> length 8 (T0..T7), <paramref name="leftCol"/> length 8 (L0..L7), and
    /// <paramref name="topLeft"/> as the corner sample at (bx-1, by-1).
    /// </summary>
    /// <param name="firstChromaRowInSlice">
    /// First chroma pixel row (= _firstMbRowInSlice * 8) of the slice currently being encoded. Top
    /// neighbour samples whose row is below this threshold belong to a prior slice and must read as
    /// unavailable per H.264 6.4.4 so the encoder's prediction matches a slice-aware decoder. Set to
    /// <c>0</c> for the single-slice / whole-picture path so behaviour is byte-identical to pre-slice
    /// code.
    /// </param>
    private static void GatherChromaNeighbors(
        int mbx, int mby, int firstChromaRowInSlice, ReadOnlySpan<byte> recon, int reconStride,
        Span<byte> topRow, Span<byte> leftCol,
        out bool hasTop, out bool hasLeft, out bool hasTopLeft, out byte topLeft)
    {
        var bx = mbx * 8;
        var by = mby * 8;
        hasTop = by > firstChromaRowInSlice;
        hasLeft = bx > 0;
        hasTopLeft = hasTop && hasLeft;
        topLeft = hasTopLeft ? recon[(by - 1) * reconStride + bx - 1] : (byte)0;
        if (hasTop)
        {
            var rowOff = (by - 1) * reconStride;
            for (var x = 0; x < 8; x++)
            {
                topRow[x] = recon[bx + x + rowOff];
            }
        }
        else
        {
            topRow.Clear();
        }

        if (hasLeft)
        {
            for (var y = 0; y < 8; y++)
            {
                leftCol[y] = recon[bx - 1 + (by + y) * reconStride];
            }
        }
        else
        {
            leftCol.Clear();
        }
    }

    /// <summary>Fill <paramref name="pred8x8"/> with the chroma 8x8 prediction for <paramref name="mode"/>
    /// (0=DC, 1=Horizontal, 2=Vertical, 3=Plane) per H.264 8.3.4. <paramref name="firstChromaRowInSlice"/>
    /// gates the "top" neighbour: chroma pixels with row &lt;= <paramref name="firstChromaRowInSlice"/>
    /// belong to a prior slice and must read as unavailable per H.264 6.4.4.</summary>
    private static void ComputeChromaPrediction(
        int mode, int mbx, int mby, int firstChromaRowInSlice, ReadOnlySpan<byte> recon, int reconStride, Span<byte> pred8x8)
    {
        Span<byte> topRow = stackalloc byte[8];
        Span<byte> leftCol = stackalloc byte[8];
        GatherChromaNeighbors(mbx, mby, firstChromaRowInSlice, recon, reconStride, topRow, leftCol,
            out var hasTop, out var hasLeft, out var hasTopLeft, out var topLeft);

        switch (mode)
        {
            case 1: // Horizontal: each row replicates leftCol[y]; falls back to DC if no left.
            {
                if (!hasLeft)
                {
                    ComputeChromaDcPrediction(mbx, mby, firstChromaRowInSlice, recon, reconStride, pred8x8);
                    return;
                }

                for (var y = 0; y < 8; y++)
                {
                    var v = leftCol[y];
                    var rowOff = y * 8;
                    for (var x = 0; x < 8; x++)
                    {
                        pred8x8[rowOff + x] = v;
                    }
                }

                return;
            }

            case 2: // Vertical: each column replicates topRow[x].
            {
                if (!hasTop)
                {
                    ComputeChromaDcPrediction(mbx, mby, firstChromaRowInSlice, recon, reconStride, pred8x8);
                    return;
                }

                for (var y = 0; y < 8; y++)
                {
                    var rowOff = y * 8;
                    for (var x = 0; x < 8; x++)
                    {
                        pred8x8[rowOff + x] = topRow[x];
                    }
                }

                return;
            }

            case 3: // Plane (H.264 8.3.4.5): linear plane fit using top + left + top-left.
            {
                if (!hasTop || !hasLeft || !hasTopLeft)
                {
                    ComputeChromaDcPrediction(mbx, mby, firstChromaRowInSlice, recon, reconStride, pred8x8);
                    return;
                }

                var H = 0;
                var V = 0;
                for (var i = 0; i < 4; i++)
                {
                    H += (i + 1) * (topRow[4 + i] - (i == 3 ? topLeft : topRow[2 - i]));
                    V += (i + 1) * (leftCol[4 + i] - (i == 3 ? topLeft : leftCol[2 - i]));
                }

                var b = (34 * H + 32) >> 6;
                var c = (34 * V + 32) >> 6;
                var a = 16 * (leftCol[7] + topRow[7]);
                for (var j = 0; j < 8; j++)
                {
                    var rowOff = j * 8;
                    for (var i = 0; i < 8; i++)
                    {
                        var p = (a + b * (i - 3) + c * (j - 3) + 16) >> 5;
                        pred8x8[rowOff + i] = (byte)Math.Clamp(p, 0, 255);
                    }
                }

                return;
            }

            default: // DC (mode 0): per-sub-block DC predictors.
                ComputeChromaDcPrediction(mbx, mby, firstChromaRowInSlice, recon, reconStride, pred8x8);
                return;
        }
    }

    /// <summary>
    /// DC chroma 8x8 expanded into a per-sample 8x8 prediction (each 4x4 sub-block uniform), wrapping
    /// <see cref="ComputeChromaDcSubblockPredictions"/> so the unified <see cref="PrepareChroma8x8"/> path
    /// can consume it like any other mode.
    /// </summary>
    private static void ComputeChromaDcPrediction(
        int mbx, int mby, int firstChromaRowInSlice, ReadOnlySpan<byte> recon, int reconStride, Span<byte> pred8x8)
    {
        Span<byte> subBlkPreds = stackalloc byte[4];
        ComputeChromaDcSubblockPredictions(mbx, mby, firstChromaRowInSlice, recon, reconStride, subBlkPreds);
        for (var blk = 0; blk < 4; blk++)
        {
            var ox = (blk & 1) * 4;
            var oy = (blk >> 1) * 4;
            var v = subBlkPreds[blk];
            for (var rr = 0; rr < 4; rr++)
            {
                var rowOff = (oy + rr) * 8;
                for (var cc = 0; cc < 4; cc++)
                {
                    pred8x8[rowOff + ox + cc] = v;
                }
            }
        }
    }

    /// <summary>
    /// Intra chroma DC (8×8) per H.264 8.3.4.2 — the four 4×4 sub-block DC predictors (TL, TR, BL, BR),
    /// each averaging the neighbouring samples that clause selects for its position.
    /// <paramref name="preds"/> length 4 in blk-iteration order (0=TL, 1=TR, 2=BL, 3=BR), the same order
    /// used by <see cref="PrepareChroma8x8"/>.
    /// </summary>
    private static void ComputeChromaDcSubblockPredictions(
        int mbx, int mby, int firstChromaRowInSlice, ReadOnlySpan<byte> recon, int reconStride, Span<byte> preds)
    {
        var bx = mbx * 8;
        var by = mby * 8;
        var hasTop = by > firstChromaRowInSlice;
        var hasLeft = bx > 0;

        Span<int> top = stackalloc int[8];
        Span<int> left = stackalloc int[8];
        if (hasTop)
        {
            var rowOff = (by - 1) * reconStride;
            for (var x = 0; x < 8; x++)
            {
                top[x] = recon[bx + x + rowOff];
            }
        }

        if (hasLeft)
        {
            for (var y = 0; y < 8; y++)
            {
                left[y] = recon[bx - 1 + (by + y) * reconStride];
            }
        }

        var dcTop0 = top[0] + top[1] + top[2] + top[3];
        var dcTop1 = top[4] + top[5] + top[6] + top[7];
        var dcLeft0 = left[0] + left[1] + left[2] + left[3];
        var dcLeft1 = left[4] + left[5] + left[6] + left[7];

        if (hasTop && hasLeft)
        {
            preds[0] = (byte)((dcTop0 + dcLeft0 + 4) >> 3);
            preds[1] = (byte)((dcTop1 + 2) >> 2);
            preds[2] = (byte)((dcLeft1 + 2) >> 2);
            preds[3] = (byte)((dcTop1 + dcLeft1 + 4) >> 3);
        }
        else if (hasTop)
        {
            var p0 = (byte)((dcTop0 + 2) >> 2);
            var p1 = (byte)((dcTop1 + 2) >> 2);
            preds[0] = p0;
            preds[1] = p1;
            preds[2] = p0;
            preds[3] = p1;
        }
        else if (hasLeft)
        {
            var p0 = (byte)((dcLeft0 + 2) >> 2);
            var p1 = (byte)((dcLeft1 + 2) >> 2);
            preds[0] = p0;
            preds[1] = p0;
            preds[2] = p1;
            preds[3] = p1;
        }
        else
        {
            preds[0] = 128;
            preds[1] = 128;
            preds[2] = 128;
            preds[3] = 128;
        }
    }

    /// <summary>Pick the best chroma intra prediction mode (H.264 8.3.4) using J = SAD + lambda*R over
    /// U+V planes. DC is always available; H needs left, V needs top, Plane needs top+left+top-left.
    /// Bit cost is the ue(v) length of the mode index: 1, 3, 3, 5 bits for modes 0..3.</summary>
    private int ChooseChromaIntraMode(
        int mbx, int mby,
        ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV, int srcStride,
        ReadOnlySpan<byte> recU, ReadOnlySpan<byte> recV, int reconStride,
        int qpThisMb)
    {
        var bx = mbx * 8;
        var by = mby * 8;
        // H.264 6.4.4: top chroma row in a prior slice reports unavailable. _firstMbRowInSlice * 8
        // is the slice's first chroma row; samples above it must not contribute to mode SAD or to
        // the candidate prediction kernel.
        var firstChromaRowInSlice = _firstMbRowInSlice * 8;
        var hasTop = by > firstChromaRowInSlice;
        var hasLeft = bx > 0;
        var hasTopLeft = hasTop && hasLeft;

        Span<byte> predU = stackalloc byte[64];
        Span<byte> predV = stackalloc byte[64];
        ReadOnlySpan<int> chromaModeBitCost = [1, 3, 3, 5];

        // Gather src U/V once (same across all 4 candidate modes); only pred changes per mode.
        Span<byte> srcUBlk = stackalloc byte[64];
        Span<byte> srcVBlk = stackalloc byte[64];
        _kernels.GatherChroma8x8(srcU, srcStride, bx, by, srcUBlk);
        _kernels.GatherChroma8x8(srcV, srcStride, bx, by, srcVBlk);

        var bestMode = 0;
        var bestJ = long.MaxValue;
        for (var mode = 0; mode < 4; mode++)
        {
            switch (mode)
            {
                case 1 when !hasLeft: continue;
                case 2 when !hasTop: continue;
                case 3 when !hasTopLeft: continue;
            }

            ComputeChromaPrediction(mode, mbx, mby, firstChromaRowInSlice, recU, reconStride, predU);
            ComputeChromaPrediction(mode, mbx, mby, firstChromaRowInSlice, recV, reconStride, predV);

            var sad = _kernels.SadChromaPair(srcUBlk, srcVBlk, predU, predV);

            var sadL = _intra4x4SadLambdaUserOverride ?? LambdaSadForQp(qpThisMb);
            long j = sad + (long)sadL * chromaModeBitCost[mode];
            if (j < bestJ)
            {
                bestJ = j;
                bestMode = mode;
            }
        }

        return bestMode;
    }

    /// <summary>4:2:0 chroma 8×8: WHT DC + 4×4 chroma AC (cbp_chroma=2 when needed). <paramref name="pred8x8"/>
    /// is the per-sample prediction for the chosen chroma intra mode (DC/H/V/Plane), row-major 8×8.</summary>
    private bool PrepareChroma8x8(
        int mbx,
        int mby,
        ReadOnlySpan<byte> src,
        int srcStride,
        Span<byte> recon,
        int reconStride,
        ReadOnlySpan<byte> pred8x8,
        Span<short> vlcDc4,
        Span<short> acZigZag4Blocks,
        Span<byte> chromaNz4,
        int qpThisMb,
        bool skipChromaDcCoordinateRefinement = false)
    {
        var bx = mbx * 8;
        var by = mby * 8;
        Span<short> residualS = stackalloc short[16];
        Span<int> coeff = stackalloc int[16];
        Span<int> coeffStore = stackalloc int[16 * 4];
        Span<int> dc4 = stackalloc int[4];
        acZigZag4Blocks.Slice(0, 16 * 4).Clear();
        chromaNz4[..4].Clear();

        for (var blk = 0; blk < 4; blk++)
        {
            var ox = (blk & 1) * 4;
            var oy = (blk >> 1) * 4;
            for (var i = 0; i < 16; i++)
            {
                var rr = i / 4;
                var cc = i % 4;
                var r = by + oy + rr;
                var c = bx + ox + cc;
                var p = pred8x8[(oy + rr) * 8 + (ox + cc)];
                residualS[i] = (short)(src[r * srcStride + c] - p);
            }

            _kernels.ForwardDct4x4(residualS, coeff);
            coeff.Slice(0, 16).CopyTo(coeffStore.Slice(blk * 16, 16));
            dc4[blk] = coeff[0] << 1;
        }

        var chromaQp = H264ChromaDcScale.ChromaQpFromLuma(qpThisMb, 0);
        var qmul = H264ChromaDcScale.ChromaDcQmul(chromaQp);
        var chromaDcLambda = _chromaDcRdLambdaUserOverride
            ? _chromaDcRdLambda
            : H264ChromaDcScale.DefaultChromaDcRdLambdaFromLumaQp(qpThisMb);
        H264ChromaDcScale.QuantChromaDcLevelsFromDctDc(
            dc4,
            qmul,
            chromaDcLambda,
            vlcDc4,
            customCost: null,
            skipCoordinateRefinement: skipChromaDcCoordinateRefinement);
        H264ChromaDcScale.ChromaDcDequantIdct(vlcDc4, qmul, dc4);

        var chromaQpClamped = Math.Clamp(chromaQp, 0, 51);
        var anyAc = false;
        Span<int> q = stackalloc int[16];
        Span<int> zz = stackalloc int[16];
        Span<short> zzS = stackalloc short[16];
        Span<int> fwd = stackalloc int[16];
        Span<int> invRes = stackalloc int[16];
        for (var blk = 0; blk < 4; blk++)
        {
            coeffStore.Slice(blk * 16, 16).CopyTo(q);
            q[0] = 0;
            _kernels.Quant4x4(q, chromaQpClamped);
            q[0] = 0;

            var nz = 0;
            for (var k = 1; k < 16; k++)
            {
                if (q[k] != 0)
                {
                    nz++;
                }
            }

            if (nz != 0)
            {
                anyAc = true;
            }

            chromaNz4[blk] = (byte)Math.Min(nz, 15);

            H264BlockTransform.RasterToZigzag(q, zz);
            H264BlockTransform.CopyZigzagToShort(zz, zzS);
            zzS.CopyTo(acZigZag4Blocks.Slice(blk * 16, 16));

            q[0] = 0;
            H264BlockTransform.DequantAc4x4Spec(q, chromaQpClamped, fwd);
            fwd[0] = dc4[blk] << 1;
            H264BlockTransform.InverseDct4x4Spec(fwd, invRes);
            var ox = (blk & 1) * 4;
            var oy = (blk >> 1) * 4;
            var reconRowOff = (by + oy) * reconStride + bx + ox;
            for (var rr = 0; rr < 4; rr++, reconRowOff += reconStride)
            {
                var i0 = rr * 4;
                var predRowOff = (oy + rr) * 8 + ox;
                recon[reconRowOff + 0] = (byte)Math.Clamp(pred8x8[predRowOff + 0] + invRes[i0 + 0], 0, 255);
                recon[reconRowOff + 1] = (byte)Math.Clamp(pred8x8[predRowOff + 1] + invRes[i0 + 1], 0, 255);
                recon[reconRowOff + 2] = (byte)Math.Clamp(pred8x8[predRowOff + 2] + invRes[i0 + 2], 0, 255);
                recon[reconRowOff + 3] = (byte)Math.Clamp(pred8x8[predRowOff + 3] + invRes[i0 + 3], 0, 255);
            }
        }

        return anyAc;
    }

    private static bool ChromaDcCoeffAny(ReadOnlySpan<short> vlcDc4)
    {
        for (var i = 0; i < 4; i++)
        {
            if (vlcDc4[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>H.264 7.3.3 — <c>disable_deblocking_filter_idc</c> and optional offsets in coded slice syntax.</summary>
    internal static void WriteDisableDeblockingFilterSliceSyntax(
        H264RbspBitBuffer bs,
        uint disableDeblockingFilterIdc,
        int sliceAlphaC0OffsetDiv2 = 0,
        int sliceBetaOffsetDiv2 = 0)
    {
        bs.WriteUe(disableDeblockingFilterIdc);
        if (disableDeblockingFilterIdc != 1)
        {
            bs.WriteSe(sliceAlphaC0OffsetDiv2);
            bs.WriteSe(sliceBetaOffsetDiv2);
        }
    }

    private void WriteSliceHeader(
        H264RbspBitBuffer bs,
        bool isIdr,
        bool isPslice,
        int frameNum,
        int idrPicId,
        int sliceQpDelta,
        int firstMbInSlice = 0)
    {
        bs.WriteUe((uint)firstMbInSlice); // first_mb_in_slice
        if (isIdr)
        {
            // I slice (2). Values 5..9 are redundant with 0..4 for IDR NALs — 5 means P, not I (H.264 Table 7-6).
            bs.WriteUe(2);
        }
        else if (isPslice)
        {
            bs.WriteUe(0);
        }
        else
        {
            bs.WriteUe(2);
        }

        bs.WriteUe(0); // pic_parameter_set_id

        // frame_num bit width = Log2MaxFrameNumMinus4 + 4, matching the SPS log2_max_frame_num_minus4 field.
        const int log2MaxFnm = H264ParameterSets.Log2MaxFrameNumMinus4 + 4;
        bs.WriteBits(log2MaxFnm, (uint)(frameNum & ((1 << log2MaxFnm) - 1)));

        if (isIdr)
        {
            bs.WriteUe((uint)idrPicId);
        }

        // pic_order_cnt_type == 2: POC is derived from frame_num, so no pic_order_cnt_lsb appears in the
        // slice header (H.264 7.3.3 / 8.2.1.3). Verified to decode cleanly on VideoToolbox.
        if (!isIdr && isPslice)
        {
            var numRefIdxActiveMinus1 = _shared.DpbCount >= 2 ? 1 : 0;
            if (numRefIdxActiveMinus1 > 0)
            {
                bs.WriteBit(true);                          // num_ref_idx_active_override_flag
                bs.WriteUe((uint)numRefIdxActiveMinus1);   // num_ref_idx_l0_active_minus1
            }
            else
            {
                bs.WriteBit(false); // num_ref_idx_active_override_flag — PPS default suffices
            }
            bs.WriteBit(false); // ref_pic_list_modification_flag_l0
        }

        if (isIdr)
        {
            bs.WriteBit(false);
            bs.WriteBit(false);
        }
        else
        {
            bs.WriteBit(false); // adaptive_ref_pic_marking_mode_flag
        }

        bs.WriteSe(sliceQpDelta); // slice_qp_delta (see H.264 7.4.5)
        // When idc != 1 (0 or 2), slice_alpha_c0_offset_div2 and slice_beta_offset_div2 must follow (7.3.3).
        // idc=1: lightweight (skip all deblocking). idc=2: within-slice only (multi-slice). idc=0: normal.
        var disableDeblockingFilterIdc = _lightweightDeblocking ? 1u : (_filterAcrossSlicesDisabled ? 2u : 0u);
        WriteDisableDeblockingFilterSliceSyntax(bs, disableDeblockingFilterIdc);
    }

    /// <summary>
    /// Gather the four neighbour MVs (A=left, B=top, C=top-right, D=top-left) used by H.264
    /// 8.4.1.3.1 (median predictor) and 8.4.1.1 (skip predictor) for the MB at (mbx, mby). A
    /// neighbour is "available" only if it lies inside the slice AND was inter-coded AND has the
    /// same refIdx as <paramref name="requiredRefIdx"/> (H.264 §8.4.1.3.1 cond_term); intra
    /// neighbours and neighbours with different refIdx are reported as not-available with mv=(0,0).
    /// Resolves each neighbour partition at the edge sample used by §6.4.13 / §8.4.1.3.1.
    /// </summary>
    // §8.4.1.3.1: neighbour availability for the median MVP is based solely on inter-coded status —
    // no reference-index restriction. aRefIdx/bRefIdx are set to the actual ref index of the A/B
    // neighbour (or -1 when unavailable) so the caller can apply §8.4.1.1 P_Skip refIdx=0 checks.
    private void GatherInterNeighbourMvs(
        int mbx, int mby,
        out H264MotionEstimator.Mv mvA, out bool aAvail, out int aRefIdx,
        out H264MotionEstimator.Mv mvB, out bool bAvail, out int bRefIdx,
        out H264MotionEstimator.Mv mvC, out bool cAvail, out int cRefIdx,
        out H264MotionEstimator.Mv mvD, out bool dAvail, out int dRefIdx)
    {
        mvA = default; aAvail = false; aRefIdx = -1;
        mvB = default; bAvail = false; bRefIdx = -1;
        mvC = default; cAvail = false; cRefIdx = -1;
        mvD = default; dAvail = false; dRefIdx = -1;

        if (mbx > 0)
        {
            var left = mby * _mbW + (mbx - 1);
            aAvail = _mbIsInter[left];
            if (aAvail) { aRefIdx = _shared.MbRefIdx[left]; mvA = GetNeighbourMvAtPixel(left, 15, 0); }
        }

        // H.264 6.4.4: MBs in a different slice are marked not-available. Slices are partitioned
        // row-wise, so top/top-right/top-left neighbours fall in the prior slice exactly when
        // mby == _firstMbRowInSlice. Use mby > _firstMbRowInSlice so the median MV predictor
        // matches the decoder's neighbour availability for sliceCount > 1.
        if (mby > _firstMbRowInSlice)
        {
            var top = (mby - 1) * _mbW + mbx;
            bAvail = _mbIsInter[top];
            if (bAvail) { bRefIdx = _shared.MbRefIdx[top]; mvB = GetNeighbourMvAtPixel(top, 0, 15); }

            if (mbx + 1 < _mbW)
            {
                var topRight = (mby - 1) * _mbW + (mbx + 1);
                cAvail = _mbIsInter[topRight];
                if (cAvail) { cRefIdx = _shared.MbRefIdx[topRight]; mvC = GetNeighbourMvAtPixel(topRight, 0, 15); }
            }

            if (mbx > 0)
            {
                var topLeft = (mby - 1) * _mbW + (mbx - 1);
                dAvail = _mbIsInter[topLeft];
                if (dAvail) { dRefIdx = _shared.MbRefIdx[topLeft]; mvD = GetNeighbourMvAtPixel(topLeft, 15, 15); }
            }
        }
    }

    /// <summary>
    /// Returns the committed luma motion vector for the partition of <paramref name="neighbourMbIdx"/>
    /// that covers luma sample (<paramref name="xInMb"/>, <paramref name="yInMb"/>) (0-based within
    /// that MB). Implements §6.4.13.4 partition address derivation for the four inter shapes
    /// Kiln can emit. Only call when <c>_mbIsInter[neighbourMbIdx]</c> is true.
    /// </summary>
    private H264MotionEstimator.Mv GetNeighbourMvAtPixel(int neighbourMbIdx, int xInMb, int yInMb) =>
        _mbPartitions[neighbourMbIdx] switch
        {
            H264MotionEstimator.McPartition.Mb16x8 =>
                _mbSubPartMvs[neighbourMbIdx * 4 + (yInMb >= 8 ? 1 : 0)],
            H264MotionEstimator.McPartition.Mb8x16 =>
                _mbSubPartMvs[neighbourMbIdx * 4 + (xInMb >= 8 ? 1 : 0)],
            H264MotionEstimator.McPartition.Mb8x8 =>
                _mbSubPartMvs[neighbourMbIdx * 4 + (yInMb >= 8 ? 2 : 0) | (xInMb >= 8 ? 1 : 0)],
            _ /* Mb16x16 and any intra-coded shapes stored as Mb16x16 */ =>
                _mbMvs[neighbourMbIdx],
        };

    // §8.4.1.1: P_Skip MV is (0,0) only when A or B is absent (PART_NOT_AVAILABLE) or is refIdx 0
    // with a zero MV; otherwise the §8.4.1.3 refIdx-0 predictor. Neither an *intra* neighbour
    // (present, refIdx -1) nor a refIdx>0 neighbour is a zero condition — both fall through to the
    // predictor, where §8.4.1.3.2's single-matching-refIdx rule and median handle them, matching the
    // decoder. Two historical desyncs lived here: conflating intra (refIdx -1) with "unavailable"
    // (the old `aRefIdx != 0` test), and forcing (0,0) for a refIdx>0 A/B neighbour.
    // The latter diverged silently whenever the (0,0) and spec-predicted blocks happened to match
    // pixel-wise (flat regions): the decoder's MV field carried the spec value into every later
    // MVP/P_Skip derivation touching that MB, compounding into dB-scale drift on motion content.
    internal static H264MotionEstimator.Mv DerivePSkipMv(
        H264MotionEstimator.Mv mvA,
        int aRefIdx,
        H264MotionEstimator.Mv mvB,
        int bRefIdx,
        H264MotionEstimator.Mv medianPredictor,
        bool aAbsent,
        bool bAbsent)
    {
        if (aAbsent || bAbsent)
            return default;
        if ((aRefIdx == 0 && mvA.X == 0 && mvA.Y == 0) || (bRefIdx == 0 && mvB.X == 0 && mvB.Y == 0))
            return default;
        return medianPredictor;
    }

    /// <summary>
    /// Try to encode MB <paramref name="mbIndex"/> as inter (P_Skip or P_L0_16x16). Returns
    /// <c>true</c> if the MB collapsed to skip (caller increments mb_skip_run; no bits written
    /// here); <c>false</c> if a full P_L0_16x16 mb-layer was emitted (mb_skip_run already flushed
    /// inside this call). Always sets <see cref="_mbIsInter"/>[mbIndex]=true on either path so
    /// the caller knows the inter path took ownership and need not fall through to intra.
    /// </summary>
    private bool TryEncodePInterMacroblock(
        H264RbspBitBuffer bs,
        int mbIndex,
        ReadOnlySpan<byte> srcY,
        int strideY,
        ReadOnlySpan<byte> srcU,
        ReadOnlySpan<byte> srcV,
        int strideUv,
        int qpThisMb,
        bool useTrellis,
        ref int pendingSkipRun)
    {
        var mbx = mbIndex % _mbW;
        var mby = mbIndex / _mbW;
        var uvW = _width / 2;
        var mbX = mbx * 16;
        var mbY = mby * 16;

        // §8.4.1.3.1: gather all inter neighbours without refIdx filtering for the median MVP.
        // §8.4.1.1: P_Skip additionally requires A and B to have refIdx=0; aRefIdx/bRefIdx carry that.
        GatherInterNeighbourMvs(mbx, mby,
            out var mvA, out var aAvail, out var aRefIdx,
            out var mvB, out var bAvail, out var bRefIdx,
            out var mvC, out var cAvail, out var cRefIdx,
            out var mvD, out var dAvail, out var dRefIdx);

        // §8.4.1.3.2 positional absence (decoder PART_NOT_AVAILABLE): a neighbour MB is *absent* only
        // when it lies outside the picture or current slice — independent of whether a present
        // neighbour is inter or intra. Only absent neighbours drive the C←D substitution and the B&C
        // inheritance rule; an intra-in-P neighbour is present (MV (0,0), refIdx -1) and must not.
        var topAbsent = mby <= _firstMbRowInSlice;
        var aAbsent = mbx == 0;
        var bAbsent = topAbsent;
        var cAbsent = topAbsent || mbx + 1 >= _mbW;
        var dAbsent = topAbsent || mbx == 0;

        // Median predictor for ref-0 ME seed and P_Skip. P_Skip is always refIdx=0, so the
        // §8.4.1.3.2 directional rule is evaluated against refIdx 0 here.
        var mvPredictor = H264MotionEstimator.PredictMvWithRefIdx(
            mvA, aRefIdx, mvB, bRefIdx, mvC, cRefIdx, mvD, dRefIdx, currentRefIdx: 0,
            aAbsent, bAbsent, cAbsent, dAbsent);
        var mvSkipPred = DerivePSkipMv(mvA, aRefIdx, mvB, bRefIdx, mvPredictor, aAbsent, bAbsent);

        var chromaMbX = mbX / 2;
        var chromaMbY = mbY / 2;

        Span<byte> predY = stackalloc byte[256];
        Span<byte> predU = stackalloc byte[64];
        Span<byte> predV = stackalloc byte[64];
        var traceMb = H264PInterDiagnostics.ShouldTraceMb(_currentFrameNum, mbx, mby);
        if (traceMb)
        {
            H264PInterDiagnostics.TraceMbDecision(
                _currentFrameNum, _currentCodedFrameIndex, mbx, mby,
                $"neighbors A={(aAvail ? 1 : 0)}r{aRefIdx}({mvA.X},{mvA.Y}) B={(bAvail ? 1 : 0)}r{bRefIdx}({mvB.X},{mvB.Y}) " +
                $"C={(cAvail ? 1 : 0)}r{cRefIdx}({mvC.X},{mvC.Y}) D={(dAvail ? 1 : 0)}r{dRefIdx}({mvD.X},{mvD.Y}) " +
                $"mvp=({mvPredictor.X},{mvPredictor.Y}) skip=({mvSkipPred.X},{mvSkipPred.Y})");
        }
        var sadSkip = int.MaxValue;

        // Phase 1 — cheap skip evaluation. Reconstruct inter prediction at the SKIP-predicted MV
        // (8.4.1.1) and measure SAD vs source. If it's small enough, accept skip without ME or
        // residual coding; the encoder commits the inter prediction directly to _recY/U/V (which
        // is what the decoder will produce for a P_Skip at this MV — encoder and decoder agree).
        // The threshold is SAD-per-pixel-averaged; ~4/sample on 384 luma+chroma samples = 1536
        // covers typical deblock noise after intra reconstruction at QP≤32 and keeps us from
        // collapsing real motion into skip (which has SAD an order of magnitude larger).
        // Median / skip MV can be out of range for chroma bilinear on this MB while neighbour MVs
        // were valid for their positions — skip phase 1 when padded ref access would overrun.
        var collectPhase1Timing = H264PInterDiagnostics.CollectPhase1Timing;
        var phase1StartTicks = collectPhase1Timing ? Stopwatch.GetTimestamp() : 0;
        var phase1LapTicks = phase1StartTicks;
        var phase1PredLumaTicks = 0L;
        var phase1PredChromaTicks = 0L;
        var phase1SadTicks = 0L;
        var phase1SseTicks = 0L;
        var phase1Outcome = H264PInterDiagnostics.Phase1Outcome.MvUnsafe;
        var phase1PredBuilt = false;
        if (H264InterReconstructor.IsMvSafeForInter16x16AtMb(
                _width, _height, mbX, mbY, mvSkipPred.X, mvSkipPred.Y, HaloLuma, HaloChroma))
        {
            phase1PredBuilt = true;
            H264InterReconstructor.ReconstructLuma(
                _paddedRefY, _paddedStrideY, HaloLuma,
                mbX, mbY,
                mvSkipPred.X, mvSkipPred.Y,
                16, 16,
                predY, 16,
                _kernels);
            if (collectPhase1Timing)
            {
                var now = Stopwatch.GetTimestamp();
                phase1PredLumaTicks = now - phase1LapTicks;
                phase1LapTicks = now;
            }
            H264InterReconstructor.ReconstructChroma(
                _paddedRefU, _paddedStrideUv, HaloChroma,
                chromaMbX, chromaMbY,
                mvSkipPred.X, mvSkipPred.Y,
                8, 8,
                predU, 8,
                _kernels);
            H264InterReconstructor.ReconstructChroma(
                _paddedRefV, _paddedStrideUv, HaloChroma,
                chromaMbX, chromaMbY,
                mvSkipPred.X, mvSkipPred.Y,
                8, 8,
                predV, 8,
                _kernels);
            if (collectPhase1Timing)
            {
                var now = Stopwatch.GetTimestamp();
                phase1PredChromaTicks = now - phase1LapTicks;
                phase1LapTicks = now;
            }

            sadSkip = _kernels.Sad16x16(srcY.Slice(mbY * strideY + mbX), strideY, predY, 16)
                    + _kernels.Sad8x8(srcU.Slice(chromaMbY * strideUv + chromaMbX), strideUv, predU, 8)
                    + _kernels.Sad8x8(srcV.Slice(chromaMbY * strideUv + chromaMbX), strideUv, predV, 8);
            if (collectPhase1Timing)
            {
                var now = Stopwatch.GetTimestamp();
                phase1SadTicks = now - phase1LapTicks;
                phase1LapTicks = now;
            }
            phase1Outcome = H264PInterDiagnostics.Phase1Outcome.SadReject;
            // Total SAD (luma+chroma MB): tight enough that obvious translation is not mistaken for
            // P_Skip (diagonal motion regression), but high enough that QP/deblock noise on flat chroma
            // does not force Phase-2-only paths on identical-frame P-slices.
            const int SkipSadThreshold = 1536;
            var skipSadThreshold = (mvSkipPred.X == 0 && mvSkipPred.Y == 0 && _experimentalZeroMvSkipSadThreshold.HasValue)
                ? _experimentalZeroMvSkipSadThreshold.Value
                : SkipSadThreshold;
            if (sadSkip <= skipSadThreshold)
            {
                // SAD averages error across the whole MB, so a skip whose error is concentrated in a
                // few samples — e.g. the leading edge of a scroll, where one column is off-screen in
                // the reference — can pass the SAD gate while reconstructing badly. Guard with an SSE
                // gate: SSE squares the error, so it spikes on concentrated mispredictions that SAD
                // hides (observed split: genuine skips ~260, leading-edge skips ~18 000). A skip that
                // fails it falls through to Phase 2, which codes residual (or an Intra_16×16 MB) and
                // lifts that block back to QP-bounded quality instead of leaving a 30 dB hole.
                //
                // The gate must track the quantiser: "acceptable skip error" is error the residual
                // path could not remove anyway, and that floor scales with Qstep². 8192 was measured
                // at QP 28, so scale by LambdaSatdForQp (∝ Qstep², §J.1-style mode-decision lambda)
                // anchored there, capped at the measured 8192 so no QP becomes more lenient. Without
                // the scaling, a spec-MV P_Skip at low QP can lock onto a mediocre reconstruction
                // (e.g. a scroll's intra-coded leading edge) and propagate its error across the
                // frame instead of re-coding it — measured at −1 dB on tile-period scroll at QP 20
                // once the P_Skip MV fix (refIdx>0 neighbours no longer force a zero skip MV)
                // made those skips reachable.
                var skipSseThreshold = Math.Min(8192, Math.Max(768, 8192 * LambdaSatdForQp(qpThisMb) / LambdaSatdForQp(28)));
                var skipSse = ComputeInterPredictionSse(srcY, strideY, srcU, srcV, strideUv, mbX, mbY, predY, predU, predV);
                if (collectPhase1Timing)
                {
                    phase1SseTicks = Stopwatch.GetTimestamp() - phase1LapTicks;
                }
                phase1Outcome = H264PInterDiagnostics.Phase1Outcome.SseReject;
                if (skipSse <= skipSseThreshold)
                {
                H264PInterDiagnostics.TraceMbDecision(
                    _currentFrameNum, _currentCodedFrameIndex, mbx, mby,
                    $"sliceSeq={_sliceSeq} phase1-skip sadSkip={sadSkip} skipSse={skipSse} threshold={skipSadThreshold} mvSkip=({mvSkipPred.X},{mvSkipPred.Y})");
                // SKIP path: write the inter prediction at the skip MV directly into _recY/U/V (no
                // residual). The decoder reconstructs the same samples; future frames' references
                // (the padded buffers) will track this lossy-but-decoder-consistent reconstruction.
                CopyInterPredToReconY(predY, mbX, mbY);
                CopyInterPredToReconUv(predU, _recU, chromaMbX, chromaMbY);
                CopyInterPredToReconUv(predV, _recV, chromaMbX, chromaMbY);
                _mbIsInter[mbIndex] = true;
                _mbIsSkip[mbIndex] = true;
                _mbMvs[mbIndex] = mvSkipPred;
                // _nonZeros / _chromaNonZeros are zero (Array.Clear at slice start covers this).
                pendingSkipRun++;
                H264PInterDiagnostics.NotifyPhase1Skip();
                if (collectPhase1Timing)
                {
                    H264PInterDiagnostics.NotifyPhase1Timing(
                        Stopwatch.GetTimestamp() - phase1StartTicks,
                        phase1PredLumaTicks, phase1PredChromaTicks, phase1SadTicks, phase1SseTicks,
                        H264PInterDiagnostics.Phase1Outcome.Accept,
                        fractionalLumaMv: ((mvSkipPred.X | mvSkipPred.Y) & 3) != 0);
                }
                return true;
                }
            }
        }

        if (collectPhase1Timing)
        {
            H264PInterDiagnostics.NotifyPhase1Timing(
                Stopwatch.GetTimestamp() - phase1StartTicks,
                phase1PredLumaTicks, phase1PredChromaTicks, phase1SadTicks, phase1SseTicks,
                phase1Outcome,
                fractionalLumaMv: phase1PredBuilt && ((mvSkipPred.X | mvSkipPred.Y) & 3) != 0);
        }
        H264PInterDiagnostics.NotifyPhase2Entered();

        // Phase 2 — full inter path: run sub-partition ME from the median predictor.
        var collectPhase2Timing = H264PInterDiagnostics.IsPhase2TimingEnabled;
        var phase2StartTicks = collectPhase2Timing ? Stopwatch.GetTimestamp() : 0;
        var phase2MeStartTicks = phase2StartTicks;
        var phase2MeTicks = 0L;
        var phase2PredTicks = 0L;
        var phase2LumaTicks = 0L;
        var phase2ChromaTicks = 0L;
        var phase2WriteTicks = 0L;

        var srcMbOff = mbY * strideY + mbX;
        var current = srcY.Slice(srcMbOff);
        var variance = H264VarianceFastPath.VarianceMb16x16(current, strideY);
        var adaptiveRange = variance < 64 ? 8 : variance < 512 ? 16 : 32;
        H264MotionEstimator.Mv? temporalMv = _shared.PaddedRefValid ? _shared.PrevMbMvs[mbIndex] : null;
        if (sadSkip > 2048)
        {
            // Poor skip prediction usually means real motion, so widen ME even for low-variance MBs —
            // but a slice-top row has no B neighbour, so P_Skip is normatively (0,0) (§8.4.1.1) and
            // sadSkip runs high on any moving content there. That signals a missing spatial predictor,
            // not a scene change: probe the co-located previous-frame MV with one SAD first, and
            // search tightly around that seed when it explains the motion (at most half the skip SAD;
            // the probe is luma-only, so the margin also absorbs sadSkip's chroma term). Only when the
            // temporal seed fails too does this conclude the search must widen. The probe requires a
            // seed distinct from the skip MV that already failed: for an equal seed (e.g. the all-zero
            // PrevMbMvs after an IDR) it would re-measure the failed prediction minus its chroma term
            // and pass spuriously on chroma-heavy error, collapsing the range below real motion.
            var sadTemporal = int.MaxValue;
            var probeAttempted = false;
            if (!H264PInterDiagnostics.DisableTemporalSeedProbe &&
                temporalMv is { } tSeed && (tSeed.X != mvSkipPred.X || tSeed.Y != mvSkipPred.Y))
            {
                probeAttempted = true;
                sadTemporal = TemporalSeedProbeSadLuma(current, strideY, mbX, mbY, tSeed);
            }

            H264PInterDiagnostics.NotifyTemporalProbe(probeAttempted, sadTemporal < sadSkip >> 1);
            if (sadTemporal < sadSkip >> 1)
            {
                var tmv = temporalMv!.Value;
                var temporalIntPelMag = Math.Max(Math.Abs(tmv.X), Math.Abs(tmv.Y)) >> 2;
                adaptiveRange = Math.Clamp(temporalIntPelMag + 4, 8, 32);
            }
            else
            {
                adaptiveRange = Math.Max(adaptiveRange, sadSkip > 4096 ? 32 : 24);
            }
        }
        else if (temporalMv.HasValue && adaptiveRange < 16)
        {
            var tmv = temporalMv.Value;
            var temporalIntPelMag = Math.Max(Math.Abs(tmv.X), Math.Abs(tmv.Y)) >> 2;
            // Keep low-variance MBs from collapsing search too aggressively when prior-frame motion
            // already indicates a larger displacement.
            var temporalRangeFloor = Math.Clamp(temporalIntPelMag + 2, 8, 32);
            adaptiveRange = Math.Max(adaptiveRange, temporalRangeFloor);
        }
        // Flat MBs can still fail skip hard during scene/region toggles. Smaller luma partitions cannot
        // improve a nearly constant source block enough to justify exhaustive subpartition ME there.
        var allowSubPartitionSearch = variance >= 64;
        if (allowSubPartitionSearch)
            _subPartMbCount++;
        var rangeCapThisMb = _subPartMbCount > _subPartBudget ? Math.Min(4, _subPartitionRangeCap) : _subPartitionRangeCap;

        // Deterministic effort-budget ladder (H264BaselineEncoderOptions.MotionSearchEffortCapPerMb):
        // consumption is this slice's own accumulated search effort, so the tier for a given MB
        // depends only on previously encoded MBs of the same slice — never on wall clock or thread
        // scheduling. Each tier sheds the costliest remaining content-scaled term first (measured
        // on divergent-motion 1080p, notes/07): the 65x65 exhaustive fallback, then the second
        // reference and the wide seed window, then the sub-partition shapes entirely. Encoder
        // search policy only; every output remains a normatively valid P-macroblock.
        var seedSearchRangeThisMb = 32;
        var allowExhaustiveFallback = true;
        var allowRef1Search = true;
        if (_meEffortBudget != long.MaxValue)
        {
            var consumed = H264MotionEstimator.ThreadSearchEffort - _meEffortAtSliceStart;
            if (consumed >= _meEffortBudget)
            {
                allowSubPartitionSearch = false;
                allowExhaustiveFallback = false;
                allowRef1Search = false;
                seedSearchRangeThisMb = 8;
                adaptiveRange = Math.Min(adaptiveRange, 8);
                rangeCapThisMb = Math.Min(4, rangeCapThisMb);
                H264PInterDiagnostics.NotifyMeBudgetTier(3);
            }
            else if (consumed >= _meEffortBudget - (_meEffortBudget >> 2))
            {
                allowExhaustiveFallback = false;
                allowRef1Search = false;
                seedSearchRangeThisMb = 16;
                adaptiveRange = Math.Min(adaptiveRange, 16);
                rangeCapThisMb = Math.Min(4, rangeCapThisMb);
                H264PInterDiagnostics.NotifyMeBudgetTier(2);
            }
            else if (consumed >= _meEffortBudget >> 1)
            {
                allowExhaustiveFallback = false;
                rangeCapThisMb = Math.Min(8, rangeCapThisMb);
                H264PInterDiagnostics.NotifyMeBudgetTier(1);
            }
        }

        var lambdaThisMb = LambdaSatdForQp(qpThisMb);

        // ME against DPB slot 0 (most recent reference, refIdx=0).
        var partResult = H264MotionEstimator.SearchMbSubPartitions(
            current, strideY,
            _paddedRefY, _paddedStrideY,
            mbX + HaloLuma, mbY + HaloLuma,
            mvPredictor,
            adaptiveRange,
            _useMotionSatd,
            _kernels,
            temporalMv: temporalMv,
            fastSearch: _fastSearch,
            fastSeedSearchRange: seedSearchRangeThisMb,
            lambda: lambdaThisMb,
            pictureWidth: _width,
            pictureHeight: _height,
            allowSubPartitionSearch: allowSubPartitionSearch,
            subPartitionRangeCap: rangeCapThisMb,
            allowExhaustiveFallback: allowExhaustiveFallback);
        var winRefIdx = 0;
        // Ref1 must beat ref0 by a rate-aware margin (below); when ref0 already sits at the
        // "good enough" floor (4 per sample over a 16x16 luma MB, the same floor the sub-partition
        // search early-outs at), a full second search is usually wasted work. But "usually" is not
        // "always" — tile-period scroll is a measured case where ref0 is acceptable while ref1 (the
        // less-requantised IDR reconstruction) is meaningfully better — so instead of skipping
        // outright, compare the references with symmetric cheap luma SAD probes: ref1 at the
        // linear-motion extrapolation 2×Mv0 (two frames back) and at Mv0 itself (co-located /
        // static), against ref0 at Mv0. Only when ref1 shows no margin-clearing advantage under the
        // same metric is the full second search skipped. On well-predicted content this halves the
        // inter ME work per MB.
        var ref1TieMargin = H264PInterDiagnostics.DisableRef1TieMargin ? 0 : 2 * lambdaThisMb;
        // "Good enough" must track the quantiser: at low QP a residual of 4 per sample still codes
        // significant coefficients (and a better reference still pays off), so the floor scales with
        // lambda and only saturates at the sub-partition early-out floor for mid/high QPs.
        var ref1SearchFloor = Math.Min(4 * 16 * 16, 32 * lambdaThisMb);
        var searchRef1 = _shared.DpbCount >= 2 && allowRef1Search;
        if (searchRef1 && !H264PInterDiagnostics.DisableRef1TieMargin &&
            partResult.TotalSad <= ref1SearchFloor + ref1TieMargin)
        {
            var mv0 = partResult.Mv0;
            var mvLinear = new H264MotionEstimator.Mv(
                (short)Math.Clamp(2 * mv0.X, short.MinValue, short.MaxValue),
                (short)Math.Clamp(2 * mv0.Y, short.MinValue, short.MaxValue));
            var probeRef1 = Math.Min(
                SeedProbeSadLuma(_shared.DpbPaddedY[1], current, strideY, mbX, mbY, mvLinear),
                SeedProbeSadLuma(_shared.DpbPaddedY[1], current, strideY, mbX, mbY, mv0));
            var probeRef0 = SeedProbeSadLuma(_paddedRefY, current, strideY, mbX, mbY, mv0);
            searchRef1 = probeRef1 != int.MaxValue && probeRef1 + ref1TieMargin < probeRef0;
        }

        if (searchRef1)
        {
            // Search ref1 using ref0's winning MV as the temporal seed. Without this, ref1 always
            // starts its hex search from (0,0) — the refIdx-filtered spatial predictor collapses
            // to zero when all neighbours used ref0, making the competition fundamentally unfair.
            var partResult1 = H264MotionEstimator.SearchMbSubPartitions(
                current, strideY,
                _shared.DpbPaddedY[1], _paddedStrideY,
                mbX + HaloLuma, mbY + HaloLuma,
                mvPredictor,
                adaptiveRange,
                _useMotionSatd,
                _kernels,
                temporalMv: partResult.Mv0,
                fastSearch: _fastSearch,
                fastSeedSearchRange: seedSearchRangeThisMb,
                lambda: lambdaThisMb,
                pictureWidth: _width,
                pictureHeight: _height,
                allowSubPartitionSearch: allowSubPartitionSearch,
                subPartitionRangeCap: rangeCapThisMb,
                allowExhaustiveFallback: allowExhaustiveFallback);

            // Ref1 must beat ref0 by a rate-aware margin, not a raw SAD tie-break. ref_idx_l0 is
            // te(v)-coded at 1 bit either way (§9.1.1), but a two-frames-back MV roughly doubles the
            // MVD magnitude (extra ~2 bits), and — far costlier downstream — a refIdx≠0 winner
            // disables P_Skip for every MB that predicts from it (§8.4.1.1 requires A/B refIdx 0)
            // and feeds its double-length MV back into the temporal seed field. On noise-tied
            // content the raw comparison made ref1 win ~half the time for no distortion gain,
            // cascading skip failures rows deep below every slice-top row. Encoder
            // search policy only; either reference is normatively valid.
            if (partResult1.TotalSad + ref1TieMargin < partResult.TotalSad)
            {
                partResult = partResult1;
                winRefIdx = 1;
                GatherInterNeighbourMvs(mbx, mby,
                    out mvA, out aAvail, out aRefIdx,
                    out mvB, out bAvail, out bRefIdx,
                    out mvC, out cAvail, out cRefIdx,
                    out mvD, out dAvail, out dRefIdx);
                mvPredictor = H264MotionEstimator.PredictMvWithRefIdx(
                    mvA, aRefIdx, mvB, bRefIdx, mvC, cRefIdx, mvD, dRefIdx, currentRefIdx: 1,
                    aAbsent, bAbsent, cAbsent, dAbsent);
            }
        }

        H264PInterDiagnostics.TraceMbDecision(
            _currentFrameNum, _currentCodedFrameIndex, mbx, mby,
            $"sliceSeq={_sliceSeq} phase2 me variance={variance} sadSkip={sadSkip} range={adaptiveRange} part={partResult.Partition} " +
            $"totalSad={partResult.TotalSad} m0=({partResult.Mv0.X},{partResult.Mv0.Y}) m1=({partResult.Mv1.X},{partResult.Mv1.Y}) " +
            $"m2=({partResult.Mv2.X},{partResult.Mv2.Y}) m3=({partResult.Mv3.X},{partResult.Mv3.Y}) refIdx={winRefIdx}");
        if (!AreActivePartitionMvsChromaSafe(partResult, mbX, mbY))
        {
            // Chroma-unsafe fallback: use refIdx=0 safe 16x16 (simpler path; chroma safety checked again below).
            var safe = H264MotionEstimator.SearchMb16x16(
                current, strideY,
                _paddedRefY, _paddedStrideY,
                mbX + HaloLuma, mbY + HaloLuma,
                mvPredictor,
                adaptiveRange,
                _useMotionSatd,
                _kernels,
                pictureWidth: _width,
                pictureHeight: _height,
                fractionalPelRefinementRounds: 2,
                lambda: lambdaThisMb);
            partResult = new H264MotionEstimator.PartitionResult(
                H264MotionEstimator.McPartition.Mb16x16,
                safe.BestMv,
                default,
                default,
                default,
                safe.BestSad);
            winRefIdx = 0;
            // Re-derive predictor for ref 0.
            GatherInterNeighbourMvs(mbx, mby,
                out mvA, out aAvail, out aRefIdx,
                out mvB, out bAvail, out bRefIdx,
                out var mvC2, out var cAvail2, out var cRefIdx2,
                out var mvD2, out var dAvail2, out var dRefIdx2);
            mvPredictor = H264MotionEstimator.PredictMvWithRefIdx(
                mvA, aRefIdx, mvB, bRefIdx, mvC2, cRefIdx2, mvD2, dRefIdx2, currentRefIdx: 0,
                aAbsent, bAbsent, cAbsent, dAbsent);
            H264PInterDiagnostics.TraceMbDecision(
                _currentFrameNum, _currentCodedFrameIndex, mbx, mby,
                $"sliceSeq={_sliceSeq} phase2 fallback-safe16x16 mv=({safe.BestMv.X},{safe.BestMv.Y}) sad={safe.BestSad}");
        }

        if (collectPhase2Timing)
            phase2MeTicks = Stopwatch.GetTimestamp() - phase2MeStartTicks;

        var interPredReconstructed = false;
        // Intra-in-P fallback (§7.4.5: a P slice may contain I_16×16 macroblocks). When inter
        // prediction is poor — most often the leading edge of a scroll, an occlusion, or a local
        // scene change, where the matching content is off-screen in every reference — a spatial
        // Intra_16×16 macroblock can cost far fewer bits. The candidate is scored with SATD of the
        // prediction residual (a sound proxy for post-transform residual bits, unlike prediction
        // SSE which over-penalises inter whose residual coding would have removed that energy
        // cheaply) plus an estimate of the signalling bits. Disable via the Phase2b diagnostic kill
        // switch. Runs before the inter residual loop so the loser path is never reconstructed.
        if (_enableIntraInPFallback && !H264PInterDiagnostics.ShouldDisablePhase2b())
        {
            if (collectPhase1Timing)
            {
                H264PInterDiagnostics.NotifyPhase2InterPredRebuild(
                    phase1PredBuilt && winRefIdx == 0 &&
                    partResult.Partition == H264MotionEstimator.McPartition.Mb16x16 &&
                    partResult.Mv0.X == mvSkipPred.X && partResult.Mv0.Y == mvSkipPred.Y);
            }
            ReconstructInterPredPerPartition(
                partResult, winRefIdx, mbX, mbY, chromaMbX, chromaMbY, predY, predU, predV, _kernels);
            interPredReconstructed = true;

            Span<byte> i16SrcFlat = stackalloc byte[256];
            for (var ry = 0; ry < 16; ry++)
                srcY.Slice((mbY + ry) * strideY + mbX, 16).CopyTo(i16SrcFlat.Slice(ry * 16, 16));

            // SATD(source, inter prediction) — luma residual-bit proxy. predY is a contiguous 16×16.
            var interSatd = _kernels.Satd16x16(i16SrcFlat, 16, predY, 16);
            var interBits = EstimateInterBitCost(partResult, mvPredictor);
            var rdLambda = Math.Max(1, LambdaSatdForQp(qpThisMb));
            var interRd = checked(interSatd + rdLambda * interBits);

            Span<byte> i16Top = stackalloc byte[16];
            Span<byte> i16Left = stackalloc byte[16];
            // H.264 6.4.4: top neighbour MB belongs to the prior slice when mby == _firstMbRowInSlice;
            // decoder treats it as unavailable. Match it so encoder/decoder pick the same I_16×16 mode.
            var i16TopAvail = mby > _firstMbRowInSlice;
            var i16LeftAvail = mbx > 0;
            var i16TopLeftAvail = i16TopAvail && i16LeftAvail;
            byte i16TopLeft = 0;

            if (i16TopAvail)
                _recY.AsSpan().Slice((mbY - 1) * _width + mbX, 16).CopyTo(i16Top);
            if (i16LeftAvail)
                for (var y = 0; y < 16; y++) i16Left[y] = _recY[(mbY + y) * _width + (mbX - 1)];
            if (i16TopLeftAvail)
                i16TopLeft = _recY[(mbY - 1) * _width + (mbX - 1)];

            var (bestI16Mode, _) = H264Intra16x16Prediction.BestI16x16Mode(
                i16SrcFlat,
                i16Top, i16TopAvail,
                i16Left, i16LeftAvail,
                i16TopLeft, i16TopLeftAvail,
                _kernels);

            Span<byte> i16Pred = stackalloc byte[256];
            _kernels.PredictIntra16x16(
                bestI16Mode, i16Top, i16TopAvail, i16Left, i16LeftAvail, i16TopLeft, i16TopLeftAvail, i16Pred);
            var intraSatd = _kernels.Satd16x16(i16SrcFlat, 16, i16Pred, 16);
            var intraBits = EstimateIntra16x16BitCost(bestI16Mode);
            var intraRd = checked(intraSatd + rdLambda * intraBits);
            // Switch to intra only on a decisive win (≥25% lower cost). The SATD proxy is luma-only
            // and ignores that, at a fixed QP, an MB whose inter prediction is merely *adequate*
            // still reconstructs to full quality with a modest residual — flipping it to intra to
            // shave a few bits trades real PSNR for little gain (e.g. a scroll's leading edge). The
            // margin reserves the fallback for genuine prediction failures (scene cuts, occlusion,
            // off-screen content) where intra is dramatically cheaper.
            var chooseIntra = intraRd + (intraRd >> 2) < interRd;
            H264PInterDiagnostics.NotifyPhase2bCandidateRd(
                interSatd, interBits, interRd,
                intraSatd, intraBits, intraRd,
                chooseIntra);

            // I_4×4-in-P. A single Intra_16×16 mode captures only a planar/DC trend; the macroblocks
            // that produce the visible blocking on fast SMW motion (scroll leading edge, occlusion,
            // fades to/from black, scene cuts) carry high-frequency detail an Intra_4×4 MB predicts far
            // better. Evaluating I_4×4 is comparatively costly, so it is gated on a poor inter SATD —
            // only the macroblocks the inter search already failed on, a minority even within a motion
            // frame. When I_4×4 decisively beats both the inter match and the I_16×16 candidate, hand
            // off to the existing P-slice I_NxN encoder by returning false with _mbIsInter still clear
            // (the slice loop then calls WriteMacroblock(isPslice:true)). The proxy SATD predicts from
            // source neighbours, so a decisive margin is required to absorb its slight optimism; either
            // intra outcome decodes correctly (MV-predictor and Intra_4×4 MPM are intra-neighbour-aware).
            if (!chooseIntra
                && _enableIntraInPFallback
                && interSatd >= I4x4InPInterSatdGate)
            {
                var i4Satd = EstimateMbI4x4SatdFromSource(srcY, strideY, mbx, mby);
                var i4Bits = intraBits + I4x4InPModeBitsEstimate;
                var i4Rd = checked(i4Satd + rdLambda * i4Bits);
                if (i4Rd + (i4Rd >> 2) < interRd && i4Rd < intraRd)
                {
                    // Hand off to the P-slice I_NxN path. Nothing has been emitted for this MB yet and
                    // pendingSkipRun is untouched (the slice loop flushes it); the inter prediction
                    // written into _recY above is overwritten by the I_4×4 reconstruction.
                    H264PInterDiagnostics.NotifyPhase2bIntraWin();
                    return false;
                }
            }

            if (chooseIntra)
            {
                H264PInterDiagnostics.NotifyPhase2bIntraWin();
                bs.WriteUe((uint)pendingSkipRun);
                pendingSkipRun = 0;
                EncodeI16x16Macroblock(bs, mbIndex, srcY, strideY, srcU, srcV, strideUv,
                    qpThisMb, useTrellis,
                    bestI16Mode,
                    i16Top, i16TopAvail, i16Left, i16LeftAvail, i16TopLeft, i16TopLeftAvail,
                    isPSlice: true);
                if (collectPhase2Timing)
                {
                    var phase2TotalTicks = Stopwatch.GetTimestamp() - phase2StartTicks;
                    H264PInterDiagnostics.NotifyPhase2Timing(
                        phase2TotalTicks, phase2MeTicks, phase2PredTicks, phase2LumaTicks, phase2ChromaTicks, phase2WriteTicks);
                }
                // Return true so the outer loop treats this as "handled" and does not call
                // WriteMacroblock a second time. The MB is fully written by EncodeI16x16Macroblock.
                return true;
            }
        }

        var phase2PredStartTicks = collectPhase2Timing ? Stopwatch.GetTimestamp() : 0;
        if (!interPredReconstructed)
        {
            if (collectPhase1Timing)
            {
                H264PInterDiagnostics.NotifyPhase2InterPredRebuild(
                    phase1PredBuilt && winRefIdx == 0 &&
                    partResult.Partition == H264MotionEstimator.McPartition.Mb16x16 &&
                    partResult.Mv0.X == mvSkipPred.X && partResult.Mv0.Y == mvSkipPred.Y);
            }
            // Reconstruct luma/chroma per-partition, filling the 16×16 predY and 8×8 predU/predV buffers.
            // The residual loop below reads these without knowing the partition shape.
            ReconstructInterPredPerPartition(
                partResult, winRefIdx, mbX, mbY, chromaMbX, chromaMbY, predY, predU, predV, _kernels);
        }
        if (collectPhase2Timing)
            phase2PredTicks = Stopwatch.GetTimestamp() - phase2PredStartTicks;
        var predSse = traceMb
            ? ComputeInterPredictionSse(srcY, strideY, srcU, srcV, strideUv, mbX, mbY, predY, predU, predV)
            : 0;

        // Compute residual + transform + quant + reconstruct for all 16 luma 4×4 blocks.
        var phase2LumaStartTicks = collectPhase2Timing ? Stopwatch.GetTimestamp() : 0;
        Span<short> mbBlkZ = stackalloc short[16 * 16];
        Span<byte> srcBlk = stackalloc byte[16];
        Span<byte> predBlk = stackalloc byte[16];
        var blkCbp = 0;
        for (var sIdx = 0; sIdx < 16; sIdx++)
        {
            var br = ScanIdxToBr[sIdx];
            var bc = ScanIdxToBc[sIdx];
            var raster = (br << 2) + bc;
            var lx = bc * 4;
            var ly = br * 4;
            _kernels.GatherSrcBlock4x4(srcY, (mbY + ly) * strideY + (mbX + lx), strideY, srcBlk);
            for (var rr = 0; rr < 4; rr++)
            {
                var po = (ly + rr) * 16 + lx;
                predBlk[rr * 4 + 0] = predY[po + 0];
                predBlk[rr * 4 + 1] = predY[po + 1];
                predBlk[rr * 4 + 2] = predY[po + 2];
                predBlk[rr * 4 + 3] = predY[po + 3];
            }

            var nz = useTrellis
                ? H264TransformBundle.EncodeResidual4x4Trellis(srcBlk, predBlk, qpThisMb,
                    mbBlkZ.Slice(raster * 16, 16), _recY.AsSpan((mbY + ly) * _width + (mbX + lx)), _width,
                    H264TrellisQuant4x4.LambdaForQp(qpThisMb))
                : _kernels.EncodeResidual4x4(
                        srcBlk, predBlk, qpThisMb,
                        mbBlkZ.Slice(raster * 16, 16),
                        _recY.AsSpan((mbY + ly) * _width + (mbX + lx)), _width);

            _nonZeros[mbIndex * 16 + raster] = (byte)Math.Min(nz, 16);

            var bi = br >> 1;
            var bj = bc >> 1;
            if (nz != 0)
            {
                blkCbp |= 1 << (bi * 2 + bj);
            }
        }
        if (collectPhase2Timing)
            phase2LumaTicks = Stopwatch.GetTimestamp() - phase2LumaStartTicks;

        var phase2ChromaStartTicks = collectPhase2Timing ? Stopwatch.GetTimestamp() : 0;
        Span<short> chromaDcU = stackalloc short[4];
        Span<short> chromaDcV = stackalloc short[4];
        Span<short> chromaAcU = stackalloc short[16 * 4];
        Span<short> chromaAcV = stackalloc short[16 * 4];
        Span<byte> nzU = stackalloc byte[4];
        Span<byte> nzV = stackalloc byte[4];
        var anyAcU = PrepareChroma8x8(mbx, mby, srcU, strideUv, _recU.AsSpan(), uvW, predU, chromaDcU, chromaAcU, nzU, qpThisMb,
            skipChromaDcCoordinateRefinement: _preferRealtimeLatencyTuning);
        var anyAcV = PrepareChroma8x8(mbx, mby, srcV, strideUv, _recV.AsSpan(), uvW, predV, chromaDcV, chromaAcV, nzV, qpThisMb,
            skipChromaDcCoordinateRefinement: _preferRealtimeLatencyTuning);
        nzU.CopyTo(_chromaNonZeros.AsSpan(mbIndex * 8, 4));
        nzV.CopyTo(_chromaNonZeros.AsSpan(mbIndex * 8 + 4, 4));
        if (collectPhase2Timing)
            phase2ChromaTicks = Stopwatch.GetTimestamp() - phase2ChromaStartTicks;

        var anyDcU = ChromaDcCoeffAny(chromaDcU);
        var anyDcV = ChromaDcCoeffAny(chromaDcV);
        byte cbpChroma = 0;
        if (anyDcU || anyDcV || anyAcU || anyAcV)
        {
            cbpChroma = (byte)((anyAcU || anyAcV) ? 2 : 1);
        }
        var cbp = (byte)(blkCbp | (cbpChroma << 4));
        var reconSse = traceMb
            ? ComputeInterReconstructionSse(srcY, strideY, srcU, srcV, strideUv, mbX, mbY)
            : 0;

        // Skip decision (8.4.1.1): valid only for P_16×16 where the single MV equals the skip predictor
        // AND the winning reference is ref0. P_skip always implies ref0; a ref1 winner with zero
        // residual must be coded as P_inter (ref_idx=1, CBP=0) — never as skip.
        var residualEmpty = cbp == 0;
        var canSkip = residualEmpty
            && winRefIdx == 0
            && partResult.Partition == H264MotionEstimator.McPartition.Mb16x16
            && partResult.Mv0.X == mvSkipPred.X && partResult.Mv0.Y == mvSkipPred.Y;

        _mbIsInter[mbIndex] = true;

        if (canSkip)
        {
            H264PInterDiagnostics.TraceMbDecision(
                _currentFrameNum, _currentCodedFrameIndex, mbx, mby,
                $"sliceSeq={_sliceSeq} phase2->skip residualEmpty=1 cbp={cbp} predSse={predSse} reconSse={reconSse} mv=({mvSkipPred.X},{mvSkipPred.Y})");
            _mbIsSkip[mbIndex] = true;
            _mbMvs[mbIndex] = mvSkipPred;
            _mbPartitions[mbIndex] = H264MotionEstimator.McPartition.Mb16x16;
            _mbSubPartMvs[mbIndex * 4] = mvSkipPred;
            pendingSkipRun++;
            if (collectPhase2Timing)
            {
                var phase2TotalTicks = Stopwatch.GetTimestamp() - phase2StartTicks;
                H264PInterDiagnostics.NotifyPhase2Timing(
                    phase2TotalTicks, phase2MeTicks, phase2PredTicks, phase2LumaTicks, phase2ChromaTicks, phase2WriteTicks);
            }
            return true;
        }

        var phase2WriteStartTicks = collectPhase2Timing ? Stopwatch.GetTimestamp() : 0;
        // Flush pending mb_skip_run per H.264 7.3.4.
        bs.WriteUe((uint)pendingSkipRun);
        pendingSkipRun = 0;

        // Write the partition-specific inter MB header and record MVs.
        CommitInterMbHeader(
            bs, mbIndex, partResult, winRefIdx, _shared.DpbCount >= 2 ? 1 : 0,
            mvPredictor, mvB, bAvail, mvC, cAvail, mvD, dAvail, cbp, qpThisMb);
        H264PInterDiagnostics.TraceMbDecision(
            _currentFrameNum, _currentCodedFrameIndex, mbx, mby,
            $"sliceSeq={_sliceSeq} phase2->inter cbp={cbp} partition={partResult.Partition} residualEmpty={(residualEmpty ? 1 : 0)} predSse={predSse} reconSse={reconSse}");

        // Luma residual.
        for (var sIdx = 0; sIdx < 16; sIdx++)
        {
            var br = ScanIdxToBr[sIdx];
            var bc = ScanIdxToBc[sIdx];
            var raster = (br << 2) + bc;
            var bi = br >> 1;
            var bj = bc >> 1;
            if ((blkCbp & (1 << (bi * 2 + bj))) == 0) continue;

            var blkZ = mbBlkZ.Slice(raster * 16, 16);
            var nc = DeriveLumaNc(mbIndex, br, bc);
            H264CavlcResidual.WriteBlockResidual(bs, blkZ, 15, H264ResidualKind.Luma4X4, nc);
        }

        // Chroma DC / AC residual blocks.
        if (cbpChroma >= 1)
        {
            H264CavlcResidual.WriteBlockResidual(bs, chromaDcU, 3, H264ResidualKind.ChromaDc, 0);
            H264CavlcResidual.WriteBlockResidual(bs, chromaDcV, 3, H264ResidualKind.ChromaDc, 0);
        }

        if (cbpChroma == 2)
        {
            Span<sbyte> chromaNzcCtx = stackalloc sbyte[ChromaCtxSlots];
            FillChromaNzcContext(mbIndex, chromaNzcCtx);
            Span<short> chromaPack15 = stackalloc short[15];
            for (var comp = 0; comp < 2; comp++)
            {
                var compAc = comp == 0 ? chromaAcU : chromaAcV;
                for (var cb = 0; cb < 4; cb++)
                {
                    var slot = ChromaCtxSlot(comp, cb >> 1, cb & 1);
                    var nc = DeriveCoeffTokenNc(chromaNzcCtx[slot - 1], chromaNzcCtx[slot - ChromaCtxStride]);
                    var blkZ = compAc.Slice(cb * 16, 16);
                    for (var t = 0; t < 15; t++)
                    {
                        chromaPack15[t] = blkZ[1 + t];
                    }

                    H264CavlcResidual.WriteBlockResidual(bs, chromaPack15, 14, H264ResidualKind.ChromaAc, nc);
                    chromaNzcCtx[slot] = (sbyte)H264CavlcResidual.TotalCoefficients(chromaPack15, 14);
                    _chromaNonZeros[mbIndex * 8 + comp * 4 + cb] = (byte)chromaNzcCtx[slot];
                }
            }
        }

        if (collectPhase2Timing)
        {
            phase2WriteTicks = Stopwatch.GetTimestamp() - phase2WriteStartTicks;
            var phase2TotalTicks = Stopwatch.GetTimestamp() - phase2StartTicks;
            H264PInterDiagnostics.NotifyPhase2Timing(
                phase2TotalTicks, phase2MeTicks, phase2PredTicks, phase2LumaTicks, phase2ChromaTicks, phase2WriteTicks);
        }

        return false;
    }

    /// <summary>
    /// Reconstruct the 16×16 luma and 8×8 chroma prediction buffers using per-partition MVs from
    /// <paramref name="part"/>. After this call, <paramref name="predY"/>, <paramref name="predU"/>,
    /// and <paramref name="predV"/> are fully populated from the padded reference.
    /// </summary>
    /// <summary>
    /// Encodes one Intra_16×16 macroblock. Shared by the P-slice inter/intra decision
    /// (<paramref name="isPSlice"/>=true) and the I-slice I16×16 vs I4×4 RD competition
    /// (<paramref name="isPSlice"/>=false). Writes the full macroblock_layer syntax
    /// (mb_type through residuals), updates <see cref="_recY"/>/<see cref="_recU"/>/<see cref="_recV"/>,
    /// <see cref="_nonZeros"/>, <see cref="_chromaNonZeros"/>, and <see cref="_mbIsInter"/>.
    /// </summary>
    private void EncodeI16x16Macroblock(
        H264RbspBitBuffer bs,
        int mbIndex,
        ReadOnlySpan<byte> srcY, int strideY,
        ReadOnlySpan<byte> srcU, ReadOnlySpan<byte> srcV, int strideUv,
        int qpThisMb,
        bool useTrellis,
        int i16Mode,
        ReadOnlySpan<byte> topRow16, bool topAvail,
        ReadOnlySpan<byte> leftCol16, bool leftAvail,
        byte topLeft, bool topLeftAvail,
        bool isPSlice)
    {
        var mbx = mbIndex % _mbW;
        var mby = mbIndex / _mbW;
        var mbX = mbx * 16;
        var mbY = mby * 16;
        var uvW = _width / 2;

        // ── Luma I16×16 prediction ──────────────────────────────────────────────
        Span<byte> predY16 = stackalloc byte[256];
        _kernels.PredictIntra16x16(i16Mode, topRow16, topAvail, leftCol16, leftAvail, topLeft, topLeftAvail, predY16);

        // ── Forward DCT for each 4×4 block; collect raw (pre-quant) DC values ──
        // Process in raster order (block 0 = TL, 1 = one right, 4 = one down, ...)
        // so that the DC array index matches the Hadamard grid layout expected by
        // H264LumaDcHadamard.ForwardHadamard4x4.
        Span<int> rawDc16 = stackalloc int[16];
        Span<int> coeffStore = stackalloc int[16 * 16]; // indexed [raster * 16 + coeff]
        Span<short> residualBuf = stackalloc short[16]; // reused per block

        for (var raster = 0; raster < 16; raster++)
        {
            var br = raster >> 2;  // block row (0-3)
            var bc = raster & 3;   // block col (0-3)
            var lx = bc * 4;
            var ly = br * 4;
            for (var ry = 0; ry < 4; ry++)
            for (var rx = 0; rx < 4; rx++)
            {
                var s = srcY[(mbY + ly + ry) * strideY + (mbX + lx + rx)];
                var p = predY16[(ly + ry) * 16 + (lx + rx)];
                residualBuf[ry * 4 + rx] = (short)(s - p);
            }
            var coeff = coeffStore.Slice(raster * 16, 16);
            H264BlockTransform.ForwardDct4X4Scalar(residualBuf, coeff);
            rawDc16[raster] = coeff[0];
        }

        // ── Hadamard + quantize the 16 luma DC values ───────────────────────────
        Span<int> hadDc = stackalloc int[16];
        H264LumaDcHadamard.ForwardHadamard4x4(rawDc16, hadDc);
        H264LumaDcHadamard.QuantLumaDcHadamard(hadDc, qpThisMb);

        // H.264 7.4.5.3.1 — Intra16x16DCLevel[i] is indexed by the inverse 4×4 luma block scan
        // (zigzag), not raster. WriteBlockResidual treats its input as zigzag-scanned coefficients,
        // so we must convert the Hadamard 4×4 grid (raster order) into zigzag scan order here.
        Span<short> lumaDcQ = stackalloc short[16];
        var zzDc = H264Zigzag.Frame4X4;
        for (var i = 0; i < 16; i++) lumaDcQ[i] = (short)hadDc[zzDc[i]];

        // ── Decoder-exact DC reconstruction (§8.5.10) ───────────────────────────
        // hadDc currently holds the quantised DC levels (raster). Reconstruct exactly as a
        // conforming decoder does — inverse Hadamard (no norm) + spec multiply-scale — so the
        // injected per-block DC is bit-identical to the decoder for any non-zero DC residual.
        Span<int> reconDc = stackalloc int[16];
        H264LumaDcHadamard.ReconstructLumaDcFromQuant(hadDc, qpThisMb, reconDc);

        // ── Quantize AC, reconstruct, update nonzero counts ─────────────────────
        Span<short> lumaAcBlocks = stackalloc short[16 * 16];  // raster order, pos[0] always 0
        Span<byte> lumaAcNz = stackalloc byte[16];
        lumaAcBlocks.Clear();
        lumaAcNz.Clear();
        var cbpLumaAny = false;
        var zz = H264Zigzag.Frame4X4;
        Span<int> dequantBuf = stackalloc int[16]; // reused per block
        Span<int> invResBuf = stackalloc int[16];  // reused per block
        Span<int> forwardAcBuf = stackalloc int[16];
        Span<short> zzBufTrellis = stackalloc short[16];
        Span<int> zzIntTrellis = stackalloc int[16];

        for (var raster = 0; raster < 16; raster++)
        {
            var br = raster >> 2;
            var bc = raster & 3;
            var lx = bc * 4;
            var ly = br * 4;
            var coeff = coeffStore.Slice(raster * 16, 16);

            coeff.CopyTo(forwardAcBuf);
            coeff[0] = 0; // DC handled by Hadamard plane
            forwardAcBuf[0] = 0;

            H264BlockTransform.Quant4X4Scalar(coeff, qpThisMb);

            var acOut = lumaAcBlocks.Slice(raster * 16, 16);
            var nzAc = 0;
            if (useTrellis)
            {
                for (var i = 0; i < 16; i++)
                {
                    zzBufTrellis[i] = (short)coeff[zz[i]];
                }

                _ = H264TrellisQuant4x4.Apply(zzBufTrellis, forwardAcBuf, qpThisMb,
                    H264TrellisQuant4x4.LambdaForQp(qpThisMb));

                for (var i = 0; i < 16; i++)
                {
                    zzIntTrellis[i] = zzBufTrellis[i];
                }

                H264BlockTransform.ZigzagToRaster(zzIntTrellis, coeff);
                for (var i = 1; i < 16; i++)
                {
                    var v = zzBufTrellis[i];
                    acOut[i] = v;
                    if (v != 0)
                    {
                        nzAc++;
                    }
                }
            }
            else
            {
                for (var i = 1; i < 16; i++)
                {
                    var v = coeff[zz[i]];
                    acOut[i] = (short)v;
                    if (v != 0)
                    {
                        nzAc++;
                    }
                }
            }
            lumaAcNz[raster] = (byte)nzAc;
            if (nzAc > 0) cbpLumaAny = true;
            _nonZeros[mbIndex * 16 + raster] = (byte)nzAc;

            H264BlockTransform.DequantAc4x4Spec(coeff, qpThisMb, dequantBuf);
            dequantBuf[0] = reconDc[raster]; // inject Hadamard-domain reconstructed DC

            H264BlockTransform.InverseDct4x4Spec(dequantBuf, invResBuf);

            var recBase = (mbY + ly) * _width + (mbX + lx);
            for (var ry = 0; ry < 4; ry++)
            for (var rx = 0; rx < 4; rx++)
            {
                var p = predY16[(ly + ry) * 16 + (lx + rx)];
                _recY[recBase + ry * _width + rx] = (byte)Math.Clamp(p + invResBuf[ry * 4 + rx], 0, 255);
            }
        }

        // ── Chroma ─────────────────────────────────────────────────────────────
        var chromaMode = ChooseChromaIntraMode(mbx, mby, srcU, srcV, strideUv, _recU.AsSpan(), _recV.AsSpan(), uvW, qpThisMb);
        Span<byte> chromaPredU = stackalloc byte[64];
        Span<byte> chromaPredV = stackalloc byte[64];
        ComputeChromaPrediction(chromaMode, mbx, mby, _firstMbRowInSlice * 8, _recU.AsSpan(), uvW, chromaPredU);
        ComputeChromaPrediction(chromaMode, mbx, mby, _firstMbRowInSlice * 8, _recV.AsSpan(), uvW, chromaPredV);

        Span<short> chromaDcU = stackalloc short[4];
        Span<short> chromaDcV = stackalloc short[4];
        Span<short> chromaAcU = stackalloc short[16 * 4];
        Span<short> chromaAcV = stackalloc short[16 * 4];
        Span<byte> nzU = stackalloc byte[4];
        Span<byte> nzV = stackalloc byte[4];
        var anyAcU = PrepareChroma8x8(mbx, mby, srcU, strideUv, _recU.AsSpan(), uvW, chromaPredU,
            chromaDcU, chromaAcU, nzU, qpThisMb, _preferRealtimeLatencyTuning);
        var anyAcV = PrepareChroma8x8(mbx, mby, srcV, strideUv, _recV.AsSpan(), uvW, chromaPredV,
            chromaDcV, chromaAcV, nzV, qpThisMb, _preferRealtimeLatencyTuning);
        nzU.CopyTo(_chromaNonZeros.AsSpan(mbIndex * 8, 4));
        nzV.CopyTo(_chromaNonZeros.AsSpan(mbIndex * 8 + 4, 4));

        var anyDcU = ChromaDcCoeffAny(chromaDcU);
        var anyDcV = ChromaDcCoeffAny(chromaDcV);
        byte cbpChroma = 0;
        if (anyDcU || anyDcV || anyAcU || anyAcV)
            cbpChroma = (byte)((anyAcU || anyAcV) ? 2 : 1);

        var cbpLuma = cbpLumaAny ? 1 : 0;

        // ── MB state update ─────────────────────────────────────────────────────
        _mbIsInter[mbIndex] = false;
        _mbIsSkip[mbIndex] = false;
        _mbMvs[mbIndex] = default;
        _mbPartitions[mbIndex] = H264MotionEstimator.McPartition.Mb16x16;
        _mbSubPartMvs[mbIndex * 4] = default;
        _mbSubPartMvs[mbIndex * 4 + 1] = default;
        _mbSubPartMvs[mbIndex * 4 + 2] = default;
        _mbSubPartMvs[mbIndex * 4 + 3] = default;

        // ── Write macroblock syntax ─────────────────────────────────────────────
        // Chroma AC nC requires left+above neighbours across MB boundaries (H.264 9.2.1).
        // Pre-fill the nnz cache after luma and update it as chroma AC blocks are written,
        // matching the Intra4x4 and P-inter paths.
        var chromaNzcCtxI16 = new sbyte[ChromaCtxSlots];
        FillChromaNzcContext(mbIndex, chromaNzcCtxI16);
        // Copy stackalloc spans to arrays so they can be captured by the lambda below.
        var nzUArr = nzU.ToArray();
        var nzVArr = nzV.ToArray();

        H264SliceMbWriter.WriteIntra16x16Macroblock(
            bs,
            predMode: i16Mode,
            chromaPredMode: chromaMode,
            lumaDcQuantised: lumaDcQ,
            lumaAcBlocks: lumaAcBlocks,
            lumaBlkNonZeros: lumaAcNz,
            chromaDcU: chromaDcU,
            chromaDcV: chromaDcV,
            chromaAcU: chromaAcU,
            chromaAcV: chromaAcV,
            chromaNzU: nzU,
            chromaNzV: nzV,
            cbpLuma: cbpLuma,
            cbpChroma: cbpChroma,
            qpDelta: qpThisMb - _lastMbQp,
            isPSlice: isPSlice,
            ncLookupChroma: n =>
            {
                var comp = n >> 2;
                var cb = n & 3;
                var slot = ChromaCtxSlot(comp, cb >> 1, cb & 1);
                var nc = DeriveCoeffTokenNc(chromaNzcCtxI16[slot - 1], chromaNzcCtxI16[slot - ChromaCtxStride]);
                // Write-back so blocks later in the raster order see this block as their A / B neighbour.
                chromaNzcCtxI16[slot] = (sbyte)(comp == 0 ? nzUArr[cb] : nzVArr[cb]);
                return nc;
            },
            ncLookup: blk => DeriveLumaNc(mbIndex, blk >> 2, blk & 3));

        // Per H.264 8.3.1.1: when a neighbouring MB is Intra_16x16 its I4x4 prediction
        // mode is treated as DC_PRED (2) by any subsequent I4x4 MB's MPM derivation.
        // Fill _intraModes so FillIntra4x4ModeContext delivers the correct value.
        _intraModes.AsSpan(mbIndex * IntraModeBoundaryStride, IntraModeBoundaryStride).Fill(2);
        // §7.3.5.1: mb_qp_delta is always present for Intra_16x16 (condition includes
        // MbPartPredMode==Intra_16x16). Advance decoder-equivalent QPY,PREV.
        _lastMbQp = qpThisMb;
    }

    private void ReconstructInterPredPerPartition(
        H264MotionEstimator.PartitionResult part,
        int winRefIdx,
        int mbX, int mbY, int chromaMbX, int chromaMbY,
        Span<byte> predY, Span<byte> predU, Span<byte> predV,
        IH264KernelSet kernels)
    {
        var refY = _shared.DpbPaddedY[winRefIdx];
        var refU = _shared.DpbPaddedU[winRefIdx];
        var refV = _shared.DpbPaddedV[winRefIdx];
        switch (part.Partition)
        {
            case H264MotionEstimator.McPartition.Mb16x16:
                H264InterReconstructor.ReconstructLuma(
                    refY, _paddedStrideY, HaloLuma, mbX, mbY, part.Mv0.X, part.Mv0.Y, 16, 16, predY, 16, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refU, _paddedStrideUv, HaloChroma, chromaMbX, chromaMbY, part.Mv0.X, part.Mv0.Y, 8, 8, predU, 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refV, _paddedStrideUv, HaloChroma, chromaMbX, chromaMbY, part.Mv0.X, part.Mv0.Y, 8, 8, predV, 8, kernels);
                break;

            case H264MotionEstimator.McPartition.Mb16x8:
                H264InterReconstructor.ReconstructLuma(
                    refY, _paddedStrideY, HaloLuma, mbX, mbY, part.Mv0.X, part.Mv0.Y, 16, 8, predY, 16, kernels);
                H264InterReconstructor.ReconstructLuma(
                    refY, _paddedStrideY, HaloLuma, mbX, mbY + 8, part.Mv1.X, part.Mv1.Y, 16, 8, predY[128..], 16, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refU, _paddedStrideUv, HaloChroma, chromaMbX, chromaMbY, part.Mv0.X, part.Mv0.Y, 8, 4, predU, 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refU, _paddedStrideUv, HaloChroma, chromaMbX, chromaMbY + 4, part.Mv1.X, part.Mv1.Y, 8, 4, predU[32..], 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refV, _paddedStrideUv, HaloChroma, chromaMbX, chromaMbY, part.Mv0.X, part.Mv0.Y, 8, 4, predV, 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refV, _paddedStrideUv, HaloChroma, chromaMbX, chromaMbY + 4, part.Mv1.X, part.Mv1.Y, 8, 4, predV[32..], 8, kernels);
                break;

            case H264MotionEstimator.McPartition.Mb8x16:
                H264InterReconstructor.ReconstructLuma(
                    refY, _paddedStrideY, HaloLuma, mbX, mbY, part.Mv0.X, part.Mv0.Y, 8, 16, predY, 16, kernels);
                H264InterReconstructor.ReconstructLuma(
                    refY, _paddedStrideY, HaloLuma, mbX + 8, mbY, part.Mv1.X, part.Mv1.Y, 8, 16, predY[8..], 16, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refU, _paddedStrideUv, HaloChroma, chromaMbX, chromaMbY, part.Mv0.X, part.Mv0.Y, 4, 8, predU, 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refU, _paddedStrideUv, HaloChroma, chromaMbX + 4, chromaMbY, part.Mv1.X, part.Mv1.Y, 4, 8, predU[4..], 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refV, _paddedStrideUv, HaloChroma, chromaMbX, chromaMbY, part.Mv0.X, part.Mv0.Y, 4, 8, predV, 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refV, _paddedStrideUv, HaloChroma, chromaMbX + 4, chromaMbY, part.Mv1.X, part.Mv1.Y, 4, 8, predV[4..], 8, kernels);
                break;

            default:
                H264InterReconstructor.ReconstructLuma(
                    refY, _paddedStrideY, HaloLuma, mbX, mbY, part.Mv0.X, part.Mv0.Y, 8, 8, predY, 16, kernels);
                H264InterReconstructor.ReconstructLuma(
                    refY, _paddedStrideY, HaloLuma, mbX + 8, mbY, part.Mv1.X, part.Mv1.Y, 8, 8, predY[8..], 16, kernels);
                H264InterReconstructor.ReconstructLuma(
                    refY, _paddedStrideY, HaloLuma, mbX, mbY + 8, part.Mv2.X, part.Mv2.Y, 8, 8, predY[128..], 16, kernels);
                H264InterReconstructor.ReconstructLuma(
                    refY, _paddedStrideY, HaloLuma, mbX + 8, mbY + 8, part.Mv3.X, part.Mv3.Y, 8, 8, predY[136..], 16, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refU, _paddedStrideUv, HaloChroma, chromaMbX, chromaMbY, part.Mv0.X, part.Mv0.Y, 4, 4, predU, 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refU, _paddedStrideUv, HaloChroma, chromaMbX + 4, chromaMbY, part.Mv1.X, part.Mv1.Y, 4, 4, predU[4..], 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refU, _paddedStrideUv, HaloChroma, chromaMbX, chromaMbY + 4, part.Mv2.X, part.Mv2.Y, 4, 4, predU[32..], 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refU, _paddedStrideUv, HaloChroma, chromaMbX + 4, chromaMbY + 4, part.Mv3.X, part.Mv3.Y, 4, 4, predU[36..], 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refV, _paddedStrideUv, HaloChroma, chromaMbX, chromaMbY, part.Mv0.X, part.Mv0.Y, 4, 4, predV, 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refV, _paddedStrideUv, HaloChroma, chromaMbX + 4, chromaMbY, part.Mv1.X, part.Mv1.Y, 4, 4, predV[4..], 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refV, _paddedStrideUv, HaloChroma, chromaMbX, chromaMbY + 4, part.Mv2.X, part.Mv2.Y, 4, 4, predV[32..], 8, kernels);
                H264InterReconstructor.ReconstructChroma(
                    refV, _paddedStrideUv, HaloChroma, chromaMbX + 4, chromaMbY + 4, part.Mv3.X, part.Mv3.Y, 4, 4, predV[36..], 8, kernels);
                break;
        }
    }

    private bool AreActivePartitionMvsChromaSafe(H264MotionEstimator.PartitionResult part, int mbX, int mbY)
    {
        static bool IsSafe(int pictureWidth, int pictureHeight, int x, int y, int w, int h, H264MotionEstimator.Mv mv) =>
            H264InterReconstructor.IsMvSafeForInterBlockAtMb(pictureWidth, pictureHeight, x, y, w, h, mv.X, mv.Y);

        return part.Partition switch
        {
            H264MotionEstimator.McPartition.Mb16x16 =>
                IsSafe(_width, _height, mbX, mbY, 16, 16, part.Mv0),
            H264MotionEstimator.McPartition.Mb16x8 =>
                IsSafe(_width, _height, mbX, mbY, 16, 8, part.Mv0) &&
                IsSafe(_width, _height, mbX, mbY + 8, 16, 8, part.Mv1),
            H264MotionEstimator.McPartition.Mb8x16 =>
                IsSafe(_width, _height, mbX, mbY, 8, 16, part.Mv0) &&
                IsSafe(_width, _height, mbX + 8, mbY, 8, 16, part.Mv1),
            _ =>
                IsSafe(_width, _height, mbX, mbY, 8, 8, part.Mv0) &&
                IsSafe(_width, _height, mbX + 8, mbY, 8, 8, part.Mv1) &&
                IsSafe(_width, _height, mbX, mbY + 8, 8, 8, part.Mv2) &&
                IsSafe(_width, _height, mbX + 8, mbY + 8, 8, 8, part.Mv3),
        };
    }

    private static int ComputeLumaPredictionSse(ReadOnlySpan<byte> src, ReadOnlySpan<byte> pred)
    {
        var sse = 0;
        for (var i = 0; i < 256; i++)
        {
            var d = src[i] - pred[i];
            sse += d * d;
        }

        return sse;
    }

    /// <summary>
    /// One luma SAD of the current MB against the padded reference at the co-located previous-frame
    /// MV, truncated to integer pel (arithmetic >> 2, matching the temporal seed candidate in
    /// <see cref="H264MotionEstimator.SearchMbSubPartitions"/>). Encoder-search heuristic only — it
    /// never affects normative prediction — used to decide whether a failed P_Skip means "widen the
    /// search" or "the previous frame already explains this motion". Returns
    /// <see cref="int.MaxValue"/> when the displaced window falls outside the padded reference.
    /// </summary>
    private int TemporalSeedProbeSadLuma(
        ReadOnlySpan<byte> currentMb, int currentStride, int mbX, int mbY, H264MotionEstimator.Mv mv) =>
        SeedProbeSadLuma(_paddedRefY, currentMb, currentStride, mbX, mbY, mv);

    /// <summary>
    /// One luma SAD of the current MB against an arbitrary padded reference plane at an integer-pel
    /// truncated MV (see <see cref="TemporalSeedProbeSadLuma"/>); also drives the ref1 competition
    /// gate. Returns <see cref="int.MaxValue"/> when the displaced window falls outside the plane.
    /// </summary>
    private int SeedProbeSadLuma(
        ReadOnlySpan<byte> paddedRef,
        ReadOnlySpan<byte> currentMb,
        int currentStride,
        int mbX,
        int mbY,
        H264MotionEstimator.Mv mv)
    {
        var rx = mbX + HaloLuma + (mv.X >> 2);
        var ry = mbY + HaloLuma + (mv.Y >> 2);
        var paddedH = paddedRef.Length / _paddedStrideY;
        if (rx < 0 || ry < 0 || rx + 16 > _paddedStrideY || ry + 16 > paddedH)
            return int.MaxValue;
        return _kernels.Sad16x16(
            currentMb, currentStride, paddedRef.Slice(ry * _paddedStrideY + rx), _paddedStrideY);
    }

    private static int ComputeInterPredictionSse(
        ReadOnlySpan<byte> srcY,
        int strideY,
        ReadOnlySpan<byte> srcU,
        ReadOnlySpan<byte> srcV,
        int strideUv,
        int mbX,
        int mbY,
        ReadOnlySpan<byte> predY,
        ReadOnlySpan<byte> predU,
        ReadOnlySpan<byte> predV)
    {
        var sse = 0;
        for (var y = 0; y < 16; y++)
        {
            var srcOff = (mbY + y) * strideY + mbX;
            var predOff = y * 16;
            for (var x = 0; x < 16; x++)
            {
                var d = srcY[srcOff + x] - predY[predOff + x];
                sse += d * d;
            }
        }

        var mbChromaX = mbX >> 1;
        var mbChromaY = mbY >> 1;
        for (var y = 0; y < 8; y++)
        {
            var srcOff = (mbChromaY + y) * strideUv + mbChromaX;
            var predOff = y * 8;
            for (var x = 0; x < 8; x++)
            {
                var du = srcU[srcOff + x] - predU[predOff + x];
                var dv = srcV[srcOff + x] - predV[predOff + x];
                sse += du * du;
                sse += dv * dv;
            }
        }

        return sse;
    }

    private int ComputeInterReconstructionSse(
        ReadOnlySpan<byte> srcY,
        int strideY,
        ReadOnlySpan<byte> srcU,
        ReadOnlySpan<byte> srcV,
        int strideUv,
        int mbX,
        int mbY)
    {
        var sse = 0;
        for (var y = 0; y < 16; y++)
        {
            var srcOff = (mbY + y) * strideY + mbX;
            var recOff = (mbY + y) * _width + mbX;
            for (var x = 0; x < 16; x++)
            {
                var d = srcY[srcOff + x] - _recY[recOff + x];
                sse += d * d;
            }
        }

        var mbChromaX = mbX >> 1;
        var mbChromaY = mbY >> 1;
        var uvW = _width >> 1;
        for (var y = 0; y < 8; y++)
        {
            var srcOff = (mbChromaY + y) * strideUv + mbChromaX;
            var recOff = (mbChromaY + y) * uvW + mbChromaX;
            for (var x = 0; x < 8; x++)
            {
                var du = srcU[srcOff + x] - _recU[recOff + x];
                var dv = srcV[srcOff + x] - _recV[recOff + x];
                sse += du * du;
                sse += dv * dv;
            }
        }

        return sse;
    }

    private static int EstimateInterBitCost(H264MotionEstimator.PartitionResult part, H264MotionEstimator.Mv mvPredictor)
    {
        var bits = EstimateUeBits((uint)part.Partition); // mb_type
        var vectors = part.Partition switch
        {
            H264MotionEstimator.McPartition.Mb16x16 => 1,
            H264MotionEstimator.McPartition.Mb16x8 => 2,
            H264MotionEstimator.McPartition.Mb8x16 => 2,
            _ => 4,
        };

        bits += vectors * 4; // ref_idx(0) + signalling overhead
        bits += EstimateMvDeltaBits(part.Mv0, mvPredictor);
        if (vectors >= 2) bits += EstimateMvDeltaBits(part.Mv1, mvPredictor);
        if (vectors >= 3) bits += EstimateMvDeltaBits(part.Mv2, mvPredictor);
        if (vectors >= 4) bits += EstimateMvDeltaBits(part.Mv3, mvPredictor);
        bits += 8; // coded_block_pattern + occasional qp_delta
        return bits;
    }

    private static int EstimateIntra16x16BitCost(int i16Mode)
    {
        // In P-slice: mb_type codeNum roughly 18..26 depending mode/cbp; add chroma mode + qp delta + residual payload budget.
        return EstimateUeBits((uint)(18 + Math.Clamp(i16Mode, 0, 3))) + 64;
    }

    private static int EstimateMvDeltaBits(H264MotionEstimator.Mv mv, H264MotionEstimator.Mv pred)
    {
        var dx = mv.X - pred.X;
        var dy = mv.Y - pred.Y;
        return EstimateSeBits(dx) + EstimateSeBits(dy);
    }

    private static int EstimateSeBits(int value)
    {
        var codeNum = value <= 0 ? (uint)(-value * 2) : (uint)(value * 2 - 1);
        return EstimateUeBits(codeNum);
    }

    private static int EstimateUeBits(uint codeNum)
    {
        var bits = BitOperations.Log2(codeNum + 1u) + 1;
        return (bits << 1) - 1;
    }

    /// <summary>
    /// Writes the partition-specific inter MB header (mb_type + ref_idx + MVD), records MVs in the
    /// per-MB arrays used by the MV predictor and deblocking filter, and writes the CBP + QP delta.
    /// </summary>
    private void CommitInterMbHeader(
        H264RbspBitBuffer bs,
        int mbIndex,
        H264MotionEstimator.PartitionResult part,
        int winRefIdx,
        int numRefIdxActiveMinus1,
        H264MotionEstimator.Mv mvPredictor,
        H264MotionEstimator.Mv mvB, bool bAvail,
        H264MotionEstimator.Mv mvC, bool cAvail,
        H264MotionEstimator.Mv mvD, bool dAvail,
        byte cbp,
        int qpThisMb)
    {
        _mbPartitions[mbIndex] = part.Partition;
        _mbIsInter[mbIndex] = true;
        _shared.MbRefIdx[mbIndex] = (byte)winRefIdx;

        // Positional absence of the MB-level neighbours (decoder PART_NOT_AVAILABLE): a neighbour is
        // absent only when outside the picture/slice. Sub-partition MVP calls pass these (rather than
        // inter-ness) so the §8.4.1.3.1 C←D substitution / B&C rule fire only for genuinely absent
        // neighbours — an intra-in-P neighbour is present and contributes MV (0,0)/refIdx -1.
        var cmbx = mbIndex % _mbW;
        var cmby = mbIndex / _mbW;
        var cTopAbsent = cmby <= _firstMbRowInSlice;
        var mbLeftAbsent = cmbx == 0;
        var mbTopAbsent = cTopAbsent;
        var mbTopRightAbsent = cTopAbsent || cmbx + 1 >= _mbW;
        var mbTopLeftAbsent = cTopAbsent || cmbx == 0;

        switch (part.Partition)
        {
            case H264MotionEstimator.McPartition.Mb16x16:
            {
                var mvdX = part.Mv0.X - mvPredictor.X;
                var mvdY = part.Mv0.Y - mvPredictor.Y;
                var mbx16x16 = mbIndex % _mbW;
                var mby16x16 = mbIndex / _mbW;
                H264PInterDiagnostics.TraceMbDecision(
                    _currentFrameNum, _currentCodedFrameIndex, mbx16x16, mby16x16,
                    $"write16x16 mv=({part.Mv0.X},{part.Mv0.Y}) mvp=({mvPredictor.X},{mvPredictor.Y}) mvd=({mvdX},{mvdY}) cbp={cbp}");
                H264PSliceMbWriter.WritePInter16x16Header(bs, winRefIdx, mvdX, mvdY, numRefIdxActiveMinus1);
                _mbMvs[mbIndex] = part.Mv0;
                _mbSubPartMvs[mbIndex * 4] = part.Mv0;
                break;
            }

            case H264MotionEstimator.McPartition.Mb16x8:
            {
                // Partition 0 (top 16×8): §8.4.1.3 eqn. 8-203 — when the above (B) neighbour has the
                // same refIdx (always refIdx=0 in single-ref Baseline), mvp = mvLXB directly.
                // When B is unavailable, fall back to §8.4.1.3.1 median using MB-level neighbours.
                // Partition 1 (bottom 16×8): §8.4.1.3 eqn. 8-204 — when the left (A) neighbour has
                // same refIdx, mvp = mvLXA. A is resolved partition-aware (see GetNeighbourMvAtPixel).
                // B/C neighbours of partition 1 resolve to partition-0 MV (in-MB), so the fallback
                // when A is absent reduces to part.Mv0.
                var mbxL16x8 = mbIndex % _mbW;
                var mbyL16x8 = mbIndex / _mbW;
                var leftIdx16x8 = mbIndex - 1;
                var topIdx16x8 = (mbyL16x8 - 1) * _mbW + mbxL16x8;
                var topRightIdx16x8 = (mbyL16x8 - 1) * _mbW + (mbxL16x8 + 1);
                var topLeftIdx16x8 = (mbyL16x8 - 1) * _mbW + (mbxL16x8 - 1);
                var leftInter16x8 = mbxL16x8 > 0 && _mbIsInter[leftIdx16x8];
                var leftRef16x8 = leftInter16x8 ? _shared.MbRefIdx[leftIdx16x8] : -1;
                var topRef16x8 = bAvail ? _shared.MbRefIdx[topIdx16x8] : -1;
                var topRightRef16x8 = cAvail ? _shared.MbRefIdx[topRightIdx16x8] : -1;
                var topLeftRef16x8 = dAvail ? _shared.MbRefIdx[topLeftIdx16x8] : -1;

                // Partition 0: §8.4.1.3 eqn. 8-203 — B neighbour at pixel (0, -1) = top MB row 15.
                // Use B directly only when B is inter AND has the same refIdx (§8.4.1.3 condition).
                // Otherwise fall back to the FULL §8.4.1.3.2 median over A/B/C — which retains
                // refIdx-mismatched neighbours (only the directional single-match rule may drop them).
                H264MotionEstimator.Mv mvp0_16x8;
                if (bAvail && topRef16x8 == winRefIdx)
                {
                    mvp0_16x8 = GetNeighbourMvAtPixel(topIdx16x8, 0, 15);
                }
                else
                {
                    var mvAp0 = leftInter16x8 ? GetNeighbourMvAtPixel(leftIdx16x8, 15, 0) : default;
                    var mvBp0 = bAvail ? GetNeighbourMvAtPixel(topIdx16x8, 0, 15) : default;
                    var mvCp0 = cAvail ? GetNeighbourMvAtPixel(topRightIdx16x8, 0, 15) : default;
                    var mvDp0 = dAvail ? GetNeighbourMvAtPixel(topLeftIdx16x8, 15, 15) : default;
                    mvp0_16x8 = H264MotionEstimator.PredictMvWithRefIdx(
                        mvAp0, leftRef16x8, mvBp0, topRef16x8, mvCp0, topRightRef16x8, mvDp0, topLeftRef16x8, winRefIdx,
                        mbLeftAbsent, mbTopAbsent, mbTopRightAbsent, mbTopLeftAbsent);
                }

                // Partition 1: §8.4.1.3 eqn. 8-204 — A neighbour at pixel (-1, 8) = left MB row 8.
                H264MotionEstimator.Mv mvp1_16x8;
                if (leftInter16x8 && leftRef16x8 == winRefIdx)
                {
                    mvp1_16x8 = GetNeighbourMvAtPixel(leftIdx16x8, 15, 8);
                }
                else
                {
                    // Full §8.4.1.3.2 median: A=left@row8, B=partition-0 (in-MB, winRefIdx),
                    // C unavailable→C←D=left@row7. Retains refIdx-mismatched A.
                    var mvAp1 = leftInter16x8 ? GetNeighbourMvAtPixel(leftIdx16x8, 15, 8) : default;
                    var mvDp1 = leftInter16x8 ? GetNeighbourMvAtPixel(leftIdx16x8, 15, 7) : default;
                    // A=left (positional), B=partition-0 (in-MB, present), C=absent → C←D=left@row7,
                    // D=left@row7 (positional).
                    mvp1_16x8 = H264MotionEstimator.PredictMvWithRefIdx(
                        mvAp1, leftRef16x8, part.Mv0, winRefIdx, default, -1, mvDp1, leftRef16x8, winRefIdx,
                        aAbsent: mbLeftAbsent, bAbsent: false, cAbsent: true, dAbsent: mbLeftAbsent);
                }

                var mvd0X16x8 = part.Mv0.X - mvp0_16x8.X;
                var mvd0Y16x8 = part.Mv0.Y - mvp0_16x8.Y;
                var mvd1X16x8 = part.Mv1.X - mvp1_16x8.X;
                var mvd1Y16x8 = part.Mv1.Y - mvp1_16x8.Y;
                H264PInterDiagnostics.TraceMbDecision(
                    _currentFrameNum, _currentCodedFrameIndex, mbxL16x8, mbyL16x8,
                    $"write16x8 mv0=({part.Mv0.X},{part.Mv0.Y}) mvp0=({mvp0_16x8.X},{mvp0_16x8.Y}) " +
                    $"mv1=({part.Mv1.X},{part.Mv1.Y}) mvp1=({mvp1_16x8.X},{mvp1_16x8.Y}) " +
                    $"leftRef={leftRef16x8} topRef={topRef16x8} cAvail={(cAvail ? 1 : 0)} win={winRefIdx}");
                H264PSliceMbWriter.WritePInter16x8Header(bs, winRefIdx, winRefIdx, mvd0X16x8, mvd0Y16x8, mvd1X16x8, mvd1Y16x8, numRefIdxActiveMinus1);
                _mbMvs[mbIndex] = part.Mv0;
                _mbSubPartMvs[mbIndex * 4 + 0] = part.Mv0;
                _mbSubPartMvs[mbIndex * 4 + 1] = part.Mv1;
                break;
            }

            case H264MotionEstimator.McPartition.Mb8x16:
            {
                // Partition 0 (left 8×16): §8.4.1.3 eqn. 8-205 — when the left (A) neighbour has the
                // same refIdx (always in single-ref Baseline), mvp = mvLXA directly.
                // When A is unavailable, fall back to §8.4.1.3.1 median (existing path).
                // Partition 1 (right 8×16): §8.4.1.3 eqn. 8-206 — when C is available and same ref,
                // mvp = mvLXC. C for the right partition is at (16,-1), i.e. the top-right MB.
                var mbxL8x16 = mbIndex % _mbW;
                var mbyL8x16 = mbIndex / _mbW;
                var leftIdx8x16 = mbIndex - 1;
                var topIdx8x16 = (mbyL8x16 - 1) * _mbW + mbxL8x16;
                var topRightIdx8x16 = (mbyL8x16 - 1) * _mbW + (mbxL8x16 + 1);
                var topLeftIdx8x16 = (mbyL8x16 - 1) * _mbW + (mbxL8x16 - 1);
                var leftInter8x16 = mbxL8x16 > 0 && _mbIsInter[leftIdx8x16];
                var leftRef8x16 = leftInter8x16 ? _shared.MbRefIdx[leftIdx8x16] : -1;
                var topRef8x16 = bAvail ? _shared.MbRefIdx[topIdx8x16] : -1;
                var topRightRef8x16 = cAvail ? _shared.MbRefIdx[topRightIdx8x16] : -1;
                var topLeftRef8x16 = dAvail ? _shared.MbRefIdx[topLeftIdx8x16] : -1;

                // Partition 0: §8.4.1.3 eqn. 8-205 — A neighbour at pixel (-1, 0) = left MB col 15 row 0.
                H264MotionEstimator.Mv mvp0_8x16;
                if (leftInter8x16 && leftRef8x16 == winRefIdx)
                {
                    mvp0_8x16 = GetNeighbourMvAtPixel(leftIdx8x16, 15, 0);
                }
                else
                {
                    // A unavailable or different refIdx: FULL §8.4.1.3.2 median. A=left@(15,0),
                    // B=above col0 row15, C=above col8 row15, D=above-left. Retains mismatched A.
                    var mvAp0 = leftInter8x16 ? GetNeighbourMvAtPixel(leftIdx8x16, 15, 0) : default;
                    var mvBp0 = bAvail ? GetNeighbourMvAtPixel(topIdx8x16, 0, 15) : default;
                    var mvCp0 = bAvail ? GetNeighbourMvAtPixel(topIdx8x16, 8, 15) : default;
                    var mvDp0 = dAvail ? GetNeighbourMvAtPixel(topLeftIdx8x16, 15, 15) : default;
                    // A=left, B and C both in the top MB (above col0/col8 → share its presence), D=top-left.
                    mvp0_8x16 = H264MotionEstimator.PredictMvWithRefIdx(
                        mvAp0, leftRef8x16, mvBp0, topRef8x16, mvCp0, topRef8x16, mvDp0, topLeftRef8x16, winRefIdx,
                        aAbsent: mbLeftAbsent, bAbsent: mbTopAbsent, cAbsent: mbTopAbsent, dAbsent: mbTopLeftAbsent);
                }

                // Partition 1: §8.4.1.3 eqn. 8-206 — mvp = mvC when refIdxC == refIdx. CRITICAL: C is
                // the neighbour AFTER the §8.4.1.3.1 substitution, i.e. the top-right MB at (16,-1)
                // when available, otherwise D = above col 7 at (7,-1). At the right picture edge the
                // top-right MB is unavailable, so the shortcut must test the substituted neighbour
                // (above col 7) — not skip straight to the median. Missing this drove an enc/dec MVP
                // mismatch (the decoder fires the shortcut on the substituted C; the encoder did not).
                // The §8.4.1.3.1 C←D substitution keys on *positional* absence of the top-right MB, not
                // its inter-ness: a present-but-intra top-right contributes MV (0,0)/refIdx -1 (no
                // substitution), only a genuinely absent one is replaced by D (above col 7).
                H264MotionEstimator.Mv effC8x16; int effCRef8x16; bool effCAbsent;
                if (!mbTopRightAbsent)
                {
                    effC8x16 = cAvail ? GetNeighbourMvAtPixel(topRightIdx8x16, 0, 15) : default;
                    effCRef8x16 = topRightRef8x16; // cAvail ? committed refIdx : -1 (intra → -1)
                    effCAbsent = false;
                }
                else if (!mbTopAbsent)
                {
                    effC8x16 = GetNeighbourMvAtPixel(topIdx8x16, 7, 15); effCRef8x16 = topRef8x16; effCAbsent = false;
                }
                else { effC8x16 = default; effCRef8x16 = -1; effCAbsent = true; }

                H264MotionEstimator.Mv mvp1_8x16;
                if (effCRef8x16 == winRefIdx)
                {
                    mvp1_8x16 = effC8x16;
                }
                else
                {
                    // Full §8.4.1.3.2 median: A=partition 0 (in-MB, winRefIdx), B=above col8 row15,
                    // C=the (already substituted) above-right neighbour, D unavailable.
                    var mvBp1 = bAvail ? GetNeighbourMvAtPixel(topIdx8x16, 8, 15) : default;
                    mvp1_8x16 = H264MotionEstimator.PredictMvWithRefIdx(
                        part.Mv0, winRefIdx, mvBp1, topRef8x16, effC8x16, effCRef8x16, default, -1, winRefIdx,
                        aAbsent: false, bAbsent: mbTopAbsent, cAbsent: effCAbsent, dAbsent: true);
                }

                var mvd0X8x16 = part.Mv0.X - mvp0_8x16.X;
                var mvd0Y8x16 = part.Mv0.Y - mvp0_8x16.Y;
                var mvd1X8x16 = part.Mv1.X - mvp1_8x16.X;
                var mvd1Y8x16 = part.Mv1.Y - mvp1_8x16.Y;
                H264PInterDiagnostics.TraceMbDecision(
                    _currentFrameNum, _currentCodedFrameIndex, mbxL8x16, mbyL8x16,
                    $"write8x16 mv0=({part.Mv0.X},{part.Mv0.Y}) mvp0=({mvp0_8x16.X},{mvp0_8x16.Y}) mvd0=({mvd0X8x16},{mvd0Y8x16}) " +
                    $"mv1=({part.Mv1.X},{part.Mv1.Y}) mvp1=({mvp1_8x16.X},{mvp1_8x16.Y}) mvd1=({mvd1X8x16},{mvd1Y8x16}) cbp={cbp}");
                H264PSliceMbWriter.WritePInter8x16Header(bs, winRefIdx, winRefIdx, mvd0X8x16, mvd0Y8x16, mvd1X8x16, mvd1Y8x16, numRefIdxActiveMinus1);
                _mbMvs[mbIndex] = part.Mv0;
                _mbSubPartMvs[mbIndex * 4 + 0] = part.Mv0;
                _mbSubPartMvs[mbIndex * 4 + 1] = part.Mv1;
                break;
            }

            default: // Mb8x8
            {
                // Each 8×8 sub-MB is P_L0_8×8 with refIdx=0. Per-sub-MB MV predictors use
                // §8.4.1.3.1 (median) since 8×8 is not a 16×8/8×16 partition and the directional
                // shortcuts (eqns. 8-203–8-206) do not apply. Neighbour A/B/C/D are resolved
                // per §8.4.1.3.2 + §6.4.11.7 (Table 6-2) for each sub-MB's (xS, yS) offset:
                //  sub-MB 0 (TL, 0,0): A=left@(−1,0); B=above@(0,−1); C=above@(8,−1);     D=above-left
                //  sub-MB 1 (TR, 8,0): A=sub-0 Mv0;   B=above@(8,−1); C=top-right@(16,-1); D=above@(7,−1)
                //  sub-MB 2 (BL, 0,8): A=left@(−1,8); B=sub-0 Mv0;    C=sub-1 Mv1;         D=left@(−1,7)
                //  sub-MB 3 (BR, 8,8): A=sub-2 Mv2;   B=sub-1 Mv1;    C←D=sub-0 Mv0;       D=sub-0 Mv0
                System.Diagnostics.Debug.Assert(part.Partition == H264MotionEstimator.McPartition.Mb8x8,
                    "CommitInterMbHeader default case must only be reached for Mb8x8 partitions.");
                var mbxL8x8 = mbIndex % _mbW;
                var mbyL8x8 = mbIndex / _mbW;
                // §8.4.1.3.2 median for 8×8: the directional single-matching-refIdx rule applies, so
                // external neighbours carry their committed refIdx (in-MB sub-partitions are winRefIdx).
                var aAvail8x8 = mbxL8x8 > 0 && _mbIsInter[mbIndex - 1];
                var topIdx8x8 = (mbyL8x8 - 1) * _mbW + mbxL8x8;
                var topLeftIdx8x8 = (mbyL8x8 - 1) * _mbW + (mbxL8x8 - 1);
                var topRightIdx8x8 = (mbyL8x8 - 1) * _mbW + (mbxL8x8 + 1);
                var leftRef8x8 = aAvail8x8 ? _shared.MbRefIdx[mbIndex - 1] : -1;
                var topRef8x8 = bAvail ? _shared.MbRefIdx[topIdx8x8] : -1;
                var topLeftRef8x8 = dAvail ? _shared.MbRefIdx[topLeftIdx8x8] : -1;
                var topRightRef8x8 = cAvail ? _shared.MbRefIdx[topRightIdx8x8] : -1;

                // Sub-MB 0 (TL): partition-aware A/B/C/D from external MBs.
                var mvA0 = aAvail8x8 ? GetNeighbourMvAtPixel(mbIndex - 1, 15, 0) : default;
                var mvB0 = bAvail ? GetNeighbourMvAtPixel(topIdx8x8, 0, 15) : default;
                // C for sub-0 is at (8, -1) in above MB (same MB as B, col 8).
                var mvC0 = bAvail ? GetNeighbourMvAtPixel(topIdx8x8, 8, 15) : default;
                var mvD0 = dAvail ? GetNeighbourMvAtPixel(topLeftIdx8x8, 15, 15) : default;
                // A=left, B=above col0 + C=above col8 (both in the top MB), D=top-left.
                var p0 = H264MotionEstimator.PredictMvWithRefIdx(
                    mvA0, leftRef8x8, mvB0, topRef8x8, mvC0, topRef8x8, mvD0, topLeftRef8x8, winRefIdx,
                    aAbsent: mbLeftAbsent, bAbsent: mbTopAbsent, cAbsent: mbTopAbsent, dAbsent: mbTopLeftAbsent);

                // Sub-MB 1 (TR): A=sub-0 Mv0 (in-MB); B=above col8, C=top-right MB (16,-1), D=above col7.
                var mvB1 = bAvail ? GetNeighbourMvAtPixel(topIdx8x8, 8, 15) : default;
                var mvD1 = bAvail ? GetNeighbourMvAtPixel(topIdx8x8, 7, 15) : default;
                var p1 = H264MotionEstimator.PredictMvWithRefIdx(
                    part.Mv0, winRefIdx, mvB1, topRef8x8, mvC, topRightRef8x8, mvD1, topRef8x8, winRefIdx,
                    aAbsent: false, bAbsent: mbTopAbsent, cAbsent: mbTopRightAbsent, dAbsent: mbTopAbsent);

                // Sub-MB 2 (BL): A=left MB at row 8; B=sub-0 Mv0; C=sub-1 Mv1; D=left MB at row 7.
                var mvA2 = aAvail8x8 ? GetNeighbourMvAtPixel(mbIndex - 1, 15, 8) : default;
                var mvD2 = aAvail8x8 ? GetNeighbourMvAtPixel(mbIndex - 1, 15, 7) : default;
                var p2 = H264MotionEstimator.PredictMvWithRefIdx(
                    mvA2, leftRef8x8, part.Mv0, winRefIdx, part.Mv1, winRefIdx, mvD2, leftRef8x8, winRefIdx,
                    aAbsent: mbLeftAbsent, bAbsent: false, cAbsent: false, dAbsent: mbLeftAbsent);

                // Sub-MB 3 (BR): A=sub-2 Mv2; B=sub-1 Mv1; C is unavailable to the right, so C←D=sub-0 Mv0.
                var p3 = H264MotionEstimator.PredictMvWithRefIdx(
                    part.Mv2, winRefIdx, part.Mv1, winRefIdx, default, -1, part.Mv0, winRefIdx, winRefIdx,
                    aAbsent: false, bAbsent: false, cAbsent: true, dAbsent: false);

                var refIndices = new[] { winRefIdx, winRefIdx, winRefIdx, winRefIdx };
                var subMbTypes = new[] {
                    H264PSliceMbWriter.SubMbType.P_L0_8x8,
                    H264PSliceMbWriter.SubMbType.P_L0_8x8,
                    H264PSliceMbWriter.SubMbType.P_L0_8x8,
                    H264PSliceMbWriter.SubMbType.P_L0_8x8
                };
                // Guard: only P_L0_8×8 sub-partitions are supported in this MVP computation path.
                // If ME is extended to emit 8×4/4×8/4×4 sub-MB types, the per-sub-partition MVP loops
                // must be updated before removing this assertion (BL-011 / §7.3.5.2 Table 7-17).
                System.Diagnostics.Debug.Assert(
                    subMbTypes[0] == H264PSliceMbWriter.SubMbType.P_L0_8x8 &&
                    subMbTypes[1] == H264PSliceMbWriter.SubMbType.P_L0_8x8 &&
                    subMbTypes[2] == H264PSliceMbWriter.SubMbType.P_L0_8x8 &&
                    subMbTypes[3] == H264PSliceMbWriter.SubMbType.P_L0_8x8,
                    "All four sub_mb_type values must be P_L0_8×8; heterogeneous types require per-subMbPartIdx MVP loops.");
                var mvds = new (int X, int Y)[]
                {
                    (part.Mv0.X - p0.X, part.Mv0.Y - p0.Y),
                    (part.Mv1.X - p1.X, part.Mv1.Y - p1.Y),
                    (part.Mv2.X - p2.X, part.Mv2.Y - p2.Y),
                    (part.Mv3.X - p3.X, part.Mv3.Y - p3.Y),
                };
                H264PInterDiagnostics.TraceMbDecision(
                    _currentFrameNum, _currentCodedFrameIndex, mbxL8x8, mbyL8x8,
                    $"write8x8 mv0=({part.Mv0.X},{part.Mv0.Y}) p0=({p0.X},{p0.Y}) mvd0=({mvds[0].X},{mvds[0].Y}) " +
                    $"mv1=({part.Mv1.X},{part.Mv1.Y}) p1=({p1.X},{p1.Y}) mvd1=({mvds[1].X},{mvds[1].Y}) " +
                    $"mv2=({part.Mv2.X},{part.Mv2.Y}) p2=({p2.X},{p2.Y}) mvd2=({mvds[2].X},{mvds[2].Y}) " +
                    $"mv3=({part.Mv3.X},{part.Mv3.Y}) p3=({p3.X},{p3.Y}) mvd3=({mvds[3].X},{mvds[3].Y}) cbp={cbp}");
                H264PSliceMbWriter.WritePInter8x8Header(bs, refIndices, subMbTypes, mvds, numRefIdxActiveMinus1);
                _mbMvs[mbIndex] = part.Mv0;
                _mbSubPartMvs[mbIndex * 4 + 0] = part.Mv0;
                _mbSubPartMvs[mbIndex * 4 + 1] = part.Mv1;
                _mbSubPartMvs[mbIndex * 4 + 2] = part.Mv2;
                _mbSubPartMvs[mbIndex * 4 + 3] = part.Mv3;
                break;
            }
        }

        bs.WriteUe((uint)H264Cbp.InterCbpCodeNum(cbp));
        if (cbp != 0)
        {
            bs.WriteSe(qpThisMb - _lastMbQp);
            _lastMbQp = qpThisMb; // decoder advances QPY,PREV only when mb_qp_delta is present
        }
    }

    private unsafe void WriteMacroblock(
        H264RbspBitBuffer bs,
        int mbIndex,
        ReadOnlySpan<byte> srcY,
        int strideY,
        ReadOnlySpan<byte> u,
        ReadOnlySpan<byte> v,
        int strideUv,
        bool isPslice,
        int qpThisMb,
        bool useTrellis)
    {
        var mbx = mbIndex % _mbW;
        var mby = mbIndex / _mbW;
        var uvW = _width / 2;

        Span<sbyte> modeCtx = stackalloc sbyte[LumaCtxSlots];
        FillIntra4x4ModeContext(mbIndex, modeCtx);
        GetIntraNeighbourMbAvailability(mbx, mby, out var leftMbAvailable, out var aboveMbAvailable);

        if (!isPslice)
        {
            // ── I16×16 vs I4×4 RD competition (I-slice path) ─────────────────────
            // Pass 0: score the best Intra_16×16 mode by SAD.
            var mbX = mbx * 16;
            var mbY = mby * 16;
            Span<byte> i16Top = stackalloc byte[16];
            Span<byte> i16Left = stackalloc byte[16];
            // H.264 6.4.4: top neighbour MB belongs to the prior slice when mby == _firstMbRowInSlice;
            // the decoder treats it as unavailable, so the encoder must too (matches the P-slice
            // path at line ~1486). Single-slice keeps the previous behaviour since
            // _firstMbRowInSlice == 0.
            var i16TopAvail = mby > _firstMbRowInSlice;
            var i16LeftAvail = mbx > 0;
            var i16TopLeftAvail = i16TopAvail && i16LeftAvail;
            byte i16TopLeft = 0;
            if (i16TopAvail)
                _recY.AsSpan((mbY - 1) * _width + mbX, 16).CopyTo(i16Top);
            if (i16LeftAvail)
                for (var y = 0; y < 16; y++) i16Left[y] = _recY[(mbY + y) * _width + (mbX - 1)];
            if (i16TopLeftAvail)
                i16TopLeft = _recY[(mbY - 1) * _width + (mbX - 1)];

            Span<byte> i16SrcFlat = stackalloc byte[256];
            for (var ry = 0; ry < 16; ry++)
                srcY.Slice((mbY + ry) * strideY + mbX, 16).CopyTo(i16SrcFlat.Slice(ry * 16, 16));

            var (bestI16Mode, bestI16Sad) = H264Intra16x16Prediction.BestI16x16Mode(
                i16SrcFlat,
                i16Top, i16TopAvail,
                i16Left, i16LeftAvail,
                i16TopLeft, i16TopLeftAvail,
                _kernels);

            // Pass 1: I4×4 cost estimate — predict + SAD + λ-RDO pick + reconstruct for all 16 blocks.
            // Reconstruction is essential: _recY must be updated as each block is processed so that
            // subsequent blocks read the correct left/top reference pixels (H.264 8.3.1.2 neighbour rule).
            // Both mbBlkZI (quantised coefficients) and bestModesAll are filled here; pass 2 just writes
            // the bitstream syntax without re-running the costly transform.
            Span<short> mbBlkZI = stackalloc short[16 * 16];
            Span<byte> bestPredsPackedAll = stackalloc byte[16 * 16];
            Span<byte> bestModesAll = stackalloc byte[16];
            var blkCbpI = 0;

            Span<byte> candPredsBuf1 = stackalloc byte[9 * 16];
            Span<int> candSads1 = stackalloc int[9];
            Span<int> candModes1 = stackalloc int[9];
            Span<byte> topRow1 = stackalloc byte[9];
            Span<byte> leftCol1 = stackalloc byte[4];
            Span<byte> srcBlk1 = stackalloc byte[16];

            // Save the initial mode context so pass 2 can restart from the same state.
            Span<sbyte> modeCtxInitial = stackalloc sbyte[LumaCtxSlots];
            modeCtx.CopyTo(modeCtxInitial);

            long sumI4Cost = 0;
            long sumI4Sad = 0; // pure SAD sum (no rate term) used for I16×16 vs I4×4 distortion comparison

            // Phase 4: top-K indices for SATD pruning (K=3 candidates from SAD stage).
            Span<int> topKIdx1 = stackalloc int[3];
            Span<byte> topPreds1 = stackalloc byte[48];
            Span<int> topSatds1 = stackalloc int[3];

            for (var sIdx = 0; sIdx < 16; sIdx++)
            {
                var br = ScanIdxToBr[sIdx];
                var bc = ScanIdxToBc[sIdx];
                var raster = (br << 2) + bc;
                var gx = mbx * 16 + bc * 4;
                var gy = mby * 16 + br * 4;
                GatherNeighbors(gx, gy, br, bc, mbx, mby, topRow1, leftCol1, out var topAvail1, out var leftAvail1, out _);
                _kernels.GatherSrcBlock4x4(srcY, gy * strideY + gx, strideY, srcBlk1);

                var slot1 = LumaCtxSlot(br, bc);
                var predModeForRdo1 = H264Intra4X4Prediction.NeighborPredMode(
                    modeCtx[slot1 - 1], modeCtx[slot1 - LumaCtxStride]);

                // Phase 4 — variance fast-path: skip directional modes 3–8 for low-activity blocks.
                var isFlat1 = H264VarianceFastPath.IsLowVariance4x4(srcBlk1, threshold: H264VarianceFastPath.VarianceThreshold);

                var numValid1 = 0;
                for (var cand = 0; cand <= 8; cand++)
                {
                    // Variance fast-path: flat blocks only evaluate DC(2), V(0), H(1).
                    if (isFlat1 && cand > 2)
                        continue;
                    if (!IsIntra4x4ModeAllowed(cand, br, bc, leftMbAvailable, aboveMbAvailable))
                        continue;
                    _kernels.Predict4x4(cand, topRow1, leftCol1, topAvail1, leftAvail1, candPredsBuf1.Slice(numValid1 * 16, 16));
                    candModes1[numValid1] = cand;
                    numValid1++;
                }
                if (numValid1 > 0)
                    _kernels.SadMany4x4(srcBlk1, candPredsBuf1, candSads1, numValid1);

                // Phase 4 — two-stage SATD pruning: select top-3 by SAD, then pick by SATD+λ.
                var numTopK1 = SelectTopKSadIndices(candSads1, numValid1, topKIdx1);
                for (var i = 0; i < numTopK1; i++)
                {
                    var k = topKIdx1[i];
                    candPredsBuf1.Slice(k * 16, 16).CopyTo(topPreds1.Slice(i * 16, 16));
                }

                if (numTopK1 > 0)
                {
                    _kernels.SatdMany4x4(srcBlk1, topPreds1, topSatds1, numTopK1);
                }

                var bestMode1 = 2;
                var bestJ1 = long.MaxValue;
                var bestSad1 = 0;
                var bestPredOff1 = -1;
                for (var i = 0; i < numTopK1; i++)
                {
                    var k = topKIdx1[i];
                    var cand = candModes1[k];
                    var bitCost = cand == predModeForRdo1 ? 1 : 4;
                    var satd = topSatds1[i];
                    long j = satd + (long)LambdaSatdForQp(qpThisMb) * bitCost;
                    if (j < bestJ1 || (j == bestJ1 && cand >= bestMode1))
                    {
                        bestJ1 = j;
                        bestSad1 = candSads1[k];
                        bestMode1 = cand;
                        bestPredOff1 = k * 16;
                    }
                }
                bestModesAll[raster] = (byte)bestMode1;
                sumI4Cost += bestJ1 == long.MaxValue ? 0 : bestJ1;
                sumI4Sad += bestJ1 == long.MaxValue ? 0 : bestSad1;

                if (bestPredOff1 >= 0)
                    candPredsBuf1.Slice(bestPredOff1, 16).CopyTo(bestPredsPackedAll.Slice(raster * 16, 16));
                else
                    _kernels.Predict4x4(bestMode1, topRow1, leftCol1, topAvail1, leftAvail1, bestPredsPackedAll.Slice(raster * 16, 16));
                var bestPredSlice = bestPredsPackedAll.Slice(raster * 16, 16);

                // Reconstruct immediately so subsequent blocks see correct left/top reference pixels.
                var nz1 = useTrellis
                    ? H264TransformBundle.EncodeResidual4x4Trellis(
                        srcBlk1, bestPredSlice, qpThisMb,
                        mbBlkZI.Slice(raster * 16, 16),
                        _recY.AsSpan(gy * _width + gx), _width,
                        H264TrellisQuant4x4.LambdaForQp(qpThisMb))
                    : _kernels.EncodeResidual4x4(
                            srcBlk1, bestPredSlice, qpThisMb,
                            mbBlkZI.Slice(raster * 16, 16),
                            _recY.AsSpan(gy * _width + gx), _width);

                _nonZeros[mbIndex * 16 + raster] = (byte)Math.Min(nz1, 16);
                var bi1 = br >> 1;
                var bj1 = bc >> 1;
                if (nz1 != 0)
                    blkCbpI |= 1 << (bi1 * 2 + bj1);

                // Update the mode context so subsequent blocks see the correct MPM (mirrors pass 2).
                modeCtx[slot1] = (sbyte)bestMode1;
            }

            // ── Decision: I16×16 vs I4×4 (pure-distortion comparison) ──────────
            // Compare raw SADs only. This encoder's lambda table (2–10) is far smaller
            // than the standard H.264 value (~34 at QP=28), so any lambda-weighted
            // formula gives I16×16 an enormous rate-overhead advantage that makes it
            // win for textured MBs where I4×4 has genuinely lower distortion.
            //
            // Using pure SAD:
            //   I16×16 wins iff bestI16Sad ≤ sumI4Sad
            //   Ties broken in favour of I16×16 (it has lower rate regardless of SAD).
            //
            // TODO: revisit once the lambda table is calibrated to the standard scale
            //       (lambda = 0.85 × 2^((QP-12)/3)); at that point a small negative
            //       I16_VS_I4_RATE_BIAS could be re-introduced to account for mb_type
            //       signalling savings.
            // I16×16 wins only when its SAD is strictly zero (perfect prediction = lossless for this MB).
            // Non-zero SAD MBs stay with I4×4 regardless of relative SADs, because any I16×16 residual
            // produces different post-quantization reconstruction from the golden reference fixtures'
            // I4×4, causing pairwise
            // PSNR regression in natural/game content where 40-70% of MBs would otherwise select I16×16.
            // A zero-SAD I16×16 MB has no residual and thus identical decoded pixels vs I4×4.
            // TODO: replace with a proper SAD-threshold once vs-source PSNR quality is used as the target.
            if (bestI16Sad == 0 && sumI4Sad == 0)
            {
                // I16×16 wins: encode it (overwrites _recY and _nonZeros with correct I16×16 values).
                EncodeI16x16Macroblock(bs, mbIndex, srcY, strideY, u, v, strideUv,
                    qpThisMb, useTrellis,
                    bestI16Mode,
                    i16Top, i16TopAvail, i16Left, i16LeftAvail, i16TopLeft, i16TopLeftAvail,
                    isPSlice: false);
                return;
            }

            // ── I4×4 pass 2: reset pred cache and write bitstream using pass-1 results ─
            // Pass 1 already computed mbBlkZI and _recY; pass 2 only needs to write the
            // mode bits and call WriteBlockResidual — no re-transform needed.
            modeCtxInitial.CopyTo(modeCtx);
            bs.WriteUe(0); // mb_type = I_NxN (Table 7-11)

            for (var sIdx = 0; sIdx < 16; sIdx++)
            {
                var br = ScanIdxToBr[sIdx];
                var bc = ScanIdxToBc[sIdx];
                var raster = (br << 2) + bc;

                var bestMode = (int)bestModesAll[raster];
                var slot2 = LumaCtxSlot(br, bc);
                var predModeForRdo2 = H264Intra4X4Prediction.NeighborPredMode(
                    modeCtx[slot2 - 1], modeCtx[slot2 - LumaCtxStride]);

                var modeFlag2 = bestMode == predModeForRdo2;
                bs.WriteBit(modeFlag2);
                if (!modeFlag2)
                {
                    var rem2 = bestMode < predModeForRdo2 ? bestMode : bestMode - 1;
                    bs.WriteBits(3, (uint)rem2);
                }
                modeCtx[slot2] = (sbyte)bestMode;
            }

            if (!AreIntra4x4ModesDecodable(modeCtx, leftMbAvailable, aboveMbAvailable))
                throw new InvalidOperationException(
                    "Selected Intra_4x4 prediction modes read neighbouring samples that are unavailable (H.264 8.3.1.2).");

            StoreIntra4x4ModeBoundary(modeCtx, _intraModes.AsSpan(mbIndex * IntraModeBoundaryStride, IntraModeBoundaryStride));

            var chosenChromaModeI = ChooseChromaIntraMode(mbx, mby, u, v, strideUv, _recU.AsSpan(), _recV.AsSpan(), uvW, qpThisMb);
            bs.WriteUe((uint)chosenChromaModeI);

            Span<byte> chromaPredUI = stackalloc byte[64];
            Span<byte> chromaPredVI = stackalloc byte[64];
            ComputeChromaPrediction(chosenChromaModeI, mbx, mby, _firstMbRowInSlice * 8, _recU.AsSpan(), uvW, chromaPredUI);
            ComputeChromaPrediction(chosenChromaModeI, mbx, mby, _firstMbRowInSlice * 8, _recV.AsSpan(), uvW, chromaPredVI);

            Span<short> chromaDcUI = stackalloc short[4];
            Span<short> chromaDcVI = stackalloc short[4];
            Span<short> chromaAcUI = stackalloc short[16 * 4];
            Span<short> chromaAcVI = stackalloc short[16 * 4];
            Span<byte> nzUI = stackalloc byte[4];
            Span<byte> nzVI = stackalloc byte[4];
            var anyAcUI = PrepareChroma8x8(mbx, mby, u, strideUv, _recU.AsSpan(), uvW, chromaPredUI, chromaDcUI, chromaAcUI, nzUI, qpThisMb);
            var anyAcVI = PrepareChroma8x8(mbx, mby, v, strideUv, _recV.AsSpan(), uvW, chromaPredVI, chromaDcVI, chromaAcVI, nzVI, qpThisMb);
            nzUI.CopyTo(_chromaNonZeros.AsSpan(mbIndex * 8, 4));
            nzVI.CopyTo(_chromaNonZeros.AsSpan(mbIndex * 8 + 4, 4));

            var anyDcUI = ChromaDcCoeffAny(chromaDcUI);
            var anyDcVI = ChromaDcCoeffAny(chromaDcVI);
            byte cbpChromaI = 0;
            if (anyDcUI || anyDcVI || anyAcUI || anyAcVI)
                cbpChromaI = (byte)((anyAcUI || anyAcVI) ? 2 : 1);

            var cbpI = (byte)(blkCbpI | (cbpChromaI << 4));
            bs.WriteUe((uint)H264Cbp.IntraCbpCodeNum(cbpI));
            if (cbpI != 0)
            {
                bs.WriteSe(qpThisMb - _lastMbQp);
                _lastMbQp = qpThisMb; // §7.3.5.1: mb_qp_delta present when cbp>0; advance QPY,PREV
            }

            for (var sIdx = 0; sIdx < 16; sIdx++)
            {
                var br2 = ScanIdxToBr[sIdx];
                var bc2 = ScanIdxToBc[sIdx];
                var raster = (br2 << 2) + bc2;
                var bi2 = br2 >> 1;
                var bj2 = bc2 >> 1;
                if ((blkCbpI & (1 << (bi2 * 2 + bj2))) == 0)
                    continue;
                var nc = DeriveLumaNc(mbIndex, br2, bc2);
                H264CavlcResidual.WriteBlockResidual(bs, mbBlkZI.Slice(raster * 16, 16), 15, H264ResidualKind.Luma4X4, nc);
            }

            if (cbpChromaI >= 1)
            {
                H264CavlcResidual.WriteBlockResidual(bs, chromaDcUI, 3, H264ResidualKind.ChromaDc, 0);
                H264CavlcResidual.WriteBlockResidual(bs, chromaDcVI, 3, H264ResidualKind.ChromaDc, 0);
            }
            if (cbpChromaI == 2)
            {
                Span<sbyte> chromaNzcCtxI = stackalloc sbyte[ChromaCtxSlots];
                FillChromaNzcContext(mbIndex, chromaNzcCtxI);
                Span<short> chromaPack15I = stackalloc short[15];
                for (var comp = 0; comp < 2; comp++)
                {
                    var compAc = comp == 0 ? chromaAcUI : chromaAcVI;
                    for (var cb = 0; cb < 4; cb++)
                    {
                        var slot = ChromaCtxSlot(comp, cb >> 1, cb & 1);
                        var nc = DeriveCoeffTokenNc(chromaNzcCtxI[slot - 1], chromaNzcCtxI[slot - ChromaCtxStride]);
                        var blkZ = compAc.Slice(cb * 16, 16);
                        for (var t = 0; t < 15; t++) chromaPack15I[t] = blkZ[1 + t];
                        H264CavlcResidual.WriteBlockResidual(bs, chromaPack15I, 14, H264ResidualKind.ChromaAc, nc);
                        chromaNzcCtxI[slot] = (sbyte)H264CavlcResidual.TotalCoefficients(chromaPack15I, 14);
                    }
                }
            }

            _mbIsInter[mbIndex] = false;
            _mbIsSkip[mbIndex] = false;
            _mbMvs[mbIndex] = default;
            _mbPartitions[mbIndex] = H264MotionEstimator.McPartition.Mb16x16;
            _mbSubPartMvs[mbIndex * 4] = default;
            _mbSubPartMvs[mbIndex * 4 + 1] = default;
            _mbSubPartMvs[mbIndex * 4 + 2] = default;
            _mbSubPartMvs[mbIndex * 4 + 3] = default;
            return;
        }

        // ── P-slice I_NxN fallback (WriteMacroblock called when TryEncodePInterMacroblock
        //    returned false and neither skip nor I16×16 applied). ──────────────────────────
        bs.WriteUe(5); // mb_type = I_NxN in P-slice (Table 7-14 codeNum 5)

        Span<byte> pred = stackalloc byte[16];
        Span<byte> topRow = stackalloc byte[9];
        Span<byte> leftCol = stackalloc byte[4];
        Span<short> mbBlkZ = stackalloc short[16 * 16];
        Span<byte> srcBlk = stackalloc byte[16];

        var blkCbp = 0;

        // RDO scratch: predict every available candidate mode into a contiguous 9×16-byte buffer,
        // then dispatch one batched SAD against the shared source so the SIMD path holds the source
        // vector in a register across all N candidates instead of reloading it per call.
        Span<byte> candPredsBuf = stackalloc byte[9 * 16];
        Span<int> candSads = stackalloc int[9];
        Span<int> candModes = stackalloc int[9];

        // Phase 4: top-K indices for SATD pruning.
        Span<int> topKIdx = stackalloc int[3];
        Span<byte> topPreds = stackalloc byte[48];
        Span<int> topSatds = stackalloc int[3];

        for (var sIdx = 0; sIdx < 16; sIdx++)
        {
            var br = ScanIdxToBr[sIdx];
            var bc = ScanIdxToBc[sIdx];
            var raster = (br << 2) + bc;
            var gx = mbx * 16 + bc * 4;
            var gy = mby * 16 + br * 4;
            GatherNeighbors(gx, gy, br, bc, mbx, mby, topRow, leftCol, out var topAvail, out var leftAvail, out _);

            _kernels.GatherSrcBlock4x4(srcY, gy * strideY + gx, strideY, srcBlk);

            // Compute MPM up-front so RDO can charge the proper bit cost (1 bit if matches, 4 if not).
            var slot = LumaCtxSlot(br, bc);
            var predModeForRdo = H264Intra4X4Prediction.NeighborPredMode(
                modeCtx[slot - 1], modeCtx[slot - LumaCtxStride]);

            // Phase 4 — variance fast-path: skip directional modes 3–8 for low-activity blocks.
            var isFlat = H264VarianceFastPath.IsLowVariance4x4(srcBlk, threshold: H264VarianceFastPath.VarianceThreshold);

            // All 9 Intra_4x4 modes (0..8) per H.264 8.3.1.2 are now bit-exact against the senior's
            // per-(x,y) parity oracle (see H264Intra4x4PredictorParityTests). Junior-C verified modes
            // 4..8 needed no code change; this widening lets the encoder consider the full mode set.
            var numValid = 0;
            for (var cand = 0; cand <= 8; cand++)
            {
                // Variance fast-path: flat blocks only evaluate DC(2), V(0), H(1).
                if (isFlat && cand > 2)
                    continue;
                if (!IsIntra4x4ModeAllowed(cand, br, bc, leftMbAvailable, aboveMbAvailable))
                {
                    continue;
                }

                _kernels.Predict4x4(cand, topRow, leftCol, topAvail, leftAvail, candPredsBuf.Slice(numValid * 16, 16));
                candModes[numValid] = cand;
                numValid++;
            }

            if (numValid > 0)
            {
                _kernels.SadMany4x4(srcBlk, candPredsBuf, candSads, numValid);
            }

            // Phase 4 — two-stage SATD pruning: select top-3 by SAD, then pick by SATD+λ.
            var numTopK = SelectTopKSadIndices(candSads, numValid, topKIdx);
            for (var i = 0; i < numTopK; i++)
            {
                var k = topKIdx[i];
                candPredsBuf.Slice(k * 16, 16).CopyTo(topPreds.Slice(i * 16, 16));
            }

            if (numTopK > 0)
            {
                _kernels.SatdMany4x4(srcBlk, topPreds, topSatds, numTopK);
            }

            var bestMode = 2;
            var bestJ = long.MaxValue;
            var bestPredOff = -1;
            for (var i = 0; i < numTopK; i++)
            {
                var k = topKIdx[i];
                var cand = candModes[k];
                var bitCost = cand == predModeForRdo ? 1 : 4;
                var satd = topSatds[i];
                long j = satd + (long)LambdaSatdForQp(qpThisMb) * bitCost;
                if (j < bestJ || (j == bestJ && cand >= bestMode))
                {
                    bestJ = j;
                    bestMode = cand;
                    bestPredOff = k * 16;
                }
            }

            if (bestPredOff >= 0)
            {
                candPredsBuf.Slice(bestPredOff, 16).CopyTo(pred);
            }
            else
            {
                _kernels.Predict4x4(bestMode, topRow, leftCol, topAvail, leftAvail, pred);
            }

            // Bundled residual encode + reconstruct (see H264TransformBundle).
            var nz = useTrellis
                ? H264TransformBundle.EncodeResidual4x4Trellis(
                    srcBlk,
                    pred,
                    qpThisMb,
                    mbBlkZ.Slice(raster * 16, 16),
                    _recY.AsSpan(gy * _width + gx),
                    _width,
                    H264TrellisQuant4x4.LambdaForQp(qpThisMb))
                : _kernels.EncodeResidual4x4(
                        srcBlk,
                        pred,
                        qpThisMb,
                        mbBlkZ.Slice(raster * 16, 16),
                        _recY.AsSpan(gy * _width + gx),
                        _width);

            _nonZeros[mbIndex * 16 + raster] = (byte)Math.Min(nz, 16);

            var bi = br >> 1;
            var bj = bc >> 1;
            if (nz != 0)
            {
                blkCbp |= 1 << (bi * 2 + bj);
            }

            var predMode = predModeForRdo;
            var modeFlag = bestMode == predMode;
            bs.WriteBit(modeFlag);
            if (!modeFlag)
            {
                var rem = bestMode < predMode ? bestMode : bestMode - 1;
                bs.WriteBits(3, (uint)rem);
            }

            modeCtx[slot] = (sbyte)bestMode;
        }

        if (!AreIntra4x4ModesDecodable(modeCtx, leftMbAvailable, aboveMbAvailable))
        {
            throw new InvalidOperationException(
                "Selected Intra_4x4 prediction modes read neighbouring samples that are unavailable (H.264 8.3.1.2).");
        }

        StoreIntra4x4ModeBoundary(modeCtx, _intraModes.AsSpan(mbIndex * IntraModeBoundaryStride, IntraModeBoundaryStride));

        // Choose chroma intra prediction mode (H.264 8.3.4 modes 0=DC, 1=Horizontal, 2=Vertical, 3=Plane).
        // SAD-only joint scoring across U+V; same mode applies to both planes per spec.
        var chosenChromaMode = ChooseChromaIntraMode(mbx, mby, u, v, strideUv, _recU.AsSpan(), _recV.AsSpan(), uvW, qpThisMb);
        bs.WriteUe((uint)chosenChromaMode);

        Span<byte> chromaPredU = stackalloc byte[64];
        Span<byte> chromaPredV = stackalloc byte[64];
        ComputeChromaPrediction(chosenChromaMode, mbx, mby, _firstMbRowInSlice * 8, _recU.AsSpan(), uvW, chromaPredU);
        ComputeChromaPrediction(chosenChromaMode, mbx, mby, _firstMbRowInSlice * 8, _recV.AsSpan(), uvW, chromaPredV);

        Span<short> chromaDcU = stackalloc short[4];
        Span<short> chromaDcV = stackalloc short[4];
        Span<short> chromaAcU = stackalloc short[16 * 4];
        Span<short> chromaAcV = stackalloc short[16 * 4];
        Span<byte> nzU = stackalloc byte[4];
        Span<byte> nzV = stackalloc byte[4];
        var anyAcU = PrepareChroma8x8(mbx, mby, u, strideUv, _recU.AsSpan(), uvW, chromaPredU, chromaDcU, chromaAcU, nzU, qpThisMb);
        var anyAcV = PrepareChroma8x8(mbx, mby, v, strideUv, _recV.AsSpan(), uvW, chromaPredV, chromaDcV, chromaAcV, nzV, qpThisMb);
        nzU.CopyTo(_chromaNonZeros.AsSpan(mbIndex * 8, 4));
        nzV.CopyTo(_chromaNonZeros.AsSpan(mbIndex * 8 + 4, 4));

        var anyDcU = ChromaDcCoeffAny(chromaDcU);
        var anyDcV = ChromaDcCoeffAny(chromaDcV);
        byte cbpChroma = 0;
        if (anyDcU || anyDcV || anyAcU || anyAcV)
        {
            cbpChroma = (byte)((anyAcU || anyAcV) ? 2 : 1);
        }
        var cbp = (byte)(blkCbp | (cbpChroma << 4));
        bs.WriteUe((uint)H264Cbp.IntraCbpCodeNum(cbp));
        if (cbp != 0)
        {
            // mb_qp_delta is signed delta from the previous MB's QP (or slice QP for the first MB);
            // see H.264 7.4.5. Only written when cbp>0; advance decoder-equivalent QPY,PREV.
            bs.WriteSe(qpThisMb - _lastMbQp);
            _lastMbQp = qpThisMb;
        }

        for (var sIdx = 0; sIdx < 16; sIdx++)
        {
            var br = ScanIdxToBr[sIdx];
            var bc = ScanIdxToBc[sIdx];
            var raster = (br << 2) + bc;
            var bi = br >> 1;
            var bj = bc >> 1;
            if ((blkCbp & (1 << (bi * 2 + bj))) == 0)
            {
                continue;
            }

            var blkZ = mbBlkZ.Slice(raster * 16, 16);
            var nc = DeriveLumaNc(mbIndex, br, bc);
            H264CavlcResidual.WriteBlockResidual(bs, blkZ, 15, H264ResidualKind.Luma4X4, nc);
        }

        if (cbpChroma >= 1)
        {
            H264CavlcResidual.WriteBlockResidual(bs, chromaDcU, 3, H264ResidualKind.ChromaDc, 0);
            H264CavlcResidual.WriteBlockResidual(bs, chromaDcV, 3, H264ResidualKind.ChromaDc, 0);
        }

        if (cbpChroma == 2)
        {
            Span<sbyte> chromaNzcCtx = stackalloc sbyte[ChromaCtxSlots];
            FillChromaNzcContext(mbIndex, chromaNzcCtx);
            Span<short> chromaPack15 = stackalloc short[15];
            for (var comp = 0; comp < 2; comp++)
            {
                var compAc = comp == 0 ? chromaAcU : chromaAcV;
                for (var cb = 0; cb < 4; cb++)
                {
                    var slot = ChromaCtxSlot(comp, cb >> 1, cb & 1);
                    var nc = DeriveCoeffTokenNc(chromaNzcCtx[slot - 1], chromaNzcCtx[slot - ChromaCtxStride]);
                    var blkZ = compAc.Slice(cb * 16, 16);
                    for (var t = 0; t < 15; t++)
                    {
                        chromaPack15[t] = blkZ[1 + t];
                    }

                    H264CavlcResidual.WriteBlockResidual(
                        bs,
                        chromaPack15,
                        14,
                        H264ResidualKind.ChromaAc,
                        nc);
                    chromaNzcCtx[slot] = (sbyte)H264CavlcResidual.TotalCoefficients(chromaPack15, 14);
                    _chromaNonZeros[mbIndex * 8 + comp * 4 + cb] = (byte)chromaNzcCtx[slot];
                }
            }
        }
    }

    /// <summary>
    /// Sentinel stored in a neighbour-context grid slot whose neighbouring block does not exist for the
    /// purposes of H.264 §9.2.1 — outside the picture, or in a different slice (§6.4.4).
    /// </summary>
    private const sbyte NzcSlotUnavailable = -1;

    /// <summary>
    /// nC for <c>coeff_token</c> from the total-coefficient counts of neighbours A and B, exactly as
    /// H.264 §9.2.1 defines it: both available → <c>(nA + nB + 1) &gt;&gt; 1</c>; only one available →
    /// that one; neither available → 0. Pass <see cref="NzcSlotUnavailable"/> (any negative value) for
    /// an unavailable neighbour.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int DeriveCoeffTokenNc(int nA, int nB)
    {
        if (nA < 0)
        {
            return nB < 0 ? 0 : nB;
        }

        return nB < 0 ? nA : (nA + nB + 1) >> 1;
    }

    /// <summary>
    /// Seeds the halo of the chroma neighbour-context grid (see <see cref="ChromaCtxSlot"/>) with the
    /// total-coefficient counts of the left and above macroblocks' edge 4×4 chroma blocks, for both
    /// components. Interior slots are filled by the caller as each chroma AC block is written, so that
    /// blocks later in raster order read the counts of the blocks they follow (H.264 §9.2.1).
    /// </summary>
    /// <remarks>
    /// Cross-slice neighbours are left unavailable: §6.4.4 makes a macroblock in another slice
    /// unavailable for neighbour derivation, so the first macroblock row of a non-first slice derives
    /// nC with no B neighbour.
    /// </remarks>
    private void FillChromaNzcContext(int mbIndex, Span<sbyte> ctx)
    {
        ctx.Fill(NzcSlotUnavailable);

        if (mbIndex % _mbW > 0)
        {
            // A neighbour of column 0 is the left macroblock's column 1.
            var left = (mbIndex - 1) * 8;
            for (var component = 0; component < 2; component++)
            {
                for (var row = 0; row < 2; row++)
                {
                    ctx[ChromaCtxSlot(component, row, -1)] = (sbyte)_chromaNonZeros[left + component * 4 + row * 2 + 1];
                }
            }
        }

        if (mbIndex / _mbW > _firstMbRowInSlice)
        {
            // B neighbour of row 0 is the above macroblock's row 1.
            var above = (mbIndex - _mbW) * 8;
            for (var component = 0; component < 2; component++)
            {
                for (var col = 0; col < 2; col++)
                {
                    ctx[ChromaCtxSlot(component, -1, col)] = (sbyte)_chromaNonZeros[above + component * 4 + 2 + col];
                }
            }
        }
    }

    /// <summary>
    /// Cheap luma Intra_4×4 SATD proxy for the P-slice intra competition. Predicts each of the 16 4×4
    /// blocks from <em>source</em> neighbours — this MB's reconstructed interior does not exist until
    /// the MB is committed — and returns the summed best-mode SATD. Mirrors <see cref="GatherNeighbors"/>'s
    /// availability/substitution rules and reuses <see cref="IsIntra4x4ModeAllowed"/> so the candidate set
    /// matches the real I_4×4 encoder; the only divergence is reading source instead of reconstructed
    /// pixels, which makes the estimate mildly optimistic. The caller (the intra-in-P fallback) demands
    /// a decisive RD margin before switching to I_4×4, absorbing that optimism.
    /// </summary>
    private int EstimateMbI4x4SatdFromSource(ReadOnlySpan<byte> srcY, int strideY, int mbx, int mby)
    {
        GetIntraNeighbourMbAvailability(mbx, mby, out var leftMbAvailable, out var aboveMbAvailable);

        Span<byte> srcBlk = stackalloc byte[16];
        Span<byte> topRow = stackalloc byte[9];
        Span<byte> leftCol = stackalloc byte[4];
        Span<byte> preds = stackalloc byte[9 * 16];
        Span<int> satds = stackalloc int[9];

        long sum = 0;
        for (var sIdx = 0; sIdx < 16; sIdx++)
        {
            var br = ScanIdxToBr[sIdx];
            var bc = ScanIdxToBc[sIdx];
            var raster = (br << 2) + bc;
            var gx = mbx * 16 + bc * 4;
            var gy = mby * 16 + br * 4;
            var topAvail = gy > _firstMbRowInSlice * 16;
            var leftAvail = gx > 0;

            for (var r = 0; r < 4; r++)
                srcY.Slice((gy + r) * strideY + gx, 4).CopyTo(srcBlk.Slice(r * 4, 4));

            if (topAvail)
            {
                for (var c = 0; c < 4; c++) topRow[1 + c] = srcY[(gy - 1) * strideY + gx + c];
                topRow[0] = leftAvail ? srcY[(gy - 1) * strideY + gx - 1] : topRow[1];
                var topRightAvail = IsTopRightAvailable(br, bc, mbx, mby) && (gx + 4 <= _width - 4);
                if (topRightAvail)
                {
                    for (var c = 0; c < 4; c++) topRow[5 + c] = srcY[(gy - 1) * strideY + gx + 4 + c];
                }
                else
                {
                    var t3 = topRow[4];
                    topRow[5] = t3; topRow[6] = t3; topRow[7] = t3; topRow[8] = t3;
                }
            }
            else
            {
                topRow.Clear();
            }

            if (leftAvail)
                for (var r = 0; r < 4; r++) leftCol[r] = srcY[(gy + r) * strideY + gx - 1];
            else
                leftCol.Clear();

            var numValid = 0;
            for (var cand = 0; cand <= 8; cand++)
            {
                if (!IsIntra4x4ModeAllowed(cand, br, bc, leftMbAvailable, aboveMbAvailable))
                    continue;
                _kernels.Predict4x4(cand, topRow, leftCol, topAvail, leftAvail, preds.Slice(numValid * 16, 16));
                numValid++;
            }
            if (numValid > 0)
                _kernels.SatdMany4x4(srcBlk, preds, satds, numValid);

            var best = int.MaxValue;
            for (var i = 0; i < numValid; i++)
                if (satds[i] < best) best = satds[i];
            if (best != int.MaxValue) sum += best;
        }
        return (int)Math.Min(sum, int.MaxValue);
    }

    /// <summary>
    /// Gather neighbor samples for one luma 4×4 block. <paramref name="topRow"/> layout: TL at [0], T0..T7 at
    /// [1..8] (T4..T7 replicated from T3 when top-right block isn't reconstructed, per H.264 8.3.1.2.1).
    /// <paramref name="topRightAvail"/> reports whether T4..T7 came from real reconstructed samples or the
    /// substitution rule (Intra4x4 modes can still use them either way).
    /// </summary>
    private void GatherNeighbors(
        int gx,
        int gy,
        int br,
        int bc,
        int mbx,
        int mby,
        Span<byte> topRow,
        Span<byte> leftCol,
        out bool topAvail,
        out bool leftAvail,
        out bool topRightAvail)
    {
        // Slice boundary (H.264 6.4.4): blocks whose top row lives in the prior slice are unavailable.
        // _firstMbRowInSlice * 16 is the y-coordinate of the slice's first luma row. For br==0 of an
        // MB at row _firstMbRowInSlice, gy hits that line exactly and topAvail must be false.
        topAvail = gy > _firstMbRowInSlice * 16;
        leftAvail = gx > 0;
        if (topAvail)
        {
            for (var c = 0; c < 4; c++)
            {
                topRow[1 + c] = _recY[(gy - 1) * _width + gx + c];
            }

            topRow[0] = leftAvail
                ? _recY[(gy - 1) * _width + gx - 1]
                : topRow[1];
        }
        else
        {
            topRow.Clear();
        }

        topRightAvail = false;
        if (topAvail)
        {
            topRightAvail = IsTopRightAvailable(br, bc, mbx, mby) && (gx + 4 <= _width - 4);
            if (topRightAvail)
            {
                for (var c = 0; c < 4; c++)
                {
                    topRow[5 + c] = _recY[(gy - 1) * _width + gx + 4 + c];
                }
            }
            else
            {
                // H.264 8.3.1.2.1 substitution: replicate T3 into T4..T7 when top-right not reconstructed.
                var t3 = topRow[4];
                topRow[5] = t3;
                topRow[6] = t3;
                topRow[7] = t3;
                topRow[8] = t3;
            }
        }

        if (leftAvail)
        {
            for (var r = 0; r < 4; r++)
            {
                leftCol[r] = _recY[(gy + r) * _width + gx - 1];
            }
        }
        else
        {
            leftCol.Clear();
        }
    }

    /// <summary>
    /// Whether the block at (br,bc) in MB (mbx,mby) has reconstructed samples for top-right positions
    /// (gx+4..gx+7, gy-1). Encoder visits MBs in raster order and 4×4 blocks within an MB in Z-order
    /// (<see cref="ScanIdxToBr"/>/<see cref="ScanIdxToBc"/>), so the answer is purely structural.
    /// </summary>
    private bool IsTopRightAvailable(int br, int bc, int mbx, int mby)
    {
        if (bc == 3)
        {
            // Top-right falls in the column to the right of the current MB.
            // Slice boundary (H.264 6.4.4): if (mby == _firstMbRowInSlice) the prior MB row is in
            // the prior slice and reports as unavailable.
            return br == 0 && mby > _firstMbRowInSlice && mbx + 1 < _mbW;
        }

        if (br == 0)
        {
            return mby > _firstMbRowInSlice;
        }

        // Within the MB: (br-1, bc+1) coded already iff its scan index < ours (Z-order property).
        return BrBcToScanIdx(br - 1, bc + 1) < BrBcToScanIdx(br, bc);
    }

    private static int BrBcToScanIdx(int br, int bc) => InverseScan[br * 4 + bc];

    /// <summary>Inverse of <see cref="ScanIdxToBr"/>/<see cref="ScanIdxToBc"/>: (br*4+bc) -> scan index.</summary>
    private static ReadOnlySpan<byte> InverseScan =>
    [
        0, 1, 4, 5,
        2, 3, 6, 7,
        8, 9, 12, 13,
        10, 11, 14, 15,
    ];

    /// <summary>
    /// Sample dependencies of each Intra_4×4 prediction mode (H.264 §8.3.1.2), indexed by mode number.
    /// Bit 0 set — the mode reads the left neighbouring samples p[−1, 0..3]; bit 1 set — the mode reads
    /// the above neighbouring samples p[0..3, −1].
    /// </summary>
    /// <remarks>
    /// Modes 0 (Vertical), 3 (Diagonal_Down_Left) and 7 (Vertical_Left) need only the row above. The
    /// above-right samples p[4..7, −1] that modes 3 and 7 also read are substituted with p[3, −1]
    /// whenever the above-right block is unavailable (§8.3.1.2.1), so above availability alone suffices.
    /// Modes 1 (Horizontal) and 8 (Horizontal_Up) need only the left column. Modes 4
    /// (Diagonal_Down_Right), 5 (Vertical_Right) and 6 (Horizontal_Down) need both, plus the corner
    /// sample p[−1, −1], which exists whenever both neighbours do. Mode 2 (DC) has no dependency:
    /// §8.3.1.2.4 defines it for every availability combination, falling back to 1 &lt;&lt; (BitDepth − 1)
    /// when nothing is available.
    /// </remarks>
    private static ReadOnlySpan<byte> Intra4x4ModeSampleNeeds =>
    [
        //   0     1     2     3     4     5     6     7     8
          0b10, 0b01, 0b00, 0b10, 0b11, 0b11, 0b11, 0b10, 0b01,
    ];

    /// <summary>Bit of <see cref="Intra4x4ModeSampleNeeds"/> meaning "reads the left neighbouring samples".</summary>
    private const int NeedsLeftSamples = 0b01;

    /// <summary>Bit of <see cref="Intra4x4ModeSampleNeeds"/> meaning "reads the above neighbouring samples".</summary>
    private const int NeedsAboveSamples = 0b10;

    /// <summary>
    /// Availability of the left (A) and above (B) neighbouring macroblocks of macroblock
    /// (<paramref name="mbx"/>, <paramref name="mby"/>), per H.264 §6.4.11.4 combined with §6.4.4: a
    /// macroblock outside the picture or belonging to a different slice is unavailable. The above
    /// macroblock of a slice's first macroblock row is therefore unavailable even mid-picture, which is
    /// what keeps a multi-slice bitstream decodable.
    /// </summary>
    private void GetIntraNeighbourMbAvailability(int mbx, int mby, out bool leftMbAvailable, out bool aboveMbAvailable)
    {
        leftMbAvailable = IsLeftMbAvailable(mbx);
        aboveMbAvailable = IsAboveMbAvailable(mby, _firstMbRowInSlice);
    }

    /// <summary>
    /// Whether macroblock column <paramref name="mbx"/> has a left (A) neighbour: false only at the left
    /// picture edge. Slices are contiguous in macroblock raster order, so a macroblock's left neighbour is
    /// always in the same slice.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsLeftMbAvailable(int mbx) => mbx > 0;

    /// <summary>
    /// Whether macroblock row <paramref name="mby"/> has an above (B) neighbour, given that the enclosing
    /// slice starts at macroblock row <paramref name="firstMbRowInSlice"/>. False at the top picture edge
    /// and — per §6.4.4, which makes macroblocks of other slices unavailable — also on the first
    /// macroblock row of every non-first slice.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsAboveMbAvailable(int mby, int firstMbRowInSlice) => mby > firstMbRowInSlice;

    /// <summary>
    /// Whether the encoder may offer Intra_4×4 prediction <paramref name="mode"/> as a candidate for the
    /// 4×4 block at geometric position (<paramref name="row"/>, <paramref name="col"/>) of a macroblock
    /// with the given neighbouring-macroblock availability.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first test is the specification requirement (§8.3.1.2, tabulated in
    /// <see cref="Intra4x4ModeSampleNeeds"/>): a mode may not be used when a neighbouring sample it reads
    /// does not exist. Sample availability follows from the block's position — a block with
    /// <c>row &gt; 0</c> takes its above samples from the 4×4 block row above it <em>inside</em> the same
    /// macroblock, and a block with <c>col &gt; 0</c> takes its left samples from inside the macroblock,
    /// so only the top row and the left column depend on the neighbouring macroblocks at all.
    /// </para>
    /// <para>
    /// The remaining tests are Kiln's own mode-candidate policy, not a specification constraint. Along
    /// the top or left edge of a picture — or of a slice, which the availability inputs treat the same
    /// way — the prediction that seeds a macroblock is either missing outright or, for the blocks nearest
    /// the edge, only one step removed from the DC fallback. Directional candidates there win the RD
    /// comparison on a near-degenerate predictor and then quantize poorly, so the search narrows: with no
    /// above macroblock the purely-horizontal modes are dropped throughout the macroblock and the
    /// above-dependent modes are additionally dropped for the two blocks sitting directly beneath the
    /// synthesised top block row; with no left macroblock the purely-horizontal modes are dropped for the
    /// three blocks whose left samples are furthest from genuinely reconstructed content. Every mode
    /// dropped here would still be legal to signal, so narrowing the candidate set only changes which
    /// mode the encoder picks, never whether a decoder can follow it.
    /// </para>
    /// </remarks>
    internal static bool IsIntra4x4ModeAllowed(int mode, int row, int col, bool leftMbAvailable, bool aboveMbAvailable)
    {
        var needs = Intra4x4ModeSampleNeeds[mode];
        var leftSamplesAvailable = col > 0 || leftMbAvailable;
        var aboveSamplesAvailable = row > 0 || aboveMbAvailable;

        if ((needs & NeedsLeftSamples) != 0 && !leftSamplesAvailable)
        {
            return false;
        }

        if ((needs & NeedsAboveSamples) != 0 && !aboveSamplesAvailable)
        {
            return false;
        }

        var horizontalOnly = needs == NeedsLeftSamples;

        if (!aboveMbAvailable)
        {
            if (horizontalOnly)
            {
                return false;
            }

            if ((needs & NeedsAboveSamples) != 0 && row == 1 && col <= 1)
            {
                return false;
            }
        }

        if (!leftMbAvailable
            && horizontalOnly
            && ((row == 0 && col == 2) || (row == 2 && (col == 0 || col == 2))))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Verifies that every Intra_4×4 mode selected for the current macroblock (read out of the
    /// neighbour-context grid) reads only neighbouring samples that exist — the encoder-side equivalent
    /// of the conformance test a decoder applies to an I_NxN macroblock before reconstructing it
    /// (§8.3.1.2). Only the top block row and left block column can fail: every other block's
    /// neighbouring samples come from inside the macroblock. A failure means the candidate filter and the
    /// mode actually signalled disagree, which would desynchronise the decoder, so callers treat it as
    /// fatal.
    /// </summary>
    private static bool AreIntra4x4ModesDecodable(ReadOnlySpan<sbyte> modeCtx, bool leftMbAvailable, bool aboveMbAvailable)
    {
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                int mode = modeCtx[LumaCtxSlot(row, col)];
                if ((uint)mode >= 9)
                {
                    return false;
                }

                var needs = Intra4x4ModeSampleNeeds[mode];
                if ((needs & NeedsLeftSamples) != 0 && col == 0 && !leftMbAvailable)
                {
                    return false;
                }

                if ((needs & NeedsAboveSamples) != 0 && row == 0 && !aboveMbAvailable)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// nC for the luma 4×4 block at geometric (<paramref name="row"/>, <paramref name="col"/>) of
    /// macroblock <paramref name="mbIndex"/>, per H.264 §9.2.1. Neighbours A and B are located per
    /// §6.4.11.4 — inside the macroblock for interior blocks, in the left / above macroblock along the
    /// edges. §6.4.4 makes a macroblock in a different slice unavailable, so the first macroblock row of
    /// a non-first slice has no B neighbour: CAVLC coefficient contexts do not carry across a slice
    /// boundary.
    /// </summary>
    private int DeriveLumaNc(int mbIndex, int row, int col)
    {
        var block = mbIndex * 16 + row * 4 + col;

        int nA;
        if (col > 0)
        {
            nA = _nonZeros[block - 1];
        }
        else if (mbIndex % _mbW > 0)
        {
            nA = _nonZeros[(mbIndex - 1) * 16 + row * 4 + 3];
        }
        else
        {
            nA = NzcSlotUnavailable;
        }

        int nB;
        if (row > 0)
        {
            nB = _nonZeros[block - 4];
        }
        else if (mbIndex / _mbW > _firstMbRowInSlice)
        {
            nB = _nonZeros[(mbIndex - _mbW) * 16 + 12 + col];
        }
        else
        {
            nB = NzcSlotUnavailable;
        }

        return DeriveCoeffTokenNc(nA, nB);
    }

    /// <summary>Bytes of <see cref="_intraModes"/> per macroblock; see that field for the layout and its derivation.</summary>
    internal const int IntraModeBoundaryStride = 7;

    /// <summary>
    /// Grid value meaning "this macroblock has no such neighbour"; §8.3.1.1 then substitutes DC.
    /// </summary>
    internal const sbyte Intra4x4ModeUnavailable = -1;

    /// <summary>
    /// Copies the seven boundary Intra_4×4 modes of a just-coded macroblock out of its neighbour-context
    /// grid into that macroblock's <see cref="_intraModes"/> slot; see that field for the layout.
    /// </summary>
    internal static void StoreIntra4x4ModeBoundary(ReadOnlySpan<sbyte> modeCtx, Span<byte> boundary)
    {
        boundary[0] = (byte)modeCtx[LumaCtxSlot(0, 3)];
        boundary[1] = (byte)modeCtx[LumaCtxSlot(1, 3)];
        boundary[2] = (byte)modeCtx[LumaCtxSlot(2, 3)];
        boundary[3] = (byte)modeCtx[LumaCtxSlot(3, 3)];
        boundary[4] = (byte)modeCtx[LumaCtxSlot(3, 0)];
        boundary[5] = (byte)modeCtx[LumaCtxSlot(3, 1)];
        boundary[6] = (byte)modeCtx[LumaCtxSlot(3, 2)];
    }

    /// <summary>
    /// Seeds the halo of the Intra_4×4 prediction-mode neighbour-context grid (see
    /// <see cref="LumaCtxSlot"/>) with the modes §6.4.11.4 designates as this macroblock's A and B
    /// neighbours: the left macroblock's rightmost 4×4 column and the above macroblock's bottom 4×4 row.
    /// The body of the grid is filled in block by block as the macroblock is coded.
    /// </summary>
    /// <remarks>
    /// §8.3.1.1 derives <c>predIntra4x4PredMode</c> as <c>Min(A, B)</c> and substitutes DC (2) when
    /// either neighbour is unavailable — represented here by <see cref="Intra4x4ModeUnavailable"/>, which
    /// <see cref="H264Intra4X4Prediction.NeighborPredMode"/> maps to DC. With
    /// <c>constrained_intra_pred_flag</c> equal to 0, a neighbouring macroblock that exists but is not
    /// coded in an Intra_4×4 mode is <em>present</em> and contributes DC rather than counting as
    /// unavailable, so inter and Intra_16×16 neighbours seed 2. Getting this distinction wrong makes the
    /// decoder derive a different most-probable mode and therefore decode different prediction modes for
    /// the entire macroblock.
    /// </remarks>
    internal static void FillIntra4x4ModeContext(
        Span<sbyte> modeCtx,
        bool leftMbAvailable,
        bool leftMbIsIntra4x4,
        ReadOnlySpan<byte> leftMbBoundary,
        bool aboveMbAvailable,
        bool aboveMbIsIntra4x4,
        ReadOnlySpan<byte> aboveMbBoundary)
    {
        modeCtx.Fill(Intra4x4ModeUnavailable);

        if (aboveMbAvailable)
        {
            for (var col = 0; col < 4; col++)
            {
                modeCtx[LumaCtxSlot(-1, col)] = aboveMbIsIntra4x4
                    ? (sbyte)aboveMbBoundary[col == 3 ? 3 : 4 + col]
                    : (sbyte)2;
            }
        }

        if (leftMbAvailable)
        {
            for (var row = 0; row < 4; row++)
            {
                modeCtx[LumaCtxSlot(row, -1)] = leftMbIsIntra4x4 ? (sbyte)leftMbBoundary[row] : (sbyte)2;
            }
        }
    }

    /// <summary>
    /// Instance wrapper over <see cref="FillIntra4x4ModeContext(Span{sbyte}, bool, bool, ReadOnlySpan{byte}, bool, bool, ReadOnlySpan{byte})"/>
    /// that resolves neighbour availability and fetches the stored boundary modes for macroblock
    /// <paramref name="mbIndex"/>.
    /// </summary>
    private void FillIntra4x4ModeContext(int mbIndex, Span<sbyte> modeCtx)
    {
        var leftMbAvailable = mbIndex % _mbW > 0;
        var aboveMbAvailable = mbIndex / _mbW > _firstMbRowInSlice;
        var leftIdx = mbIndex - 1;
        var aboveIdx = mbIndex - _mbW;

        FillIntra4x4ModeContext(
            modeCtx,
            leftMbAvailable,
            leftMbAvailable && !_mbIsInter[leftIdx],
            leftMbAvailable
                ? _intraModes.AsSpan(leftIdx * IntraModeBoundaryStride, IntraModeBoundaryStride)
                : default,
            aboveMbAvailable,
            aboveMbAvailable && !_mbIsInter[aboveIdx],
            aboveMbAvailable
                ? _intraModes.AsSpan(aboveIdx * IntraModeBoundaryStride, IntraModeBoundaryStride)
                : default);
    }

    /// <summary>
    /// SIMD MB micro-kernel: gather a 4×4 source block from the strided <paramref name="srcY"/> plane
    /// <summary>
    /// Selects indices of the K candidates with the smallest SAD values from <paramref name="sads"/>,
    /// writing them into <paramref name="topKIdx"/>. Returns the number of indices filled (min of K
    /// and <paramref name="numValid"/>). O(K × numValid) — negligible for K=3, numValid≤9.
    /// </summary>
    private static int SelectTopKSadIndices(ReadOnlySpan<int> sads, int numValid, Span<int> topKIdx)
    {
        var k = Math.Min(topKIdx.Length, numValid);
        Span<bool> used = stackalloc bool[numValid];
        for (var i = 0; i < k; i++)
        {
            var minIdx = -1;
            for (var j = 0; j < numValid; j++)
            {
                if (!used[j] && (minIdx < 0 || sads[j] < sads[minIdx]))
                    minIdx = j;
            }
            topKIdx[i] = minIdx;
            used[minIdx] = true;
        }
        return k;
    }

    /// <summary>
    /// Copy a packed 16×16 inter prediction into <see cref="_recY"/> at MB position
    /// (<paramref name="mbX"/>, <paramref name="mbY"/>). Used by the P_Skip path so the encoder's
    /// reconstruction matches the decoder's (no residual added).
    /// </summary>
    private void CopyInterPredToReconY(ReadOnlySpan<byte> pred16, int mbX, int mbY)
    {
        for (var r = 0; r < 16; r++)
        {
            var dstOff = (mbY + r) * _width + mbX;
            for (var c = 0; c < 16; c++)
            {
                _recY[dstOff + c] = pred16[r * 16 + c];
            }
        }
    }

    /// <summary>
    /// Copy a packed 8×8 inter prediction into <paramref name="recPlane"/> at chroma MB position
    /// (<paramref name="chromaMbX"/>, <paramref name="chromaMbY"/>). Used by the P_Skip path for
    /// each chroma plane.
    /// </summary>
    private void CopyInterPredToReconUv(ReadOnlySpan<byte> pred8, byte[] recPlane, int chromaMbX, int chromaMbY)
    {
        var uvW = _width / 2;
        for (var r = 0; r < 8; r++)
        {
            var dstOff = (chromaMbY + r) * uvW + chromaMbX;
            for (var c = 0; c < 8; c++)
            {
                recPlane[dstOff + c] = pred8[r * 8 + c];
            }
        }
    }
}
