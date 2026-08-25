using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kiln.Internal.H264;
using Kiln.RateControl;

namespace Kiln;

/// <summary>Encoder options (baseline profile, CAVLC).</summary>
public sealed class H264BaselineEncoderOptions
{
    /// <summary>QP [0,51].</summary>
    public int QuantizationParameter { get; set; } = 28;

    /// <summary>
    /// Speed/quality preset over the four measured motion-search knobs:
    /// <see cref="MaxReferenceFrames"/>, <see cref="UseMotionSatd"/>,
    /// <see cref="SubPartitionRangeCap"/> and <see cref="MotionSearchEffortCapPerMb"/>.
    /// Default <see cref="EncoderSpeedMode.HighQuality"/> applies no overrides, so streams from
    /// default options stay byte-identical to earlier releases. See <see cref="EncoderSpeedMode"/>
    /// for what each rung sets and its measured cost.
    /// <para>
    /// Composition rule: an explicit assignment to any of the four knobs always wins over the mode
    /// — the mode only fills in knobs the caller never assigned. Assigning a knob its own default
    /// value still counts as explicit. All other options (<see cref="SliceCount"/>,
    /// <see cref="LightweightDeblocking"/>, QP, …) are never touched by a mode. Every preset keeps
    /// the effort-counted work budgets of <see cref="MotionSearchEffortCapPerMb"/>, so bitstreams
    /// remain deterministic: identical inputs produce identical bytes regardless of wall clock or
    /// thread scheduling. Like every option, the mode is read when the encoder is constructed.
    /// </para>
    /// </summary>
    public EncoderSpeedMode SpeedMode { get; set; } = EncoderSpeedMode.HighQuality;

    /// <summary>IDR NAL every N coded frames (first frame is always IDR).</summary>
    public int KeyframeIntervalFrames { get; set; } = 60;

    public byte ProfileIdc { get; set; } = 66;

    /// <summary>
    /// H.264 <c>level_idc</c> to signal in the SPS (e.g. 31 = Level 3.1), or 0 (default) to pick
    /// automatically: the lowest Annex A Table A-1 level whose frame-size limit (MaxFS) admits the
    /// coded picture, floored at Level 3.1 so small pictures keep signalling the historical Kiln
    /// default. 0 is unambiguous as an "auto" sentinel — Table A-1 has no level 0. Set explicitly
    /// when a decoder contract requires a specific level; the constructor throws (naming the lowest
    /// sufficient level) if the frame size exceeds the explicit level's MaxFS.
    /// Read the signalled value back from <see cref="H264BaselineEncoder.LevelIdc"/>.
    /// </summary>
    public byte LevelIdc { get; set; }

    /// <summary>
    /// Number of reference frames the encoder may use/signal, clamped to [1, <see cref="Internal.H264.H264FrameSharedState.MaxDpbSize"/>].
    /// 1 = single-reference (WebRTC / hardware-decoder safe); 2 = multi-reference. See
    /// <see cref="Internal.H264.H264FrameSharedState.MaxReferenceFrames"/>.
    /// When never assigned, <see cref="SpeedMode"/> presets may lower this (reads return the
    /// preset's value); an explicit assignment always wins over the mode.
    /// </summary>
    public int MaxReferenceFrames
    {
        get => _maxReferenceFramesIsSet ? _maxReferenceFrames : SpeedModePreset(SpeedMode).MaxReferenceFrames;
        set
        {
            _maxReferenceFrames = value;
            _maxReferenceFramesIsSet = true;
        }
    }

    private int _maxReferenceFrames = 2;
    private bool _maxReferenceFramesIsSet;

    /// <summary>
    /// RD weight λ for chroma-DC refinement (<c>J = D + λ·R</c>). If null, derived from <see cref="QuantizationParameter"/>.
    /// </summary>
    public double? ChromaDcRdLambda { get; set; }

    /// <summary>
    /// SAD-domain λ for intra-4×4 mode selection (<c>J = SAD + λ·R</c>). If null, derived from <see cref="QuantizationParameter"/>
    /// via the encoder's per-QP table. Senior tunes this via the lambda sweep harness; ordinary callers pass null.
    /// </summary>
    public int? Intra4x4SadLambda { get; set; }

    /// <summary>When true (default), use hardware SIMD where available for DCT, quant, and intra SAD.</summary>
    public bool PreferHardwareIntrinsics { get; set; } = true;

    /// <summary>
    /// When true, skips chroma-DC rate–distortion refinement for inter-coded chroma. IDR/I paths
    /// keep full fidelity. Default false for parity with golden / existing tests. This flag does
    /// not bound motion-estimation cost; for a deterministic worst-case frame-time bound see
    /// <see cref="MotionSearchEffortCapPerMb"/>.
    /// </summary>
    public bool PreferRealtimeLatencyTuning { get; set; }

    /// <summary>
    /// When true, emits <c>disable_deblocking_filter_idc = 1</c> so the decoder skips in-loop filtering;
    /// the encoder skips deblocking applies as well, keeping encoder reconstruction aligned with the bitstream.
    /// Default off; opt-in trade-off for realtime CPU reduction.
    /// </summary>
    public bool LightweightDeblocking { get; set; }

    /// <summary>
    /// When true (default), P-frame 16×16 inter ME uses a hex/diamond integer search plus qpel refinement;
    /// when false, uses exhaustive integer search within the ME range.
    /// </summary>
    public bool FastSearch { get; set; } = true;

    /// <summary>
    /// When true (default), integer-pel inter ME scores candidates with SATD; fractional refinement still uses SAD.
    /// When never assigned, <see cref="SpeedMode"/> presets may disable this (reads return the
    /// preset's value); an explicit assignment always wins over the mode.
    /// </summary>
    public bool UseMotionSatd
    {
        get => _useMotionSatdIsSet ? _useMotionSatd : SpeedModePreset(SpeedMode).UseMotionSatd;
        set
        {
            _useMotionSatd = value;
            _useMotionSatdIsSet = true;
        }
    }

