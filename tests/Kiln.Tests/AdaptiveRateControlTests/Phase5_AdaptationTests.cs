using Microsoft.Extensions.Logging.Abstractions;
using Kiln.Internal.H264.Adaptation;
using Kiln.RateControl;
using Kiln.Recovery;

namespace Kiln.Tests.AdaptiveRateControlTests;

/// <summary>
/// Phase 5 — resolution / fps / speed-mode adaptation, plus the all-phases master controller
/// (<see cref="H264AdaptiveRateController"/>). Includes guards for the three bugs the reconstruction
/// fixed: the master controller double-applying recovery, the resolution ladder matching rungs by
/// record name (so adaptation never fired), and the bitrate→rung index overrunning a custom ladder.
/// </summary>
public sealed class Phase5_AdaptationTests
{
    private static EncoderNetworkFeedback Stable() => new(
        EstimatedAvailableBitrateBps: 15_000_000, PacketLossRatio: 0.0,
        RoundTripTime: TimeSpan.FromMilliseconds(20), Jitter: TimeSpan.FromMilliseconds(2),
        PendingRtpBytes: 5_000, NackCount: 0, PictureLossIndication: false,
        FullIntraRequest: false, ClientDecodeDelay: null);

    private static EncoderNetworkFeedback Severe() => new(
        EstimatedAvailableBitrateBps: 1_000_000, PacketLossRatio: 0.15,
        RoundTripTime: TimeSpan.FromMilliseconds(150), Jitter: TimeSpan.FromMilliseconds(30),
        PendingRtpBytes: 300_000, NackCount: 20, PictureLossIndication: false,
        FullIntraRequest: false, ClientDecodeDelay: null);

    private static EncoderPipelineStats GoodStats() => new(
        LastEncodeDuration: TimeSpan.FromMilliseconds(4), AverageEncodeDuration: TimeSpan.FromMilliseconds(4),
        PendingInputFrames: 1, PendingEncodedFrames: 0, DroppedInputFrames: 0, DroppedEncodedFrames: 0,
        LastEncodedFrameBytes: 15_000, LastFrameQp: 24, LastFrameWasIdr: false,
        MotionComplexity: 0.2, TextureComplexity: 0.3, SceneChangeDetected: false);

    private static EncoderPipelineStats BusyStats() => GoodStats() with
    {
        AverageEncodeDuration = TimeSpan.FromMilliseconds(15),
        PendingInputFrames = 5,
    };

    private static EncoderAdaptationDecision Decision(int w, int h, int fps, EncoderSpeedMode speed) => new(
        TargetBitrateBps: 8_000_000, MaxFrameBytes: 30_000, TargetFps: fps, Width: w, Height: h,
        BaseQp: 28, ForceIdr: false, EnableIntraRefresh: false, SpeedMode: speed);

    private static AdaptationPolicy NewPolicy(RateControlConfig? config = null) =>
        new(config ?? new RateControlConfig(), new ResolutionLadder(), new FpsLadder(),
            NullLogger<AdaptationPolicy>.Instance);

    // ── cascade: speed mode → fps → resolution ────────────────────────────────────────────────────

    [Fact]
    public void SevereCongestion_EscalatesSpeedMode_First()
    {
        var d = NewPolicy().DecideAdaptation(Severe(), BusyStats(), Decision(1920, 1080, 60, EncoderSpeedMode.Balanced));
        Assert.True(d.SpeedMode > EncoderSpeedMode.Balanced, "first severe step should escalate speed mode");
        Assert.Equal(1080, d.Height);
        Assert.Equal(60, d.Fps);
    }

    [Fact]
    public void SevereCongestion_ReducesFps_WhenSpeedAlreadyMaxed()
    {
        var d = NewPolicy().DecideAdaptation(Severe(), BusyStats(), Decision(1920, 1080, 60, EncoderSpeedMode.VeryFast));
        Assert.Equal(30, d.Fps);
        Assert.Equal(1080, d.Height);
    }

    [Fact]
    public void SevereCongestion_ReducesResolution_WhenSpeedAndFpsMinned()
    {
        var d = NewPolicy().DecideAdaptation(Severe(), BusyStats(), Decision(1920, 1080, 15, EncoderSpeedMode.VeryFast));
        Assert.True(d.Height < 1080, $"should drop resolution; got {d.Width}x{d.Height}");
        Assert.Equal(1600, d.Width);
        Assert.Equal(900, d.Height);
    }

    [Fact]
    public void StableNetwork_RecoversResolution()
    {
        var policy = NewPolicy();
        var current = Decision(1280, 720, 60, EncoderSpeedMode.Balanced);
        // The first stable evaluation (no cooldown yet) walks resolution up one rung.
        var d = policy.DecideAdaptation(Stable(), GoodStats(), current);
        Assert.True(d.Height > 720, $"stability should raise resolution; got {d.Height}");
        Assert.Equal(1600, d.Width);
        Assert.Equal(900, d.Height);
    }

