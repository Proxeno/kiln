using System.Diagnostics;
using Kiln;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Loose encode-time sanity check for profiling in CI (catches pathological hangs).
/// </summary>
public sealed class H264SliceEncodeProfileTests
{
    [Fact]
    public void Baseline_encoder_320x240_many_frames_completes_under_budget()
    {
        const int w = 320;
        const int h = 240;
        var ys = w * h;
        var uv = ys / 4;
        var y = new byte[ys];
        var u = new byte[uv];
        var v = new byte[uv];
        for (var i = 0; i < ys; i++)
        {
            y[i] = (byte)(i & 0xFF);
        }

        Array.Fill(u, (byte)128);
        Array.Fill(v, (byte)128);

        var buf = new byte[ys * 2 + 1_000_000];
        var sw = Stopwatch.StartNew();
        using (var enc = new H264BaselineEncoder(w, h))
        {
            for (var f = 0; f < 15; f++)
            {
                _ = enc.EncodeFrame(y, u, v, w, w / 2, buf, forceKeyframe: f == 0);
            }
        }

        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 120_000, $"encode took {sw.ElapsedMilliseconds}ms (budget 120s smoke)");
    }
}
