using System.Diagnostics;
using FluentAssertions;
using Kiln.RateControl;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// End-to-end guards for <see cref="H264StreamingSession"/> — the feedback loop between
/// <c>Kiln.RateControl</c> and the encoder. These tests demonstrate adaptation <em>taking
/// effect</em>, not merely compiling: bit budgets and QP move under congestion and coded frames
/// shrink; PLI produces an IDR and the cooldown produces the surfaced intra-refresh fallback;
/// severe congestion walks the speed ladder down and stability walks it back (crossing the
/// reference-count boundary both ways mid-GOP); and the produced streams decode cleanly, with the
/// encoder reconstruction byte-exact against ffmpeg across every transition.
/// </summary>
public sealed class H264StreamingSessionTests
{
    private const int W = 320;
    private const int H = 240;

    private static EncoderNetworkFeedback Feedback(
        double loss = 0.0, int rttMs = 20, int queueBytes = 5_000, int nacks = 0,
        bool pli = false, bool fir = false) => new(
        EstimatedAvailableBitrateBps: 10_000_000,
        PacketLossRatio: loss,
        RoundTripTime: TimeSpan.FromMilliseconds(rttMs),
        Jitter: TimeSpan.FromMilliseconds(2),
        PendingRtpBytes: queueBytes,
        NackCount: nacks,
        PictureLossIndication: pli,
        FullIntraRequest: fir,
        ClientDecodeDelay: null);

    /// <summary>Good network: below every congestion threshold (loss &lt; 2%, RTT &lt; 50 ms, queue &lt; 100 KB).</summary>
    private static EncoderNetworkFeedback Good() => Feedback();

    /// <summary>Congested but not "severe": trips the bitrate downshift (loss &gt; 2%, RTT &gt; 50 ms)
    /// without tripping the resolution/fps/speed cascade (loss ≤ 10%, RTT ≤ 100 ms).</summary>
    private static EncoderNetworkFeedback Congested() => Feedback(loss: 0.06, rttMs: 80);

    /// <summary>Severe congestion: also trips the adaptation cascade (loss &gt; 10%).</summary>
    private static EncoderNetworkFeedback Severe() => Feedback(loss: 0.15, rttMs: 150, queueBytes: 250_000, nacks: 20);

    /// <summary>Config that pins resolution and fps to a single rung so speed mode is the only
    /// cascade target — sessions in these tests never rescale, and TargetFps stays put.</summary>
    private static RateControlConfig PinnedGeometryConfig(int idrCooldown = 60, int adaptationCooldown = 2) => new()
    {
        SupportedWidths = [W],
        SupportedHeights = [H],
        SupportedFps = [30],
        IdrCooldownFrames = idrCooldown,
        AdaptationCooldownFrames = adaptationCooldown,
    };

    // ── determinism ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Identical_frames_and_feedback_produce_identical_bitstreams()
    {
        var first = RunScenario();
        var second = RunScenario();
        second.stream.Should().Equal(first.stream, "the session must add no wall-clock or scheduling inputs");
        second.results.Should().Equal(first.results);

        static (byte[] stream, List<(int bytes, int qp, bool idr, EncoderSpeedMode mode)> results) RunScenario()
        {
            var frames = SessionTestContent.Generate(W, H, 12);
            using var session = new H264StreamingSession(W, H, rateControlConfig: PinnedGeometryConfig());
            var annex = new byte[session.RecommendedOutputBufferSize];
            var stream = new MemoryStream();
            var results = new List<(int, int, bool, EncoderSpeedMode)>();
            for (var i = 0; i < 24; i++)
            {
                var feedback = i switch
                {
                    < 8 => Good(),
                    < 16 => Severe(),
                    16 => Feedback(pli: true),
                    _ => Good(),
                };
                var r = SessionTestContent.Encode(session, frames[i % frames.Length], annex, feedback);
                stream.Write(annex, 0, r.BytesWritten);
                results.Add((r.BytesWritten, r.AppliedSliceQp, r.WasIdr, r.AppliedSpeedMode));
            }

            return (stream.ToArray(), results);
        }
    }

    // ── bitrate / QP adaptation taking effect ────────────────────────────────────────────────────

