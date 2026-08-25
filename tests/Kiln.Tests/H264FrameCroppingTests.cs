using System.Diagnostics;
using FluentAssertions;
using Kiln;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Non-multiple-of-16 frame dimensions via SPS frame cropping (§7.3.2.1.1 / §7.4.2.1.1):
/// bit-level SPS assertions (always run), <see cref="H264SourcePlaneExtender"/> unit tests, and
/// ffmpeg decode oracles proving the decoder outputs exactly the display size. The ffmpeg tests
/// self-skip when ffmpeg is not on PATH, mirroring <see cref="H264FfmpegDecodeSmokeTests"/>.
/// </summary>
public sealed class H264FrameCroppingTests
{
    // ── SPS bit-level tests (no ffmpeg) ─────────────────────────────────────────────────────────

    [Theory]
    // display → coded 1920×1088: frame_crop_bottom_offset = 8/CropUnitY = 4 (§7.4.2.1.1).
    [InlineData(1920, 1080, 1920, 1088, 0u, 4u)]
    // display → coded 1376×768: frame_crop_right_offset = 10/CropUnitX = 5.
    [InlineData(1366, 768, 1376, 768, 5u, 0u)]
    // display → coded 640×368: bottom 8/2 = 4.
    [InlineData(640, 360, 640, 368, 0u, 4u)]
    // both axes unaligned: coded 112×64, right 12/2 = 6, bottom 12/2 = 6.
    [InlineData(100, 52, 112, 64, 6u, 6u)]
    public void Sps_signals_right_bottom_crop_offsets(
        int displayW, int displayH, int codedW, int codedH, uint expectedRight, uint expectedBottom)
    {
        var levelIdc = H264LevelLimits.MinimumLevelForFrameSize(codedW / 16, codedH / 16);
        var rbsp = H264ParameterSets.WriteSpsRbsp(
            codedW, codedH, profileIdc: 66, levelIdc: levelIdc,
            displayWidth: displayW, displayHeight: displayH);

        var sps = ParseBaselineSps(rbsp);
        sps.MbWidth.Should().Be(codedW / 16);
        sps.MbHeight.Should().Be(codedH / 16);
        sps.FrameCroppingFlag.Should().BeTrue("display size differs from coded size");
        sps.CropLeft.Should().Be(0u, "left offset stays 0 so the MB grid aligns to the visible origin");
        sps.CropTop.Should().Be(0u);
        sps.CropRight.Should().Be(expectedRight);
        sps.CropBottom.Should().Be(expectedBottom);

        // Round-trip through §7.4.2.1.1: display = coded − CropUnit·(left+right) / − CropUnit·(top+bottom).
        (codedW - H264ParameterSets.CropUnit * (int)(sps.CropLeft + sps.CropRight)).Should().Be(displayW);
        (codedH - H264ParameterSets.CropUnit * (int)(sps.CropTop + sps.CropBottom)).Should().Be(displayH);
    }

    [Fact]
    public void Sps_has_no_cropping_block_for_aligned_dimensions()
    {
        var rbsp = H264ParameterSets.WriteSpsRbsp(1280, 720, profileIdc: 66, levelIdc: 31);
        var sps = ParseBaselineSps(rbsp);
        sps.FrameCroppingFlag.Should().BeFalse("aligned dimensions must keep the pre-crop SPS byte-identical");

        // Passing display == coded explicitly must produce the same bytes as the default (no-crop) form.
        var explicitRbsp = H264ParameterSets.WriteSpsRbsp(
            1280, 720, profileIdc: 66, levelIdc: 31, displayWidth: 1280, displayHeight: 720);
        explicitRbsp.Should().Equal(rbsp);
    }

