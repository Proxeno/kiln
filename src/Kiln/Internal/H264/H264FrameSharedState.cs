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
    /// Effective cap on how many reference frames the encoder will actually use and signal, in
    /// [1, <see cref="MaxDpbSize"/>]. Set once by <see cref="H264BaselineEncoder"/> from options.
    /// 1 forces a single-reference stream (ref_idx_l0 always 0, num_ref_idx_l0_active = 1,
    /// max_num_ref_frames = 1) — what real-time WebRTC peers and hardware (VideoToolbox) decoders
    /// expect; multi-reference P-frames are decoded fine by browser software decoders but are dropped
    /// by some strict hardware decoders, which manifests as "stop motion" (only IDRs survive).
    /// </summary>
    public int MaxReferenceFrames = MaxDpbSize;

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
    public readonly H264ReferenceTransformAtlas[] DpbLumaAtlas;

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
    public H264ReferenceTransformAtlas LumaReferenceTransformAtlas => DpbLumaAtlas[0];

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

    /// <summary>Per-MB winning reference index (0 or 1) for inter-coded MBs. Used by deblocking and MVP computation.</summary>
    public readonly byte[] MbRefIdx;
    /// <summary>Per-partition winning reference index for sub-partition inter MBs (4 entries per MB, index = mbIndex*4+partIndex).</summary>
    public readonly byte[] MbSubPartRefIdx;

    public H264FrameSharedState(int width, int height)
    {
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
        DpbLumaAtlas = new H264ReferenceTransformAtlas[MaxDpbSize];
        for (var i = 0; i < MaxDpbSize; i++)
        {
            DpbPaddedY[i] = new byte[paddedYSize];
            DpbPaddedU[i] = new byte[paddedUvSize];
            DpbPaddedV[i] = new byte[paddedUvSize];
            DpbLumaAtlas[i] = new H264ReferenceTransformAtlas(PaddedStrideY, height + 2 * HaloLuma);
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
        MbMvs = new H264MotionEstimator.Mv[mbCount];
        PrevMbMvs = new H264MotionEstimator.Mv[mbCount];
        MbSubPartMvs = new H264MotionEstimator.Mv[mbCount * 4];
        MbPartitions = new H264MotionEstimator.McPartition[mbCount];
        MbRefIdx = new byte[mbCount];
        MbSubPartRefIdx = new byte[mbCount * 4];
    }
}
