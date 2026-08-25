namespace Kiln.Internal.H264;

/// <summary>
/// Per-frame state shared by all <see cref="H264BaselineSliceEncoder"/> instances that encode the
/// same access unit. Sliced encoding splits the picture into MB row ranges; each slice writes only
/// to its own MB indices / luma & chroma rows, so concurrent writes to these arrays are disjoint
/// and safe. The reference-picture buffers are written outside the parallel region (orchestrator
/// drives <see cref="H264BaselineSliceEncoder.PadReconstructedReference"/> after all slices land).
/// </summary>
/// <remarks>
/// Allocated once per <see cref="H264BaselineEncoder"/>. A single instance is passed to every
/// slice encoder so they all observe the same reconstruction, the same per-MB neighbour caches
/// (read via the slice-aware <c>_firstMbRowInSlice</c> guards in
/// <see cref="H264BaselineSliceEncoder"/>), and the same reference-picture-valid flag.
/// </remarks>
internal sealed class H264FrameSharedState
{
    /// <summary>Luma reference halo (16 pels per side) — sized for the 6-tap qpel filter (H.264 8.4.2.1).</summary>
    public const int HaloLuma = 16;

    /// <summary>Chroma reference halo (8 pels per side) — sized for the bilinear chroma filter (H.264 8.4.1.4).</summary>
    public const int HaloChroma = 8;

    /// <summary>Maximum number of decoded reference frames the DPB (Decoded Picture Buffer) can physically store.</summary>
    public const int MaxDpbSize = 2;

    /// <summary>
    /// Effective cap on how many reference frames the encoder will actually use, in
    /// [1, <see cref="MaxDpbSize"/>]. Initialised at construction from options; mutable between
    /// frames via <see cref="SetMaxReferenceFrames"/> for mid-stream speed adaptation.
    /// 1 forces a single-reference stream (ref_idx_l0 always 0, num_ref_idx_l0_active = 1) —
    /// what real-time WebRTC peers and hardware (VideoToolbox) decoders
    /// expect; multi-reference P-frames are decoded fine by browser software decoders but are dropped
    /// by some strict hardware decoders, which manifests as "stop motion" (only IDRs survive).
    /// The SPS <c>max_num_ref_frames</c> is signalled once from the construction-time value and is
    /// an upper bound the live cap may sit below (see <see cref="SetMaxReferenceFrames"/>).
    /// </summary>
    public int MaxReferenceFrames { get; private set; }

    /// <summary>
    /// Change the live reference-frame cap between frames (never during a slice encode — the
    /// parallel slice section reads it and <see cref="DpbCount"/> concurrently).
    /// <para>
    /// Legality: the SPS-signalled <c>max_num_ref_frames</c> is an upper bound on the decoder's
    /// reference list, not a per-slice requirement — each P slice signals its own active count via
    /// <c>num_ref_idx_active_override_flag</c> (§7.3.3), and this encoder derives that count from
    /// <see cref="DpbCount"/>. Lowering the cap therefore takes effect immediately: this method
    /// clamps <see cref="DpbCount"/>, so the next slice header signals fewer active references and
    /// motion search stops reading the retired slot. Raising the cap (never above what the SPS
    /// signalled — the caller must enforce that) takes effect after one frame: the retired DPB slot
    /// went stale while capped, and the next reference rotation refills slot 1 from slot 0 before
    /// <see cref="DpbCount"/> grows, so no stale plane is ever searched. This mirrors exactly what a
    /// conformant decoder holds: with <c>max_num_ref_frames = 2</c> its sliding window (§8.2.5.3)
    /// retains the two most recent decoded pictures regardless of how many the encoder references.
    /// </para>
    /// </summary>
    public void SetMaxReferenceFrames(int maxReferenceFrames)
    {
        MaxReferenceFrames = Math.Clamp(maxReferenceFrames, 1, MaxDpbSize);
        if (DpbCount > MaxReferenceFrames)
        {
            DpbCount = MaxReferenceFrames;
        }
    }

    public readonly byte[] RecY;
    public readonly byte[] RecU;
    public readonly byte[] RecV;

    /// <summary>
    /// Decoded Picture Buffer: [0] = most recent decoded frame, [1] = frame before that.
    /// Each slot holds a padded luma/chroma plane. Use <see cref="DpbCount"/> to determine how many
    /// slots are currently valid. Written only outside the parallel slice region.
    /// </summary>
    public readonly byte[][] DpbPaddedY;
    public readonly byte[][] DpbPaddedU;
    public readonly byte[][] DpbPaddedV;