    private bool _useMotionSatd = true;
    private bool _useMotionSatdIsSet;

    /// <summary>
    /// When true (default), P-slices may emit Intra_16×16 macroblocks: each inter MB is scored
    /// against its best I_16×16 candidate (SATD residual-bit proxy + signalling cost) and the
    /// cheaper wins. This rescues leading-edge scroll, occlusion, and local scene-change MBs whose
    /// content is off-screen in every reference. Set false to force a pure-inter P-slice (used by
    /// inter-path unit tests that assert specific MV-prediction / reference decisions).
    /// <para>
    /// The I16×16 luma-DC reconstruction is decode-exact (<c>H264LumaDcHadamard.ReconstructLumaDcFromQuant</c>
    /// reproduces the spec §8.5.10 inverse-Hadamard + multiply-scale rather than inverting the
    /// encoder's own quant), so I16×16 MBs with non-zero DC residual match the decoder bit-for-bit.
    /// </para>
    /// </summary>
    public bool EnableIntraInPFallback { get; set; } = true;

    /// <summary>
    /// Experimental: when set, uses this SAD threshold (luma+chroma MB) for Phase-1 skip decisions
    /// only when the skip predictor MV is exactly (0,0). Null keeps the default threshold.
    /// Intended for correctness A/B runs; keep null in production until all quality fixtures pass.
    /// </summary>
    public int? ExperimentalZeroMvSkipSadThreshold { get; set; }

    /// <summary>
    /// Trellis quantization level: 0=off (default, byte-identical to previous encoder),
    /// 1=greedy per-coefficient trellis quantization (improves RD at ~5% encode CPU cost).
    /// </summary>
    public int TrellisLevel { get; set; } = 0;

    /// <summary>
    /// Variance-based spatial adaptive quantization strength.
    /// 0.0 = disabled (default, byte-identical to previous encoder).
    /// 1.0 = standard strength (lowers QP for complex macroblocks, raises for flat ones).
    /// Typical range: 0.5–1.5.
    /// </summary>
    public double AdaptiveQuantStrength { get; set; } = 0.0;

    /// <summary>
    /// Target bits per frame for temporal rate control. 0 = constant QP (default).
    /// When set, the encoder's per-slice <c>H264RateControl</c> adjusts per-MB QP to approach this budget.
    /// Multi-slice frames assign each slice a proportional share of the picture budget with a slice-local MB schedule.
    /// </summary>
    public int TargetBitsPerFrame { get; set; } = 0;

    /// <summary>
    /// Number of H.264 slices per frame for parallel encoding.
    /// <list type="bullet">
    ///   <item><c>1</c> (default): single-slice — bitstream byte-identical to the pre-change encoder.</item>
    ///   <item><c>null</c>: auto-derive as min(MBRowCount, max(1, ProcessorCount−1)) capped at 8.</item>
    ///   <item><c>N &gt; 1</c>: N slices; emits multiple slice NALs per access unit with
    ///     <c>disable_deblocking_filter_idc=2</c> (within-slice filtering only).</item>
    /// </list>
    /// </summary>
    public int? SliceCount { get; set; } = 1;

    /// <summary>
    /// Maximum sub-partition integer-pixel search radius around the 16×16 seed MV. Default 16 (full range,
    /// byte-identical to previous encoder). Set to 8 for latency-first streaming at the cost of slightly
    /// worse sub-partition decisions when the optimal MV is far from the 16×16 seed.
    /// After a quarter of a slice's macroblocks have run sub-partition search in the current frame,
    /// the radius drops to 4 for the slice's remaining MBs regardless of this setting — a worst-case
    /// complexity bound that only binds on sustained high-motion content. The budget is proportional
    /// to the slice's MB count, so the per-frame total is independent of <see cref="SliceCount"/>.
    /// When never assigned, <see cref="SpeedMode"/> presets may lower this (reads return the
    /// preset's value); an explicit assignment always wins over the mode.
    /// </summary>
    public int SubPartitionRangeCap
    {
        get => _subPartitionRangeCapIsSet ? _subPartitionRangeCap : SpeedModePreset(SpeedMode).SubPartitionRangeCap;
        set
        {
            _subPartitionRangeCap = value;
            _subPartitionRangeCapIsSet = true;
        }
    }

    private int _subPartitionRangeCap = 16;
    private bool _subPartitionRangeCapIsSet;

    /// <summary>
    /// Deterministic per-frame motion-search complexity ceiling, in candidate-evaluation units per
    /// macroblock (the units of the internal search-effort counter: one 16×16 SATD candidate = 16).
    /// <c>0</c> (default) = unbounded, byte-identical to previous behaviour. When set, every slice
    /// receives an equal share of a frame budget of value × frame MB count (equal — not
    /// proportional to slice size — because the effort-balanced partition gives high-motion bands
    /// fewer rows on purpose); consumption is counted as motion search runs, and
    /// as it crosses 50% / 75% / 100% of the budget the search degrades in steps: first the
    /// exhaustive-window fallback is skipped and the sub-partition radius drops to 8; then the
    /// second reference frame is dropped, the search window shrinks, and the radius drops to 4;
    /// finally sub-partition shapes are skipped entirely (16×16-only ME). The count is algorithmic
    /// work, not wall clock, so identical inputs still produce identical bitstreams. Typical 1080p
    /// content measures ~150 units/MB and is unaffected by ceilings ≥ 512; divergent-motion stress
    /// content measures ~700 unbounded. See the README options table for measured latency/quality
    /// trade-offs.
    /// When never assigned, <see cref="SpeedMode"/> presets set a cap (reads return the preset's
    /// value); an explicit assignment — including an explicit 0 — always wins over the mode.
    /// </summary>
    public int MotionSearchEffortCapPerMb
    {
        get => _motionSearchEffortCapPerMbIsSet ? _motionSearchEffortCapPerMb : SpeedModePreset(SpeedMode).MotionSearchEffortCapPerMb;
        set
        {
            _motionSearchEffortCapPerMb = value;
            _motionSearchEffortCapPerMbIsSet = true;
        }
    }