    [Fact]
    public void Congestion_shrinks_bit_budget_raises_qp_and_shrinks_coded_frames()
    {
        var frames = SessionTestContent.Generate(W, H, 8);
        using var session = new H264StreamingSession(W, H, rateControlConfig: PinnedGeometryConfig());
        var annex = new byte[session.RecommendedOutputBufferSize];

        var goodPhase = new List<H264StreamingEncodeResult>();
        for (var i = 0; i < 20; i++)
        {
            goodPhase.Add(SessionTestContent.Encode(session, frames[i % frames.Length], annex, Good()));
        }

        var congestedPhase = new List<H264StreamingEncodeResult>();
        for (var i = 20; i < 50; i++)
        {
            congestedPhase.Add(SessionTestContent.Encode(session, frames[i % frames.Length], annex, Congested()));
        }

        var lastGood = goodPhase[^1];
        var lastCongested = congestedPhase[^1];

        // The controller multiplies bitrate down 0.7× per congested frame to the configured floor,
        // and the session's applied per-picture budget must track it.
        lastCongested.Decision.TargetBitrateBps.Should().BeLessThan(lastGood.Decision.TargetBitrateBps / 4);
        lastCongested.AppliedTargetBitsPerFrame.Should().BeLessThan(lastGood.AppliedTargetBitsPerFrame / 4);
        lastCongested.AppliedSliceQp.Should().BeGreaterThan(lastGood.AppliedSliceQp,
            "sustained congestion must raise the base QP");

        // And the encoder must measurably obey: steady-state P frames shrink under the tighter
        // budget. Compare tail-of-phase averages (both phases well past IDR and controller settle).
        var goodAvg = goodPhase.TakeLast(8).Average(r => (double)r.BytesWritten);
        var congestedAvg = congestedPhase.TakeLast(8).Average(r => (double)r.BytesWritten);
        congestedAvg.Should().BeLessThan(goodAvg / 2,
            $"coded frames must track the collapsed budget (good {goodAvg:F0} B vs congested {congestedAvg:F0} B)");
    }

    // ── recovery: PLI → IDR, cooldown → surfaced intra-refresh request ───────────────────────────

    [Fact]
    public void Pli_forces_an_idr_and_the_cooldown_surfaces_intra_refresh_instead()
    {
        var frames = SessionTestContent.Generate(W, H, 8);
        using var session = new H264StreamingSession(
            W, H,
            new H264BaselineEncoderOptions { KeyframeIntervalFrames = 1000 },
            PinnedGeometryConfig(idrCooldown: 10));
        var annex = new byte[session.RecommendedOutputBufferSize];

        for (var i = 0; i < 5; i++)
        {
            var r = SessionTestContent.Encode(session, frames[i % frames.Length], annex, Good());
            r.WasIdr.Should().Be(i == 0, "only the first frame is a scheduled IDR");
        }

        var pliResult = SessionTestContent.Encode(session, frames[5 % frames.Length], annex, Feedback(pli: true));
        pliResult.WasIdr.Should().BeTrue("a PLI outside the cooldown must force an IDR");
        pliResult.IntraRefreshRequested.Should().BeFalse();

        var cooldownResult = SessionTestContent.Encode(session, frames[6 % frames.Length], annex, Feedback(pli: true));
        cooldownResult.WasIdr.Should().BeFalse("a PLI during the IDR cooldown must not storm keyframes");
        cooldownResult.IntraRefreshRequested.Should().BeTrue(
            "the cooldown fallback is surfaced honestly — Kiln implements no intra refresh, so the " +
            "session encodes a normal frame and reports the pending request");

        var (idrCount, pliCount, _) = session.RecoveryPolicy.GetMetrics();
        idrCount.Should().Be(1, "recovery is applied exactly once per frame (ownership contract)");
        pliCount.Should().Be(2);
    }

    // ── speed-mode cascade, down and back up across the reference-count boundary ─────────────────

