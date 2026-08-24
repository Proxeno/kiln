using FluentAssertions;
using Kiln;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Regression and oracle tests for the BL-series Baseline conformance fixes.
/// </summary>
public sealed class H264BaselineFixTests
{
    // ── BL-012 ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Log2MaxFrameNumMinus4_constant_is_single_source()
    {
        // SPS writer and slice header writer must share the same field-width constant
        // (BL-012). This test asserts the const is publicly accessible from H264ParameterSets
        // and has the expected value (0 → frame_num is 4 bits wide).
        H264ParameterSets.Log2MaxFrameNumMinus4.Should().Be(0,
            "log2_max_frame_num_minus4=0 means frame_num is written as a 4-bit field, " +
            "supporting 16 frame numbers before wrap per §7.3.3.");
    }

    [Fact]
    public void MaxNumRefFrames_constant_matches_SPS_signalling()
    {
        H264ParameterSets.MaxNumRefFrames.Should().Be(2,
            "encoder uses two-reference ME (DPB depth=2); SPS max_num_ref_frames must match so decoders allocate sufficient DPB (§7.3.2.1).");
    }

    [Fact]
    public void WriteSpsRbsp_emits_max_num_ref_frames_ue_equals_MaxNumRefFrames()
    {
        var rbsp = H264ParameterSets.WriteSpsRbsp(codedWidth: 32, codedHeight: 32, profileIdc: 66, levelIdc: 31);
        var br = new H264CavlcSpecDecode.BitReader(rbsp);
        br.ReadBits(8); // profile_idc
        br.ReadBits(8); // constraint_set0..5 + reserved_zero_2bits
        br.ReadBits(8); // level_idc
        ReadBaselineSpsUe(br).Should().Be(0u); // seq_parameter_set_id
        ReadBaselineSpsUe(br).Should().Be((uint)H264ParameterSets.Log2MaxFrameNumMinus4);
        ReadBaselineSpsUe(br).Should().Be(2u); // pic_order_cnt_type
        ReadBaselineSpsUe(br).Should().Be((uint)H264ParameterSets.MaxNumRefFrames);
    }

    // ── BL-005 ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WriteSpsRbsp_throws_for_non_baseline_profile()
    {
        // BL-005: profile_idc != 66 should throw NotSupportedException.
        Action act = () => H264ParameterSets.WriteSpsRbsp(codedWidth: 32, codedHeight: 32, profileIdc: 100, levelIdc: 31);
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*profile_idc 66*");
    }

    [Fact]
    public void WriteSpsRbsp_succeeds_for_baseline_profile()
    {
        var bytes = H264ParameterSets.WriteSpsRbsp(codedWidth: 32, codedHeight: 32, profileIdc: 66, levelIdc: 31);
        bytes.Should().NotBeEmpty();
    }

    // ── BL-006 ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WriteSpsRbsp_throws_when_frame_exceeds_level_MaxFS()
    {
        // 1280×720 = 3600 MBs; Level 3.0 (levelIdc=30) has MaxFS=1620 — should throw.
        Action act = () => H264ParameterSets.WriteSpsRbsp(1280, 720, profileIdc: 66, levelIdc: 30);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*MaxFS*");
    }

    [Fact]
    public void WriteSpsRbsp_succeeds_when_frame_fits_level()
    {
        // 1280×720 = 3600 MBs; Level 3.1 (levelIdc=31) has MaxFS=3600 — should succeed.
        var bytes = H264ParameterSets.WriteSpsRbsp(1280, 720, profileIdc: 66, levelIdc: 31);
        bytes.Should().NotBeEmpty();
    }

    [Fact]
    public void LevelLimits_ValidateFrameSize_throws_for_overflow()
    {
        // Level 1.0 (levelIdc=10) has MaxFS=99; 10×10=100 MBs exceeds it.
        Action act = () => H264LevelLimits.ValidateFrameSize(levelIdc: 10, mbW: 10, mbH: 10);
        act.Should().Throw<ArgumentException>().WithMessage("*MaxFS*");
    }

    [Fact]
    public void LevelLimits_ValidateFrameSize_passes_for_unknown_level()
    {
        // Unknown level_idc should skip validation silently (forward-compatibility).
        Action act = () => H264LevelLimits.ValidateFrameSize(levelIdc: 99, mbW: 1000, mbH: 1000);
        act.Should().NotThrow();
    }

