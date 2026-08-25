using Microsoft.Extensions.Logging;
using Kiln.Internal.H264;
using Kiln.Internal.H264.Adaptation;
using Kiln.RateControl;
using Kiln.Recovery;

namespace Kiln;

/// <summary>
/// Per-frame outcome of <see cref="H264StreamingSession.EncodeFrame"/>: what was encoded, what the
/// controller decided, and which parts of the decision the session could not apply by itself.
/// </summary>
public readonly record struct H264StreamingEncodeResult(
    /// <summary>Annex B bytes written for this access unit.</summary>
    int BytesWritten,

    /// <summary>True when this frame was coded as an IDR (scheduled, forced, or recovery-driven).</summary>
    bool WasIdr,

    /// <summary>Slice luma QP the session applied (the controller's <c>BaseQp</c>, clamped to [0, 51]).</summary>
    int AppliedSliceQp,

    /// <summary>Picture bit budget the session applied (<c>TargetBitrateBps / TargetFps</c>).</summary>
    int AppliedTargetBitsPerFrame,

    /// <summary>Speed mode in effect for this frame (applied before encoding).</summary>
    EncoderSpeedMode AppliedSpeedMode,

    /// <summary>
    /// True when the controller recommends a different output resolution than the session is
    /// currently encoding (<see cref="Decision"/>.<c>Width</c>/<c>Height</c>). The session cannot
    /// rescale frames; the caller acts on it by supplying rescaled frames after calling
    /// <see cref="H264StreamingSession.ChangeResolution"/>. Until then the recommendation repeats.
    /// </summary>
    bool ResolutionChangeRecommended,

    /// <summary>
    /// True when the recovery policy asked for gradual intra refresh instead of an IDR (typically a
    /// PLI/FIR during IDR cooldown). Kiln does not implement intra refresh — the only
    /// <see cref="IIntraRefreshPolicy"/> implementation is an explicit no-op stub — so the session
    /// encodes a normal frame and surfaces the request here instead of pretending. Callers with no
    /// refresh mechanism of their own should treat it as "recovery is pending until the cooldown
    /// permits the next IDR".
    /// </summary>
    bool IntraRefreshRequested,

    /// <summary>The full controller decision this frame was encoded under.</summary>
    EncoderAdaptationDecision Decision);

/// <summary>
/// The feedback loop between <see cref="Kiln.RateControl"/> and <see cref="H264BaselineEncoder"/>:
/// owns one encoder and one adaptive rate controller, and per frame turns
/// <see cref="EncoderNetworkFeedback"/> into applied encoder settings — so a streaming server feeds
/// network feedback in and gets adaptive encoding out without writing the glue itself.
/// <para>
/// <b>What adapts when.</b> Every adaptation input falls into one of three tiers:
/// <list type="number">
/// <item><b>Free per frame</b> — slice QP, the picture bit budget, forced IDRs, and the search-only
/// speed knobs (<c>UseMotionSatd</c>, <c>SubPartitionRangeCap</c>, <c>MotionSearchEffortCapPerMb</c>).
/// The session applies these every frame from the controller's decision.</item>
/// <item><b>Bounded by the SPS</b> — the reference-frame count. The SPS signals
/// <c>max_num_ref_frames</c> once; below that ceiling the count is a per-frame search decision
/// (lowering is immediate, raising takes effect after one frame of DPB refill — no IDR needed).
/// The session reserves the full DPB in the SPS unless the caller explicitly set
/// <see cref="H264BaselineEncoderOptions.MaxReferenceFrames"/>, in which case that explicit value
/// caps every speed mode for the whole session (the WebRTC / hardware-decoder-safety contract wins
/// over adaptation).</item>
/// <item><b>Requires a new encoder</b> — resolution. A resolution change means a new SPS and a new
/// DPB; the session handles it in <see cref="ChangeResolution"/> by transparently recreating the
/// encoder (next frame is an IDR carrying the new SPS). It cannot be automatic because Kiln has no
/// scaler: the caller must supply frames at the new size, so the controller's resolution decisions
/// surface as recommendations (<see cref="H264StreamingEncodeResult.ResolutionChangeRecommended"/>)
/// until the caller acts.</item>
/// </list>
/// Frame rate never touches the bitstream (the SPS carries no VUI timing): the controller's
/// <c>TargetFps</c> is a pacing contract for the caller, and the session budgets per-frame bits at
/// it — a caller who will not adapt fps should configure a single-rung
/// <see cref="RateControlConfig.SupportedFps"/> so it never moves.
/// </para>
/// <para>
/// <b>Determinism.</b> Identical frames plus identical feedback (including any
/// <see cref="EncoderPipelineTimings"/> passed) produce an identical bitstream: the session adds no
/// wall-clock or scheduling inputs of its own, and all encoder-side adaptation is applied between
/// frames. Same threading contract as <see cref="H264BaselineEncoder"/>: one frame at a time,
/// external synchronisation.
/// </para>
/// <para>
/// <b>Recovery ownership.</b> The session's controller owns the single
/// <see cref="H264RecoveryPolicy"/> instance and invokes it exactly once per frame;
/// <see cref="RecoveryPolicy"/> is exposed for metrics/reset only — never call
/// <see cref="H264RecoveryPolicy.DecideRecovery"/> on it yourself.
/// </para>
/// </summary>
public sealed class H264StreamingSession : IDisposable
{
    private readonly H264BaselineEncoderOptions _options;
    private readonly RateControlConfig _config;
    private readonly H264AdaptiveRateController _controller;
    private readonly bool _refsExplicit;
    private readonly int _explicitRefs;
    private readonly bool _satdExplicit;
    private readonly bool _rangeCapExplicit;
    private readonly bool _effortCapExplicit;