    [Fact]
    public void Severe_congestion_walks_speed_down_and_stability_walks_it_back()
    {
        var frames = SessionTestContent.Generate(W, H, 8);
        using var session = new H264StreamingSession(
            W, H,
            new H264BaselineEncoderOptions { SpeedMode = EncoderSpeedMode.Balanced, KeyframeIntervalFrames = 1000 },
            PinnedGeometryConfig());
        var annex = new byte[session.RecommendedOutputBufferSize];

        session.CurrentSpeedMode.Should().Be(EncoderSpeedMode.Balanced);
        session.EncoderForTests.SignalledMaxReferenceFrames.Should().Be(2,
            "the session reserves the full DPB in the SPS so later upshifts need no IDR");
        session.EncoderForTests.ActiveReferenceFrames.Should().Be(1, "Balanced runs single-reference");

        for (var i = 0; i < 12; i++)
        {
            SessionTestContent.Encode(session, frames[i % frames.Length], annex, Severe());
        }

        session.CurrentSpeedMode.Should().Be(EncoderSpeedMode.VeryFast,
            "sustained severe congestion must cascade the speed mode to the floor");

        for (var i = 0; i < 30; i++)
        {
            SessionTestContent.Encode(session, frames[i % frames.Length], annex, Good());
        }

        session.CurrentSpeedMode.Should().Be(EncoderSpeedMode.HighQuality,
            "sustained stability must walk the speed mode back up — this only works because " +
            "LowLatencyRateController is told the applied state (its own state otherwise never recovers)");
        session.EncoderForTests.ActiveReferenceFrames.Should().Be(2,
            "the HighQuality upshift restores the second reference mid-GOP under the reserved SPS maximum");
    }

    [Fact]
    public void Explicit_single_reference_option_caps_every_mode_for_the_whole_session()
    {
        using var session = new H264StreamingSession(
            W, H, new H264BaselineEncoderOptions { MaxReferenceFrames = 1 });
        session.EncoderForTests.SignalledMaxReferenceFrames.Should().Be(1,
            "an explicit MaxReferenceFrames is a decoder-compatibility contract the session must not override");
        session.EncoderForTests.ActiveReferenceFrames.Should().Be(1);
    }

    // ── full adaptive stream: decoder oracle across every transition ─────────────────────────────

    [Fact]
    public void Adaptive_session_stream_decodes_cleanly_and_recon_matches_ffmpeg()
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        const int Frames = 40;
        var frames = SessionTestContent.Generate(W, H, 8);
        using var session = new H264StreamingSession(
            W, H,
            new H264BaselineEncoderOptions { KeyframeIntervalFrames = 1000 },
            PinnedGeometryConfig(idrCooldown: 10));
        var annex = new byte[session.RecommendedOutputBufferSize];
        var stream = new MemoryStream();
        var reconPerFrame = new byte[Frames][];
        var ys = W * H;

        var sawIdrFromPli = false;
        for (var i = 0; i < Frames; i++)
        {
            // Good → severe (speed cascade incl. ref 2→1) → PLI (recovery IDR) → good again
            // (speed walks back up incl. ref 1→2 mid-GOP). Every tier-1/tier-2 path in one stream.
            var feedback = i switch
            {
                < 8 => Good(),
                < 18 => Severe(),
                18 => Feedback(pli: true),
                _ => Good(),
            };
            var r = SessionTestContent.Encode(session, frames[i % frames.Length], annex, feedback);
            stream.Write(annex, 0, r.BytesWritten);
            reconPerFrame[i] = session.EncoderForTests.LastReconstructedY[..ys].ToArray();
            if (i == 18)
            {
                sawIdrFromPli = r.WasIdr;
            }
        }

        sawIdrFromPli.Should().BeTrue("the PLI must have produced a recovery IDR");
        session.CurrentSpeedMode.Should().Be(EncoderSpeedMode.HighQuality, "the stable tail must recover quality");

