using FluentAssertions;
using Kiln;
using Kiln.Internal.H264;

namespace Kiln.Tests;

public sealed class H264AnnexBAccessUnitTests
{
    [Fact]
    public void EncodeFrame_emits_annex_b_start_codes_and_multiple_nals()
    {
        const int w = 64;
        const int h = 64;
        var ys = w * h;
        var uv = ys / 4;
        var y = new byte[ys];
        var u = new byte[uv];
        var v = new byte[uv];
        Array.Fill(y, (byte)100);
        Array.Fill(u, (byte)120);
        Array.Fill(v, (byte)130);

        var buf = new byte[ys * 2 + 512_000];
        int n;
        using (var enc = new H264BaselineEncoder(w, h))
        {
            n = enc.EncodeFrame(y, u, v, w, w / 2, buf, forceKeyframe: true);
        }

        n.Should().BeGreaterThan(32);
        var span = buf.AsSpan(0, n);
        var starts = 0;
        for (var i = 0; i + 3 < span.Length; i++)
        {
            if (span[i] == 0 && span[i + 1] == 0 && span[i + 2] == 1)
            {
                starts++;
            }
        }

        starts.Should().BeGreaterThanOrEqualTo(3, "SPS + PPS + slice NAL each start with 0x000001");
    }
}
