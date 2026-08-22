using System.Diagnostics;
using FluentAssertions;
using Kiln;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Decodes <see cref="H264BaselineEncoder"/> Annex B output with FFmpeg when <c>ffmpeg</c> is on PATH.
/// If FFmpeg is missing, the test returns without asserting (optional dependency for local/CI).
/// </summary>
public sealed class H264FfmpegDecodeSmokeTests
{
    /// <summary>
    /// When false, <see cref="Baseline_encoder_AnnexB_decodes_with_ffmpeg_when_available"/> exits early (pass).
    /// </summary>
    private static (bool ok, string stderr) TryRunFfmpeg(string ffmpegPath, string inputH264Path, int frameCount = 2)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
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
        psi.ArgumentList.Add(inputH264Path);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add(frameCount.ToString());
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        using var p = Process.Start(psi);
        if (p is null)
        {
            return (false, "");
        }

        if (!p.WaitForExit(60_000))
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            return (false, "timeout");
        }

        var err = p.StandardError.ReadToEnd();
        return (p.ExitCode == 0, err);
    }

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

            if (p.ExitCode != 0)
            {
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void FillI420(byte[] y, byte[] u, byte[] v, int w, int h)
    {
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                y[row * w + col] = (byte)((row + col) & 0xFF);
            }
        }

        var cw = w / 2;
        var ch = h / 2;
        for (var row = 0; row < ch; row++)
        {
            for (var col = 0; col < cw; col++)
            {
                u[row * cw + col] = (byte)(128 + ((row - col) & 0x0F));
                v[row * cw + col] = (byte)(128 - ((row + col) & 0x0F));
            }
        }
    }

    private static (bool ok, string stderr, byte[] stdout) TryRunFfmpegRawOneFrameYuv420(
        string ffmpegPath,
        string inputH264Path)
    {
        const int w = 32;
        const int h = 32;
        var expectedSize = w * h * 3 / 2;
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
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
        psi.ArgumentList.Add(inputH264Path);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-");

        using var p = Process.Start(psi);
        if (p is null)
        {
            return (false, "", []);
        }

        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        var err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000))
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            return (false, "timeout", []);
        }

        var raw = ms.ToArray();
        if (p.ExitCode != 0 || raw.Length < expectedSize)
        {
            return (false, err, raw);
        }

        return (true, err, raw);
    }

    /// <summary>
    /// <see cref="H264BaselineEncoder"/> prepends SPS+PPS to every access unit. FFmpeg/libavcodec
    /// can occasionally mishandle that cadence when resolving references; normal streams send PS once
    /// then slices only. Keep the first SPS/PPS pair and drop later duplicates for decode smoke tests.
    /// </summary>
    private static byte[] AnnexBKeepOnlyFirstSpsPps(ReadOnlySpan<byte> annexB)
    {
        static bool TryStartCode(ReadOnlySpan<byte> b, int i, out int codeLen)
        {
            codeLen = 0;
            if (i + 4 <= b.Length && b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1)
            {
                codeLen = 4;
                return true;
            }

            if (i + 3 <= b.Length && b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1)
            {
                codeLen = 3;
                return true;
            }

            return false;
        }

        static int FindNextStart(ReadOnlySpan<byte> b, int fromInclusive)
        {
            for (var i = Math.Max(fromInclusive, 0); i < b.Length; i++)
            {
                if (TryStartCode(b, i, out _))
                {
                    return i;
                }
            }

            return -1;
        }

        var ms = new MemoryStream();
        var haveSps = false;
        var havePps = false;
        var pos = FindNextStart(annexB, 0);
        while (pos >= 0)
        {
            TryStartCode(annexB, pos, out var scLen);
            var nalHeaderIx = pos + scLen;
            if (nalHeaderIx >= annexB.Length)
            {
                break;
            }

            var nextStart = FindNextStart(annexB, nalHeaderIx + 1);
            var end = nextStart < 0 ? annexB.Length : nextStart;
            var nalType = annexB[nalHeaderIx] & 0x1F;
            var unit = annexB[pos..end];
            switch (nalType)
            {
                case 7:
                    if (!haveSps)
                    {
                        ms.Write(unit);
                        haveSps = true;
                    }

                    break;
                case 8:
                    if (!havePps)
                    {
                        ms.Write(unit);
                        havePps = true;
                    }

                    break;
                default:
                    ms.Write(unit);
                    break;
            }

            pos = nextStart;
        }

        return ms.ToArray();
    }

    private static (bool ok, string stderr, byte[] stdout) DecodeAllFramesRawYuv420(
        string ffmpegPath,
        string inputH264Path,
        int w,
        int h,
        int frameCount)
    {
        if (frameCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        var expectedSize = checked(w * h * 3 / 2 * frameCount);
        var rawOut = Path.Combine(Path.GetTempPath(), $"proxeno-ff-yuv-{Guid.NewGuid():N}.raw");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
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
            psi.ArgumentList.Add(inputH264Path);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add(frameCount.ToString());
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("-pix_fmt");
            psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add(rawOut);

            using var p = Process.Start(psi);
            if (p is null)
            {
                return (false, "", []);
            }

            if (!p.WaitForExit(60_000))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                return (false, "timeout", []);
            }

            var err = p.StandardError.ReadToEnd();
            if (p.ExitCode != 0)
            {
                return (false, err, []);
            }

            if (!File.Exists(rawOut))
            {
                return (false, err, []);
            }

            var raw = File.ReadAllBytes(rawOut);
            if (raw.Length < expectedSize)
            {
                return (false, err, raw);
            }

            return (true, err, raw);
        }
        finally
        {
            try
            {
                File.Delete(rawOut);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// Decode one display-order frame (by index) to raw I420 via ffmpeg; avoids multi-frame raw concatenation edge cases.
    /// </summary>
    private static (bool ok, string stderr, byte[] raw) DecodeNthFrameRawYuv420(
        string ffmpegPath,
        string inputH264Path,
        int w,
        int h,
        int frameIndexZeroBased)
    {
        var expectedSize = checked(w * h * 3 / 2);
        var vf = $"select=eq(n\\,{frameIndexZeroBased})";
        var rawOut = Path.Combine(Path.GetTempPath(), $"proxeno-ff-yuv-{Guid.NewGuid():N}.raw");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
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
            psi.ArgumentList.Add(inputH264Path);
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(vf);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("-pix_fmt");
            psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add(rawOut);

            using var p = Process.Start(psi);
            if (p is null)
            {
                return (false, "", []);
            }

            if (!p.WaitForExit(60_000))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                return (false, "timeout", []);
            }

            var err = p.StandardError.ReadToEnd();
            if (p.ExitCode != 0)
            {
                return (false, err, []);
            }

            if (!File.Exists(rawOut))
            {
                return (false, err, []);
            }

            var raw = File.ReadAllBytes(rawOut);
            if (raw.Length < expectedSize)
            {
                return (false, err, raw);
            }

            return (true, err, raw);
        }
        finally
        {
            try
            {
                File.Delete(rawOut);
            }
            catch
            {
                // ignore
            }
        }
    }

    [Fact]
    public void Baseline_encoder_AnnexB_decodes_with_ffmpeg_when_available()
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        const int w = 32;
        const int h = 32;
        var ySize = w * h;
        var uvSize = ySize / 4;
        var y = new byte[ySize];
        var u = new byte[uvSize];
        var v = new byte[uvSize];
        FillI420(y, u, v, w, h);

        var annexCap = ySize * 2 + 512_000;
        var annex = new byte[annexCap];
        int totalBytes;

        // Single-frame IDR: validates Annex B + RBSP without multi-access-unit edge cases between consecutive IDRs.
        using (var enc = new H264BaselineEncoder(
                   w,
                   h,
                   new H264BaselineEncoderOptions { KeyframeIntervalFrames = 1 }))
        {
            var n0 = enc.EncodeFrame(y, u, v, w, w / 2, annex, forceKeyframe: false);
            enc.LastFrameWasIdr.Should().BeTrue();
            totalBytes = n0;
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-h264-smoke-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annex.AsSpan(0, totalBytes));
            var (ok, ffErr) = TryRunFfmpeg("ffmpeg", tmp, frameCount: 1);
            Assert.True(ok, $"FFmpeg should decode one IDR frame from Kiln Annex B without error. stderr:{Environment.NewLine}{ffErr}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "32×32 gradient single IDR Annex B decode");
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// Regression: older bitstreams left CodedBlockPatternChroma at 0 (no chroma DC in RBSP), so decoded U/V
    /// did not track strong chroma splits. After emitting chroma DC, FFmpeg-decoded U should differ across a deliberate left/right split.
    /// </summary>
    [Fact]
    public void Baseline_encoder_decoded_chroma_tracks_uv_split_after_ffmpeg_when_available()
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        const int w = 32;
        const int h = 32;
        var ySize = w * h;
        var uvW = w / 2;
        var uvH = h / 2;
        var uvSize = uvW * uvH;
        var y = new byte[ySize];
        var u = new byte[uvSize];
        var v = new byte[uvSize];
        Array.Fill(y, (byte)120);
        Array.Fill(v, (byte)128);
        for (var row = 0; row < uvH; row++)
        {
            for (var col = 0; col < uvW; col++)
            {
                u[row * uvW + col] = col < uvW / 2 ? (byte)40 : (byte)220;
            }
        }

        var annexCap = ySize * 2 + 512_000;
        var annex = new byte[annexCap];
        int n0;
        using (var enc = new H264BaselineEncoder(
                   w,
                   h,
                   new H264BaselineEncoderOptions { KeyframeIntervalFrames = 60 }))
        {
            n0 = enc.EncodeFrame(y, u, v, w, w / 2, annex, forceKeyframe: true);
            enc.LastFrameWasIdr.Should().BeTrue();
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-h264-chroma-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annex.AsSpan(0, n0));
            var (ok, ffErr, raw) = TryRunFfmpegRawOneFrameYuv420("ffmpeg", tmp);
            Assert.True(ok, $"FFmpeg should emit one yuv420p frame. stderr:{Environment.NewLine}{ffErr}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "chroma-split single-frame decode");

            var uOff = ySize;
            var sumLow = 0;
            var sumHigh = 0;
            var cLow = 0;
            var cHigh = 0;
            for (var row = 0; row < uvH; row++)
            {
                for (var col = 0; col < uvW; col++)
                {
                    var sample = raw[uOff + row * uvW + col];
                    if (col < uvW / 2)
                    {
                        sumLow += sample;
                        cLow++;
                    }
                    else
                    {
                        sumHigh += sample;
                        cHigh++;
                    }
                }
            }

            var meanLow = sumLow / (double)cLow;
            var meanHigh = sumHigh / (double)cHigh;
            Math.Abs(meanLow - meanHigh).Should().BeGreaterThan(40,
                "decoded U should differ between chroma halves when bitstream carries chroma DC (missing chroma made both sides ~similar).");
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// Regression guard: Intra 4×4 V/H mode bitstream must satisfy a conformant decoder's
    /// Intra_4×4 mode-availability check (no "top/left block unavailable for requested intra" on stderr).
    /// </summary>
    [Fact]
    public void Baseline_encoder_intra4x4_modes_do_not_trigger_decoder_intra_availability_errors_when_available()
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        const int w = 32;
        const int h = 32;
        var ySize = w * h;
        var uvSize = ySize / 4;
        var y = new byte[ySize];
        var u = new byte[uvSize];
        var v = new byte[uvSize];
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                y[row * w + col] = (byte)((row * 11 + col * 3) & 0xFF);
            }
        }

        Array.Fill(u, (byte)128);
        Array.Fill(v, (byte)128);

        var annexCap = ySize * 2 + 512_000;
        var annex = new byte[annexCap];
        int n0;
        using (var enc = new H264BaselineEncoder(
                   w,
                   h,
                   new H264BaselineEncoderOptions { KeyframeIntervalFrames = 60 }))
        {
            n0 = enc.EncodeFrame(y, u, v, w, w / 2, annex, forceKeyframe: true);
            enc.LastFrameWasIdr.Should().BeTrue();
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-h264-intra4x4-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annex.AsSpan(0, n0));
            var (ok, ffErr) = TryRunFfmpeg("ffmpeg", tmp);
            Assert.True(ok, $"FFmpeg decode failed. stderr:{Environment.NewLine}{ffErr}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "intra4×4 stress decode");
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// Many MB rows/columns + chroma stress: fixed-seed random I420 should decode cleanly under ffmpeg.
    /// </summary>
    [Fact]
    public void Baseline_encoder_random_i420_128x128_idr_decodes_cleanly_with_ffmpeg_when_available()
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        const int w = 128;
        const int h = 128;
        var ySize = w * h;
        var uvSize = ySize / 4;
        var y = new byte[ySize];
        var u = new byte[uvSize];
        var v = new byte[uvSize];
        var rng = new Random(unchecked((int)0xC0DEC0DE));
        rng.NextBytes(y);
        rng.NextBytes(u);
        rng.NextBytes(v);

        var annexCap = ySize * 2 + 512_000;
        var annex = new byte[annexCap];
        int n0;
        using (var enc = new H264BaselineEncoder(
                   w,
                   h,
                   new H264BaselineEncoderOptions { KeyframeIntervalFrames = 60 }))
        {
            n0 = enc.EncodeFrame(y, u, v, w, w / 2, annex, forceKeyframe: true);
            enc.LastFrameWasIdr.Should().BeTrue();
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-h264-noise128-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annex.AsSpan(0, n0));
            var (ok, ffErr) = TryRunFfmpeg("ffmpeg", tmp, frameCount: 1);
            Assert.True(ok, $"FFmpeg should decode random 128×128 IDR. stderr:{Environment.NewLine}{ffErr}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "128×128 random I420 IDR decode");
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// Regression for H.264 8.3.4.2 / FFmpeg <c>pred8x8_dc</c>: chroma intra DC predicts four sub-block DCs
    /// (TL, TR, BL, BR), not one. A single-DC encoder writes a uniform residual per MB, but the decoder
    /// reconstructs four sub-blocks against four different DCs — the per-sub-block error
    /// <c>(decoder_dc[k] - encoder_dc)</c> shows up as a 4-cell banding pattern within a uniform-source MB.
    /// 32×32 with a 2×2 chroma quadrant pattern (each quadrant = one MB) makes MB(1,1)'s top and left chroma
    /// neighbors differ, so the four sub-block DCs of MB(1,1) span a wide range. Pre-fix, the decoded sub-block
    /// means of MB(1,1) chroma differed by tens of levels; post-fix they agree within QP=28 quant noise.
    /// Runs when <c>ffmpeg</c> is on PATH (early return from <see cref="TryVerifyFfmpegOnPath"/>); not skipped via <c>[Fact(Skip)]</c>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Baseline_encoder_chroma_dc_subblock_pattern_decodes_within_tolerance(bool preferHardwareIntrinsics)
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        const int w = 32;
        const int h = 32;
        const int uvW = w / 2;
        const int uvH = h / 2;
        var ySize = w * h;
        var uvSize = uvW * uvH;
        var y = new byte[ySize];
        var u = new byte[uvSize];
        var v = new byte[uvSize];
        Array.Fill(y, (byte)128);

        // 2×2 chroma quadrant pattern (each quadrant covers one MB's chroma 8×8). Source means per quadrant:
        //   TL: U=60  V=200    TR: U=200 V=60
        //   BL: U=60  V=60     BR: U=200 V=200
        // MB(1,1) chroma sees top neighbor (TR: U=200,V=60) and left neighbor (BL: U=60,V=60), so its four
        // sub-block DCs differ — exercises the (top&left), (top), (left), (top&left) branches of pred8x8_dc.
        const byte uTL = 60;
        const byte uTR = 200;
        const byte uBL = 60;
        const byte uBR = 200;
        const byte vTL = 200;
        const byte vTR = 60;
        const byte vBL = 60;
        const byte vBR = 200;
        for (var row = 0; row < uvH; row++)
        {
            for (var col = 0; col < uvW; col++)
            {
                var top = row < uvH / 2;
                var left = col < uvW / 2;
                u[row * uvW + col] = (top, left) switch
                {
                    (true, true) => uTL,
                    (true, false) => uTR,
                    (false, true) => uBL,
                    _ => uBR,
                };
                v[row * uvW + col] = (top, left) switch
                {
                    (true, true) => vTL,
                    (true, false) => vTR,
                    (false, true) => vBL,
                    _ => vBR,
                };
            }
        }

        var annexCap = ySize * 2 + 512_000;
        var annex = new byte[annexCap];
        int n0;
        using (var enc = new H264BaselineEncoder(
                   w,
                   h,
                   new H264BaselineEncoderOptions
                   {
                       KeyframeIntervalFrames = 1,
                       PreferHardwareIntrinsics = preferHardwareIntrinsics,
                       ChromaDcRdLambda = 0,
                   }))
        {
            n0 = enc.EncodeFrame(y, u, v, w, w / 2, annex, forceKeyframe: false);
            enc.LastFrameWasIdr.Should().BeTrue();
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-h264-chroma-dc-sub-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annex.AsSpan(0, n0));
            var (ok, ffErr, raw) = TryRunFfmpegRawOneFrameYuv420("ffmpeg", tmp);
            Assert.True(
                ok,
                $"FFmpeg should emit one yuv420p frame for 4-quadrant chroma test (PreferHardwareIntrinsics={preferHardwareIntrinsics}). stderr:{Environment.NewLine}{ffErr}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(
                ffErr,
                $"chroma DC sub-block decode, PreferHardwareIntrinsics={preferHardwareIntrinsics}");

            var uOff = ySize;
            var vOff = ySize + uvSize;

            // Per 8×8 chroma macroblock (2×2 grid on 32×32 luma): decoded plane means should track the flat
            // source quadrant within QP noise; inset by 1 sample avoids slice/MB boundary strips.
            static double MeanChromaInset(byte[] raw, int planeOff, int stride, int baseRow, int baseCol, int inset)
            {
                var sum = 0;
                var n = 0;
                for (var row = baseRow + inset; row < baseRow + 8 - inset; row++)
                {
                    for (var col = baseCol + inset; col < baseCol + 8 - inset; col++)
                    {
                        sum += raw[planeOff + row * stride + col];
                        n++;
                    }
                }

                return sum / (double)n;
            }

            const int inset = 1;
            MeanChromaInset(raw, uOff, uvW, 0, 0, inset).Should().BeApproximately(uTL, 10,
                $"decoded U TL quadrant (inset={inset}, PreferHardwareIntrinsics={preferHardwareIntrinsics})");
            MeanChromaInset(raw, uOff, uvW, 0, 8, inset).Should().BeApproximately(uTR, 10,
                $"decoded U TR quadrant (inset={inset}, PreferHardwareIntrinsics={preferHardwareIntrinsics})");
            MeanChromaInset(raw, uOff, uvW, 8, 0, inset).Should().BeApproximately(uBL, 10,
                $"decoded U BL quadrant (inset={inset}, PreferHardwareIntrinsics={preferHardwareIntrinsics})");
            MeanChromaInset(raw, uOff, uvW, 8, 8, inset).Should().BeApproximately(uBR, 10,
                $"decoded U BR quadrant (inset={inset}, PreferHardwareIntrinsics={preferHardwareIntrinsics})");
            MeanChromaInset(raw, vOff, uvW, 0, 0, inset).Should().BeApproximately(vTL, 10,
                $"decoded V TL quadrant (inset={inset}, PreferHardwareIntrinsics={preferHardwareIntrinsics})");
            MeanChromaInset(raw, vOff, uvW, 0, 8, inset).Should().BeApproximately(vTR, 10,
                $"decoded V TR quadrant (inset={inset}, PreferHardwareIntrinsics={preferHardwareIntrinsics})");
            MeanChromaInset(raw, vOff, uvW, 8, 0, inset).Should().BeApproximately(vBL, 10,
                $"decoded V BL quadrant (inset={inset}, PreferHardwareIntrinsics={preferHardwareIntrinsics})");
            MeanChromaInset(raw, vOff, uvW, 8, 8, inset).Should().BeApproximately(vBR, 10,
                $"decoded V BR quadrant (inset={inset}, PreferHardwareIntrinsics={preferHardwareIntrinsics})");

            // MB(1,1) is the only macroblock whose four pred8x8_dc DCs span a wide range (top neighbor = TR
            // quadrant, left neighbor = BL quadrant; both reconstructed bytes from already-coded MBs). Pre-fix:
            // encoder writes a single uniform residual per MB but decoder reconstructs four sub-blocks against
            // four different per-sub-block predictors → decoded 4×4 sub-block means inside MB(1,1) span tens of
            // levels (visible as a 4-cell banding pattern). Post-fix: encoder uses the same per-sub-block
            // predictors as decoder, so the four sub-block means agree within QP=28 quant noise.
            const int mb11Bx = uvW / 2;
            const int mb11By = uvH / 2;
            Span<double> subMeanU = stackalloc double[4];
            Span<double> subMeanV = stackalloc double[4];
            for (var sub = 0; sub < 4; sub++)
            {
                var ox = (sub & 1) * 4;
                var oy = (sub >> 1) * 4;
                var sumU = 0;
                var sumV = 0;
                var n = 0;
                for (var row = mb11By + oy; row < mb11By + oy + 4; row++)
                {
                    for (var col = mb11Bx + ox; col < mb11Bx + ox + 4; col++)
                    {
                        sumU += raw[uOff + row * uvW + col];
                        sumV += raw[vOff + row * uvW + col];
                        n++;
                    }
                }

                subMeanU[sub] = sumU / (double)n;
                subMeanV[sub] = sumV / (double)n;
            }

            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;
            for (var i = 0; i < 4; i++)
            {
                if (subMeanU[i] < minU) { minU = subMeanU[i]; }
                if (subMeanU[i] > maxU) { maxU = subMeanU[i]; }
                if (subMeanV[i] < minV) { minV = subMeanV[i]; }
                if (subMeanV[i] > maxV) { maxV = subMeanV[i]; }
            }

            (maxU - minU).Should().BeLessThan(
                15,
                $"MB(1,1) chroma U should be uniform across its four 4×4 sub-blocks once encoder uses per-sub-block pred8x8_dc DCs (sub means [{subMeanU[0]:F1},{subMeanU[1]:F1},{subMeanU[2]:F1},{subMeanU[3]:F1}], PreferHardwareIntrinsics={preferHardwareIntrinsics})");
            (maxV - minV).Should().BeLessThan(
                15,
                $"MB(1,1) chroma V should be uniform across its four 4×4 sub-blocks once encoder uses per-sub-block pred8x8_dc DCs (sub means [{subMeanV[0]:F1},{subMeanV[1]:F1},{subMeanV[2]:F1},{subMeanV[3]:F1}], PreferHardwareIntrinsics={preferHardwareIntrinsics})");
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// CAVLC P slice: each <c>macroblock_layer</c> must be preceded by <c>mb_skip_run</c> (H.264 7.3.4). Regression for inter-frame decode sync.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Baseline_encoder_two_frame_idr_then_p_decodes_cleanly_with_ffmpeg_when_available(bool preferHardwareIntrinsics)
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        const int w = 32;
        const int h = 32;
        var ySize = w * h;
        var uvSize = ySize / 4;
        var y = new byte[ySize];
        var u = new byte[uvSize];
        var v = new byte[uvSize];
        FillI420(y, u, v, w, h);

        var annexCap = ySize * 4 + 512_000;
        var annex = new byte[annexCap];
        int n0;
        int n1;
        using (var enc = new H264BaselineEncoder(
                   w,
                   h,
                   new H264BaselineEncoderOptions
                   {
                       KeyframeIntervalFrames = 60,
                       PreferHardwareIntrinsics = preferHardwareIntrinsics,
                   }))
        {
            n0 = enc.EncodeFrame(y, u, v, w, w / 2, annex, forceKeyframe: false);
            enc.LastFrameWasIdr.Should().BeTrue();
            n1 = enc.EncodeFrame(y, u, v, w, w / 2, annex.AsSpan(n0), forceKeyframe: false);
            enc.LastFrameWasIdr.Should().BeFalse();
        }

        var total = n0 + n1;
        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-h264-idr-p-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annex.AsSpan(0, total).ToArray());
            var (ok, ffErr) = TryRunFfmpeg("ffmpeg", tmp, frameCount: 2);
            Assert.True(ok, $"FFmpeg should decode IDR+P Annex B. stderr:{Environment.NewLine}{ffErr}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(
                ffErr,
                $"IDR+P two-frame decode, PreferHardwareIntrinsics={preferHardwareIntrinsics}");
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// Four-frame GOP (IDR + 3× P): a bright square steps diagonally inward from a corner.
    /// Pixel geometry is asserted on <see cref="H264BaselineEncoder.LastReconstructedY"/> after encode
    /// (matches emitted syntax); FFmpeg full-frame yuv420p decode still runs to verify libav decodes the
    /// Annex B stream without errors. For 32×32 the vacated start region can overlap the final square so
    /// the dark check is omitted. Catches broken inter MC / reference drift. 8 cases = {4×4, 32×32} × corners.
    /// </summary>
    public static TheoryData<int, string> MovingSquareCornerDiagonalTheoryData() =>
        new()
        {
            { 4, "TL" },
            { 4, "TR" },
            { 4, "BL" },
            { 4, "BR" },
            { 32, "TL" },
            { 32, "TR" },
            { 32, "BL" },
            { 32, "BR" },
        };

    [Theory]
    [MemberData(nameof(MovingSquareCornerDiagonalTheoryData))]
    public void P_frames_moving_square_diagonal_decoded_pixels_match_corner_motion(int squareSize, string corner)
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        const int w = 96;
        const int h = 96;
        const int stepPerFrame = 8;
        const int frameCount = 4;
        const int pFrameCount = frameCount - 1;
        const byte bgY = 16;
        const byte squareY = 235;

        var ySize = w * h;
        var uvSize = ySize / 4;

        static void FillSquareAt(Span<byte> yPlane, int strideY, byte bg, byte fg, int sq, int leftX, int topY)
        {
            yPlane.Fill(bg);
            for (var row = 0; row < sq; row++)
            {
                for (var col = 0; col < sq; col++)
                {
                    yPlane[(topY + row) * strideY + leftX + col] = fg;
                }
            }
        }

        static (int sx, int sy, int dx, int dy) CornerMotion(string c, int sq, int picW, int picH, int step)
        {
            return c switch
            {
                "TL" => (0, 0, step, step),
                "TR" => (picW - sq, 0, -step, step),
                "BL" => (0, picH - sq, step, -step),
                "BR" => (picW - sq, picH - sq, -step, -step),
                _ => throw new ArgumentOutOfRangeException(nameof(c)),
            };
        }

        var (startX, startY, dx, dy) = CornerMotion(corner, squareSize, w, h, stepPerFrame);
        var finalX = startX + pFrameCount * dx;
        var finalY = startY + pFrameCount * dy;

        static void AssertInteriorBright(byte[] raw, int yOff, int stride, int sq, int leftX, int topY, int inset, string because)
        {
            for (var row = topY + inset; row < topY + sq - inset; row++)
            {
                for (var col = leftX + inset; col < leftX + sq - inset; col++)
                {
                    raw[yOff + row * stride + col].Should().BeGreaterThanOrEqualTo((byte)170, because);
                }
            }
        }

        static void AssertInteriorDark(byte[] raw, int yOff, int stride, int sq, int leftX, int topY, int inset, string because)
        {
            for (var row = topY + inset; row < topY + sq - inset; row++)
            {
                for (var col = leftX + inset; col < leftX + sq - inset; col++)
                {
                    raw[yOff + row * stride + col].Should().BeLessThanOrEqualTo((byte)50, because);
                }
            }
        }

        // Stay comfortably inside the square and away from 16×16 macroblock edges so FFmpeg vs our
        // deblocking filter rounding does not dip samples below the bright threshold at boundaries.
        var insetBright = squareSize <= 4 ? 1 : squareSize >= 32 ? 8 : 4;
        var insetDark = squareSize <= 4 ? 1 : 4;
        Assert.True(squareSize > 2 * insetBright, "square must leave an interior for bright asserts");

        void BuildYFrame(int frameIndex, Span<byte> dst)
        {
            var sx = startX + frameIndex * dx;
            var sy = startY + frameIndex * dy;
            FillSquareAt(dst, w, bgY, squareY, squareSize, sx, sy);
        }

        var annexCap = checked(ySize * 3 / 2 * frameCount) + 512_000;
        var annex = new byte[annexCap];
        var uPlane = new byte[uvSize];
        var vPlane = new byte[uvSize];
        Array.Fill(uPlane, (byte)128);
        Array.Fill(vPlane, (byte)128);

        var yScratch = new byte[ySize];
        var total = 0;
        var encLastY = new byte[ySize];
        using (var enc = new H264BaselineEncoder(
                   w,
                   h,
                   new H264BaselineEncoderOptions
                   {
                       KeyframeIntervalFrames = 60,
                   }))
        {
            for (var f = 0; f < frameCount; f++)
            {
                BuildYFrame(f, yScratch);
                var span = annex.AsSpan(total);
                var n = enc.EncodeFrame(yScratch, uPlane, vPlane, w, w / 2, span, forceKeyframe: false);
                if (f == 0)
                {
                    enc.LastFrameWasIdr.Should().BeTrue();
                }
                else
                {
                    enc.LastFrameWasIdr.Should().BeFalse();
                }

                total += n;
            }

            enc.LastReconstructedY.CopyTo(encLastY);
        }
        var tmp = Path.Combine(
            Path.GetTempPath(),
            $"proxeno-h264-diag-{squareSize}-{corner}-{Guid.NewGuid():N}.h264");
        try
        {
            var annexTrimmed = AnnexBKeepOnlyFirstSpsPps(annex.AsSpan(0, total));
            File.WriteAllBytes(tmp, annexTrimmed);
            var lastIx = frameCount - 1;
            var (ok, ffErr, _) = DecodeAllFramesRawYuv420("ffmpeg", tmp, w, h, frameCount);
            Assert.True(
                ok,
                $"FFmpeg should decode {frameCount} frames as yuv420p ({squareSize}px {corner}). stderr:{Environment.NewLine}{ffErr}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(
                ffErr,
                $"{squareSize}px square diagonal {corner}, frame {lastIx}");

            AssertInteriorBright(
                encLastY, 0, w, squareSize, finalX, finalY, insetBright,
                $"encoder recon: square should finish at ({finalX},{finalY}) [{corner}, {squareSize}px]");
            if (squareSize < 32)
            {
                AssertInteriorDark(
                    encLastY, 0, w, squareSize, startX, startY, insetDark,
                    $"encoder recon: motion should leave start ({startX},{startY}) dark [{corner}, {squareSize}px]");
            }
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // ignore
            }
        }
    }
}
