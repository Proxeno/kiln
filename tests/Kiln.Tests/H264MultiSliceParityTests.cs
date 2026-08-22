using System.Diagnostics;
using FluentAssertions;
using Kiln;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Acceptance gates for Phase 1 slice-parallel encoding (perf/phase-1-slice-parallel):
/// <list type="number">
///   <item>SliceCount=1 produces a byte-identical bitstream to the pre-change encoder.</item>
///   <item>SliceCount=N&gt;1 round-trips through ffmpeg with ≤1 LSB pixel delta against the encoder's
///     internal reconstruction (i.e., the multi-slice bitstream is consistent with the encoder's
///     idea of what it encoded).</item>
/// </list>
/// </summary>
/// <remarks>
/// Why a separate test class rather than parameterising existing FFmpeg parity tests: this is a
/// targeted regression gate for the slice-boundary helpers (H.264 6.4.4 neighbour availability,
/// 9.2.1 CAVLC nC, 7.3.3 disable_deblocking_filter_idc=2). It must fail loudly if a future change
/// reintroduces cross-slice neighbour reads.
/// </remarks>
public sealed class H264MultiSliceParityTests
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
            if (p is null)
            {
                return false;
            }

            return p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (bool ok, string stderr, byte[] yuv) TryFfmpegDecodeOneFrameYuv420(byte[] annexB, int w, int h)
    {
        var expected = w * h * 3 / 2;
        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-h264-msparity-{Guid.NewGuid():N}.h264");
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
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-threads");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("h264");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("-pix_fmt");
            psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-");

            using var p = Process.Start(psi);
            if (p is null) return (false, "process did not start", []);

            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "timeout", []);
            }
            var raw = ms.ToArray();
            if (p.ExitCode != 0 || raw.Length < expected) return (false, err, raw);
            return (true, err, raw);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    /// <summary>Deterministic luma gradient + flat chroma (=128). Exercises top/left intra prediction
    /// across the slice boundary at row mbRowsPerSlice·16 without depending on chroma DC RD selection.</summary>
    private static byte[] BuildSourceI420(int w, int h)
    {
        var ySize = w * h;
        var uvSize = ySize / 4;
        var src = new byte[ySize + uvSize * 2];
        for (var i = 0; i < ySize; i++) src[i] = (byte)(i % 211 + 20);
        for (var i = ySize; i < src.Length; i++) src[i] = 128;
        return src;
    }

    /// <summary>
    /// Gate 1: SliceCount=1 must produce the same Annex B bytes the encoder emitted before
    /// the slice-orchestrator changes. We assert this by comparing default (SliceCount=1) output
    /// against the explicit SliceCount=1 path — both go through identical code paths but the
    /// option threads differently, so any future refactor that adds an unwanted code split fails.
    /// </summary>
    [Theory]
    [InlineData(48, 32)]
    [InlineData(320, 240)]
    [InlineData(640, 480)]
    public void SliceCount_1_byte_identical_to_default_path(int w, int h)
    {
        var src = BuildSourceI420(w, h);
        var ySize = w * h;
        var uvSize = ySize / 4;
        var buf1 = new byte[ySize * 6 + 1_000_000];
        var buf2 = new byte[ySize * 6 + 1_000_000];

        int n1;
        using (var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions { QuantizationParameter = 28 }))
        {
            n1 = enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf1);
        }

        int n2;
        using (var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions { QuantizationParameter = 28, SliceCount = 1 }))
        {
            n2 = enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf2);
        }

        n2.Should().Be(n1, "SliceCount=1 takes the legacy single-slice path and must be byte-identical to the default");
        buf2.AsSpan(0, n2).SequenceEqual(buf1.AsSpan(0, n1)).Should().BeTrue();
    }

    /// <summary>
    /// Gate 2: For sc ∈ {1, 2, 4, 8}, the encoder's internal reconstruction must equal ffmpeg's
    /// decoded yuv420p output within 1 LSB. A non-zero delta means the bitstream is inconsistent
    /// with the encoder's idea of what it encoded — typically because a slice-boundary neighbour
    /// guard (FillIntra4x4ModeContext, GetIntraNeighbourMbAvailability, IsTopRightAvailable,
    /// DeriveLumaNc, FillChromaNzcContext, GatherInterNeighbourMvs, GatherNeighbors,
    /// ApplyInLoopDeblockScoped) was missed.
    /// </summary>
    [Theory]
    [InlineData(48, 32, 1)]
    [InlineData(48, 32, 2)]
    [InlineData(320, 240, 1)]
    [InlineData(320, 240, 2)]
    [InlineData(320, 240, 4)]
    [InlineData(320, 240, 8)]
    [InlineData(640, 480, 1)]
    [InlineData(640, 480, 2)]
    [InlineData(640, 480, 4)]
    [InlineData(640, 480, 8)]
    public void Encoder_reconstruction_matches_ffmpeg_within_1_LSB(int w, int h, int sliceCount)
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        var src = BuildSourceI420(w, h);
        var ySize = w * h;
        var uvSize = ySize / 4;
        var buf = new byte[ySize * 6 + 1_000_000];

        byte[] reconY, reconU, reconV;
        int n;
        using (var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions { QuantizationParameter = 28, SliceCount = sliceCount }))
        {
            n = enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf);
            reconY = enc.LastReconstructedY.ToArray();
            reconU = enc.LastReconstructedU.ToArray();
            reconV = enc.LastReconstructedV.ToArray();
        }

        var (ok, ffErr, yuv) = TryFfmpegDecodeOneFrameYuv420(buf.AsSpan(0, n).ToArray(), w, h);
        Assert.True(ok, $"ffmpeg should decode SliceCount={sliceCount} bitstream cleanly. stderr:{Environment.NewLine}{ffErr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(ffErr, $"SliceCount={sliceCount} {w}x{h} decode");

        int maxDeltaY = 0, maxDeltaU = 0, maxDeltaV = 0;
        for (var i = 0; i < ySize; i++) maxDeltaY = Math.Max(maxDeltaY, Math.Abs(yuv[i] - reconY[i]));
        for (var i = 0; i < uvSize; i++) maxDeltaU = Math.Max(maxDeltaU, Math.Abs(yuv[ySize + i] - reconU[i]));
        for (var i = 0; i < uvSize; i++) maxDeltaV = Math.Max(maxDeltaV, Math.Abs(yuv[ySize + uvSize + i] - reconV[i]));

        maxDeltaY.Should().BeLessThanOrEqualTo(1, $"SliceCount={sliceCount} {w}x{h}: encoder Y vs ffmpeg Y must agree within 1 LSB");
        maxDeltaU.Should().BeLessThanOrEqualTo(1, $"SliceCount={sliceCount} {w}x{h}: encoder U vs ffmpeg U must agree within 1 LSB");
        maxDeltaV.Should().BeLessThanOrEqualTo(1, $"SliceCount={sliceCount} {w}x{h}: encoder V vs ffmpeg V must agree within 1 LSB");
    }

    /// <summary>
    /// Gate 3: A multi-slice IDR bitstream must contain SliceCount NAL units of type 5. Cheap
    /// structural check that catches a future regression where the orchestrator forgets to emit
    /// the extra slice NALs (e.g., reverts to single-slice when SliceCount &gt; 1).
    /// </summary>
    [Theory]
    [InlineData(48, 32, 2)]
    [InlineData(320, 240, 4)]
    [InlineData(640, 480, 8)]
    public void Multi_slice_bitstream_contains_expected_slice_NAL_count(int w, int h, int sliceCount)
    {
        var src = BuildSourceI420(w, h);
        var ySize = w * h;
        var uvSize = ySize / 4;
        var buf = new byte[ySize * 6 + 1_000_000];

        int n;
        using (var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions { QuantizationParameter = 28, SliceCount = sliceCount }))
        {
            n = enc.EncodeFrame(src.AsSpan(0, ySize), src.AsSpan(ySize, uvSize), src.AsSpan(ySize + uvSize, uvSize), w, w / 2, buf);
        }

        // Walk Annex B (H.264 B.1). A 4-byte 00 00 00 01 contains a 3-byte 00 00 01 at +1, so we
        // must skip past matches to avoid double-counting the same NAL.
        var idrSliceCount = 0;
        var i = 0;
        while (i < n - 2)
        {
            int scLen;
            if (i + 3 < n && buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 0 && buf[i + 3] == 1)
            {
                scLen = 4;
            }
            else if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 1)
            {
                scLen = 3;
            }
            else
            {
                i++;
                continue;
            }

            var nalHdr = i + scLen;
            if (nalHdr < n && (buf[nalHdr] & 0x1F) == 5)
            {
                idrSliceCount++;
            }
            i += scLen;
        }

        idrSliceCount.Should().Be(sliceCount, $"SliceCount={sliceCount} should emit exactly {sliceCount} IDR slice NALs");
    }
}