    [Theory]
    [InlineData(1919, 1080)] // odd width
    [InlineData(1920, 1079)] // odd height
    [InlineData(1920, 1090)] // display > coded
    public void Sps_rejects_unrepresentable_display_sizes(int displayW, int displayH)
    {
        Action act = () => H264ParameterSets.WriteSpsRbsp(
            1920, 1088, profileIdc: 66, levelIdc: 40, displayWidth: displayW, displayHeight: displayH);
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Encoder wiring, without ffmpeg: the SPS NAL emitted ahead of the IDR carries the crop block.
    /// </summary>
    [Fact]
    public void Encoder_annexB_sps_carries_crop_for_unaligned_dimensions()
    {
        const int w = 100;
        const int h = 52;
        var (y, u, v) = MakeCheckerboard(w, h);
        var annex = new byte[512_000];
        using var enc = new H264BaselineEncoder(w, h);
        var n = enc.EncodeFrame(y, u, v, w, w / 2, annex);
        n.Should().BeGreaterThan(0);

        var spsRbsp = ExtractNalRbsp(annex.AsSpan(0, n), nalType: 7);
        spsRbsp.Should().NotBeNull("IDR access unit must carry an SPS NAL");
        var sps = ParseBaselineSps(spsRbsp!);
        sps.MbWidth.Should().Be(7);
        sps.MbHeight.Should().Be(4);
        sps.FrameCroppingFlag.Should().BeTrue();
        sps.CropRight.Should().Be(6u);
        sps.CropBottom.Should().Be(6u);
    }

    // ── Encoder surface (no ffmpeg) ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(101, 52)]
    [InlineData(100, 51)]
    public void Encoder_rejects_odd_dimensions(int w, int h)
    {
        Action act = () => new H264BaselineEncoder(w, h);
        act.Should().Throw<ArgumentException>().WithMessage("*even*");
    }

    [Fact]
    public void Encoder_1080p_with_explicit_insufficient_level_throws_naming_required_level()
    {
        // Padded 1920×1088 = 8160 MBs > MaxFS 3600 of level_idc 31 (Annex A Table A-1). With an
        // explicit level the encoder must not silently upgrade it: the exception must name the
        // minimum sufficient level (40) and explain that the MB count is of the padded picture,
        // since 8160 won't match arithmetic done on 1920×1080.
        Action act = () => new H264BaselineEncoder(
            1920, 1080, new H264BaselineEncoderOptions { LevelIdc = 31 });
        act.Should().Throw<ArgumentException>()
            .WithMessage("*MaxFS*")
            .WithMessage("*8160*")
            .WithMessage("*padded*")
            .WithMessage("*level_idc 40*");
    }

    [Theory]
    [InlineData(320, 240, 31)]    // 300 MBs — min level 1.1, floored at the 3.1 default
    [InlineData(1280, 720, 31)]   // 3600 MBs — exactly Level 3.1's MaxFS (the historical default)
    [InlineData(1366, 768, 32)]   // padded 1376×768 = 4128 MBs — first above 3.1
    [InlineData(1920, 1080, 40)]  // padded 1920×1088 = 8160 MBs
    [InlineData(3840, 2160, 51)]  // 32400 MBs — 4K needs Level 5.1
    public void Encoder_default_level_auto_selects_lowest_sufficient_floored_at_31(
        int w, int h, byte expectedLevelIdc)
    {
        using var enc = new H264BaselineEncoder(w, h);
        enc.LevelIdc.Should().Be(expectedLevelIdc);
    }

    [Fact]
    public void Encoder_auto_level_is_signalled_in_sps()
    {
        var (y, u, v) = MakeCheckerboard(1920, 1080);
        using var enc = new H264BaselineEncoder(1920, 1080);
        var annexB = new byte[enc.RecommendedOutputBufferSize];
        var written = enc.EncodeFrame(y, u, v, 1920, 960, annexB);
        written.Should().BeGreaterThan(0);

        // SPS NAL: start code (4) + header (1), then profile_idc, constraint flags, level_idc.
        annexB[4].Should().Be(0x67, "first NAL of an IDR access unit is the SPS");
        annexB[7].Should().Be(40, "auto-selected level_idc 40 must be written to the SPS");
    }

    [Fact]
    public void Encoder_explicit_level_is_exposed_unchanged()
    {
        using var enc = new H264BaselineEncoder(
            1280, 720, new H264BaselineEncoderOptions { LevelIdc = 42 });
        enc.LevelIdc.Should().Be(42);
    }

    [Fact]
    public void EncodeFrame_with_undersized_output_span_names_recommended_size()
    {
        var (y, u, v) = MakeCheckerboard(64, 64);
        using var enc = new H264BaselineEncoder(64, 64);
        var annexB = new byte[16];
        Action act = () => enc.EncodeFrame(y, u, v, 64, 32, annexB);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*RecommendedOutputBufferSize*");
    }

    [Fact]
    public void Encoder_exposes_display_and_coded_dimensions()
    {
        using var enc = new H264BaselineEncoder(
            1920, 1080, new H264BaselineEncoderOptions { LevelIdc = 40 });
        enc.Width.Should().Be(1920);
        enc.Height.Should().Be(1080);
        enc.CodedWidth.Should().Be(1920);
        enc.CodedHeight.Should().Be(1088);
    }

    [Fact]
    public void MinimumLevelForFrameSize_matches_table_A1()
    {
        H264LevelLimits.MinimumLevelForFrameSize(80, 45).Should().Be(31);  // 3600 MBs = 1280×720
        H264LevelLimits.MinimumLevelForFrameSize(86, 48).Should().Be(32);  // 4128 MBs = 1376×768
        H264LevelLimits.MinimumLevelForFrameSize(120, 68).Should().Be(40); // 8160 MBs = 1920×1088
        H264LevelLimits.MinimumLevelForFrameSize(1000, 1000).Should().Be(0); // beyond every level
    }

    [Fact]
    public void CopyLastReconstructedTo_returns_display_sized_crop_of_coded_planes()
    {
        const int w = 100;
        const int h = 52;
        const int codedW = 112;
        const int codedH = 64;
        var (y, u, v) = MakeCheckerboard(w, h);
        var annex = new byte[512_000];
        using var enc = new H264BaselineEncoder(w, h);
        enc.EncodeFrame(y, u, v, w, w / 2, annex);

        // Uncropped coded planes keep CodedWidth stride (documented 0.x contract).
        enc.LastReconstructedY.Length.Should().Be(codedW * codedH);
        enc.LastReconstructedU.Length.Should().Be(codedW / 2 * (codedH / 2));

        var cropY = new byte[w * h];
        var cropU = new byte[w / 2 * (h / 2)];
        var cropV = new byte[w / 2 * (h / 2)];
        enc.CopyLastReconstructedTo(cropY, cropU, cropV, w, w / 2);

        for (var row = 0; row < h; row++)
        {
            cropY.AsSpan(row * w, w).ToArray().Should().Equal(
                enc.LastReconstructedY.Slice(row * codedW, w).ToArray(),
                $"cropped row {row} must be the left {w} samples of the coded row");
        }

        cropU.AsSpan(0, w / 2).ToArray().Should().Equal(
            enc.LastReconstructedU.Slice(0, w / 2).ToArray());
        cropV.AsSpan(0, w / 2).ToArray().Should().Equal(
            enc.LastReconstructedV.Slice(0, w / 2).ToArray());
    }

    // ── H264SourcePlaneExtender unit tests (no ffmpeg) ──────────────────────────────────────────

    [Fact]
    public void Extender_replicates_last_column_row_and_corner()
    {
        const int srcW = 5;
        const int srcH = 3;
        const int srcStride = 6; // stride > width: extender must honour the source stride
        const int dstW = 8;
        const int dstH = 6;
        var src = new byte[srcStride * srcH];
        for (var yy = 0; yy < srcH; yy++)
        {
            for (var xx = 0; xx < srcW; xx++)
            {
                src[yy * srcStride + xx] = (byte)(10 * yy + xx + 1);
            }

            src[yy * srcStride + srcW] = 0xEE; // stride slack — must never be read
        }

        var dst = new byte[dstW * dstH];
        H264SourcePlaneExtender.Extend(src, srcStride, srcW, srcH, dst, dstW, dstW, dstH);

        for (var yy = 0; yy < srcH; yy++)
        {
            for (var xx = 0; xx < srcW; xx++)
            {
                dst[yy * dstW + xx].Should().Be(src[yy * srcStride + xx], "interior must copy verbatim");
            }

            for (var xx = srcW; xx < dstW; xx++)
            {
                dst[yy * dstW + xx].Should().Be(src[yy * srcStride + srcW - 1],
                    $"row {yy} right extension must replicate the last real column");
            }
        }

        for (var yy = srcH; yy < dstH; yy++)
        {
            for (var xx = 0; xx < dstW; xx++)
            {
                dst[yy * dstW + xx].Should().Be(dst[(srcH - 1) * dstW + xx],
                    "bottom extension must replicate the last extended row");
            }
        }

        // Bottom-right corner region repeats the bottom-right source sample.
        dst[dstH * dstW - 1].Should().Be(src[(srcH - 1) * srcStride + srcW - 1]);
    }

    // ── ffmpeg decode oracles (self-skip when ffmpeg is absent) ─────────────────────────────────

    /// <summary>
    /// The proof that cropping works end-to-end: the decoder's raw output is <em>exactly</em>
    /// display-sized. An uncropped 1376×768 stream would decode to 1,585,152 bytes/frame, not
    /// 1,573,632 — so exact equality (not ≥) is the load-bearing assertion.
    /// </summary>
    [Fact]
    public void Ffmpeg_decodes_1366x768_at_exactly_display_size_with_clean_boundary()
    {
        if (!FfmpegOnPath())
        {
            return;
        }

        const int w = 1366;
        const int h = 768;
        var (y, u, v) = MakeCheckerboard(w, h);
        var annex = new byte[w * h * 4];
        using var enc = new H264BaselineEncoder(
            w, h, new H264BaselineEncoderOptions { LevelIdc = 32, QuantizationParameter = 18 });
        var n = enc.EncodeFrame(y, u, v, w, w / 2, annex);

        var raw = DecodeAllFramesExact(annex.AsSpan(0, n), w, h, frameCount: 1);
        AssertBoundaryBandPsnr(raw, y, w, h, minDb: 32.0);
    }

    /// <summary>
    /// 6-frame GOP (IDR + 5 P) over a moving checkerboard: catches replicated padding corrupting
    /// visible samples when it re-enters through the DPB and the 6-tap qpel filter.
    /// </summary>
    [Fact]
    public void Ffmpeg_decodes_6_frame_gop_at_unaligned_size_without_boundary_corruption()
    {
        if (!FfmpegOnPath())
        {
            return;
        }

        const int w = 324;
        const int h = 244; // coded 336×256 — both axes cropped
        const int frames = 6;
        var annex = new byte[2_000_000];
        var pos = 0;
        var lastY = Array.Empty<byte>();
        using var enc = new H264BaselineEncoder(
            w, h, new H264BaselineEncoderOptions { QuantizationParameter = 18, KeyframeIntervalFrames = 60 });
        for (var f = 0; f < frames; f++)
        {
            var (y, u, v) = MakeCheckerboard(w, h, shift: f * 3);
            pos += enc.EncodeFrame(y, u, v, w, w / 2, annex.AsSpan(pos));
            lastY = y;
        }

        var raw = DecodeAllFramesExact(annex.AsSpan(0, pos), w, h, frames);
        var lastFrame = raw.AsSpan((frames - 1) * (w * h * 3 / 2), w * h * 3 / 2).ToArray();
        AssertBoundaryBandPsnr(lastFrame, lastY, w, h, minDb: 32.0);
    }

    /// <summary>Multi-slice at 1080p: coded mbH = 68 does not divide evenly, exercising the remainder path.</summary>
    [Fact]
    public void Ffmpeg_decodes_multi_slice_1080p_at_exactly_1920x1080()
    {
        if (!FfmpegOnPath())
        {
            return;
        }

        const int w = 1920;
        const int h = 1080;
        var (y, u, v) = MakeCheckerboard(w, h);
        var annex = new byte[w * h * 4];
        using var enc = new H264BaselineEncoder(
            w, h,
            new H264BaselineEncoderOptions { LevelIdc = 40, QuantizationParameter = 18, SliceCount = 4 });
        var n = enc.EncodeFrame(y, u, v, w, w / 2, annex);

        var raw = DecodeAllFramesExact(annex.AsSpan(0, n), w, h, frameCount: 1);
        AssertBoundaryBandPsnr(raw, y, w, h, minDb: 32.0);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private sealed record BaselineSps(
        int MbWidth, int MbHeight, bool FrameCroppingFlag,
        uint CropLeft, uint CropRight, uint CropTop, uint CropBottom);

    /// <summary>Parse a Baseline (profile 66) SPS RBSP through the frame-cropping block (§7.3.2.1.1).</summary>
    private static BaselineSps ParseBaselineSps(byte[] rbsp)
    {
        var br = new H264CavlcSpecDecode.BitReader(rbsp);
        br.ReadBits(8).Should().Be(66); // profile_idc — Baseline: no chroma_format_idc block follows
        br.ReadBits(8); // constraint_set0..5 + reserved_zero_2bits
        br.ReadBits(8); // level_idc
        ReadUe(br); // seq_parameter_set_id
        ReadUe(br); // log2_max_frame_num_minus4
        ReadUe(br).Should().Be(2u); // pic_order_cnt_type = 2 → no further POC syntax
        ReadUe(br); // max_num_ref_frames
        br.ReadBit(); // gaps_in_frame_num_value_allowed_flag
        var mbW = (int)ReadUe(br) + 1; // pic_width_in_mbs_minus1
        var mbH = (int)ReadUe(br) + 1; // pic_height_in_map_units_minus1
        br.ReadBit().Should().Be(1); // frame_mbs_only_flag → map units are MBs, no mb_adaptive flag
        br.ReadBit(); // direct_8x8_inference_flag
        var cropping = br.ReadBit() == 1; // frame_cropping_flag
        uint left = 0, right = 0, top = 0, bottom = 0;
        if (cropping)
        {
            left = ReadUe(br);
            right = ReadUe(br);
            top = ReadUe(br);
            bottom = ReadUe(br);
        }

        return new BaselineSps(mbW, mbH, cropping, left, right, top, bottom);
    }

    /// <summary>ue(v) Exp-Golomb (§9.1).</summary>
    private static uint ReadUe(H264CavlcSpecDecode.BitReader br)
    {
        var zeros = 0;
        while (br.ReadBit() == 0)
        {
            zeros++;
        }

        return zeros == 0 ? 0u : (uint)((1 << zeros) - 1 + br.ReadBits(zeros));
    }

    /// <summary>First NAL of <paramref name="nalType"/> in an Annex B stream, EBSP-unescaped to RBSP.</summary>
    private static byte[]? ExtractNalRbsp(ReadOnlySpan<byte> annexB, int nalType)
    {
        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            var scLen = 0;
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                scLen = 3;
            }
            else if (i + 4 < annexB.Length
                && annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
            {
                scLen = 4;
            }

            if (scLen == 0)
            {
                continue;
            }

            var hdr = i + scLen;
            if ((annexB[hdr] & 0x1F) != nalType)
            {
                i = hdr; // skip past the header; scan resumes for the next start code
                continue;
            }

            var end = annexB.Length;
            for (var j = hdr + 1; j + 2 < annexB.Length; j++)
            {
                if (annexB[j] == 0 && annexB[j + 1] == 0 && (annexB[j + 2] == 1 || (j + 3 < annexB.Length && annexB[j + 2] == 0 && annexB[j + 3] == 1)))
                {
                    end = j;
                    break;
                }
            }

            // EBSP → RBSP: drop emulation_prevention_three_byte after 00 00 (§7.4.1).
            var rbsp = new List<byte>(end - hdr - 1);
            var zeroRun = 0;
            for (var j = hdr + 1; j < end; j++)
            {
                var b = annexB[j];
                if (zeroRun >= 2 && b == 3)
                {
                    zeroRun = 0;
                    continue;
                }

                zeroRun = b == 0 ? zeroRun + 1 : 0;
                rbsp.Add(b);
            }

            return [.. rbsp];
        }

        return null;
    }

    /// <summary>
    /// 8×8-cell checkerboard with a per-cell luma detail dot — structured content that makes wrongly
    /// visible replicated padding stand out (a gradient would make replication indistinguishable
    /// from a correct encode).
    /// </summary>
    private static (byte[] Y, byte[] U, byte[] V) MakeCheckerboard(int w, int h, int shift = 0)
    {
        var y = new byte[w * h];
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                var cell = (((row + shift) / 8) + ((col + shift) / 8)) & 1;
                y[row * w + col] = cell != 0 ? (byte)200 : (byte)60;
            }
        }