    private H264BaselineEncoder _encoder;
    private EncoderSpeedMode _appliedSpeedMode;
    private int _targetFps;
    private int _lastFrameBytes;
    private int _lastFrameQp;
    private bool _lastFrameWasIdr;
    private bool _disposed;

    /// <summary>
    /// Create a session encoding <paramref name="width"/>×<paramref name="height"/> under the given
    /// encoder options and rate-control configuration.
    /// </summary>
    /// <param name="width">Initial display width (even, ≥ 2).</param>
    /// <param name="height">Initial display height (even, ≥ 2).</param>
    /// <param name="encoderOptions">
    /// Encoder options; the session keeps a private copy, so later mutation of the caller's instance
    /// has no effect. <see cref="H264BaselineEncoderOptions.SpeedMode"/> is the starting mode; the
    /// controller adapts it from there. <see cref="H264BaselineEncoderOptions.TargetBitsPerFrame"/>
    /// is superseded — the session drives the per-picture bit budget from the controller every
    /// frame. Explicitly assigned speed knobs stay pinned across mode changes (the same
    /// "explicit wins" rule the options apply at construction).
    /// </param>
    /// <param name="rateControlConfig">Controller tunables; defaults are cloud-gaming oriented.</param>
    /// <param name="loggerFactory">Optional logging for controller decisions; null = silent.</param>
    public H264StreamingSession(
        int width,
        int height,
        H264BaselineEncoderOptions? encoderOptions = null,
        RateControlConfig? rateControlConfig = null,
        ILoggerFactory? loggerFactory = null)
    {
        _config = rateControlConfig ?? new RateControlConfig();
        var source = encoderOptions ?? new H264BaselineEncoderOptions();
        // Capture the caller's explicit-assignment intent before normalising the private copy —
        // the composition rule for live mode changes mirrors the options' constructor-time rule.
        _refsExplicit = source.MaxReferenceFramesIsExplicit;
        _explicitRefs = source.MaxReferenceFrames;
        _satdExplicit = source.UseMotionSatdIsExplicit;
        _rangeCapExplicit = source.SubPartitionRangeCapIsExplicit;
        _effortCapExplicit = source.MotionSearchEffortCapPerMbIsExplicit;
        _options = source.Clone();
        if (!_refsExplicit)
        {
            // Reserve the full DPB in the SPS so later speed-mode upshifts can restore the second
            // reference without an IDR (tier 2 above). Callers who pinned MaxReferenceFrames keep
            // their signalled value — the hardware-decoder-safety contract outranks adaptation.
            _options.MaxReferenceFrames = H264FrameSharedState.MaxDpbSize;
        }

        // The controller owns the per-picture bit budget; a constructor-baked budget would fight it.
        _options.TargetBitsPerFrame = 0;

        _appliedSpeedMode = source.SpeedMode;
        _encoder = CreateEncoder(width, height);
        _controller = new H264AdaptiveRateController(
            _config,
            loggerFactory?.CreateLogger<H264AdaptiveRateController>(),
            loggerFactory?.CreateLogger<LowLatencyRateController>(),
            loggerFactory?.CreateLogger<H264RecoveryPolicy>(),
            loggerFactory?.CreateLogger<AdaptationPolicy>());
        _targetFps = _config.SupportedFps is { Length: > 0 } fps ? fps[0] : 60;
        _controller.SyncAppliedState(width, height, _targetFps, _appliedSpeedMode);
    }

