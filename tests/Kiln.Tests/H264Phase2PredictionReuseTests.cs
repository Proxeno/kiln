using FluentAssertions;
using Kiln;
using Kiln.Internal.H264;
using Kiln.RateControl;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Gate for reusing the Phase-1 skip prediction in Phase 2. When the motion search lands on the
/// same 16x16 refIdx-0 MV the skip test used, Phase 2 used to reconstruct the inter prediction a
/// second time; the reused prediction must be byte-identical to that rebuild, so both paths must
/// emit the same Annex-B bitstream.
/// </summary>
public sealed class H264Phase2PredictionReuseTests
{
    /// <summary>
    /// The reuse and the rebuild must produce identical streams on synthetic clips whose motion
    /// makes the search return to the skip MV (otherwise the reuse path is never exercised).
    /// </summary>
    [Theory]
    [InlineData(EncoderSpeedMode.VeryFast, 4, 8, 4)]
    [InlineData(EncoderSpeedMode.VeryFast, 3, 4, 1)]
    [InlineData(EncoderSpeedMode.HighQuality, 4, 6, 4)]
    public void Reusing_the_phase1_prediction_emits_identical_bytes(
        EncoderSpeedMode speedMode, int shiftPerFrame, int noiseAmplitude, int sliceCount)
    {
        const int w = 128;
        const int h = 96;
        const int frameCount = 14;
        var frames = BuildFrames(w, h, shiftPerFrame, noiseAmplitude, frameCount);

        var savedDisable = H264PInterDiagnostics.DisablePhase2PredReuse;
        var savedCollect = H264PInterDiagnostics.CollectPhase1Timing;
        try
        {
            H264PInterDiagnostics.CollectPhase1Timing = true;

            H264PInterDiagnostics.ResetPhase1Timing();
            H264PInterDiagnostics.DisablePhase2PredReuse = false;
            var reusedStream = Encode(frames, w, h, speedMode, sliceCount);
            var (_, duplicatingWithReuse, reused) = H264PInterDiagnostics.ReadPhase2ReconCounts();

            H264PInterDiagnostics.ResetPhase1Timing();
            H264PInterDiagnostics.DisablePhase2PredReuse = true;
            var rebuiltStream = Encode(frames, w, h, speedMode, sliceCount);
            var (_, duplicatingWithoutReuse, reusedWithoutReuse) = H264PInterDiagnostics.ReadPhase2ReconCounts();

            reused.Should().BeGreaterThan(0,
                "the clip must exercise the reuse path (search returns to the skip MV) for this gate to prove anything");
            reusedWithoutReuse.Should().Be(0, "the A/B switch must disable reuse");
            duplicatingWithReuse.Should().Be(0, "a reused prediction is never rebuilt");
            duplicatingWithoutReuse.Should().Be(reused,
                "every reuse is exactly a rebuild the A/B path duplicates");
            reusedStream.Should().Equal(rebuiltStream, "reusing a byte-identical prediction cannot change the bitstream");
        }
        finally
        {
            H264PInterDiagnostics.DisablePhase2PredReuse = savedDisable;
            H264PInterDiagnostics.CollectPhase1Timing = savedCollect;
        }
    }

    private static byte[] Encode(byte[][] frames, int w, int h, EncoderSpeedMode speedMode, int sliceCount)
    {
        var options = new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = int.MaxValue,
            LevelIdc = 40,
            SliceCount = sliceCount,
            SpeedMode = speedMode,
        };
        var ySize = w * h;
        var uvSize = ySize / 4;
        var dest = new byte[ySize * 2 + 1_048_576];
        using var encoder = new H264BaselineEncoder(w, h, options);
        var written = 0;
        for (var i = 0; i < frames.Length; i++)
        {
            var frame = frames[i];
            written += encoder.EncodeFrame(
                frame.AsSpan(0, ySize),
                frame.AsSpan(ySize, uvSize),
                frame.AsSpan(ySize + uvSize, uvSize),
                w,
                w / 2,
                dest.AsSpan(written),
                forceKeyframe: i == 0);
            if (written > dest.Length - (ySize * 2 + 4096))
                throw new InvalidOperationException("Annex-B buffer too small");
        }

        return dest.AsSpan(0, written).ToArray();
    }

    /// <summary>
    /// Deterministic smooth texture scrolled <paramref name="shiftPerFrame"/> pixels left per frame,
    /// with per-frame sensor noise and one fast-moving block. The scroll keeps the spatial skip MV
    /// on the true motion while the noise pushes SAD above the skip gate, so the search has to run
    /// and can still return the skip MV.
    /// </summary>
    private static byte[][] BuildFrames(int w, int h, int shiftPerFrame, int noiseAmplitude, int frameCount)
    {
        var ySize = w * h;
        var uvSize = ySize / 4;
        var uvW = w / 2;
        var texW = w + shiftPerFrame * frameCount + 16;
        var texH = h + 16;
        var rng = new Random(20261126);
        var latticeW = texW / 8 + 2;
        var latticeH = texH / 8 + 2;
        var lattice = new byte[latticeW * latticeH];
        rng.NextBytes(lattice);
        var tex = new byte[texW * texH];
        for (var y = 0; y < texH; y++)
        {
            var ly = y / 8;
            for (var x = 0; x < texW; x++)
            {
                var lx = x / 8;
                tex[y * texW + x] = (byte)(40 + (lattice[ly * latticeW + lx] * 160 / 255));
            }
        }

        var frames = new byte[frameCount][];
        for (var f = 0; f < frameCount; f++)
        {
            var frame = new byte[ySize + 2 * uvSize];
            var yPlane = frame.AsSpan(0, ySize);
            var uPlane = frame.AsSpan(ySize, uvSize);
            var vPlane = frame.AsSpan(ySize + uvSize, uvSize);
            var frameRng = new Random(998877 + f);
            var shift = shiftPerFrame * f;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var value = (int)tex[y * texW + x + shift];
                    if (noiseAmplitude > 0)
                        value += frameRng.Next(-noiseAmplitude, noiseAmplitude + 1);
                    yPlane[y * w + x] = (byte)Math.Clamp(value, 0, 255);
                }
            }

            var side = 16;
            var bx = (f * 5) % (w - side);
            var by = h / 2;
            for (var yy = 0; yy < side; yy++)
                yPlane.Slice((by + yy) * w + bx, side).Fill(240);

            for (var y = 0; y < h / 2; y++)
            {
                for (var x = 0; x < uvW; x++)
                {
                    var value = (int)tex[(y * 2) * texW + (x * 2) + shift];
                    if (noiseAmplitude > 0)
                        value += frameRng.Next(-noiseAmplitude, noiseAmplitude + 1);
                    uPlane[y * uvW + x] = (byte)Math.Clamp(96 + (value - 120) / 2, 0, 255);
                    vPlane[y * uvW + x] = (byte)Math.Clamp(160 - (value - 120) / 2, 0, 255);
                }
            }

            frames[f] = frame;
        }

        return frames;
    }
}
