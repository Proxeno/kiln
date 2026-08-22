using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// H.264 7.3.3: <c>slice_alpha_c0_offset_div2</c> and <c>slice_beta_offset_div2</c> are present when
/// <c>disable_deblocking_filter_idc != 1</c>. Emitting <c>idc == 2</c> without them desynchronizes the RBSP
/// before macroblock-layer CAVLC.
/// </summary>
public sealed class H264DeblockingSliceSyntaxTests
{
    [Theory]
    [InlineData(0u, 3)]
    [InlineData(1u, 3)]
    [InlineData(2u, 5)]
    public void WriteDisableDeblockingFilterSliceSyntax_bit_length_matches_spec(uint disableDeblockingFilterIdc, int expectedBits)
    {
        var bs = new H264RbspBitBuffer();
        H264BaselineSliceEncoder.WriteDisableDeblockingFilterSliceSyntax(bs, disableDeblockingFilterIdc);
        bs.BitLength.Should().Be(expectedBits);
    }

    [Fact]
    public void WriteDisableDeblockingFilterSliceSyntax_idc2_includes_two_se_after_ue()
    {
        var correct = new H264RbspBitBuffer();
        H264BaselineSliceEncoder.WriteDisableDeblockingFilterSliceSyntax(correct, 2u);

        var wrong = new H264RbspBitBuffer();
        wrong.WriteUe(2u);

        correct.BitLength.Should().Be(wrong.BitLength + 2, "two se(0) after ue(2) add 2 bits");
    }
}