    [Fact]
    public void Cooldown_PreventsFlapping()
    {
        var policy = NewPolicy(new RateControlConfig { AdaptationCooldownFrames = 30 });
        var current = Decision(1920, 1080, 60, EncoderSpeedMode.VeryFast); // so severe step changes fps (visible)

        var down = policy.DecideAdaptation(Severe(), BusyStats(), current);
        Assert.Equal(30, down.Fps);

        // Immediately good conditions — must NOT flip back within the cooldown window.
        var next = policy.DecideAdaptation(Stable(), GoodStats(), current);
        Assert.Equal(current.TargetFps, next.Fps); // unchanged: returns currentDecision while cooling down
        Assert.Equal("", next.Reason);
    }

    // ── ladder bug fixes ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolutionLadder_MatchesRungByDimensions_NotRecordName()
    {
        var ladder = new ResolutionLadder();
        // A probe named "current" (as AdaptationPolicy passes) must still resolve the 1080p rung —
        // the old Array.IndexOf compared the whole record incl. Name and never matched.
        var lower = ladder.GetLowerResolution(new ResolutionLadder.Resolution(1920, 1080, "current"));
        Assert.NotNull(lower);
        Assert.Equal(1600, lower!.Width);
        Assert.Equal(900, lower.Height);

        var higher = ladder.GetHigherResolution(new ResolutionLadder.Resolution(1280, 720, "current"));
        Assert.NotNull(higher);
        Assert.Equal(1600, higher!.Width);
    }

    [Fact]
    public void ResolutionLadder_GetResolutionForBitrate_ClampsForCustomLadder()
    {
        // Two-rung ladder + a low bitrate would index _ladder[4] in the old code and throw.
        var ladder = new ResolutionLadder(
            new ResolutionLadder.Resolution(1280, 720, "720p"),
            new ResolutionLadder.Resolution(640, 360, "360p"));
        var res = ladder.GetResolutionForBitrate(500_000, 30);
        Assert.Equal(360, res.Height);
    }

    [Fact]
    public void FpsLadder_Steps()
    {
        var ladder = new FpsLadder();
        Assert.Equal(30, ladder.GetLowerFps(60));
        Assert.Equal(15, ladder.GetLowerFps(30));
        Assert.Null(ladder.GetLowerFps(15));
        Assert.Equal(60, ladder.GetHigherFps(30));
        Assert.Null(ladder.GetHigherFps(60));
    }

    // ── master controller (all phases) ────────────────────────────────────────────────────────────

    [Fact]
    public void Master_GetDecision_CombinesAllPhases()
    {
        var ctrl = new H264AdaptiveRateController(new RateControlConfig());

        // Congestion + loss → bitrate down, QP up, and (eventually) geometry/speed adaptation.
        var d = ctrl.GetDecision(Severe(), BusyStats());
        Assert.True(d.TargetBitrateBps < 8_000_000, "bitrate should drop under congestion");
        Assert.True(d.SpeedMode >= EncoderSpeedMode.Balanced);
    }

    [Fact]
    public void Master_ForcesIdr_OnPictureLoss()
    {
        var ctrl = new H264AdaptiveRateController(new RateControlConfig());
        var d = ctrl.GetDecision(Stable() with { PictureLossIndication = true }, GoodStats());
        Assert.True(d.ForceIdr);
    }

    /// <summary>
    /// THE regression guard for the original bug: the master controller must apply recovery exactly
    /// once per GetDecision. If it re-applied it (second policy instance / second DecideRecovery), the
    /// IDR cooldown would drain at 2× and a fresh PLI would re-fire an IDR after only ~half the
    /// configured frames. Walk the cooldown to prove single application.
    /// </summary>
    [Fact]
    public void Master_AppliesRecovery_OncePerDecision_IdrCooldownSpansConfiguredFrames()
    {
        const int cooldown = 6;
        var ctrl = new H264AdaptiveRateController(new RateControlConfig { IdrCooldownFrames = cooldown });
        var pli = Stable() with { PictureLossIndication = true };

        Assert.True(ctrl.GetDecision(pli, GoodStats()).ForceIdr); // first IDR, arms cooldown

        for (var i = 0; i < cooldown - 1; i++)
            Assert.False(ctrl.GetDecision(pli, GoodStats()).ForceIdr,
                $"IDR re-fired at frame {i + 1} — recovery applied more than once per decision");

        ctrl.GetDecision(Stable(), GoodStats());                 // cooldown elapses
        Assert.True(ctrl.GetDecision(pli, GoodStats()).ForceIdr); // re-fires once clear

        // Metrics come from the single shared recovery instance: 2 IDRs, and a PLI on every frame.
        var (idr, pli2, _) = ctrl.RecoveryPolicy.GetMetrics();
        Assert.Equal(2, idr);
        Assert.Equal(cooldown + 1, pli2);
    }
}