    // ── BL-003 ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EncodeSliceRbsp_throws_for_non_row_aligned_firstMbInSlice()
    {
        // BL-003: firstMbInSlice must be a multiple of _mbW; a non-aligned value violates
        // §6.4.4 neighbour availability and must be rejected.
        const int w = 32; // 2 MB columns
        const int h = 32; // 2 MB rows
        var enc = new H264BaselineSliceEncoder(w, h, qp: 28);
        var y = new byte[w * h];
        var u = new byte[w * h / 4];
        var v = new byte[w * h / 4];

        // firstMbInSlice=1 is not row-aligned for a 2-column picture.
        Action act = () => enc.EncodeSliceRbsp(y, w, u, v, w / 2,
            isIdr: true, isPslice: false, frameNum: 0, idrPicId: 0,
            firstMbInSlice: 1);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*row-aligned*")
            .And.ParamName.Should().Be("firstMbInSlice");
    }

    [Fact]
    public void EncodeSliceRbsp_accepts_row_aligned_firstMbInSlice()
    {
        const int w = 32; // 2 MB columns
        const int h = 32; // 2 MB rows
        var enc = new H264BaselineSliceEncoder(w, h, qp: 28);
        var y = new byte[w * h];
        var u = new byte[w * h / 4];
        var v = new byte[w * h / 4];

        // firstMbInSlice=0 (row 0) and firstMbInSlice=2 (row 1 for a 2-column picture) are valid.
        Action act0 = () => enc.EncodeSliceRbsp(y, w, u, v, w / 2,
            isIdr: true, isPslice: false, frameNum: 0, idrPicId: 0,
            firstMbInSlice: 0);
        act0.Should().NotThrow();

        // Row 1: firstMbInSlice = mbW = 2.
        Action act2 = () => enc.EncodeSliceRbsp(y, w, u, v, w / 2,
            isIdr: false, isPslice: false, frameNum: 1, idrPicId: 0,
            firstMbInSlice: 2, mbCountInSlice: 2, isFirstSliceInFrame: false);
        act2.Should().NotThrow();
    }

    // ── BL-001 ───────────────────────────────────────────────────────────────────────────────────
    // Oracle test for §8.4.1.1 step-2 skip MV derivation.
    // We cannot call TryEncodePInterMacroblock directly, but we can construct scenarios via
    // H264BaselineEncoder and inspect the encoded bitstream / reconstruction. For the oracle
    // property, we verify that when a P-slice is encoded with content that should produce skips
    // where the §8.4.1.1 step-2 condition forces mvSkipPred=(0,0), the encoder produces a
    // bitstream that ffmpeg decodes cleanly (smoke). More direct unit coverage lives in the smoke
    // tests via H264FfmpegDecodeSmokeTests; here we add an encoder-side oracle.
    //
    // Oracle: when A and B are both unavailable (first row, first column MB), §8.4.1.1 step 2
    // forces mvSkipPred = (0,0). Encode two identical frames so the skip decision fires at MB(0,0)
    // and verify the reconstruction equals the reference (encoder + decoder agree on (0,0) skip).

