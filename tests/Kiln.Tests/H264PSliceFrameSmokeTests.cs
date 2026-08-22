// Phase-1 acceptance test for the senior's phase-3 P-slice integration smoke.
//
// Authoring strategy: STRATEGY A from the F2a playbook — the real test fixtures live inside
// `#if HAS_REFERENCE_PICTURE_PADDER && HAS_PSLICE_MB_WRITER && HAS_INTER_RECONSTRUCTOR && HAS_INTER_BOUNDARY_STRENGTH`
// blocks. With ANY of those four symbols undefined (the state at file commit time), only the
// `PSliceFrame_must_be_delivered_before_test_runs` fact is compiled, and it fails with a
// senior-actionable message naming the still-disabled symbols. The senior turns the symbols on
// one-by-one as W1–W4 land; once all four are live, the real fixture body activates and runs
// the actual two-frame encode + ffmpeg-decode smoke.
//
// Drift-trap context (see h264_encoder_f2b_delegation_orchestration.md "The drift trap"):
//   This test is the LAST safety net before the F2b GOP-level drift surfaces. It catches the
//   senior-side drift sources from the orchestration doc's drift-source table:
//     - missed `_paddedRefValid` gate → frame N+1 reads un-padded `_recY` → out-of-bounds /
//       stale-data drift surfaces as size or decode-stderr regression here;
//     - MV cache not cleared at slice start → frame N+1 inherits stale neighbour MVs → wrong
//       median predictors → silent residual inflation, surfaces as size > 25% threshold here.
//   The two scenarios (identical frames → all-skip; (a, b=translated_a) → small P-slice) cover
//   both the trivial and the non-trivial inter cases.

using Xunit;

#if HAS_REFERENCE_PICTURE_PADDER && HAS_PSLICE_MB_WRITER && HAS_INTER_RECONSTRUCTOR && HAS_INTER_BOUNDARY_STRENGTH
using System.Diagnostics;
using FluentAssertions;
using Kiln;
#endif

namespace Kiln.Tests;

/// <summary>
/// Phase-1 acceptance test class for the senior's phase-3 P-slice frame integration. Activates
/// only when ALL FOUR W1–W4 modules are delivered AND the senior has flipped each
/// <c>HAS_*</c> symbol in the test csproj. Until then, the gate test fails with a clear
/// "still missing X, Y, Z" message so the senior knows exactly which workers are outstanding.
/// </summary>
public sealed class H264PSliceFrameSmokeTests
{
#if !(HAS_REFERENCE_PICTURE_PADDER && HAS_PSLICE_MB_WRITER && HAS_INTER_RECONSTRUCTOR && HAS_INTER_BOUNDARY_STRENGTH)
    /// <summary>
    /// Pre-delivery gate: until ALL FOUR W1–W4 modules are live AND their <c>HAS_*</c> symbols
    /// are flipped in the test csproj, this fact fails with an enumerated list of still-missing
    /// symbols. After the senior flips the last symbol during phase-3 integration the gate
    /// short-circuits and the real two-frame smoke fixtures (below in <c>#else</c>) activate.
    /// </summary>
    [Fact]
    public void PSliceFrame_must_be_delivered_before_test_runs()
    {
        var missing = new List<string>();
#if !HAS_REFERENCE_PICTURE_PADDER
        missing.Add("HAS_REFERENCE_PICTURE_PADDER (W1: H264ReferencePicturePadder)");
#endif
#if !HAS_PSLICE_MB_WRITER
        missing.Add("HAS_PSLICE_MB_WRITER (W2: H264PSliceMbWriter)");
#endif
#if !HAS_INTER_RECONSTRUCTOR
        missing.Add("HAS_INTER_RECONSTRUCTOR (W3: H264InterReconstructor)");
#endif
#if !HAS_INTER_BOUNDARY_STRENGTH
        missing.Add("HAS_INTER_BOUNDARY_STRENGTH (W4: H264InterBoundaryStrength)");
#endif

        Assert.Fail(
            "F2b phase-3 smoke is gated on all four W1–W4 modules being delivered AND the senior " +
            "having flipped each HAS_* symbol in tests/Kiln.Tests/Kiln.Tests.csproj. " +
            "Still missing: " + string.Join(", ", missing) + ".");
    }
#else
    // -----------------------------------------------------------------------------------------
    // Real fixture suite (active when all four worker symbols are defined).
    //
    // TODO(senior phase 3): the encode-time API is still subject to senior surgery during the
    // RD wiring + reference-picture lifecycle integration step (orchestration doc step I5).
    // For now, drive two-frame encoding via H264BaselineEncoder.EncodeFrame with
    // KeyframeIntervalFrames=2 so the second EncodeFrame call lands on a P-slice. If that
    // interface shape changes during integration, adjust this fixture to match.
    // -----------------------------------------------------------------------------------------

    private const int Width = 320;
    private const int Height = 240;