    private int _motionSearchEffortCapPerMb;
    private bool _motionSearchEffortCapPerMbIsSet;

    /// <summary>
    /// The knob values a <see cref="EncoderSpeedMode"/> preset supplies for any of the four
    /// speed-ladder options the caller never assigned (see <see cref="SpeedMode"/> for the
    /// composition rule). <see cref="EncoderSpeedMode.HighQuality"/> returns exactly the historical
    /// option defaults, keeping default-options streams byte-identical to earlier releases. The
    /// non-default rungs are measured positions on the speed/quality curve — see the
    /// <see cref="EncoderSpeedMode"/> member docs and the README performance section for numbers.
    /// </summary>
    private static (int MaxReferenceFrames, bool UseMotionSatd, int SubPartitionRangeCap, int MotionSearchEffortCapPerMb) SpeedModePreset(EncoderSpeedMode mode) => mode switch
    {
        // Historical defaults: everything on, no caps.
        EncoderSpeedMode.HighQuality => (2, true, 16, 0),
        // Single reference (the bulk of the wall-clock win on coherent content) plus a worst-case
        // effort ceiling. Coherent motion puts only a few percent of MBs into the ceiling's first
        // degradation tiers; sustained high-motion / scene-cut content does bind it, trading PSNR
        // there for the latency bound.
        EncoderSpeedMode.Balanced => (1, true, 16, 512),
        // Adds the sub-partition radius cap and a tighter effort ceiling.
        EncoderSpeedMode.Fast => (1, true, 8, 256),
        // SAD-scored integer ME plus a hard effort ceiling — the visible-quality-cost rung.
        EncoderSpeedMode.VeryFast => (1, false, 8, 128),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown EncoderSpeedMode."),
    };
}

/// <summary>Baseline H.264 encoder (I and P slices, intra macroblocks only in P). Emits Annex B byte stream.</summary>
public sealed class H264BaselineEncoder : IDisposable
{
    /// <summary>Maximum parallel slice encoders allocated up-front (matches the auto-derived cap in <see cref="GetEffectiveSliceCount"/>).</summary>
    private const int MaxSliceEncoders = 8;

    /// <summary>
    /// Shared across all baseline encoders: dedicated thread pool for multi-slice
    /// <see cref="Parallel.For"/>, avoiding shared <see cref="ThreadPool"/> injection delays.
    /// </summary>
    private static readonly TaskScheduler SliceScheduler =
        new LimitedConcurrencyLevelTaskScheduler(Math.Min(MaxSliceEncoders, Math.Max(1, Environment.ProcessorCount - 1)));

    private readonly H264BaselineEncoderOptions _options;
    private readonly H264FrameSharedState _frameShared;
    private readonly int _width;
    private readonly int _height;
    private readonly int _codedWidth;
    private readonly int _codedHeight;
    private readonly int _mbW;
    private readonly int _mbH;
    /// <summary>
    /// Coded-size scratch planes for source extension when display ≠ coded dimensions; null on the
    /// aligned fast path so aligned encodes stay allocation- and byte-identical to earlier releases.
    /// </summary>
    private readonly byte[]? _extendedY;
    private readonly byte[]? _extendedU;
    private readonly byte[]? _extendedV;
    private readonly int _picInitQpMinus26;
    /// <summary>
    /// Pool of slice encoder instances (one per parallel slice). All instances share a single
    /// <see cref="H264FrameSharedState"/> so the reconstruction, reference picture, and per-MB
    /// neighbour caches form one logical frame; per-slice state (RBSP buffer, rate control,
    /// slice-header metadata) is independent so <see cref="Parallel.For"/> can drive them concurrently.
    /// </summary>
    private readonly H264BaselineSliceEncoder[] _sliceEncoders;
    private readonly byte[] _spsRbsp;
    private readonly byte[] _ppsRbsp;
    private readonly byte[] _ebspScratch;
    private readonly int[] _slicePartitionRows;
    private readonly long[] _rowEffortWindow;
    private int _partitionSliceCount;
    private int _partitionWindowFrames;
    private int _codedFrameIndex;
    private int _h264FrameNum;
    private int _idrPicId;
    private bool _disposed;

    public H264BaselineEncoder(
        int width,
        int height,
        H264BaselineEncoderOptions? options = null)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
        {
            // 4:2:0 crop offsets move in CropUnitX = CropUnitY = 2 luma-sample units (§7.4.2.1.1,
            // Table 6-1), and the planar I420 contract halves both axes for chroma — odd display
            // extents are unrepresentable. Reject rather than round: silently rounding would make
            // Width disagree with what the caller passed and what the decoder outputs.
            throw new ArgumentException("Width and height must be even (4:2:0 chroma is subsampled 2×2).");
        }

        _options = options ?? new H264BaselineEncoderOptions();
        var qp = Math.Clamp(_options.QuantizationParameter, 0, 51);
        _width = width;
        _height = height;
        // Coded picture covers the full macroblock grid (§7.4.2.1.1): round display dimensions up
        // to multiples of 16; the SPS frame-cropping block signals the display size back down.
        _codedWidth = (width + 15) & ~15;
        _codedHeight = (height + 15) & ~15;
        _mbW = _codedWidth / 16;
        _mbH = _codedHeight / 16;
        if (_codedWidth != width || _codedHeight != height)
        {
            _extendedY = new byte[_codedWidth * _codedHeight];
            _extendedU = new byte[_codedWidth / 2 * (_codedHeight / 2)];
            _extendedV = new byte[_codedWidth / 2 * (_codedHeight / 2)];
        }