    /// <summary>
    /// Number of valid reference frames in <see cref="DpbPaddedY"/>/<see cref="DpbPaddedU"/>/<see cref="DpbPaddedV"/>.
    /// 0 after IDR (no valid reference), 1 after the first P-frame, 2 after the second P-frame and beyond.
    /// Mutated only outside the parallel slice region.
    /// </summary>
    public int DpbCount;

    // Backward-compatible aliases used by single-reference code paths.
    public byte[] PaddedRefY => DpbPaddedY[0];
    public byte[] PaddedRefU => DpbPaddedU[0];
    public byte[] PaddedRefV => DpbPaddedV[0];
    public bool PaddedRefValid => DpbCount > 0;

    public readonly int PaddedStrideY;
    public readonly int PaddedStrideUv;

    public readonly byte[] NonZeros;
    public readonly byte[] ChromaNonZeros;
    public readonly byte[] IntraModes;
    public readonly byte[] BsHorizontal;
    public readonly byte[] BsVertical;
    public readonly int[] QpY;
    public readonly int[] QpUv;
    public readonly bool[] MbIsInter;
    public readonly bool[] MbIsSkip;
    public readonly H264MotionEstimator.Mv[] MbMvs;
    public readonly H264MotionEstimator.Mv[] PrevMbMvs;
    public readonly H264MotionEstimator.Mv[] MbSubPartMvs;
    public readonly H264MotionEstimator.McPartition[] MbPartitions;

    /// <summary>
    /// Per-MB-row motion-search effort recorded during the most recent frame, in the deterministic
    /// candidate-evaluation units of <c>H264MotionEstimator.ThreadSearchEffort</c>. Each row is
    /// written exactly once per frame by the slice encoder that owns it (rows are disjoint across
    /// slices), and read by the multi-slice orchestrator before the next frame's parallel region to
    /// balance the slice partition. Rows keep their previous value on frames whose owner slice did
    /// no motion search (IDR), which leaves the partition unchanged for that frame.
    /// </summary>
    public readonly long[] RowMeEffort;

    /// <summary>Per-MB winning reference index (0 or 1) for inter-coded MBs. Used by deblocking and MVP computation.</summary>
    public readonly byte[] MbRefIdx;

    /// <param name="maxReferenceFrames">
    /// Effective reference cap from options, clamped to [1, <see cref="MaxDpbSize"/>]. The padded
    /// reference planes are always allocated for every slot regardless of this cap.
    /// </param>
    /// <param name="useMotionSatd">
    /// Whether the encoder scores integer-pel ME candidates with SATD.
    /// </param>
    public H264FrameSharedState(int width, int height, int maxReferenceFrames = MaxDpbSize, bool useMotionSatd = true)
    {
        MaxReferenceFrames = Math.Clamp(maxReferenceFrames, 1, MaxDpbSize);
        var mbW = width / 16;
        var mbH = height / 16;
        var mbCount = mbW * mbH;
        var uvW = width / 2;
        var uvH = height / 2;

        RecY = new byte[width * height];
        RecU = new byte[uvW * uvH];
        RecV = new byte[uvW * uvH];

        PaddedStrideY = width + 2 * HaloLuma;
        PaddedStrideUv = uvW + 2 * HaloChroma;
        var paddedYSize = (height + 2 * HaloLuma) * PaddedStrideY;
        var paddedUvSize = (uvH + 2 * HaloChroma) * PaddedStrideUv;
        DpbPaddedY = new byte[MaxDpbSize][];
        DpbPaddedU = new byte[MaxDpbSize][];
        DpbPaddedV = new byte[MaxDpbSize][];
        for (var i = 0; i < MaxDpbSize; i++)
        {
            DpbPaddedY[i] = new byte[paddedYSize];
            DpbPaddedU[i] = new byte[paddedUvSize];
            DpbPaddedV[i] = new byte[paddedUvSize];
        }
        DpbCount = 0;

        NonZeros = new byte[mbCount * 16];
        IntraModes = new byte[mbCount * 7];
        ChromaNonZeros = new byte[mbCount * 8];
        BsHorizontal = new byte[mbCount * 16];
        BsVertical = new byte[mbCount * 16];
        QpY = new int[mbCount];
        QpUv = new int[mbCount];
        MbIsInter = new bool[mbCount];
        MbIsSkip = new bool[mbCount];
        RowMeEffort = new long[mbH];
        MbMvs = new H264MotionEstimator.Mv[mbCount];
        PrevMbMvs = new H264MotionEstimator.Mv[mbCount];
        MbSubPartMvs = new H264MotionEstimator.Mv[mbCount * 4];
        MbPartitions = new H264MotionEstimator.McPartition[mbCount];
        MbRefIdx = new byte[mbCount];
    }
}
