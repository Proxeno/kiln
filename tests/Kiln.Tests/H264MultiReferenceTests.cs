using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Kiln;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Guard-rail tests for multi-frame H.264 encoding. The encoder currently uses a single
/// reference frame (DPB depth=2 is allocated but only slot 0 is searched). Tests verify
/// slice-header conformance, 3-frame bitstream decodability, and encoder-decoder parity.
/// ffmpeg-dependent tests return without asserting if ffmpeg is not on PATH.
/// </summary>
public sealed class H264MultiReferenceTests
{
    private const int W = 32;
    private const int H = 32;

    private static bool StrictParityEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("KILN_H264_ENCODER_DECODER_PARITY"),
            "1",
            StringComparison.OrdinalIgnoreCase);

    // ── content builders ─────────────────────────────────────────────────────────────────────────

    private static void FillFlat(byte[] y, byte[] u, byte[] v, byte luma, byte chroma)
    {
        Array.Fill(y, luma);
        Array.Fill(u, chroma);
        Array.Fill(v, chroma);
    }

    private static void FillGradient(byte[] y, byte[] u, byte[] v, int w, int h)
    {
        var uvW = w / 2;
        var uvH = h / 2;
        for (var row = 0; row < h; row++)
            for (var col = 0; col < w; col++)
                y[row * w + col] = (byte)((row * 7 + col * 5) & 0xFF);
        for (var row = 0; row < uvH; row++)
            for (var col = 0; col < uvW; col++)
            {
                u[row * uvW + col] = col < uvW / 2 ? (byte)60 : (byte)200;
                v[row * uvW + col] = row < uvH / 2 ? (byte)60 : (byte)200;
            }
    }

    private static H264BaselineEncoder MakeEncoder(bool preferHw = false, bool enableIntraInP = true) =>
        new(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 20,
            KeyframeIntervalFrames = 60,
            LightweightDeblocking = true,
            PreferRealtimeLatencyTuning = true,
            PreferHardwareIntrinsics = preferHw,
            EnableIntraInPFallback = enableIntraInP,
        });

    // ── test 1: slice header bitstream conformance ───────────────────────────────────────────────

    /// <summary>
    /// Parses the P-slice header of frame 3 in an IDR+P+P sequence and verifies that
    /// <c>num_ref_idx_active_override_flag</c> is 1 and <c>num_ref_idx_l0_active_minus1</c> is 1.
    /// When DPB has two entries the encoder must signal the override so decoders read the 1-bit
    /// Te(v) ref_idx_l0 field per inter MB. A missing or incorrect flag causes every inter MB's
    /// ref_idx to be mis-decoded.
    /// </summary>
    [Fact]
    public void SliceHeader_emits_ref_idx_override_flag_when_dpb_has_two_refs()
    {
        var ySize = W * H;
        var uvSize = ySize / 4;
        var yA = new byte[ySize]; var uA = new byte[uvSize]; var vA = new byte[uvSize];
        var yB = new byte[ySize]; var uB = new byte[uvSize]; var vB = new byte[uvSize];
        FillFlat(yA, uA, vA, luma: 80, chroma: 128);
        FillFlat(yB, uB, vB, luma: 160, chroma: 128);

        var annex = new byte[ySize * 8 + 512_000];

        using var enc = MakeEncoder();
        var n0 = enc.EncodeFrame(yA, uA, vA, W, W / 2, annex, forceKeyframe: false);
        var n1 = enc.EncodeFrame(yB, uB, vB, W, W / 2, annex.AsSpan(n0), forceKeyframe: false);
        var n2 = enc.EncodeFrame(yA, uA, vA, W, W / 2, annex.AsSpan(n0 + n1), forceKeyframe: false);

        var nals = AnnexBExtractNalUnits(annex.AsSpan(0, n0 + n1 + n2));

        // NAL layout expected: SPS(7), PPS(8), IDR-slice(5), P-slice(1), P-slice(1).
        // Find the second non-IDR P-slice (nal_unit_type=1, frame 2 — DpbCount is now 2).
        var pSlices = nals.Where(n => n.NalUnitType == 1).ToList();
        pSlices.Should().HaveCountGreaterThanOrEqualTo(2,
            "expected at least 2 non-IDR P-slice NAL units (frames 1 and 2)");

        var frame2Rbsp = pSlices[1].Rbsp;
        var br = new H264CavlcSpecDecode.BitReader(frame2Rbsp);

        // Slice header fields (H.264 §7.3.3, Baseline):
        ReadUe(br); // first_mb_in_slice
        ReadUe(br); // slice_type
        ReadUe(br); // pic_parameter_set_id
        br.ReadBits(H264ParameterSets.Log2MaxFrameNumMinus4 + 4); // frame_num
        // pic_order_cnt_type=2, non-IDR: no POC syntax element written

        var overrideFlag = br.ReadBit();
        overrideFlag.Should().Be(1,
            "when DPB has 2 entries the encoder must set num_ref_idx_active_override_flag=1 " +
            "so decoders read num_ref_idx_l0_active_minus1 and interpret 1-bit Te(v) ref_idx per MB");

        var numRefIdxActiveMinus1 = ReadUe(br);
        numRefIdxActiveMinus1.Should().Be(1u,
            "encoder signals two references (DPB depth 2), so num_ref_idx_l0_active_minus1 must be 1");
    }

    // ── test 2: ref1 wins repeated content (regression gate for temporal seed fix) ────────────────

    /// <summary>
    /// Guard rail for the multi-reference temporal seed fix. Frame 3 is pixel-identical to frame 1
    /// (stored in DPB slot 1); a correctly-seeded ref1 search finds near-zero SAD and produces a
    /// trivially small bitstream (P-skip or tiny residual). In single-reference mode or with a
    /// poorly-seeded ref1 search, ref0 (= reconstructed frame 2, luma ~160) wins instead, the
    /// ~80-luma residual is large, and the encoded size is comparable to the scene-change frame.
    /// </summary>
    [Fact]
    public void Ref1_wins_when_content_repeats_and_ref1_holds_matching_frame()
    {
        var ySize = W * H;
        var uvSize = ySize / 4;
        var yA = new byte[ySize]; var uA = new byte[uvSize]; var vA = new byte[uvSize];
        var yB = new byte[ySize]; var uB = new byte[uvSize]; var vB = new byte[uvSize];
        FillFlat(yA, uA, vA, luma: 80, chroma: 128);
        FillFlat(yB, uB, vB, luma: 160, chroma: 128);

        var annex = new byte[ySize * 16 + 512_000];

        // Pure-inter encode: this test asserts the ref1 ME mechanism via frame sizes. The flat
        // scene-change frame (n1) codes more cheaply as Intra_16×16, which would shrink n1 and break
        // the n2 < n1/4 ratio that proves ref1 reuse — so disable the intra-in-P fallback here.
        using var enc = MakeEncoder(enableIntraInP: false);
        var n0 = enc.EncodeFrame(yA, uA, vA, W, W / 2, annex, forceKeyframe: false);
        enc.LastFrameWasIdr.Should().BeTrue();
        var n1 = enc.EncodeFrame(yB, uB, vB, W, W / 2, annex.AsSpan(n0), forceKeyframe: false);
        var n2 = enc.EncodeFrame(yA, uA, vA, W, W / 2, annex.AsSpan(n0 + n1), forceKeyframe: false);

        // n1 encodes a full scene change (luma 80→160), so it carries a large residual.
        // n2 revisits luma 80; DPB slot 1 holds the reconstructed IDR. With a correctly-seeded
        // ref1 search, near-zero SAD collapses this to P-skip / minimal residual.
        n1.Should().BeGreaterThan(128, "scene-change P-frame must carry substantial residual");
        n2.Should().BeLessThan(n1 / 4,
            $"frame 3 (repeat of IDR content) must be far smaller than the scene-change frame " +
            $"when ref1 is searched correctly; got n1={n1} n2={n2}");
    }

    // ── test 3: alternating 4-frame PSNR gate ────────────────────────────────────────────────────

    /// <summary>
    /// Encodes IDR(A)+P(B)+P(A)+P(B) at low QP and verifies that frames 2 and 3 (revisiting
    /// previously-seen content) achieve PSNR &gt; 42 dB. At QP=15 and with correct ref1 temporal
    /// seeding, these frames are near-lossless reproductions from DPB slot 1. A PSNR below the
    /// threshold indicates ref1 is still losing the ME competition or the bitstream is mis-decoded.
    /// Skipped if ffmpeg is not on PATH.
    /// </summary>
    [Fact]
    public void Alternating_4_frame_sequence_achieves_high_psnr_on_repeated_content()
    {
        if (!TryVerifyFfmpegOnPath()) return;

        const int Frames = 4;
        const int Qp = 15;
        var ySize = W * H;
        var uvSize = ySize / 4;
        var frameBytes = ySize + 2 * uvSize;

        var yA = new byte[ySize]; var uA = new byte[uvSize]; var vA = new byte[uvSize];
        var yB = new byte[ySize]; var uB = new byte[uvSize]; var vB = new byte[uvSize];
        FillGradient(yA, uA, vA, W, H);
        FillFlat(yB, uB, vB, luma: 180, chroma: 100);

        var annex = new byte[ySize * 16 + 512_000];

        byte[][] srcsY = [yA, yB, yA, yB];
        byte[][] srcsU = [uA, uB, uA, uB];
        byte[][] srcsV = [vA, vB, vA, vB];
        var totalBytes = 0;

        using (var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = Qp,
            KeyframeIntervalFrames = 60,
            LightweightDeblocking = true,
            PreferRealtimeLatencyTuning = true,
        }))
        {
            for (var f = 0; f < Frames; f++)
                totalBytes += enc.EncodeFrame(srcsY[f], srcsU[f], srcsV[f], W, W / 2, annex.AsSpan(totalBytes), forceKeyframe: false);
        }

        var (ok, ffErr, raw) = FfmpegDecodeRawYuv420MultiFrame(annex.AsSpan(0, totalBytes).ToArray(), W, H, Frames);
        Assert.True(ok, $"ffmpeg decode failed. stderr:{Environment.NewLine}{ffErr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "4-frame alternating PSNR decode");

        raw.Length.Should().BeGreaterThanOrEqualTo(Frames * frameBytes);

        // Frames 2 and 3 revisit previously-seen content (in DPB slot 1). With correct ref1
        // temporal seeding at QP=15, PSNR must exceed 42 dB.
        for (var f = 2; f < Frames; f++)
        {
            var dec = raw.AsSpan(f * frameBytes);
            var src = srcsY[f];
            var psnr = ComputePsnr(src, dec[..ySize].ToArray(), ySize);
            psnr.Should().BeGreaterThan(42.0,
                $"frame {f} (repeat of content {(f % 2 == 0 ? "A" : "B")}) must reconstruct with PSNR > 42 dB at QP={Qp}; " +
                $"low PSNR indicates ref1 temporal seed fix is not working or slice header is mis-decoded");
        }
    }

    // ── test 4: ffmpeg smoke — 3-frame sequence ───────────────────────────────────────────────────

    /// <summary>
    /// Encodes IDR + P + P (3 frames with varied content to exercise the P-frame reference chain)
    /// and verifies the Annex B bitstream decodes without errors via ffmpeg. Skipped if ffmpeg is
    /// not on PATH.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Three_frame_idr_p_p_decodes_without_ffmpeg_errors(bool preferHardwareIntrinsics)
    {
        if (!TryVerifyFfmpegOnPath()) return;

        var ySize = W * H;
        var uvSize = ySize / 4;
        var yA = new byte[ySize]; var uA = new byte[uvSize]; var vA = new byte[uvSize];
        var yB = new byte[ySize]; var uB = new byte[uvSize]; var vB = new byte[uvSize];
        FillGradient(yA, uA, vA, W, H);
        FillFlat(yB, uB, vB, luma: 180, chroma: 128);

        var annex = new byte[ySize * 8 + 512_000];

        using (var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = 60,
            LightweightDeblocking = true,
            PreferRealtimeLatencyTuning = true,
            PreferHardwareIntrinsics = preferHardwareIntrinsics,
        }))
        {
            var n0 = enc.EncodeFrame(yA, uA, vA, W, W / 2, annex, forceKeyframe: false);
            enc.LastFrameWasIdr.Should().BeTrue();
            var n1 = enc.EncodeFrame(yB, uB, vB, W, W / 2, annex.AsSpan(n0), forceKeyframe: false);
            var n2 = enc.EncodeFrame(yA, uA, vA, W, W / 2, annex.AsSpan(n0 + n1), forceKeyframe: false);
            var total = n0 + n1 + n2;

            var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-h264-multref-smoke-{Guid.NewGuid():N}.h264");
            try
            {
                File.WriteAllBytes(tmp, annex.AsSpan(0, total).ToArray());
                var (ok, ffErr) = RunFfmpegNullSink(tmp, frameCount: 3);
                Assert.True(ok, $"ffmpeg decode of 3-frame multi-ref sequence failed. stderr:{Environment.NewLine}{ffErr}");
                H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "3-frame IDR+P+P decode");
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    // ── test 5: encoder-decoder reconstruction parity ────────────────────────────────────────────

    /// <summary>
    /// Encodes 3 frames, stores the encoder's per-frame internal reconstruction, then
    /// decodes the Annex B bitstream with ffmpeg and compares per-MB. Strict mode
    /// (env KILN_H264_ENCODER_DECODER_PARITY=1) fails on first divergence. Without
    /// the env var the test verifies only that decode succeeds and stderr is clean.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Encoder_decoder_reconstruction_parity_for_3_frame_multi_ref_sequence(bool preferHardwareIntrinsics)
    {
        if (!TryVerifyFfmpegOnPath()) return;

        const int Frames = 3;
        var ySize = W * H;
        var uvSize = ySize / 4;
        var frameBytes = ySize + 2 * uvSize;

        var yA = new byte[ySize]; var uA = new byte[uvSize]; var vA = new byte[uvSize];
        var yB = new byte[ySize]; var uB = new byte[uvSize]; var vB = new byte[uvSize];
        FillGradient(yA, uA, vA, W, H);
        FillFlat(yB, uB, vB, luma: 80, chroma: 128);

        var annex = new byte[ySize * 8 + 512_000];
        var reconY = new byte[Frames][];
        var reconU = new byte[Frames][];
        var reconV = new byte[Frames][];
        var totalBytes = 0;

        using (var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = 60,
            LightweightDeblocking = true,
            PreferRealtimeLatencyTuning = true,
            PreferHardwareIntrinsics = preferHardwareIntrinsics,
        }))
        {
            byte[][] srcsY = [yA, yB, yA];
            byte[][] srcsU = [uA, uB, uA];
            byte[][] srcsV = [vA, vB, vA];
            for (var f = 0; f < Frames; f++)
            {
                var n = enc.EncodeFrame(srcsY[f], srcsU[f], srcsV[f], W, W / 2, annex.AsSpan(totalBytes), forceKeyframe: false);
                reconY[f] = enc.LastReconstructedY.ToArray();
                reconU[f] = enc.LastReconstructedU.ToArray();
                reconV[f] = enc.LastReconstructedV.ToArray();
                totalBytes += n;
            }
        }

        var (ok, ffErr, raw) = FfmpegDecodeRawYuv420MultiFrame(annex.AsSpan(0, totalBytes).ToArray(), W, H, Frames);
        Assert.True(ok, $"ffmpeg decode of 3-frame multi-ref sequence failed. stderr:{Environment.NewLine}{ffErr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "3-frame parity decode");

        if (!StrictParityEnabled) return;

        raw.Length.Should().BeGreaterThanOrEqualTo(Frames * frameBytes);
        for (var f = 0; f < Frames; f++)
        {
            var dec = raw.AsSpan(f * frameBytes, frameBytes);
            var div = FindFirstDivergence(
                reconY[f], reconU[f], reconV[f],
                dec[..ySize], dec[ySize..(ySize + uvSize)], dec[(ySize + uvSize)..],
                W, H, frameIndex: f);
            if (div is not null)
                Assert.Fail($"Frame {f} reconstruction != ffmpeg decode, PreferHardwareIntrinsics={preferHardwareIntrinsics}.\n{div.DetailText}");
        }
    }

    // ── NAL unit extraction ───────────────────────────────────────────────────────────────────────

    private sealed record NalUnit(byte NalUnitType, byte[] Rbsp);

    private static List<NalUnit> AnnexBExtractNalUnits(ReadOnlySpan<byte> annexB)
    {
        // Collect start-code offsets (00 00 00 01 or 00 00 01).
        var starts = new List<int>();
        for (var i = 0; i < annexB.Length - 3; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
            {
                starts.Add(i + 4);
                i += 3;
            }
            else if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                starts.Add(i + 3);
                i += 2;
            }
        }

        var result = new List<NalUnit>();
        for (var k = 0; k < starts.Count; k++)
        {
            var nalStart = starts[k];
            var nalEnd = k + 1 < starts.Count ? FindPreceedingZeros(starts[k + 1], annexB) : annexB.Length;
            if (nalEnd <= nalStart) continue;

            var nalUnitType = (byte)(annexB[nalStart] & 0x1F);
            var rawRbsp = annexB.Slice(nalStart + 1, nalEnd - nalStart - 1);

            // Strip emulation prevention bytes (00 00 03 → 00 00).
            var rbsp = new List<byte>(rawRbsp.Length);
            for (var i = 0; i < rawRbsp.Length; i++)
            {
                if (i + 2 < rawRbsp.Length && rawRbsp[i] == 0 && rawRbsp[i + 1] == 0 && rawRbsp[i + 2] == 3)
                {
                    rbsp.Add(0); rbsp.Add(0);
                    i += 2;
                }
                else
                {
                    rbsp.Add(rawRbsp[i]);
                }
            }

            result.Add(new NalUnit(nalUnitType, rbsp.ToArray()));
        }
        return result;

        static int FindPreceedingZeros(int startAfterCode, ReadOnlySpan<byte> buf)
        {
            var pos = startAfterCode - 4;
            while (pos > 0 && buf[pos - 1] == 0) pos--;
            return pos;
        }
    }

    // ── slice header Exp-Golomb ───────────────────────────────────────────────────────────────────

    private static uint ReadUe(H264CavlcSpecDecode.BitReader br)
    {
        var zeros = 0;
        while (br.ReadBit() == 0) zeros++;
        if (zeros == 0) return 0;
        return (uint)((1 << zeros) - 1 + br.ReadBits(zeros));
    }

    private static double ComputePsnr(byte[] source, byte[] decoded, int count)
    {
        double sse = 0;
        for (var i = 0; i < count; i++) { var d = source[i] - decoded[i]; sse += d * d; }
        if (sse == 0) return double.PositiveInfinity;
        return 10.0 * Math.Log10(255.0 * 255.0 * count / sse);
    }

    // ── ffmpeg helpers ────────────────────────────────────────────────────────────────────────────

    private static bool TryVerifyFfmpegOnPath()
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
            return p is not null && p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static (bool ok, string stderr) RunFfmpegNullSink(string inputPath, int frameCount)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-threads"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("h264");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add(frameCount.ToString());
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");
        using var p = Process.Start(psi);
        if (p is null) return (false, "");
        if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch { } return (false, "timeout"); }
        return (p.ExitCode == 0, p.StandardError.ReadToEnd());
    }

    private static (bool ok, string stderr, byte[] raw) FfmpegDecodeRawYuv420MultiFrame(byte[] annexB, int w, int h, int frameCount)
    {
        var expected = frameCount * w * h * 3 / 2;
        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-h264-multref-par-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annexB);
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-threads"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("h264");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add(frameCount.ToString());
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-");
            using var p = Process.Start(psi);
            if (p is null) return (false, "process did not start", []);
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch { } return (false, "timeout", []); }
            var raw = ms.ToArray();
            return p.ExitCode != 0 || raw.Length < expected ? (false, err, raw) : (true, err, raw);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // ── per-MB divergence finder (mirrors H264EncoderDecoderParityBisectTests) ─────────────────

    private sealed record DivergenceReport(int FrameIndex, int MbX, int MbY, string DetailText);

    private static DivergenceReport? FindFirstDivergence(
        ReadOnlySpan<byte> encY, ReadOnlySpan<byte> encU, ReadOnlySpan<byte> encV,
        ReadOnlySpan<byte> decY, ReadOnlySpan<byte> decU, ReadOnlySpan<byte> decV,
        int w, int h, int frameIndex)
    {
        var uvW = w / 2;
        var mbW = w / 16;
        var mbH = h / 16;
        Span<byte> eL = stackalloc byte[256]; Span<byte> dL = stackalloc byte[256];
        Span<byte> eU = stackalloc byte[64];  Span<byte> dU = stackalloc byte[64];
        Span<byte> eV = stackalloc byte[64];  Span<byte> dV = stackalloc byte[64];

        for (var mby = 0; mby < mbH; mby++)
        {
            for (var mbx = 0; mbx < mbW; mbx++)
            {
                CopyBlock(encY, w, mbx * 16, mby * 16, 16, eL);
                CopyBlock(decY, w, mbx * 16, mby * 16, 16, dL);
                CopyBlock(encU, uvW, mbx * 8, mby * 8, 8, eU);
                CopyBlock(decU, uvW, mbx * 8, mby * 8, 8, dU);
                CopyBlock(encV, uvW, mbx * 8, mby * 8, 8, eV);
                CopyBlock(decV, uvW, mbx * 8, mby * 8, 8, dV);

                var maxL = 0; var maxU = 0; var maxV = 0;
                for (var i = 0; i < 256; i++) maxL = Math.Max(maxL, Math.Abs(eL[i] - dL[i]));
                for (var i = 0; i < 64; i++) { maxU = Math.Max(maxU, Math.Abs(eU[i] - dU[i])); maxV = Math.Max(maxV, Math.Abs(eV[i] - dV[i])); }

                if (maxL == 0 && maxU == 0 && maxV == 0) continue;

                var sb = new StringBuilder();
                sb.AppendLine($"frame={frameIndex} mb=({mbx},{mby}) maxAbs: luma={maxL} U={maxU} V={maxV}");
                sb.AppendLine("Luma 16x16 enc/dec/delta:");
                for (var r = 0; r < 16; r++)
                {
                    sb.Append($"r{r:D2}: ");
                    for (var c = 0; c < 16; c++) { var i = r * 16 + c; sb.Append($"{eL[i]:D3}/{dL[i]:D3}/{(int)eL[i] - dL[i]:+0;-#;0} "); }
                    sb.AppendLine();
                }
                return new DivergenceReport(frameIndex, mbx, mby, sb.ToString());
            }
        }
        return null;
    }

    private static void CopyBlock(ReadOnlySpan<byte> plane, int stride, int ox, int oy, int size, Span<byte> dst)
    {
        for (var r = 0; r < size; r++)
            plane.Slice((oy + r) * stride + ox, size).CopyTo(dst.Slice(r * size, size));
    }
}
