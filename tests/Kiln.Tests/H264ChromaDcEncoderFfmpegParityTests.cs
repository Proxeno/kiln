using System.Diagnostics;
using FluentAssertions;
using Kiln;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Diagnoses chroma DC / recon alignment: encoder <see cref="H264BaselineSliceEncoder.ReconstructedUPlane"/> after
/// <see cref="H264BaselineSliceEncoder.EncodeSliceRbsp"/> should match FFmpeg’s decoded U/V per 4×4 sub-block mean
/// inside MB(0,0) for the quadrant pattern used in <see cref="H264FfmpegDecodeSmokeTests"/>.
/// </summary>
public sealed class H264ChromaDcEncoderFfmpegParityTests
{
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
            return p?.WaitForExit(10_000) == true && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (bool ok, string stderr, byte[] raw) TryRunFfmpegRawOneFrameYuv420(string h264Path, int w, int h)
    {
        var expected = w * h * 3 / 2;
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("h264");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(h264Path);
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
        return (p.ExitCode == 0 && raw.Length >= expected, err, raw);
    }

    private static void FillQuadrantChromaPattern(Span<byte> u, Span<byte> v, int uvW, int uvH)
    {
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
    }

    private static double SubBlockMean(ReadOnlySpan<byte> plane, int stride, int baseRow, int baseCol, int sub)
    {
        var ox = (sub & 1) * 4;
        var oy = (sub >> 1) * 4;
        var sum = 0;
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                sum += plane[(baseRow + oy + row) * stride + baseCol + ox + col];
            }
        }

        return sum / 16.0;
    }

    /// <summary>
    /// If FFmpeg decode and encoder recon diverge (wrong DC sign, scaling, or neighbor recon), sub-block means
    /// for MB(0,0) U drift before later MBs are considered.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Encoder_chroma_plane_recon_matches_ffmpeg_mb00_per_subblock(bool preferHardwareIntrinsics)
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
        FillQuadrantChromaPattern(u, v, uvW, uvH);

        var slice = new H264BaselineSliceEncoder(w, h, qp: 28, chromaDcRdLambda: 0);
        ReadOnlySpan<byte> encU;
        ReadOnlySpan<byte> encV;
        using (new H264IntrinsicsPreference.Scope(preferHardwareIntrinsics))
        {
            _ = slice.EncodeSliceRbsp(y, w, u, v, w / 2, isIdr: true, isPslice: false, frameNum: 0, idrPicId: 0);
            encU = slice.ReconstructedUPlane;
            encV = slice.ReconstructedVPlane;
        }

        var annex = new byte[ySize * 2 + 512_000];
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
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-chroma-parity-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annex.AsSpan(0, n0));
            var (ok, ffErr, raw) = TryRunFfmpegRawOneFrameYuv420(tmp, w, h);
            Assert.True(ok, $"FFmpeg decode failed: {ffErr}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, "chroma MB(0,0) recon parity");

            var decU = raw.AsSpan(ySize, uvSize);
            var decV = raw.AsSpan(ySize + uvSize, uvSize);

            for (var sub = 0; sub < 4; sub++)
            {
                var encMeanU = SubBlockMean(encU, uvW, 0, 0, sub);
                var decMeanU = SubBlockMean(decU, uvW, 0, 0, sub);
                var encMeanV = SubBlockMean(encV, uvW, 0, 0, sub);
                var decMeanV = SubBlockMean(decV, uvW, 0, 0, sub);

                Math.Abs(encMeanU - decMeanU).Should().BeLessThan(1.5,
                    $"U MB(0,0) sub{sub} mean enc={encMeanU:F2} dec={decMeanU:F2}, intrinsics={preferHardwareIntrinsics}");
                Math.Abs(encMeanV - decMeanV).Should().BeLessThan(1.5,
                    $"V MB(0,0) sub{sub} mean enc={encMeanV:F2} dec={decMeanV:F2}, intrinsics={preferHardwareIntrinsics}");
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