    /// <summary>Current output width — the size the caller must supply frames at.</summary>
    public int Width => _encoder.Width;

    /// <summary>Current output height — the size the caller must supply frames at.</summary>
    public int Height => _encoder.Height;

    /// <summary>Frame rate the caller is expected to pace at (the controller's latest target).</summary>
    public int TargetFps => _targetFps;

    /// <summary>Speed mode currently applied to the encoder.</summary>
    public EncoderSpeedMode CurrentSpeedMode => _appliedSpeedMode;

    /// <summary>Recommended minimum <c>annexB</c> span length (tracks the current resolution).</summary>
    public int RecommendedOutputBufferSize => _encoder.RecommendedOutputBufferSize;

    /// <summary>
    /// The single recovery policy instance driving IDR/intra-refresh decisions, for metrics and
    /// <see cref="H264RecoveryPolicy.Reset"/> only. It is invoked exactly once per
    /// <see cref="EncodeFrame"/> by the controller — never call
    /// <see cref="H264RecoveryPolicy.DecideRecovery"/> on it (see the ownership contract on
    /// <see cref="LowLatencyRateController"/>).
    /// </summary>
    public H264RecoveryPolicy RecoveryPolicy => _controller.RecoveryPolicy;

    /// <summary>The most recent controller decision, or null before the first frame.</summary>
    public EncoderAdaptationDecision? LastDecision { get; private set; }

    /// <summary>Test support: the encoder currently owned by the session (replaced by
    /// <see cref="ChangeResolution"/>).</summary>
    internal H264BaselineEncoder EncoderForTests => _encoder;

    /// <summary>Reason string of the last resolution/fps/speed adaptation, for observability.</summary>
    public string LastAdaptationReason => _controller.LastAdaptationReason;

    /// <summary>
    /// Encode one frame under the controller's decision for the given network feedback. Input is
    /// planar I420 at the current <see cref="Width"/>×<see cref="Height"/>, exactly as
    /// <see cref="H264BaselineEncoder.EncodeFrame"/> takes it.
    /// </summary>
    /// <param name="y">Luma plane.</param>
    /// <param name="u">U plane (half dimensions).</param>
    /// <param name="v">V plane (half dimensions).</param>
    /// <param name="strideY">Luma stride (≥ <see cref="Width"/>).</param>
    /// <param name="strideUv">Chroma stride (≥ <see cref="Width"/>/2).</param>
    /// <param name="annexB">Destination; allocate ≥ <see cref="RecommendedOutputBufferSize"/> bytes.</param>
    /// <param name="feedback">
    /// Latest network/transport snapshot. Callers with sparse feedback (e.g. periodic RTCP) pass
    /// their most recent snapshot each frame; one-shot signals (PLI/FIR) must be passed on exactly
    /// one frame and then cleared, or every frame re-triggers recovery.
    /// </param>
    /// <param name="timings">
    /// Optional host-measured encode/queue load (see <see cref="EncoderPipelineTimings"/>); omit
    /// for the fully deterministic default in which encode-backpressure escalation never fires.
    /// </param>
    public H264StreamingEncodeResult EncodeFrame(
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> u,
        ReadOnlySpan<byte> v,
        int strideY,
        int strideUv,
        Span<byte> annexB,
        EncoderNetworkFeedback feedback,
        EncoderPipelineTimings? timings = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(feedback);

        var stats = new EncoderPipelineStats(
            LastEncodeDuration: timings?.LastEncodeDuration ?? TimeSpan.Zero,
            AverageEncodeDuration: timings?.AverageEncodeDuration ?? TimeSpan.Zero,
            PendingInputFrames: timings?.PendingInputFrames ?? 0,
            PendingEncodedFrames: timings?.PendingEncodedFrames ?? 0,
            DroppedInputFrames: timings?.DroppedInputFrames ?? 0,
            DroppedEncodedFrames: timings?.DroppedEncodedFrames ?? 0,
            LastEncodedFrameBytes: _lastFrameBytes,
            LastFrameQp: _lastFrameQp,
            LastFrameWasIdr: _lastFrameWasIdr,
            MotionComplexity: 0.0,
            TextureComplexity: 0.0,
            SceneChangeDetected: false);

        var decision = _controller.GetDecision(feedback, stats);

        // Tier-1/2 application, between frames by construction (we are between frames right here).
        if (decision.SpeedMode != _appliedSpeedMode)
        {
            ApplyMode(decision.SpeedMode);
        }

        _targetFps = Math.Max(1, decision.TargetFps);
        var targetBits = (int)Math.Min(int.MaxValue, Math.Max(1L, (long)decision.TargetBitrateBps / _targetFps));
        var qp = Math.Clamp(decision.BaseQp, 0, 51);

        var bytesWritten = _encoder.EncodeFrame(
            y, u, v, strideY, strideUv, annexB,
            forceKeyframe: decision.ForceIdr,
            sliceLumaQp: qp,
            targetBitsPerFrame: targetBits);

        _lastFrameBytes = bytesWritten;
        _lastFrameQp = qp;
        _lastFrameWasIdr = _encoder.LastFrameWasIdr;
        LastDecision = decision;

        // Report what was actually applied: real geometry (a resolution recommendation is not a
        // resolution change), the decided fps (a pacing contract the caller follows), and the
        // decided speed mode (applied above).
        _controller.SyncAppliedState(_encoder.Width, _encoder.Height, _targetFps, _appliedSpeedMode);

        return new H264StreamingEncodeResult(
            BytesWritten: bytesWritten,
            WasIdr: _encoder.LastFrameWasIdr,
            AppliedSliceQp: qp,
            AppliedTargetBitsPerFrame: targetBits,
            AppliedSpeedMode: _appliedSpeedMode,
            ResolutionChangeRecommended: decision.Width != _encoder.Width || decision.Height != _encoder.Height,
            IntraRefreshRequested: decision.EnableIntraRefresh,
            Decision: decision);
    }