        var decoded = FfmpegDecodeAllFrames(stream.ToArray());
        var frameBytes = ys + 2 * (ys / 4);
        decoded.Length.Should().BeGreaterThanOrEqualTo(Frames * frameBytes, "ffmpeg must decode every frame");
        for (var i = 0; i < Frames; i++)
        {
            decoded.AsSpan(i * frameBytes, ys).SequenceEqual(reconPerFrame[i]).Should().BeTrue(
                $"frame {i}: encoder reconstruction must be byte-exact against ffmpeg across every " +
                "adaptation transition — a mismatch is a decoder desync");
        }
    }

    // ── tier-3: resolution change via encoder recreation ─────────────────────────────────────────

    [Fact]
    public void Resolution_change_recreates_the_encoder_and_both_segments_decode()
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        const int W2 = 256;
        const int H2 = 192;
        var framesA = SessionTestContent.Generate(W, H, 5);
        var framesB = SessionTestContent.Generate(W2, H2, 5);
        using var session = new H264StreamingSession(W, H, rateControlConfig: PinnedGeometryConfig());
        var annex = new byte[session.RecommendedOutputBufferSize];
        var segmentA = new MemoryStream();
        var segmentB = new MemoryStream();

        for (var i = 0; i < 5; i++)
        {
            var r = SessionTestContent.Encode(session, framesA[i], annex, Good());
            segmentA.Write(annex, 0, r.BytesWritten);
        }

        session.ChangeResolution(W2, H2);
        session.Width.Should().Be(W2);
        session.Height.Should().Be(H2);

        for (var i = 0; i < 5; i++)
        {
            var r = SessionTestContent.Encode(session, framesB[i], annex, Good());
            segmentB.Write(annex, 0, r.BytesWritten);
            if (i == 0)
            {
                r.WasIdr.Should().BeTrue("the first frame after a resolution change must be an IDR " +
                    "carrying the new SPS — a P frame against the old resolution's DPB would desync");
            }
        }

        // Each segment starts with SPS+PPS+IDR, so each must decode standalone at its own geometry…
        FfmpegDecodeAllFrames(segmentA.ToArray()).Length.Should().Be(5 * (W * H * 3 / 2));
        FfmpegDecodeAllFrames(segmentB.ToArray()).Length.Should().Be(5 * (W2 * H2 * 3 / 2));

        // …and the concatenated stream must decode as one mid-stream resolution switch without
        // decode errors (ffmpeg reconfigures on the new SPS at the IDR boundary).
        var full = new byte[segmentA.Length + segmentB.Length];
        segmentA.ToArray().CopyTo(full, 0);
        segmentB.ToArray().CopyTo(full, (int)segmentA.Length);
        FfmpegDecodeToNullAssertNoErrors(full);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static byte[] FfmpegDecodeAllFrames(byte[] annexB)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kiln-session-{Guid.NewGuid():N}.264");
        var outYuv = tmp + ".yuv";
        try
        {
            File.WriteAllBytes(tmp, annexB);
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", tmp, "-f", "rawvideo", "-pix_fmt", "yuv420p", outYuv })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.Should().Be(0, $"ffmpeg decode must succeed; stderr: {err}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(err, "the stream must decode without errors");
            return File.ReadAllBytes(outYuv);
        }
        finally
        {
            File.Delete(tmp);
            if (File.Exists(outYuv))
            {
                File.Delete(outYuv);
            }
        }
    }

    private static void FfmpegDecodeToNullAssertNoErrors(byte[] annexB)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kiln-session-{Guid.NewGuid():N}.264");
        try
        {
            File.WriteAllBytes(tmp, annexB);
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "h264", "-i", tmp, "-f", "null", "-" })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.Should().Be(0, $"ffmpeg decode must succeed; stderr: {err}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(err,
                "the mid-stream resolution switch must not produce decode errors");
        }
        finally
        {
            File.Delete(tmp);
        }
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
            return p is not null && p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Shared content + encode plumbing for the session tests.</summary>
internal static class SessionTestContent
{
    /// <summary>Deterministic moving content: textured gradient with a scrolling bright square —
    /// enough motion for P frames to carry real residual (so budgets visibly bind) without the cost
    /// of the high-motion drift fixture.</summary>
    public static byte[][] Generate(int w, int h, int count)
    {
        var ys = w * h;
        var uv = ys / 4;
        var frames = new byte[count][];
        for (var f = 0; f < count; f++)
        {
            var buf = new byte[ys + 2 * uv];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    // Diagonal global scroll (the f terms) so P frames carry residual everywhere,
                    // not just under the square — frame-size assertions need budgets to bind.
                    var v = (byte)((x * 3 + y * 5 + f * 9 + (((x + f * 6) / 7) * ((y + f * 4) / 5) % 31)) & 0xFF);
                    buf[y * w + x] = v;
                }
            }

            var side = Math.Min(64, Math.Min(w, h) / 2);
            var bx = (f * 18) % Math.Max(1, w - side);
            var by = (f * 11) % Math.Max(1, h - side);
            for (var yy = 0; yy < side; yy++)
            {
                buf.AsSpan((by + yy) * w + bx, side).Fill(235);
            }

            buf.AsSpan(ys, uv).Fill(120);
            buf.AsSpan(ys + uv, uv).Fill(130);
            frames[f] = buf;
        }

        return frames;
    }

    public static H264StreamingEncodeResult Encode(
        H264StreamingSession session, byte[] frame, byte[] annex, EncoderNetworkFeedback feedback)
    {
        var ys = session.Width * session.Height;
        var uv = ys / 4;
        return session.EncodeFrame(
            frame.AsSpan(0, ys), frame.AsSpan(ys, uv), frame.AsSpan(ys + uv, uv),
            session.Width, session.Width / 2, annex, feedback);
    }
}
