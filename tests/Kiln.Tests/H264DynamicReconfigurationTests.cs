using System.Diagnostics;
using FluentAssertions;
using Kiln.RateControl;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Guards the live-reconfiguration surface of <see cref="H264BaselineEncoder"/>:
/// <see cref="H264BaselineEncoder.ApplySpeedMode"/>/<see cref="H264BaselineEncoder.ApplySpeedKnobs"/>
/// (search-only knobs plus the SPS-bounded reference cap) and the per-frame
/// <c>targetBitsPerFrame</c> override on <see cref="H264BaselineEncoder.EncodeFrame"/>.
/// The critical property is the tier-2 one: reference-count changes <em>inside a GOP</em> — both
/// directions — must leave the encoder's reconstruction byte-exact against a conformant decoder,
/// because a desync here is invisible to the encoder and compounds through the DPB (the P_Skip
/// lesson from v0.2.0).
/// </summary>
public sealed class H264DynamicReconfigurationTests
{
    private const int W = 640;
    private const int H = 480;

    // ── tier-2: reference-count transitions inside a GOP ─────────────────────────────────────────

    [Fact]
    public void Mid_gop_speed_mode_transitions_keep_recon_byte_exact_vs_ffmpeg()
    {
        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        const int Frames = 12;
        var frames = GenerateHighMotion(W, H);
        var ys = W * H;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 1_048_576];
        var stream = new MemoryStream();
        var reconPerFrame = new byte[Frames][];