    /// <summary>
    /// Tier-3 change: switch the session to a new output resolution by transparently recreating the
    /// encoder (new SPS, new DPB; the next <see cref="EncodeFrame"/> emits an IDR carrying the new
    /// parameter sets, and the caller must supply frames at the new size from then on). Typically
    /// called when a result reported <see cref="H264StreamingEncodeResult.ResolutionChangeRecommended"/>
    /// and the caller can rescale its source; the session never changes resolution on its own.
    /// Speed-knob state, controller state, and recovery metrics all carry across.
    /// </summary>
    public void ChangeResolution(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width == _encoder.Width && height == _encoder.Height)
        {
            return;
        }

        _encoder.Dispose();
        _encoder = CreateEncoder(width, height);
        _controller.SyncAppliedState(width, height, _targetFps, _appliedSpeedMode);
    }

    /// <summary>Reset adaptation and recovery state (stream restart / hard scene change). The
    /// encoder and its GOP position are not touched.</summary>
    public void ResetControllerState() => _controller.Reset();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _encoder.Dispose();
    }

    private H264BaselineEncoder CreateEncoder(int width, int height)
    {
        var encoder = new H264BaselineEncoder(width, height, _options);
        // Normalise the live knobs to the applied mode: the private options reserve DPB headroom in
        // the SPS (an explicit MaxReferenceFrames assignment), which would otherwise override the
        // mode's reference count.
        ApplyModeTo(encoder, _appliedSpeedMode);
        return encoder;
    }

    private void ApplyMode(EncoderSpeedMode mode)
    {
        ApplyModeTo(_encoder, mode);
        _appliedSpeedMode = mode;
    }

    /// <summary>
    /// Map a speed mode onto the four knobs with the options' composition rule — a knob the caller
    /// assigned explicitly at session start stays pinned; unassigned knobs follow the mode — and
    /// apply them (the encoder additionally caps references at its SPS-signalled maximum).
    /// </summary>
    private void ApplyModeTo(H264BaselineEncoder encoder, EncoderSpeedMode mode)
    {
        var (presetRefs, presetSatd, presetRangeCap, presetEffortCap) = H264BaselineEncoderOptions.SpeedModePreset(mode);
        encoder.ApplySpeedKnobs(
            maxReferenceFrames: _refsExplicit ? _explicitRefs : presetRefs,
            useMotionSatd: _satdExplicit ? _options.UseMotionSatd : presetSatd,
            subPartitionRangeCap: _rangeCapExplicit ? _options.SubPartitionRangeCap : presetRangeCap,
            motionSearchEffortCapPerMb: _effortCapExplicit ? _options.MotionSearchEffortCapPerMb : presetEffortCap);
    }
}