        var cw = w / 2;
        var ch = h / 2;
        var u = new byte[cw * ch];
        var v = new byte[cw * ch];
        for (var row = 0; row < ch; row++)
        {
            for (var col = 0; col < cw; col++)
            {
                var cell = (((row * 2 + shift) / 8) + ((col * 2 + shift) / 8)) & 1;
                u[row * cw + col] = cell != 0 ? (byte)96 : (byte)160;
                v[row * cw + col] = cell != 0 ? (byte)160 : (byte)96;
            }
        }

        return (y, u, v);
    }

    private static bool FfmpegOnPath()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            if (!p.WaitForExit(10_000))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                return false;
            }

            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Decode <paramref name="frameCount"/> frames to raw yuv420p and assert the output is
    /// <em>exactly</em> display-sized: <c>w*h*3/2</c> per frame. (The smoke tests' helper uses a
    /// <c>&lt;</c> check that an uncropped stream would still pass — exact equality is the point here.)
    /// </summary>
    private static byte[] DecodeAllFramesExact(ReadOnlySpan<byte> annexB, int w, int h, int frameCount)
    {
        var input = Path.Combine(Path.GetTempPath(), $"proxeno-crop-{Guid.NewGuid():N}.h264");
        var rawOut = Path.Combine(Path.GetTempPath(), $"proxeno-crop-{Guid.NewGuid():N}.raw");
        try
        {
            File.WriteAllBytes(input, annexB);
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-threads");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("h264");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(input);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add(frameCount.ToString());
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("-pix_fmt");
            psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add(rawOut);

            using var p = Process.Start(psi);
            p.Should().NotBeNull();
            p!.WaitForExit(60_000).Should().BeTrue("ffmpeg decode must not hang");
            var err = p.StandardError.ReadToEnd();
            p.ExitCode.Should().Be(0, $"ffmpeg must decode the cropped stream cleanly. stderr:{Environment.NewLine}{err}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(err, "cropped Annex B decode");

            var raw = File.ReadAllBytes(rawOut);
            raw.Length.Should().Be(checked(w * h * 3 / 2 * frameCount),
                "the decoder's raw output must be exactly display-sized — an uncropped stream would be coded-sized");
            return raw;
        }
        finally
        {
            TryDelete(input);
            TryDelete(rawOut);
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// PSNR over the rightmost and bottommost 16 <em>visible</em> luma lines only. A global PSNR
    /// barely moves when just the crop-adjacent rows are wrong; the band isolates deblocking bleed
    /// across the crop edge and padding leaking back through ME.
    /// </summary>
    private static void AssertBoundaryBandPsnr(byte[] decodedI420, byte[] sourceY, int w, int h, double minDb)
    {
        double sumSq = 0;
        long count = 0;
        for (var row = 0; row < h; row++)
        {
            var inBottomBand = row >= h - 16;
            for (var col = 0; col < w; col++)
            {
                if (!inBottomBand && col < w - 16)
                {
                    continue;
                }

                double d = decodedI420[row * w + col] - sourceY[row * w + col];
                sumSq += d * d;
                count++;
            }
        }

        var mse = sumSq / count;
        var psnr = mse <= 0 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
        psnr.Should().BeGreaterThan(minDb,
            $"boundary-band luma PSNR (right/bottom 16 visible lines) must stay clean; got {psnr:F2} dB");
    }
}
