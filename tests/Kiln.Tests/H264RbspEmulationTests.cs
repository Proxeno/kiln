using FluentAssertions;
using Kiln.Internal.H264;

namespace Kiln.Tests;

public sealed class H264RbspEmulationTests
{
    [Fact]
    public void WriteEbsp_inserts_emulation_prevention_after_two_zeros()
    {
        var rbsp = new byte[] { 0, 0, 0, 1, 0xFF };
        Span<byte> dest = stackalloc byte[H264RbspEmulation.GetEmulationPreventionBufferSize(rbsp.Length)];
        var n = H264RbspEmulation.WriteEbsp(dest, rbsp);
        n.Should().Be(6);
        dest[0].Should().Be(0);
        dest[1].Should().Be(0);
        dest[2].Should().Be(3);
        dest[3].Should().Be(0);
        dest[4].Should().Be(1);
        dest[5].Should().Be(0xFF);
    }

    [Fact]
    public void IntraCbpCodeNum_inverts_ffmpeg_golomb_to_intra4x4_cbp()
    {
        H264Cbp.IntraCbpCodeNum(15).Should().Be(2);
        H264Cbp.IntraCbpCodeNum(0).Should().Be(3);
    }
}