        _picInitQpMinus26 = qp - 26;
        var chromaRd = _options.ChromaDcRdLambda ?? H264ChromaDcScale.DefaultChromaDcRdLambdaFromLumaQp(qp);

        // One shared frame state owns the picture-sized buffers; each slice encoder sees the same
        // reconstruction & reference arrays, so disjoint per-slice writes assemble into one frame.
        // Everything below the public API operates on coded (macroblock-aligned) dimensions.
        var sharedState = new H264FrameSharedState(
            _codedWidth, _codedHeight,
            maxReferenceFrames: Math.Clamp(_options.MaxReferenceFrames, 1, H264FrameSharedState.MaxDpbSize),
            useMotionSatd: _options.UseMotionSatd);
        _frameShared = sharedState;
        var kernels = _options.PreferHardwareIntrinsics ? H264KernelSet.CreateBest() : new ScalarKernelSet();
        var encoderCount = Math.Max(1, Math.Min(MaxSliceEncoders, _mbH));
        // Equal per-slice share of the frame ME effort budget (see
        // H264BaselineEncoderOptions.MotionSearchEffortCapPerMb). Equal — not proportional to a
        // slice's MB count — because the effort-balanced partition gives high-motion bands fewer
        // rows; the balancer aims at equal per-slice effort, so the budget shares match that aim.
        var budgetSliceCount = Math.Min(Math.Max(1, GetEffectiveSliceCount()), encoderCount);
        var effortSliceBudget = _options.MotionSearchEffortCapPerMb > 0
            ? Math.Max(1L, (long)_options.MotionSearchEffortCapPerMb * (_mbW * _mbH) / budgetSliceCount)
            : 0L;
        _sliceEncoders = new H264BaselineSliceEncoder[encoderCount];
        _slicePartitionRows = new int[encoderCount + 1];
        _rowEffortWindow = new long[_mbH];
        for (var i = 0; i < encoderCount; i++)
        {
            _sliceEncoders[i] = new H264BaselineSliceEncoder(
                _codedWidth,
                _codedHeight,
                qp,
                chromaRd,
                _options.Intra4x4SadLambda,
                _options.PreferRealtimeLatencyTuning,
                _options.LightweightDeblocking,
                _options.FastSearch,
                Math.Max(0, _options.TrellisLevel),
                sharedState,
                _options.AdaptiveQuantStrength,
                _options.TargetBitsPerFrame,
                kernels,
                _options.UseMotionSatd,
                _options.EnableIntraInPFallback,
                _options.ExperimentalZeroMvSkipSadThreshold,
                _options.SubPartitionRangeCap,
                effortSliceBudget);
        }
        // LevelIdc = 0 means auto: lowest Table A-1 level whose MaxFS admits the coded picture,
        // floored at Level 3.1 (see H264BaselineEncoderOptions.LevelIdc). An explicit level is
        // validated against MaxFS inside WriteSpsRbsp and throws naming the lowest sufficient level.
        LevelIdc = _options.LevelIdc != 0
            ? _options.LevelIdc
            : H264LevelLimits.AutoLevelForFrameSize(_mbW, _mbH);
        _spsRbsp = H264ParameterSets.WriteSpsRbsp(
            _codedWidth, _codedHeight, _options.ProfileIdc, LevelIdc,
            Math.Clamp(_options.MaxReferenceFrames, 1, H264FrameSharedState.MaxDpbSize),
            displayWidth: width, displayHeight: height);
        _ppsRbsp = H264ParameterSets.WritePpsRbsp(_picInitQpMinus26);
        _ebspScratch = new byte[H264RbspEmulation.GetEmulationPreventionBufferSize(checked(_codedWidth * _codedHeight * 2 + 65_536))];
        // Same worst-case bound the encoder assumes internally (2 bytes per coded pixel of RBSP,
        // plus emulation-prevention headroom), plus start-code + NAL-header framing for SPS, PPS
        // and up to MaxSliceEncoders slice NALs.
        RecommendedOutputBufferSize = _ebspScratch.Length + (2 + MaxSliceEncoders) * 5;
    }

    /// <summary>Display width as passed to the constructor — what the decoder outputs after SPS cropping.</summary>
    public int Width => _width;

    /// <summary>Display height as passed to the constructor — what the decoder outputs after SPS cropping.</summary>
    public int Height => _height;

    /// <summary>
    /// The <c>level_idc</c> signalled in the SPS: <see cref="H264BaselineEncoderOptions.LevelIdc"/>
    /// when non-zero, otherwise the automatically selected level (the lowest Annex A Table A-1
    /// level whose MaxFS admits the coded picture, floored at Level 3.1 — see
    /// <see cref="H264BaselineEncoderOptions.LevelIdc"/>).
    /// </summary>
    public byte LevelIdc { get; }

    /// <summary>
    /// Recommended minimum length for the <c>annexB</c> destination span of <see cref="EncodeFrame"/>:
    /// the same worst-case per-frame bound the encoder sizes its internal emulation-prevention
    /// scratch from (2 bytes per coded pixel plus emulation-prevention and NAL-framing headroom).
    /// Real CAVLC frames stay far below it — uncompressed I420 is 1.5 bytes per pixel — so a span
    /// of this size does not run out; a smaller span works whenever every frame it receives fits,
    /// and <see cref="EncodeFrame"/> throws an <see cref="ArgumentException"/> naming this property
    /// when one does not.
    /// </summary>
    public int RecommendedOutputBufferSize { get; }

    /// <summary>
    /// Coded picture width: <see cref="Width"/> rounded up to a multiple of 16 (the macroblock grid).
    /// Equals <see cref="Width"/> when it is already aligned; the difference is signalled as
    /// <c>frame_crop_right_offset</c> in the SPS (§7.3.2.1.1).
    /// </summary>
    public int CodedWidth => _codedWidth;

    /// <summary>
    /// Coded picture height: <see cref="Height"/> rounded up to a multiple of 16. Equals
    /// <see cref="Height"/> when already aligned; the difference is signalled as
    /// <c>frame_crop_bottom_offset</c> in the SPS (§7.3.2.1.1).
    /// </summary>
    public int CodedHeight => _codedHeight;

    /// <summary>
    /// Encoder reconstruction (Y) after the last <see cref="EncodeFrame"/> — the <em>uncropped coded</em>
    /// plane: row-major, <see cref="CodedWidth"/> stride, <see cref="CodedHeight"/> rows. This is what
    /// must match a decoder's DPB. For a display-sized (cropped) copy use <see cref="CopyLastReconstructedTo"/>.
    /// </summary>
    public ReadOnlySpan<byte> LastReconstructedY => _sliceEncoders[0].ReconstructedYPlane;

    /// <summary>
    /// Encoder reconstruction (U) after the last <see cref="EncodeFrame"/> — the uncropped coded plane
    /// at half resolution: row-major, <see cref="CodedWidth"/>/2 stride, <see cref="CodedHeight"/>/2 rows.
    /// </summary>
    public ReadOnlySpan<byte> LastReconstructedU => _sliceEncoders[0].ReconstructedUPlane;

    /// <summary>
    /// Encoder reconstruction (V) after the last <see cref="EncodeFrame"/> — the uncropped coded plane
    /// at half resolution: row-major, <see cref="CodedWidth"/>/2 stride, <see cref="CodedHeight"/>/2 rows.
    /// </summary>
    public ReadOnlySpan<byte> LastReconstructedV => _sliceEncoders[0].ReconstructedVPlane;

    /// <summary>
    /// Copy the last reconstruction cropped to display size (<see cref="Width"/> × <see cref="Height"/>)
    /// into caller planes — the escape hatch when the uncropped coded planes of
    /// <see cref="LastReconstructedY"/>/<see cref="LastReconstructedU"/>/<see cref="LastReconstructedV"/>
    /// are inconvenient. Layout mirrors <see cref="EncodeFrame"/>: planar I420, chroma at half dimensions.
    /// </summary>
    /// <param name="y">Destination luma plane; must hold <paramref name="strideY"/> × <see cref="Height"/> bytes.</param>
    /// <param name="u">Destination U plane; must hold <paramref name="strideUv"/> × <see cref="Height"/>/2 bytes.</param>
    /// <param name="v">Destination V plane; must hold <paramref name="strideUv"/> × <see cref="Height"/>/2 bytes.</param>
    /// <param name="strideY">Destination luma stride (≥ <see cref="Width"/>).</param>
    /// <param name="strideUv">Destination chroma stride (≥ <see cref="Width"/>/2).</param>
    public void CopyLastReconstructedTo(Span<byte> y, Span<byte> u, Span<byte> v, int strideY, int strideUv)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(strideY, _width);
        ArgumentOutOfRangeException.ThrowIfLessThan(strideUv, _width / 2);

        CopyCropped(LastReconstructedY, _codedWidth, y, strideY, _width, _height);
        CopyCropped(LastReconstructedU, _codedWidth / 2, u, strideUv, _width / 2, _height / 2);
        CopyCropped(LastReconstructedV, _codedWidth / 2, v, strideUv, _width / 2, _height / 2);

        static void CopyCropped(
            ReadOnlySpan<byte> src, int srcStride, Span<byte> dst, int dstStride, int width, int height)
        {
            for (var row = 0; row < height; row++)
            {
                src.Slice(row * srcStride, width).CopyTo(dst.Slice(row * dstStride, width));
            }
        }
    }

    /// <summary>Test support: per-MB luma QP after the most recent <see cref="EncodeFrame"/>.</summary>
    internal ReadOnlySpan<int> TestHookLastEncodedQpY => _frameShared.QpY;

    /// <summary>True if the most recent <see cref="EncodeFrame"/> emitted an IDR access unit.</summary>
    public bool LastFrameWasIdr { get; private set; }

    /// <summary>
    /// Encode one frame into Annex B. Returns number of bytes written.
    /// Planar I420 at <em>display</em> size (<see cref="Width"/> × <see cref="Height"/>):
    /// <paramref name="u"/> / <paramref name="v"/> are half dimensions; strides may exceed width/2.
    /// When display ≠ coded dimensions the encoder extends the planes to the macroblock grid
    /// internally (edge replication); callers never supply padded planes.
    /// </summary>
    /// <param name="annexB">
    /// Destination for the complete Annex B access unit. Allocate at least
    /// <see cref="RecommendedOutputBufferSize"/> bytes to be safe for any frame; a smaller span
    /// throws <see cref="ArgumentException"/> mid-frame if a NAL unit does not fit.
    /// </param>
    /// <param name="sliceLumaQp">
    /// When set, coded slice luma QP for this picture (via slice <c>slice_qp_delta</c>). When null,
    /// uses constructor <see cref="H264BaselineEncoderOptions.QuantizationParameter"/>.
    /// </param>
    public int EncodeFrame(
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> u,
        ReadOnlySpan<byte> v,
        int strideY,
        int strideUv,
        Span<byte> annexB,
        bool forceKeyframe = false,
        int? sliceLumaQp = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Unaligned display size: extend the caller's planes to the coded (macroblock-aligned) size
        // by edge replication before any slice sees them. Skipped entirely on the aligned fast path
        // so existing streams stay byte-identical and no per-frame copy is introduced.
        if (_extendedY is not null)
        {
            H264SourcePlaneExtender.Extend(
                y, strideY, _width, _height,
                _extendedY, _codedWidth, _codedWidth, _codedHeight);
            H264SourcePlaneExtender.Extend(
                u, strideUv, _width / 2, _height / 2,
                _extendedU!, _codedWidth / 2, _codedWidth / 2, _codedHeight / 2);
            H264SourcePlaneExtender.Extend(
                v, strideUv, _width / 2, _height / 2,
                _extendedV!, _codedWidth / 2, _codedWidth / 2, _codedHeight / 2);
            y = _extendedY;
            u = _extendedU;
            v = _extendedV;
            strideY = _codedWidth;
            strideUv = _codedWidth / 2;
        }

        var interval = Math.Max(1, _options.KeyframeIntervalFrames);
        var isIdr = _codedFrameIndex == 0 || forceKeyframe || (_codedFrameIndex % interval == 0);
        if (isIdr)
        {
            _h264FrameNum = 0;
        }

        var isP = !isIdr;

        if (isP && _frameShared.PaddedRefValid)
            Array.Copy(_frameShared.MbMvs, _frameShared.PrevMbMvs, _frameShared.MbMvs.Length);

        var pos = 0;
        // Emit SPS/PPS only ahead of IDR access units. Repeating parameter sets before every P-frame is
        // non-conventional and some hardware decoders (VideoToolbox) mishandle the repeated in-band sets.
        // A 1s GOP already carries SPS/PPS at each IDR for mid-stream join / loss recovery.
        if (isIdr)
        {
            pos += WriteNal(annexB[pos..], 3, 7, _spsRbsp);
            pos += WriteNal(annexB[pos..], 3, 8, _ppsRbsp);
        }

        var sliceArg = sliceLumaQp.HasValue ? sliceLumaQp.Value : -1;
        var nalType = (byte)(isIdr ? 5 : 1);
        var sliceCount = GetEffectiveSliceCount();
        if (sliceCount > 1)
        {
            pos = EncodeFrameMultiSlice(y, strideY, u, v, strideUv, isIdr, isP, sliceArg, sliceCount, nalType, annexB, pos);
        }
        else
        {
            // Single-slice path. Must be byte-identical to the pre-multi-slice encoder, so we
            // keep the legacy code path: a single H264BaselineSliceEncoder runs ResetForFrame
            // (via isFirstSliceInFrame=true) and the slice-end deblock+pad inside EncodeSliceRbsp.
            var rbsp = _sliceEncoders[0].EncodeSliceRbsp(
                y, strideY, u, v, strideUv, isIdr, isP, _h264FrameNum, _idrPicId, sliceArg,
                codedFrameIndex: _codedFrameIndex);
            pos += WriteNal(annexB[pos..], 3, nalType, rbsp);
        }

        if (isIdr)
        {
            _idrPicId = (_idrPicId + 1) & 0xFFFF;
        }

        _codedFrameIndex++;
        _h264FrameNum = (_h264FrameNum + 1) & 0xF;
        LastFrameWasIdr = isIdr;
        return pos;
    }

    private int GetEffectiveSliceCount()
    {
        var requested = _options.SliceCount;
        if (requested.HasValue)
            return Math.Max(1, requested.Value);
        // Auto: min(MBRowCount, max(1, ProcessorCount-1)) capped at 8.
        return Math.Min(_mbH, Math.Min(8, Math.Max(1, Environment.ProcessorCount - 1)));
    }

    /// <summary>
    /// Encode a frame as N slices in parallel. Slice-aware neighbour guards in
    /// <see cref="H264BaselineSliceEncoder"/> (Phase 1) make each slice's MB writes disjoint, so
    /// <see cref="Parallel.For"/> can fan slices across cores. The orchestrator drives the
    /// once-per-frame reset via <see cref="H264BaselineSliceEncoder.BeginFrame"/> and the once-per-frame
    /// reference-padding via <see cref="H264BaselineSliceEncoder.PadReconstructedReference"/>; both
    /// happen outside the parallel section so the shared state is mutated under single-threaded fences.
    /// </summary>
    private unsafe int EncodeFrameMultiSlice(
        ReadOnlySpan<byte> y, int strideY,
        ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, int strideUv,
        bool isIdr, bool isP, int sliceArg,
        int sliceCount, byte nalType,
        Span<byte> annexB, int pos)
    {
        sliceCount = Math.Min(sliceCount, _sliceEncoders.Length);

        var collectFramePhases = H264PInterDiagnostics.CollectFramePhases;
        var tFrameStart = collectFramePhases ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        // Cost-balanced slice partition: the frame wall clock is the *slowest* slice, and with
        // equal-height slices the band containing the moving content runs 2x+ longer than the rest
        // (measured 6.7/15.0/15.7/7.2 ms at 1080p s=4 — see notes/05-phase-attribution.txt). Weight
        // each MB row by the previous frame's skip map (read before BeginFrame clears it) so row
        // bands equalise predicted work instead of height. Deterministic for a given input sequence;
        // slice partitioning is an encoder-side choice with no normative constraint on where
        // first_mb_in_slice boundaries fall (§7.4.3 only requires slices to tile the picture).
        ComputeSlicePartition(sliceCount);
        var partitionRows = _slicePartitionRows;

        // Single-threaded fence: clear shared per-MB caches and (if IDR) invalidate the reference.
        // Done once before the parallel loop so disjoint per-slice writes don't race with the reset.
        _sliceEncoders[0].BeginFrame(isIdr);

        var tAfterBegin = collectFramePhases ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        // Pin the caller's source planes so the parallel workers can reconstitute ReadOnlySpans
        // from raw pointers. The pin scope encloses Parallel.For (which is synchronous), so the
        // GC can't relocate the spans' backing memory while any worker is reading. Avoids the
        // ~1.4 MB/frame copy that ToArray() would introduce at 720p.
        var sliceEncoders = _sliceEncoders;
        var mbW = _mbW;
        var h264FrameNum = _h264FrameNum;
        var idrPicId = _idrPicId;
        var yLen = y.Length;
        var uLen = u.Length;
        var vLen = v.Length;
        fixed (byte* yPin = y)
        fixed (byte* uPin = u)
        fixed (byte* vPin = v)
        {
            var yAddr = (nint)yPin;
            var uAddr = (nint)uPin;
            var vAddr = (nint)vPin;
            Parallel.For(
                0,
                sliceCount,
                new ParallelOptions { TaskScheduler = SliceScheduler, MaxDegreeOfParallelism = sliceCount },
                k =>
            {
                var firstMbRow = partitionRows[k];
                var rowCount = partitionRows[k + 1] - firstMbRow;
                var firstMbInSlice = firstMbRow * mbW;
                var mbCountInSlice = rowCount * mbW;
                ReadOnlySpan<byte> ySpan;
                ReadOnlySpan<byte> uSpan;
                ReadOnlySpan<byte> vSpan;
                unsafe
                {
                    ySpan = new ReadOnlySpan<byte>((byte*)yAddr, yLen);
                    uSpan = new ReadOnlySpan<byte>((byte*)uAddr, uLen);
                    vSpan = new ReadOnlySpan<byte>((byte*)vAddr, vLen);
                }

                sliceEncoders[k].EncodeSliceRbsp(
                    ySpan, strideY,
                    uSpan, vSpan, strideUv,
                    isIdr, isP, h264FrameNum, idrPicId, sliceArg,
                    firstMbInSlice, mbCountInSlice,
                    filterAcrossSlicesDisabled: true,
                    // Orchestrator already drove BeginFrame; tell the slice encoder to skip its
                    // own ResetForFrame and the IDR ref-validity clear (both would race here).
                    isFirstSliceInFrame: false,
                    codedFrameIndex: _codedFrameIndex);
            });
        }

        var tAfterParallel = collectFramePhases ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        // Gather NALs in raster (slice-index) order. WriteNal must run sequentially because each
        // call advances `pos` and writes into the shared output buffer.
        for (var k = 0; k < sliceCount; k++)
        {
            pos += WriteNal(annexB[pos..], 3, nalType, sliceEncoders[k].LastSliceRbsp);
        }

        var tAfterGather = collectFramePhases ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        _sliceEncoders[0].PadReconstructedReference();

        if (collectFramePhases)
        {
            var tEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            long sliceSum = 0;
            long sliceMax = 0;
            for (var k = 0; k < sliceCount; k++)
            {
                var t = sliceEncoders[k].LastSliceElapsedTicks;
                sliceSum += t;
                sliceMax = Math.Max(sliceMax, t);
                H264PInterDiagnostics.NotifySliceIndexTicks(k, t);
            }

            H264PInterDiagnostics.NotifyFramePhases(
                beginFrameTicks: tAfterBegin - tFrameStart,
                parallelWallTicks: tAfterParallel - tAfterBegin,
                nalGatherTicks: tAfterGather - tAfterParallel,
                padRotateTicks: tEnd - tAfterGather,
                sliceSumTicks: sliceSum,
                sliceMaxTicks: sliceMax);
        }

        return pos;
    }

    /// <summary>
    /// Baseline cost per macroblock, in the candidate-evaluation units of
    /// <see cref="H264MotionEstimator.ThreadSearchEffort"/>, covering the work motion search does
    /// not account for (P_Skip validation, reconstruction, CAVLC, deblocking). Calibrated against
    /// per-row wall times at 1080p (notes/05-phase-attribution.txt).
    /// </summary>
    private const int RowBaseEffortPerMb = 8;

    /// <summary>
    /// Predicted extra effort per MB for a freshly placed slice-top row: the boundary breaks the
    /// P_Skip prediction chain, so the row runs near-full motion search (~100 of 120 MBs at 1080p,
    /// measured ~70 effort units per MB on the probe content). Charged to every slice but the first
    /// when evaluating a candidate partition, so the optimiser prices the boundary it creates.
    /// </summary>
    private const int TopRowEffortPerMb = 64;

    /// <summary>Frames of per-row effort averaged per partition decision (one probe content cycle).</summary>
    private const int PartitionDecisionWindowFrames = 8;

    /// <summary>
    /// Update <see cref="_slicePartitionRows"/> (slice k covers MB rows [rows[k], rows[k+1])) so
    /// slice costs equalise. The frame wall clock is the *slowest* slice, and with fixed
    /// equal-height slices the band containing the moving content runs 2x+ longer than the rest
    /// (measured 6.7/15.0/15.7/7.2 ms at 1080p s=4 — notes/05-phase-attribution.txt).
    ///
    /// The cost signal is the previous frames' per-row motion-search effort
    /// (<see cref="H264FrameSharedState.RowMeEffort"/>) — a deterministic count of candidate
    /// evaluations, not a wall-clock measure, so identical inputs produce identical bitstreams.
    /// Outcome-based signals (skip maps, MB counts) were tried first and mispredict by an order of
    /// magnitude: rows with identical skip/inter mixes differ 4x in measured time because
    /// deep-but-unsuccessful searches leave no trace in the outcome.
    ///
    /// Repartitioning is deliberately infrequent (every <see cref="PartitionDecisionWindowFrames"/>
    /// frames, from window-averaged effort): every boundary move breaks the P_Skip chain along a
    /// fresh row the following frame, so per-frame moves were measured to cost more than the
    /// balance they bought on cheap configurations. Each decision solves a greedy prefix
    /// partition in which (a) rows that are currently slice tops have their observed effort
    /// replaced by the neighbouring rows' average — that effort is the boundary's own artifact and
    /// would otherwise make the partition chase itself — and (b) every slice after the first is
    /// charged <see cref="TopRowEffortPerMb"/> per MB for the top row its boundary will disturb.
    /// Slice partitioning is an encoder-side choice; §7.4.3 only requires slices to tile the
    /// picture in raster order.
    /// </summary>
    private void ComputeSlicePartition(int sliceCount)
    {
        var rows = _slicePartitionRows;
        // The effort units are calibrated against the SATD search path; in SAD mode
        // (UseMotionSatd=false) they over-weight high-motion rows ~4x (measured on the probe
        // content) and the resulting partition is a small net loss, so SAD-mode encodes keep the
        // historical equal-height split.
        var balanceDisabled = !_options.UseMotionSatd || H264PInterDiagnostics.DisableSlicePartitionBalance;
        if (_partitionSliceCount != sliceCount || balanceDisabled)
        {
            // First frame at this slice count: equal-height start, matching the historical layout.
            var mbRowsPerSlice = _mbH / sliceCount;
            var remainder = _mbH - mbRowsPerSlice * sliceCount;
            rows[0] = 0;
            for (var k = 1; k <= sliceCount; k++)
                rows[k] = rows[k - 1] + mbRowsPerSlice + (k == sliceCount ? remainder : 0);
            _partitionSliceCount = balanceDisabled ? 0 : sliceCount;
            Array.Clear(_rowEffortWindow);
            _partitionWindowFrames = 0;
            return;
        }

        var rowEffort = _frameShared.RowMeEffort;
        for (var r = 0; r < _mbH; r++)
            _rowEffortWindow[r] += rowEffort[r];
        if (++_partitionWindowFrames < PartitionDecisionWindowFrames)
            return;

        // Per-row cost over the window, with current slice-top rows replaced by their neighbours'
        // average so the boundary's own skip-chain damage does not steer the next partition.
        Span<long> rowCost = stackalloc long[_mbH];
        for (var r = 0; r < _mbH; r++)
            rowCost[r] = _rowEffortWindow[r] / _partitionWindowFrames + (long)RowBaseEffortPerMb * _mbW;
        for (var k = 1; k < sliceCount; k++)
        {
            var top = rows[k];
            var lo = top > 0 ? rowCost[top - 1] : rowCost[top + 1];
            var hi = top + 1 < _mbH ? rowCost[top + 1] : rowCost[top - 1];
            rowCost[top] = (lo + hi) / 2;
        }

        long remainingCost = (long)(sliceCount - 1) * TopRowEffortPerMb * _mbW;
        for (var r = 0; r < _mbH; r++)
            remainingCost += rowCost[r];

        // Greedy sequential fill: give each slice its share of the remaining cost, keeping at
        // least one row per slice (and enough rows for every slice still to come).
        var row = 0;
        for (var k = 0; k < sliceCount - 1; k++)
        {
            var budget = remainingCost / (sliceCount - k);
            long acc = k > 0 ? (long)TopRowEffortPerMb * _mbW : 0;
            var maxRow = _mbH - (sliceCount - 1 - k);
            var first = row;
            while (row < maxRow && (row == first || acc + rowCost[row] / 2 < budget))
            {
                acc += rowCost[row];
                row++;
            }

            rows[k + 1] = row;
            remainingCost -= acc;
        }

        Array.Clear(_rowEffortWindow);
        _partitionWindowFrames = 0;
    }

    private int WriteNal(Span<byte> dest, byte nri, byte nalType, ReadOnlySpan<byte> rbsp) =>
        H264AnnexB.AppendNal(dest, nri, nalType, rbsp, _ebspScratch);

    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>
    /// Task scheduler with at most <see cref="MaximumConcurrencyLevel"/> dedicated
    /// <see cref="Thread"/> workers (not the shared thread pool). Workers are started on the first
    /// queued task and reused for the lifetime of the process.
    /// </summary>
    private sealed class LimitedConcurrencyLevelTaskScheduler : TaskScheduler
    {
        private readonly LinkedList<Task> _tasks = new();
        private readonly int _maxDegreeOfParallelism;
        private readonly List<Thread> _threads;
        private int _workersStarted;

        public LimitedConcurrencyLevelTaskScheduler(int maxDegreeOfParallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);
            _maxDegreeOfParallelism = maxDegreeOfParallelism;
            _threads = new List<Thread>(maxDegreeOfParallelism);
        }

        public sealed override int MaximumConcurrencyLevel => _maxDegreeOfParallelism;

        protected sealed override IEnumerable<Task> GetScheduledTasks()
        {
            lock (_tasks)
            {
                return _tasks.ToArray();
            }
        }

        protected sealed override void QueueTask(Task task)
        {
            EnsureWorkersStarted();
            lock (_tasks)
            {
                _tasks.AddLast(task);
                Monitor.PulseAll(_tasks);
            }
        }

        private void EnsureWorkersStarted()
        {
            if (_workersStarted != 0)
            {
                return;
            }

            lock (_threads)
            {
                if (_workersStarted != 0)
                {
                    return;
                }

                using var allWorkersEnteredWaitLoop = new CountdownEvent(_maxDegreeOfParallelism);
                for (var i = 0; i < _maxDegreeOfParallelism; i++)
                {
                    var countdown = allWorkersEnteredWaitLoop;
                    var thread = new Thread(() => WorkerLoop(countdown))
                    {
                        IsBackground = true,
                        Name = "Kiln.H264SlicePool." + i,
                    };
                    _threads.Add(thread);
                    thread.Start();
                }

                allWorkersEnteredWaitLoop.Wait();
                _workersStarted = 1;
            }
        }

        private void WorkerLoop(CountdownEvent ready)
        {
            var startupRegistered = false;
            while (true)
            {
                Task taskToRun;
                lock (_tasks)
                {
                    while (_tasks.First is null)
                    {
                        if (!startupRegistered)
                        {
                            startupRegistered = true;
                            ready.Signal();
                        }

                        Monitor.Wait(_tasks);
                    }

                    taskToRun = _tasks.First!.Value;
                    _tasks.RemoveFirst();
                }

                TryExecuteTask(taskToRun);
            }
        }

        protected sealed override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }
}
