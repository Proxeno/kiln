using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Unit coverage for the luma motion-vector predictor (<see cref="H264MotionEstimator.PredictMvWithRefIdx"/>),
/// the shared core of every inter MVP / P_Skip derivation. These guard the mixed-reference rules of
/// H.264 §8.4.1.3 — the directional "exactly one matching reference index" short-circuit, the §8.4.1.3.1
/// C←D and B,C←A neighbour substitutions, and the component-wise median fallback — which produced a
/// string of subtle, hard-to-find encoder/decoder divergences (a wrong predictor makes the encoder
/// write an MVD the decoder resolves to a different MV).
///
/// A neighbour reference index of −1 denotes "not available / not inter".
/// </summary>
public sealed class H264MvPredictorTests
{
    private static H264MotionEstimator.Mv Mv(int x, int y) => new((short)x, (short)y);

    private static H264MotionEstimator.Mv Predict(
        H264MotionEstimator.Mv a, int ra, H264MotionEstimator.Mv b, int rb,
        H264MotionEstimator.Mv c, int rc, H264MotionEstimator.Mv d, int rd, int cur) =>
        H264MotionEstimator.PredictMvWithRefIdx(a, ra, b, rb, c, rc, d, rd, cur);

    [Fact]
    public void Directional_rule_single_matching_ref_returns_that_neighbour()
    {
        // §8.4.1.3.2: exactly one of A/B/C shares the current ref → predictor is that neighbour's MV,
        // NOT the median. This is the row-0-vs-row-1 mixed-ref case that first exposed the bug:
        // A=ref0, B=ref1, C=ref1, current ref0 → must be A, not Median(16,32,32)=32.
        Predict(Mv(16, 0), 0, Mv(32, 0), 1, Mv(32, 0), 1, Mv(0, 0), 1, cur: 0)
            .Should().Be(Mv(16, 0));

        // Single match on B.
        Predict(Mv(1, 1), 1, Mv(2, 2), 0, Mv(3, 3), 1, Mv(9, 9), 1, cur: 0)
            .Should().Be(Mv(2, 2));

        // Single match on C.
        Predict(Mv(1, 1), 1, Mv(2, 2), 1, Mv(3, 3), 0, Mv(9, 9), 1, cur: 0)
            .Should().Be(Mv(3, 3));
    }

    [Fact]
    public void Two_or_three_matching_refs_use_component_median()
    {
        // Two neighbours match the current ref → the directional rule does NOT apply; median of all
        // three (mismatched neighbour included at its real value) is used.
        Predict(Mv(10, 0), 0, Mv(20, 0), 0, Mv(99, 0), 1, Mv(0, 0), 1, cur: 0)
            .Should().Be(Mv(20, 0)); // median(10,20,99)=20

        // All three match → straight component median, incl. negative components.
        Predict(Mv(-52, -12), 0, Mv(-40, 64), 0, Mv(-20, -64), 0, Mv(0, 0), 0, cur: 0)
            .Should().Be(Mv(-40, -12)); // median(-52,-40,-20)=-40 ; median(-12,64,-64)=-12
    }

    [Fact]
    public void C_substituted_by_D_when_top_right_unavailable()
    {
        // §8.4.1.3.1: C unavailable (ref −1) → C takes D's MV and ref. With A,B on ref1 and the
        // substituted C on ref0, the directional rule then fires on the substituted C. This is the
        // P_8×16 right-edge (eqn 8-206) class: the predictor must consider the substituted neighbour.
        Predict(Mv(1, 1), 1, Mv(2, 2), 1, Mv(0, 0), -1, Mv(5, 5), 0, cur: 0)
            .Should().Be(Mv(5, 5));

        // C←D, and the substituted C participates in the median when more than one ref matches.
        Predict(Mv(8, 0), 0, Mv(4, 0), 0, Mv(0, 0), -1, Mv(40, 0), 0, cur: 0)
            .Should().Be(Mv(8, 0)); // C←D=(40,0) r0; all three r0 → median(8,4,40)=8
    }

