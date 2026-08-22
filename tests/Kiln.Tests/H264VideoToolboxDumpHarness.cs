using Kiln;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

// Diagnostic harness (not a real assertion): emits Annex B streams from the software encoder so we
// can decode them through VideoToolbox (ffmpeg -hwaccel videotoolbox) vs software ffmpeg and find
// what the tvOS hardware decoder rejects. Run with:
//   dotnet test --filter FullyQualifiedName~H264VideoToolboxDumpHarness
public sealed class H264VideoToolboxDumpHarness
{
    // Diagnostic harness for the tvOS/VideoToolbox P-slice incompatibility bisection. Gated off normal
    // runs (writes /tmp, no assertions of value); enable with KILN_VT_DUMP=1.
    [Theory]
    [InlineData(1, 1, true, 8, true, "/tmp/sw_1ref_1slice.264")]
    [InlineData(1, 1, true, 8, false, "/tmp/sw_static.264")] // static content → P-frames are all P_Skip
    [InlineData(2, 8, true, 8, true, "/tmp/sw_multi.264")]   // ORIGINAL config: multi-ref + multi-slice
    [InlineData(1, 8, true, 8, true, "/tmp/sw_1ref_multislice.264")]
    public void DumpStream(int maxRefs, int sliceCount, bool intraInP, int subPartCap, bool moving, string path)
    {
        if (Environment.GetEnvironmentVariable("KILN_VT_DUMP") != "1")
        {
            return;
        }
        const int w = 320, h = 240, frames = 150;
        var ySize = w * h;
        var uvW = w / 2;
        var uvH = h / 2;
        var uvSize = uvW * uvH;

        var y = new byte[ySize];
        var u = new byte[uvSize];
        var v = new byte[uvSize];
        var outBuf = new byte[w * h * 2 + 512_000];

        using var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 26,
            KeyframeIntervalFrames = 60,
            MaxReferenceFrames = maxRefs,
            SliceCount = sliceCount,
            EnableIntraInPFallback = intraInP,
            SubPartitionRangeCap = subPartCap,
            // Match the streaming realtime preset shape.
            PreferRealtimeLatencyTuning = true,
            FastSearch = true,
            UseMotionSatd = true,
        });

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        for (var f = 0; f < frames; f++)
        {
            // Moving diagonal gradient + a bouncing bright block → real motion so P-frames carry MVs/residual.
            // When !moving, every frame is identical → P-frames code as all P_Skip (no residual/MV).
            var shift = moving ? f * 3 : 0;
            var motionF = moving ? f : 0;
            for (var j = 0; j < h; j++)
            {
                for (var i = 0; i < w; i++)
                {
                    y[j * w + i] = (byte)((i + j + shift) & 0xFF);
                }
            }
            var bx = 16 + (motionF * 5) % (w - 48);
            var by = 16 + (motionF * 3) % (h - 48);
            for (var j = by; j < by + 32; j++)
                for (var i = bx; i < bx + 32; i++)
                    y[j * w + i] = 235;

            for (var c = 0; c < uvSize; c++)
            {
                u[c] = (byte)(128 + ((c + shift) & 0x1F) - 16);
                v[c] = (byte)(128 - ((c + shift) & 0x1F) + 16);
            }

            var n = enc.EncodeFrame(y, u, v, w, uvW, outBuf);
            fs.Write(outBuf, 0, n);
        }

        Assert.True(new FileInfo(path).Length > 0);
    }
}
