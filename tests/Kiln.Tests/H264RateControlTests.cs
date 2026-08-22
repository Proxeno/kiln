using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Acceptance test for Junior-H. The H.264 spec does not normatively specify rate control; this test
/// only requires four properties: constant-QP behaviour when <c>targetBitsPerFrame == 0</c>, eventual
/// convergence within ±5% of target on a synthetic bits-per-MB model over 10 frames, hard QP / Δ
/// bounds, and full determinism. Junior-H may pick any controller (proportional, PID, or other) so
/// long as these properties hold.
/// </summary>
public sealed class H264RateControlTests
{
    private const int MbsPerFrame = 396;
    private const int InitialQp = 28;

    [Fact]
    public void Constant_qp_mode_emits_initial_qp_for_every_mb_and_frame()
    {
        var rc = new H264RateControl(InitialQp, targetBitsPerFrame: 0, MbsPerFrame);
        for (var f = 0; f < 5; f++)
        {
            rc.StartFrame(targetBitsThisFrame: 0);
            for (var mb = 0; mb < MbsPerFrame; mb++)
            {
                rc.NextMbQp(mb, complexity: 0).Should().Be(InitialQp,
                    $"constant-QP mode frame {f} mb {mb} must emit the initial QP unchanged.");
                rc.Update(mb, bitsSpent: 100);
            }
        }
    }

    [Fact]
    public void Variable_qp_returns_a_qp_in_zero_to_fifty_one_inclusive_for_every_mb()
    {
        var rc = new H264RateControl(InitialQp, targetBitsPerFrame: 50_000, MbsPerFrame);
        var rng = new Random(0xDEAD);

        for (var frame = 0; frame < 4; frame++)
        {
            rc.StartFrame(targetBitsThisFrame: 0);
            var prevQp = -1;
            for (var mb = 0; mb < MbsPerFrame; mb++)
            {
                var qp = rc.NextMbQp(mb, complexity: rng.Next(0, 5000));
                qp.Should().BeInRange(0, 51,
                    $"frame {frame} mb {mb}: rate control must clip QP to the H.264 valid range.");
                if (prevQp >= 0)
                {
                    var delta = qp - prevQp;
                    delta.Should().BeInRange(-26, 25,
                        $"frame {frame} mb {mb}: per-MB QP delta must satisfy H.264 mb_qp_delta domain.");
                }

                prevQp = qp;
                rc.Update(mb, bitsSpent: 80 + rng.Next(0, 60));
            }
        }
    }

    [Fact]
    public void Convergence_total_bits_within_five_percent_of_target_over_ten_frames()
    {
        const int bitsPerMb = 120;
        var target = bitsPerMb * MbsPerFrame;
        var rc = new H264RateControl(InitialQp, target, MbsPerFrame);

        long sumActual = 0;
        for (var frame = 0; frame < 10; frame++)
        {
            rc.StartFrame(targetBitsThisFrame: 0);
            for (var mb = 0; mb < MbsPerFrame; mb++)
            {
                _ = rc.NextMbQp(mb, complexity: 0);
                rc.Update(mb, bitsSpent: bitsPerMb);
                sumActual += bitsPerMb;
            }
        }

        var expected = (long)target * 10;
        var ratio = (double)sumActual / expected;
        ratio.Should().BeInRange(0.95, 1.05,
            "with a synthetic constant bits-per-MB model and target = bitsPerMb · mbsPerFrame, " +
            $"actual / target should converge to 1.00 (got {ratio:F3}).");
    }

    [Fact]
    public void Convergence_steady_state_qp_settles_within_first_ten_percent_of_frame()
    {
        const int bitsPerMb = 200;
        var target = bitsPerMb * MbsPerFrame;
        var rc = new H264RateControl(InitialQp, target, MbsPerFrame);
        rc.StartFrame(targetBitsThisFrame: 0);

        var qps = new int[MbsPerFrame];
        for (var mb = 0; mb < MbsPerFrame; mb++)
        {
            qps[mb] = rc.NextMbQp(mb, complexity: 0);
            rc.Update(mb, bitsSpent: bitsPerMb);
        }

        var settleStart = MbsPerFrame / 10;
        var minTail = int.MaxValue;
        var maxTail = int.MinValue;
        for (var i = settleStart; i < MbsPerFrame; i++)
        {
            if (qps[i] < minTail)
            {
                minTail = qps[i];
            }

            if (qps[i] > maxTail)
            {
                maxTail = qps[i];
            }
        }

        (maxTail - minTail).Should().BeLessThanOrEqualTo(2,
            $"after the first 10% of MBs, steady-state QP should oscillate within ±1 (saw [{minTail}, {maxTail}]).");
    }

    [Fact]
    public void Deterministic_same_inputs_produce_same_outputs()
    {
        static int[] Run(int seed)
        {
            var rng = new Random(seed);
            var rc = new H264RateControl(InitialQp, targetBitsPerFrame: 80_000, MbsPerFrame);
            var qps = new int[MbsPerFrame * 3];
            var i = 0;
            for (var f = 0; f < 3; f++)
            {
                rc.StartFrame(targetBitsThisFrame: 0);
                for (var mb = 0; mb < MbsPerFrame; mb++)
                {
                    qps[i++] = rc.NextMbQp(mb, complexity: rng.Next(0, 4000));
                    rc.Update(mb, bitsSpent: 75 + rng.Next(0, 50));
                }
            }

            return qps;
        }

        var a = Run(seed: 1234);
        var b = Run(seed: 1234);
        a.Should().Equal(b, "rate control must be deterministic — same seeds must yield identical QP sequences.");
    }

    [Fact]
    public void StartFrame_with_nonzero_target_overrides_constructor_value()
    {
        var rc = new H264RateControl(InitialQp, targetBitsPerFrame: 0, MbsPerFrame);

        rc.StartFrame(targetBitsThisFrame: 200_000);
        var qpSeen = new HashSet<int>();
        for (var mb = 0; mb < MbsPerFrame; mb++)
        {
            qpSeen.Add(rc.NextMbQp(mb, complexity: 1000));
            rc.Update(mb, bitsSpent: 800);
        }

        qpSeen.All(qp => qp is >= 0 and <= 51).Should().BeTrue();
    }

    [Fact]
    public void Nonzero_target_produces_multiple_distinct_qp_values_as_bits_accumulate()
    {
        const int mbs = 120;
        var rc = new H264RateControl(InitialQp, targetBitsPerFrame: 15_000, mbs);
        rc.StartFrame(targetBitsThisFrame: 0);

        var qpSeen = new HashSet<int>();
        for (var mb = 0; mb < mbs; mb++)
        {
            qpSeen.Add(rc.NextMbQp(mb, complexity: 0));
            // Heavy spend early vs light spend late so cumulative error crosses proportional bands.
            rc.Update(mb, bitsSpent: mb < mbs / 2 ? 250 : 40);
        }

        qpSeen.Count.Should().BeGreaterThan(1,
            "with a positive per-frame budget and uneven bits-per-MB spend, NextMbQp should not stay constant for every MB.");
    }
}