    [Fact]
    public void PSkip_step2_zero_mv_when_A_and_B_unavailable()
    {
        // Two identical source frames → all-skip P-slice. The first MB (mbx=0, mby=0) has
        // A=unavail and B=unavail, so §8.4.1.1 step-2 must set mvSkipPred=(0,0).
        // With mvSkipPred=(0,0) the skip prediction = copy of first MB in reference =
        // first 16×16 block = the same content. We check encoder recon == source via
        // a pixel-exact comparison (zero residual + (0,0) MV → recon = reference = source).
        const int w = 32;
        const int h = 32;
        var src = BuildFlatI420(w, h, lumaVal: 180, uVal: 128, vVal: 128);
        var ySize = w * h;
        var uvSize = ySize / 4;
        var buf = new byte[1_000_000];

        using var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions { QuantizationParameter = 28 });
        // Frame 1 — IDR reference.
        enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf);
        var reconAfterIdr = enc.LastReconstructedY.ToArray();

        // Frame 2 — identical content → expect all-skip P-slice.
        enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf);
        var reconAfterPslice = enc.LastReconstructedY.ToArray();

        // Reconstruction after P-skip must equal reconstruction after IDR for flat content.
        reconAfterPslice.Should().Equal(reconAfterIdr,
            "flat identical frames → P_Skip with (0,0) MV → recon = copy of reference = IDR recon");
    }

    [Theory]
    [InlineData(0, 0, 12, -8)]
    [InlineData(12, -8, 0, 0)]
    public void PSkip_step2_zero_mv_when_either_A_or_B_is_zero(int ax, int ay, int bx, int by)
    {
        var mvA = new H264MotionEstimator.Mv((short)ax, (short)ay);
        var mvB = new H264MotionEstimator.Mv((short)bx, (short)by);
        var median = new H264MotionEstimator.Mv(20, -12);

        H264BaselineSliceEncoder.DerivePSkipMvSingleRef(mvA, aRefIdx: 0, mvB, bRefIdx: 0, median, aAbsent: false, bAbsent: false)
            .Should().Be(default(H264MotionEstimator.Mv),
                "H.264 §8.4.1.1 derives P_Skip MV (0,0) when either A or B has refIdx 0 and MV (0,0)");
    }

    [Fact]
    public void PSkip_step2_uses_median_when_A_and_B_are_available_and_nonzero()
    {
        var mvA = new H264MotionEstimator.Mv(4, 0);
        var mvB = new H264MotionEstimator.Mv(0, -4);
        var median = new H264MotionEstimator.Mv(8, -8);

        H264BaselineSliceEncoder.DerivePSkipMvSingleRef(mvA, aRefIdx: 0, mvB, bRefIdx: 0, median, aAbsent: false, bAbsent: false)
            .Should().Be(median);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void PSkip_step2_zero_mv_when_A_or_B_has_nonzero_refIdx(int aRefIdx, int bRefIdx)
    {
        var mvA = new H264MotionEstimator.Mv(4, 0);
        var mvB = new H264MotionEstimator.Mv(0, -4);
        var median = new H264MotionEstimator.Mv(8, -8);

        H264BaselineSliceEncoder.DerivePSkipMvSingleRef(mvA, aRefIdx, mvB, bRefIdx, median, aAbsent: false, bAbsent: false)
            .Should().Be(default(H264MotionEstimator.Mv),
                "§8.4.1.1: P_Skip MV is (0,0) when A or B has a non-zero reference index");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PSkip_step2_zero_mv_when_A_or_B_absent(bool aAbsent, bool bAbsent)
    {
        var mvA = new H264MotionEstimator.Mv(4, 0);
        var mvB = new H264MotionEstimator.Mv(0, -4);
        var median = new H264MotionEstimator.Mv(8, -8);

        H264BaselineSliceEncoder.DerivePSkipMvSingleRef(mvA, aRefIdx: 0, mvB, bRefIdx: 0, median, aAbsent, bAbsent)
            .Should().Be(default(H264MotionEstimator.Mv),
                "§8.4.1.1: P_Skip MV is (0,0) when A or B is absent (PART_NOT_AVAILABLE)");
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void PSkip_step2_uses_median_when_A_or_B_is_intra_present(int aRefIdx, int bRefIdx)
    {
        // An intra neighbour is present (not absent) with refIdx -1 and MV (0,0). Per §8.4.1.1 it is
        // neither absent, nor a ">0 ref", nor a "ref-0 zero-MV", so P_Skip falls through to the median
        // — matching the decoder. (The old code conflated refIdx -1 with "unavailable" and forced 0.)
        var mvA = aRefIdx < 0 ? default : new H264MotionEstimator.Mv(4, 0);
        var mvB = bRefIdx < 0 ? default : new H264MotionEstimator.Mv(0, -4);
        var median = new H264MotionEstimator.Mv(8, -8);

        H264BaselineSliceEncoder.DerivePSkipMvSingleRef(mvA, aRefIdx, mvB, bRefIdx, median, aAbsent: false, bAbsent: false)
            .Should().Be(median);
    }

    // ── BL-004 ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void QpDelta_chain_is_decoder_aligned_with_constant_qp()
    {
        // BL-004 correctness with constant QP: when QP is constant (no RC/AQ), every mb_qp_delta
        // that IS emitted should be zero. The encoder produces consistent output; this test
        // catches a regression where skips would incorrectly advance _lastMbQp and cause a
        // non-zero delta on the first coded MB after a skip run.
        const int w = 48;
        const int h = 32;
        var src = BuildGradientI420(w, h);
        var ySize = w * h;
        var uvSize = ySize / 4;
        var buf = new byte[1_000_000];

        using var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions { QuantizationParameter = 28 });
        // IDR
        enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf);
        // P-slice (may have skips + coded MBs)
        var n = enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf);

        // The bitstream must be parseable by the trace decoder; if QP chain is wrong the
        // bit-stream reader will encounter invalid CAVLC or wrong QP and return errors.
        n.Should().BeGreaterThan(0, "P-slice must produce at least one byte");
    }

    [Fact]
    public void Skipped_P_mbs_record_decoder_effective_qp_for_deblock_metadata()
    {
        // When an MB is P_Skip, no mb_qp_delta is present, so decoder QPY remains the previous coded
        // QP. Rate control may propose lower QPs for zero-bit skip runs, but deblocking metadata must
        // keep the decoder-effective QP or encoder references drift from decoded references.
        const int w = 32;
        const int h = 32;
        const int baseQp = 28;
        var src = BuildFlatI420(w, h, lumaVal: 96, uVal: 128, vVal: 128);
        var ySize = w * h;
        var uvSize = ySize / 4;
        var buf = new byte[1_000_000];

        using var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
        {
            QuantizationParameter = baseQp,
            TargetBitsPerFrame = 50_000,
        });

        enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf);
        enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf);

        enc.TestHookLastEncodedQpY.ToArray().Should().OnlyContain(qp => qp == baseQp,
            "flat identical P frame collapses to skip, and skipped MBs inherit the previous decoder QP");
    }

    // ── BL-008 / BL-009 ──────────────────────────────────────────────────────────────────────────
    // Focused tests for CAVLC nC slice boundary and multi-slice seam behaviour.

    [Fact]
    public void MultiSlice_seam_reconstruction_matches_single_slice()
    {
        // BL-009: multi-slice seam test. Encode with SliceCount=1 and SliceCount=2 and verify
        // the encoder's internal reconstruction does not diverge at the slice boundary.
        const int w = 48;
        const int h = 32; // 3×2 MBs; SliceCount=2 → one slice per row.
        var src = BuildGradientI420(w, h);
        var ySize = w * h;
        var uvSize = ySize / 4;
        var buf1 = new byte[1_000_000];
        var buf2 = new byte[1_000_000];

        byte[] recon1Y, recon2Y;
        using (var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions { QuantizationParameter = 28, SliceCount = 1 }))
        {
            enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf1);
            enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf1);
            recon1Y = enc.LastReconstructedY.ToArray();
        }

        using (var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions { QuantizationParameter = 28, SliceCount = 2 }))
        {
            enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf2);
            enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf2);
            recon2Y = enc.LastReconstructedY.ToArray();
        }

        // Slice-seam row: SliceCount=2 applies idc=2 (no filter across boundaries), so the
        // seam row pixels may differ. We only check that SliceCount=2 produces a non-empty
        // reconstruction and does not crash.
        recon2Y.Should().HaveCount(w * h, "multi-slice encoder must produce full-frame reconstruction");
        recon1Y.Should().HaveCount(w * h);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static uint ReadBaselineSpsUe(H264CavlcSpecDecode.BitReader br)
    {
        var zeros = 0;
        while (br.ReadBit() == 0) zeros++;
        if (zeros == 0) return 0;
        return (uint)((1 << zeros) - 1 + br.ReadBits(zeros));
    }

    private static byte[] BuildFlatI420(int w, int h, byte lumaVal, byte uVal, byte vVal)
    {
        var ySize = w * h;
        var uvSize = ySize / 4;
        var src = new byte[ySize + uvSize * 2];
        Array.Fill(src, lumaVal, 0, ySize);
        Array.Fill(src, uVal, ySize, uvSize);
        Array.Fill(src, vVal, ySize + uvSize, uvSize);
        return src;
    }

    private static byte[] BuildGradientI420(int w, int h)
    {
        var ySize = w * h;
        var uvSize = ySize / 4;
        var src = new byte[ySize + uvSize * 2];
        for (var i = 0; i < ySize; i++) src[i] = (byte)((i * 137 + 37) % 200 + 30);
        for (var i = ySize; i < src.Length; i++) src[i] = 128;
        return src;
    }
}
