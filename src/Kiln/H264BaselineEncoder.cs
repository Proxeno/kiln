using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kiln.Internal.H264;

namespace Kiln;

/// <summary>Encoder options (baseline profile, CAVLC).</summary>
public sealed class H264BaselineEncoderOptions
{
    /// <summary>QP [0,51].</summary>
    public int QuantizationParameter { get; set; } = 28;

    /// <summary>IDR NAL every N coded frames (first frame is always IDR).</summary>
    public int KeyframeIntervalFrames { get; set; } = 60;

    public byte ProfileIdc { get; set; } = 66;

    /// <summary>H.264 level_idc (e.g. 31 = Level 3.1).</summary>
    public byte LevelIdc { get; set; } = 0x1F;

    /// <summary>
    /// Number of reference frames the encoder may use/signal, clamped to [1, <see cref="Internal.H264.H264FrameSharedState.MaxDpbSize"/>].
    /// 1 = single-reference (WebRTC / hardware-decoder safe); 2 = multi-reference. See
    /// <see cref="Internal.H264.H264FrameSharedState.MaxReferenceFrames"/>.
    /// </summary>
    public int MaxReferenceFrames { get; set; } = 2;

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
    /// When true, biases P-frame inter ME toward integer-pel refinement (fewer fractional-pel passes)
    /// and skips chroma-DC rate–distortion refinement for inter-coded chroma. IDR/I paths keep full fidelity.
    /// Default false for parity with golden / existing tests.
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
    /// </summary>
    public bool UseMotionSatd { get; set; } = true;

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
    /// After <c>SubPartBudget</c> (32) high-complexity MBs in a frame the radius is further halved to 4
    /// regardless of this setting (Option B frame budget).
    /// </summary>
    public int SubPartitionRangeCap { get; set; } = 16;
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
    private readonly int _mbW;
    private readonly int _mbH;
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
    private int _codedFrameIndex;
    private int _h264FrameNum;
    private int _idrPicId;
    private bool _disposed;

    public H264BaselineEncoder(
        int width,
        int height,
        H264BaselineEncoderOptions? options = null)
    {
        if ((width & 15) != 0 || (height & 15) != 0)
        {
            throw new ArgumentException("Width and height must be multiples of 16.");
        }

        _options = options ?? new H264BaselineEncoderOptions();
        var qp = Math.Clamp(_options.QuantizationParameter, 0, 51);
        _width = width;
        _height = height;
        _mbW = width / 16;
        _mbH = height / 16;
        _picInitQpMinus26 = qp - 26;
        var chromaRd = _options.ChromaDcRdLambda ?? H264ChromaDcScale.DefaultChromaDcRdLambdaFromLumaQp(qp);

        // One shared frame state owns the picture-sized buffers; each slice encoder sees the same
        // reconstruction & reference arrays, so disjoint per-slice writes assemble into one frame.
        var sharedState = new H264FrameSharedState(width, height);
        sharedState.MaxReferenceFrames = Math.Clamp(_options.MaxReferenceFrames, 1, H264FrameSharedState.MaxDpbSize);
        _frameShared = sharedState;
        var kernels = _options.PreferHardwareIntrinsics ? H264KernelSet.CreateBest() : new ScalarKernelSet();
        var encoderCount = Math.Max(1, Math.Min(MaxSliceEncoders, _mbH));
        _sliceEncoders = new H264BaselineSliceEncoder[encoderCount];
        for (var i = 0; i < encoderCount; i++)
        {
            _sliceEncoders[i] = new H264BaselineSliceEncoder(
                width,
                height,
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
                _options.SubPartitionRangeCap);
        }
        _spsRbsp = H264ParameterSets.WriteSpsRbsp(
            width, height, _options.ProfileIdc, _options.LevelIdc,
            Math.Clamp(_options.MaxReferenceFrames, 1, H264FrameSharedState.MaxDpbSize));
        _ppsRbsp = H264ParameterSets.WritePpsRbsp(_picInitQpMinus26);
        _ebspScratch = new byte[H264RbspEmulation.GetEmulationPreventionBufferSize(checked(width * height * 2 + 65_536))];
    }

    public int Width => _width;
    public int Height => _height;

    /// <summary>Encoder reconstruction (Y) after the last <see cref="EncodeFrame"/> — same layout as input: row-major, width stride.</summary>
    public ReadOnlySpan<byte> LastReconstructedY => _sliceEncoders[0].ReconstructedYPlane;

    /// <summary>Encoder reconstruction (U) after the last <see cref="EncodeFrame"/> — half resolution, row-major, width/2 stride.</summary>
    public ReadOnlySpan<byte> LastReconstructedU => _sliceEncoders[0].ReconstructedUPlane;

    /// <summary>Encoder reconstruction (V) after the last <see cref="EncodeFrame"/> — half resolution, row-major, width/2 stride.</summary>
    public ReadOnlySpan<byte> LastReconstructedV => _sliceEncoders[0].ReconstructedVPlane;

    /// <summary>Test support: per-MB luma QP after the most recent <see cref="EncodeFrame"/>.</summary>
    internal ReadOnlySpan<int> TestHookLastEncodedQpY => _frameShared.QpY;

    /// <summary>True if the most recent <see cref="EncodeFrame"/> emitted an IDR access unit.</summary>
    public bool LastFrameWasIdr { get; private set; }

    /// <summary>
    /// Encode one frame into Annex B. Returns number of bytes written.
    /// Planar I420: <paramref name="u"/> / <paramref name="v"/> are half dimensions; strides may exceed width/2.
    /// </summary>
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
        var mbRowsPerSlice = _mbH / sliceCount;
        var remainder = _mbH - mbRowsPerSlice * sliceCount;

        // Single-threaded fence: clear shared per-MB caches and (if IDR) invalidate the reference.
        // Done once before the parallel loop so disjoint per-slice writes don't race with the reset.
        _sliceEncoders[0].BeginFrame(isIdr);

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
                var firstMbRow = k * mbRowsPerSlice;
                var rowCount = mbRowsPerSlice + (k == sliceCount - 1 ? remainder : 0);
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

        // Gather NALs in raster (slice-index) order. WriteNal must run sequentially because each
        // call advances `pos` and writes into the shared output buffer.
        for (var k = 0; k < sliceCount; k++)
        {
            pos += WriteNal(annexB[pos..], 3, nalType, sliceEncoders[k].LastSliceRbsp);
        }

        _sliceEncoders[0].PadReconstructedReference();
        return pos;
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
