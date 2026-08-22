using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Pins CAVLC <c>suffixLength</c> evolution for level prefixes; references the sequential update rule (H.264 §9.2.2)
/// vs the incorrect single-step <c>if/else if</c> pattern that misaligned the macroblock layer.
/// </summary>
public sealed class H264CavlcSuffixLengthTraceTests
{
    /// <summary>
    /// Two non-zero ACs in reverse scan order: last index has |7|>3 so <c>suffixLength</c> reaches 2 after the first level.
    /// </summary>
    private static void FillTwoAcPattern(Span<short> mb)
    {
        mb.Clear();
        mb[14] = 2;
        mb[15] = 7;
    }

    [Fact]
    public void Trace_first_level_with_abs_gt_3_drives_suffixLength_to_two()
    {
        Span<short> c = stackalloc short[16];
        FillTwoAcPattern(c);

        List<H264CavlcResidual.CavlcCoeffTrace> trace = [];
        H264CavlcResidual.TraceBlockResidualLevelSteps(c, 15, trace);
        trace.Count.Should().Be(2);
        trace[0].CoeffVal.Should().Be(7);
        trace[0].SuffixLengthBefore.Should().Be(0);
        trace[0].SuffixLengthAfter.Should().Be(2, "sequential bump 0→1 then |7|>3 at suffixLength==1 ⇒ +1");

        trace[1].CoeffVal.Should().Be(2);
        trace[1].SuffixLengthBefore.Should().Be(2);
        trace[1].SuffixLengthAfter.Should().Be(2, "| coeff 2 ≤ threshold at suffixLength == 2");
    }

    [Fact]
    public void Trace_matches_spec_decode_roundtrip_for_large_first_sorted_level()
    {
        Span<short> c = stackalloc short[16];
        FillTwoAcPattern(c);

        var bytes = H264CavlcSpecDecode.EncodeBlock(c, 15, H264ResidualKind.Luma4X4, nc: 0);
        var br = new H264CavlcSpecDecode.BitReader(bytes);
        var decoded = H264CavlcSpecDecode.DecodeBlock(br, 15, nc: 0, isChromaDc: false);
        decoded[14].Should().Be(2);
        decoded[15].Should().Be(7);
    }

    [Fact]
    public void Sequential_suffix_update_handles_abs_gt_three_differently_from_single_branch()
    {
        static void SuffixSequential(ref int suffixLength, int coeffVal)
        {
            suffixLength += suffixLength == 0 ? 1 : 0;
            var threshold = 3 << (suffixLength - 1);
            if (suffixLength < 6 && (coeffVal > threshold || coeffVal < -threshold))
            {
                suffixLength++;
            }
        }

        static void SuffixIfElseBroken(ref int suffixLength, int coeffVal)
        {
            if (suffixLength == 0)
            {
                suffixLength = 1;
            }
            else if (suffixLength < 6 && (Math.Abs(coeffVal) > (3 << (suffixLength - 1))))
            {
                suffixLength++;
            }
        }

        int sla = 0, slb = 0;
        SuffixSequential(ref sla, coeffVal: 7);
        SuffixIfElseBroken(ref slb, coeffVal: 7);
        sla.Should().Be(2);
        slb.Should().Be(1, "single-branch never re-evaluates magnitude at suffixLength immediately after the 0→1 bump");

        sla = 0;
        slb = 0;
        SuffixSequential(ref sla, coeffVal: -7);
        SuffixIfElseBroken(ref slb, coeffVal: -7);
        sla.Should().Be(2);
        slb.Should().Be(1, "negative non-trailing level uses the same magnitude rule as positive");
    }
}