        using (var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
               {
                   QuantizationParameter = 23,
                   KeyframeIntervalFrames = int.MaxValue, // single GOP: every transition is mid-GOP
                   LevelIdc = 40,
               }))
        {
            enc.SignalledMaxReferenceFrames.Should().Be(2);
            for (var i = 0; i < Frames; i++)
            {
                if (i == 4)
                {
                    // Downshift mid-GOP: drops the second reference (2→1), switches to SAD scoring,
                    // tightens the range cap, and arms the effort ceiling — all between frames.
                    enc.ApplySpeedMode(EncoderSpeedMode.VeryFast);
                    enc.ActiveReferenceFrames.Should().Be(1, "the reference cap lowers immediately");
                }
                else if (i == 8)
                {
                    // Upshift mid-GOP: restores the second reference without an IDR. The DPB slot
                    // the cap retired refills on the next reference rotation, so frame 9 runs with
                    // one reference and frame 10 onward with two — matching the decoder's sliding
                    // window, which retained both pictures throughout (§8.2.5.3).
                    enc.ApplySpeedMode(EncoderSpeedMode.HighQuality);
                    enc.ActiveReferenceFrames.Should().Be(2);
                }

                var f = frames[i % frames.Length];
                var n = enc.EncodeFrame(
                    f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), W, W / 2, annex, forceKeyframe: i == 0);
                stream.Write(annex, 0, n);
                reconPerFrame[i] = enc.LastReconstructedY[..ys].ToArray();
            }
        }

        var decoded = FfmpegDecodeAllFrames(stream.ToArray());
        var frameBytes = ys + 2 * uv;
        decoded.Length.Should().BeGreaterThanOrEqualTo(Frames * frameBytes, "ffmpeg must decode all frames");

        for (var i = 0; i < Frames; i++)
        {
            var dec = decoded.AsSpan(i * frameBytes, ys);
            dec.SequenceEqual(reconPerFrame[i]).Should().BeTrue(
                $"frame {i}: encoder luma reconstruction must be byte-exact against the reference " +
                "decoder across mid-GOP reference-count transitions — any divergence is a DPB desync");
        }
    }

    [Fact]
    public void Reference_cap_is_bounded_by_signalled_sps_maximum()
    {
        // Default options signal the full DPB: modes swap the live cap freely underneath it.
        using (var enc = new H264BaselineEncoder(W, H))
        {
            enc.SignalledMaxReferenceFrames.Should().Be(2);
            enc.ActiveReferenceFrames.Should().Be(2);
            enc.ApplySpeedMode(EncoderSpeedMode.Balanced);
            enc.ActiveReferenceFrames.Should().Be(1);
            enc.ApplySpeedMode(EncoderSpeedMode.HighQuality);
            enc.ActiveReferenceFrames.Should().Be(2);
        }

        // An explicit single-reference construction (WebRTC / hardware-decoder-safe SPS) is a hard
        // ceiling: no mode may raise the live cap above what the SPS told the decoder to allocate.
        using (var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions { MaxReferenceFrames = 1 }))
        {
            enc.SignalledMaxReferenceFrames.Should().Be(1);
            enc.ApplySpeedMode(EncoderSpeedMode.HighQuality);
            enc.ActiveReferenceFrames.Should().Be(1, "the SPS-signalled maximum caps every mode");
        }
    }

    // ── tier-1 no-op and byte-identity guarantees ────────────────────────────────────────────────

    [Fact]
    public void Reapplying_the_construction_mode_every_frame_is_byte_identical()
    {
        var plain = EncodeAll(reapplyMode: false);
        var reapplied = EncodeAll(reapplyMode: true);
        reapplied.Should().Equal(plain, "reasserting the current knob values must not perturb the stream");

        static byte[] EncodeAll(bool reapplyMode)
        {
            const int Frames = 6;
            var frames = GenerateHighMotion(320, 240);
            var ys = 320 * 240;
            var uv = ys / 4;
            using var enc = new H264BaselineEncoder(320, 240);
            var annex = new byte[enc.RecommendedOutputBufferSize];
            var stream = new MemoryStream();
            for (var i = 0; i < Frames; i++)
            {
                if (reapplyMode)
                {
                    enc.ApplySpeedMode(EncoderSpeedMode.HighQuality);
                }

                var f = frames[i % frames.Length];
                var n = enc.EncodeFrame(f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), 320, 160, annex);
                stream.Write(annex, 0, n);
            }

            return stream.ToArray();
        }
    }

    // ── per-frame bit-budget override ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Per_frame_budget_equal_to_constructor_budget_is_byte_identical(int slices)
    {
        const int Budget = 30_000;
        var viaCtor = EncodeAll(slices, perFrame: null);
        var viaOverride = EncodeAll(slices, perFrame: Budget);
        viaOverride.Should().Equal(viaCtor,
            "an override equal to the constructor budget must take the identical rate-control path");

        static byte[] EncodeAll(int slices, int? perFrame)
        {
            const int Frames = 6;
            var frames = GenerateHighMotion(320, 240);
            var ys = 320 * 240;
            var uv = ys / 4;
            using var enc = new H264BaselineEncoder(320, 240, new H264BaselineEncoderOptions
            {
                TargetBitsPerFrame = Budget,
                SliceCount = slices,
            });
            var annex = new byte[enc.RecommendedOutputBufferSize];
            var stream = new MemoryStream();
            for (var i = 0; i < Frames; i++)
            {
                var f = frames[i % frames.Length];
                var n = enc.EncodeFrame(
                    f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), 320, 160, annex,
                    targetBitsPerFrame: perFrame);
                stream.Write(annex, 0, n);
            }

            return stream.ToArray();
        }
    }

    [Fact]
    public void Per_frame_budget_steers_coded_frame_size()
    {
        var starved = EncodeSizes(perFrameBits: 8_000);
        var generous = EncodeSizes(perFrameBits: 120_000);

        // Compare steady-state P frames (skip the IDR and the first P while the proportional
        // controller settles).
        for (var i = 2; i < starved.Length; i++)
        {
            starved[i].Should().BeLessThan(generous[i],
                $"frame {i}: an 8 kbit picture budget must code smaller than a 120 kbit one");
        }

        static int[] EncodeSizes(int perFrameBits)
        {
            const int Frames = 6;
            var frames = GenerateHighMotion(320, 240);
            var ys = 320 * 240;
            var uv = ys / 4;
            // Constructor budget 0 (constant QP): the per-frame override alone engages rate control.
            using var enc = new H264BaselineEncoder(320, 240);
            var annex = new byte[enc.RecommendedOutputBufferSize];
            var sizes = new int[Frames];
            for (var i = 0; i < Frames; i++)
            {
                var f = frames[i % frames.Length];
                sizes[i] = enc.EncodeFrame(
                    f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), 320, 160, annex,
                    targetBitsPerFrame: perFrameBits);
            }

            return sizes;
        }
    }

    // ── ffmpeg plumbing (mirrors H264EncoderFfmpegReconDriftTests) ───────────────────────────────

    private static byte[] FfmpegDecodeAllFrames(byte[] annexB)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kiln-dyn-reconfig-{Guid.NewGuid():N}.264");
        var outYuv = tmp + ".yuv";
        try
        {
            File.WriteAllBytes(tmp, annexB);
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", tmp, "-f", "rawvideo", "-pix_fmt", "yuv420p", outYuv })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.Should().Be(0, $"ffmpeg decode must succeed; stderr: {err}");
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(err, "the stream must decode without errors");
            return File.ReadAllBytes(outYuv);
        }
        finally
        {
            File.Delete(tmp);
            if (File.Exists(outYuv))
            {
                File.Delete(outYuv);
            }
        }
    }

    private static bool TryVerifyFfmpegOnPath()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            return p is not null && p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Same shape as the drift-test content: fast diagonal scroll of a textured lattice plus a
    /// faster-moving square — sustained motion that makes the two-reference search commit
    /// refIdx=1 winners, so the reference-count transitions above actually change behaviour.
    /// </summary>
    private static byte[][] GenerateHighMotion(int w, int h)
    {
        const int Cycle = 8;
        var ys = w * h;
        var uv = ys / 4;
        var pad = 12 * Cycle;
        var texW = w + pad;
        var texH = h + pad;
        var tex = new byte[texW * texH];
        var rng = new Random(4242);
        var latW = texW / 12 + 2;
        var latH = texH / 12 + 2;
        var lattice = new byte[latW * latH];
        rng.NextBytes(lattice);
        for (var y = 0; y < texH; y++)
        {
            for (var x = 0; x < texW; x++)
            {
                var v = lattice[(y / 12) * latW + x / 12];
                tex[y * texW + x] = (byte)(40 + (v * 170 / 255) + (((x / 6) + (y / 6)) & 1) * 12);
            }
        }

        var frames = new byte[Cycle][];
        for (var f = 0; f < Cycle; f++)
        {
            var frame = new byte[ys + 2 * uv];
            var yPlane = frame.AsSpan(0, ys);
            var shift = f * 12;
            for (var row = 0; row < h; row++)
            {
                tex.AsSpan((row + shift) * texW + shift, w).CopyTo(yPlane.Slice(row * w, w));
            }

            var side = 80;
            var bx = (f * 26) % Math.Max(1, w - side);
            var by = h / 3;
            for (var yy = 0; yy < side; yy++)
            {
                yPlane.Slice((by + yy) * w + bx, side).Fill(240);
            }

            frame.AsSpan(ys, uv).Fill(110);
            frame.AsSpan(ys + uv, uv).Fill(146);
            frames[f] = frame;
        }

        return frames;
    }
}