    private static string Fixture(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "Fixtures", "H264Golden", ..parts]);

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

            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (bool ok, string stderr) TryFfmpegDecodeAnnexB(byte[] annexB)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-pslice-smoke-{Guid.NewGuid():N}.h264");
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
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            using var p = Process.Start(psi);
            if (p is null)
            {
                return (false, "process did not start");
            }

            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "timeout");
            }

            return (p.ExitCode == 0, err);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static (int firstBytes, int secondBytes, byte[] fullAnnexB) EncodeTwoFrames(
        ReadOnlySpan<byte> y0, ReadOnlySpan<byte> u0, ReadOnlySpan<byte> v0,
        ReadOnlySpan<byte> y1, ReadOnlySpan<byte> u1, ReadOnlySpan<byte> v1,
        int qp)
    {
        var opts = new H264BaselineEncoderOptions
        {
            QuantizationParameter = qp,
            KeyframeIntervalFrames = 2,
        };

        var capacity = Width * Height * 4 + 1_000_000;
        var annex = new byte[capacity];
        int firstBytes;
        int secondBytes;
        using (var enc = new H264BaselineEncoder(Width, Height, opts))
        {
            firstBytes = enc.EncodeFrame(y0, u0, v0, Width, Width / 2, annex, forceKeyframe: false);
            enc.LastFrameWasIdr.Should().BeTrue("first frame must always be an IDR");
            var rest = annex.AsSpan(firstBytes);
            secondBytes = enc.EncodeFrame(y1, u1, v1, Width, Width / 2, rest, forceKeyframe: false);
            enc.LastFrameWasIdr.Should().BeFalse(
                "with KeyframeIntervalFrames=2 the second EncodeFrame call must produce a P-slice, not IDR");
        }

        return (firstBytes, secondBytes, annex.AsSpan(0, firstBytes + secondBytes).ToArray());
    }

    /// <summary>
    /// Test 1: encode (frame_a, frame_a). The second frame is identical to the first so a
    /// correct P-slice encoder must emit an all-skip slice with the bitstream collapsing to a
    /// single mb_skip_run + slice header. Threshold: ≤ 1% of the IDR's bytes is enough headroom
    /// for the SPS/PPS/slice-header overhead while still catching "second frame fell back to
    /// per-MB intra encoding" regressions (those would land at 50%+ of the IDR).
    /// </summary>
    [Fact]
    public void Two_identical_frames_collapse_to_all_skip_second_frame()
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        var i420Path = Fixture("frame_320x240_a.i420");
        File.Exists(i420Path).Should().BeTrue($"missing fixture {i420Path}");
        var i420 = File.ReadAllBytes(i420Path);
        i420.Length.Should().Be(Width * Height * 3 / 2);

        var ySize = Width * Height;
        var uvSize = ySize / 4;
        var y = i420.AsSpan(0, ySize);
        var u = i420.AsSpan(ySize, uvSize);
        var v = i420.AsSpan(ySize + uvSize, uvSize);

        var (firstBytes, secondBytes, fullStream) = EncodeTwoFrames(y, u, v, y, u, v, qp: 28);

        // ≤ ~1.1% of IDR for the all-skip frame; loose enough to absorb SPS/PPS reissue + small
        // residual-level distribution shifts from QP/MF bookkeeping changes.
        var ratio = (double)secondBytes / firstBytes;
        ratio.Should().BeLessThanOrEqualTo(0.011,
            $"identical-frame P-slice should collapse to all-skip; got second={secondBytes}B, IDR={firstBytes}B, " +
            $"ratio={ratio:P2}");

        var (ok, stderr) = TryFfmpegDecodeAnnexB(fullStream);
        ok.Should().BeTrue($"ffmpeg decode failed: {stderr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(stderr,
            "two-identical-frame Kiln P-slice stream must decode through ffmpeg without errors");
    }

    /// <summary>
    /// Test 2: encode (frame_a, frame_b) where frame_b is frame_a translated by (4, 0) integer
    /// pels with replicate-border on the exposed left strip. Motion estimation should locate
    /// the (4, 0) MV and the resulting P-slice should compress well — threshold &lt; 25% of
    /// IDR catches "ME never converged, fell back to intra" regressions while leaving room for
    /// the residual bits at the exposed border strip.
    /// </summary>
    [Fact]
    public void Translated_frame_pair_produces_small_pslice()
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        var aPath = Fixture("frame_320x240_a.i420");
        var bPath = Fixture("frame_320x240_b.i420");
        File.Exists(aPath).Should().BeTrue($"missing fixture {aPath}");
        File.Exists(bPath).Should().BeTrue($"missing fixture {bPath}");

        var a = File.ReadAllBytes(aPath);
        var b = File.ReadAllBytes(bPath);
        a.Length.Should().Be(Width * Height * 3 / 2);
        b.Length.Should().Be(Width * Height * 3 / 2);

        var ySize = Width * Height;
        var uvSize = ySize / 4;
        var ya = a.AsSpan(0, ySize); var ua = a.AsSpan(ySize, uvSize); var va = a.AsSpan(ySize + uvSize, uvSize);
        var yb = b.AsSpan(0, ySize); var ub = b.AsSpan(ySize, uvSize); var vb = b.AsSpan(ySize + uvSize, uvSize);

        var (firstBytes, secondBytes, fullStream) = EncodeTwoFrames(ya, ua, va, yb, ub, vb, qp: 28);

        var ratio = (double)secondBytes / firstBytes;
        ratio.Should().BeLessThan(0.25,
            $"translated-frame P-slice should compress to <25% of IDR via ME; got second={secondBytes}B, " +
            $"IDR={firstBytes}B, ratio={ratio:P2}");

        var (ok, stderr) = TryFfmpegDecodeAnnexB(fullStream);
        ok.Should().BeTrue($"ffmpeg decode failed: {stderr}");
        H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(stderr,
            "(frame_a, frame_b) Kiln P-slice stream must decode through ffmpeg without errors");
    }
#endif
}
