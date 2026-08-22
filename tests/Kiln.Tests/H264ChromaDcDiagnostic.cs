using System.Diagnostics;
using Kiln;
using Kiln.Internal.H264;
using Xunit.Abstractions;

namespace Kiln.Tests;

public sealed class H264ChromaDcDiagnostic
{
    private readonly ITestOutputHelper _output;

    public H264ChromaDcDiagnostic(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Compare_encoder_recon_vs_ffmpeg_decoded_for_failing_chroma_pattern()
    {
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

        // Use the slice encoder directly so we can read _recU/_recV after encode.
        var slice = new H264BaselineSliceEncoder(w, h, qp: 28, chromaDcRdLambda: 0);
        var rbsp = slice.EncodeSliceRbsp(y, w, u, v, w / 2, isIdr: true, isPslice: false, frameNum: 0, idrPicId: 0);
        _output.WriteLine($"slice rbsp bytes={rbsp.Length}");

        var encU = slice.ReconstructedUPlane.ToArray();
        var encV = slice.ReconstructedVPlane.ToArray();

        // Wrap RBSP in Annex B with SPS+PPS so ffmpeg can decode it.
        using var enc = new H264BaselineEncoder(
            w,
            h,
            new H264BaselineEncoderOptions
            {
                KeyframeIntervalFrames = 1,
                PreferHardwareIntrinsics = false,
                ChromaDcRdLambda = 0,
            });
        var annex = new byte[ySize * 2 + 512_000];
        var n0 = enc.EncodeFrame(y, u, v, w, w / 2, annex, forceKeyframe: false);

        var path = Path.Combine(Path.GetTempPath(), "proxeno-diag-chroma-dc.h264");
        File.WriteAllBytes(path, annex.AsSpan(0, n0).ToArray());
        _output.WriteLine($"wrote {n0} Annex B bytes to {path}");

        var (ok, ffErr, raw) = TryRunFfmpeg(path, w, h);
        _output.WriteLine($"ffmpeg ok={ok} stderr=\"{ffErr.Trim()}\"");
        if (!ok) { return; }

        var decU = raw.AsSpan(ySize, uvSize).ToArray();
        var decV = raw.AsSpan(ySize + uvSize, uvSize).ToArray();

        // Mean per chroma 8x8 macroblock (2x2 grid).
        for (var mby = 0; mby < 2; mby++)
        {
            for (var mbx = 0; mbx < 2; mbx++)
            {
                var (encMeanU, decMeanU, srcMeanU) = MeanU(encU, decU, u, mbx, mby, uvW);
                var (encMeanV, decMeanV, srcMeanV) = MeanU(encV, decV, v, mbx, mby, uvW);
                _output.WriteLine($"MB({mbx},{mby}) U  src={srcMeanU,6:F1} encRecon={encMeanU,6:F1} ffmpegDec={decMeanU,6:F1}   V  src={srcMeanV,6:F1} encRecon={encMeanV,6:F1} ffmpegDec={decMeanV,6:F1}");
            }
        }

        // Per-sub-block mean for MB(0,0) U.
        for (var sub = 0; sub < 4; sub++)
        {
            var ox = (sub & 1) * 4;
            var oy = (sub >> 1) * 4;
            var encSum = 0;
            var decSum = 0;
            for (var rr = 0; rr < 4; rr++)
            {
                for (var cc = 0; cc < 4; cc++)
                {
                    encSum += encU[(oy + rr) * uvW + (ox + cc)];
                    decSum += decU[(oy + rr) * uvW + (ox + cc)];
                }
            }
            _output.WriteLine($"MB(0,0) U sub{sub} encRecon={encSum / 16.0,6:F1} ffmpegDec={decSum / 16.0,6:F1}");
        }
    }

    private static (double enc, double dec, double src) MeanU(
        byte[] enc, byte[] dec, byte[] src, int mbx, int mby, int stride)
    {
        var bx = mbx * 8;
        var by = mby * 8;
        var encSum = 0;
        var decSum = 0;
        var srcSum = 0;
        for (var rr = 0; rr < 8; rr++)
        {
            for (var cc = 0; cc < 8; cc++)
            {
                var off = (by + rr) * stride + (bx + cc);
                encSum += enc[off];
                decSum += dec[off];
                srcSum += src[off];
            }
        }
        return (encSum / 64.0, decSum / 64.0, srcSum / 64.0);
    }

    private static (bool ok, string stderr, byte[] raw) TryRunFfmpeg(string h264Path, int w, int h)
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
        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // ffmpeg isn't installed/on PATH in this environment; the caller treats !ok as a
            // graceful no-op for this diagnostic test.
            return (false, ex.Message, []);
        }

        if (p is null) { return (false, "", []); }
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode == 0 && ms.Length >= w * h * 3 / 2, err, ms.ToArray());
    }
}