    [Fact]
    public void B_and_C_substituted_by_A_when_both_unavailable()
    {
        // §8.4.1.3.1: when B and C are both unavailable but A is, B and C inherit A — so the result is
        // A regardless of whether A's ref matches the current one (median(A,A,A)=A).
        Predict(Mv(7, 3), 0, Mv(0, 0), -1, Mv(0, 0), -1, Mv(0, 0), -1, cur: 0)
            .Should().Be(Mv(7, 3));
        Predict(Mv(9, 9), 1, Mv(0, 0), -1, Mv(0, 0), -1, Mv(0, 0), -1, cur: 0)
            .Should().Be(Mv(9, 9));
    }

    [Fact]
    public void Only_B_available_with_mismatched_ref_yields_zero_not_B()
    {
        // A and C unavailable, only B available but on a different ref. No B,C←A substitution applies
        // (A is unavailable), so the median is over (0, B, 0) with B not matching → median = 0.
        // (Catches the old availability-only predictor that wrongly returned B here.)
        Predict(Mv(0, 0), -1, Mv(50, 50), 1, Mv(0, 0), -1, Mv(0, 0), -1, cur: 0)
            .Should().Be(Mv(0, 0));
    }

    [Fact]
    public void All_neighbours_unavailable_predicts_zero()
    {
        Predict(Mv(0, 0), -1, Mv(0, 0), -1, Mv(0, 0), -1, Mv(0, 0), -1, cur: 0)
            .Should().Be(Mv(0, 0));
    }

    /// <summary>
    /// Exhaustive consistency sweep over every availability/ref combination of A/B/C/D against an
    /// independent re-statement of §8.4.1.3.1/.2 — so any future edit to the predictor that breaks a
    /// substitution or the directional rule is caught immediately, without needing a full encode.
    /// </summary>
    [Fact]
    public void Matches_independent_spec_reference_over_all_ref_combinations()
    {
        var mvA = Mv(16, -4); var mvB = Mv(-8, 12); var mvC = Mv(4, 4); var mvD = Mv(-20, 8);
        int[] refs = [-1, 0, 1];
        foreach (var ra in refs)
            foreach (var rb in refs)
                foreach (var rc in refs)
                    foreach (var rd in refs)
                        foreach (var cur in new[] { 0, 1 })
                        {
                            var actual = Predict(mvA, ra, mvB, rb, mvC, rc, mvD, rd, cur);
                            var expected = SpecReference(mvA, ra, mvB, rb, mvC, rc, mvD, rd, cur);
                            actual.Should().Be(expected,
                                $" refs A={ra} B={rb} C={rc} D={rd} cur={cur}");
                        }
    }

    // Independent re-derivation of H.264 §8.4.1.3.1 (neighbour substitution) + §8.4.1.3.2 (median /
    // directional), written separately from the production predictor so the two cross-check.
    private static H264MotionEstimator.Mv SpecReference(
        H264MotionEstimator.Mv a, int ra, H264MotionEstimator.Mv b, int rb,
        H264MotionEstimator.Mv c, int rc, H264MotionEstimator.Mv d, int rd, int cur)
    {
        // C ← D when the above-right neighbour is unavailable.
        if (rc == -1) { c = d; rc = rd; }
        // B,C ← A when both are unavailable and A is available.
        if (rb == -1 && rc == -1 && ra != -1) { b = a; rb = ra; c = a; rc = ra; }

        var va = ra != -1 ? a : Mv(0, 0);
        var vb = rb != -1 ? b : Mv(0, 0);
        var vc = rc != -1 ? c : Mv(0, 0);

        var matches = (ra == cur ? 1 : 0) + (rb == cur ? 1 : 0) + (rc == cur ? 1 : 0);
        if (matches == 1)
            return ra == cur ? va : rb == cur ? vb : vc;

        return Mv(Median3(va.X, vb.X, vc.X), Median3(va.Y, vb.Y, vc.Y));
    }

    private static int Median3(int x, int y, int z) => Math.Max(Math.Min(x, y), Math.Min(Math.Max(x, y), z));
}
