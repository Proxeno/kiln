using System.Diagnostics;
using FluentAssertions;
using Kiln.RateControl;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Gradual intra refresh (<see cref="H264BaselineEncoderOptions.IntraRefreshPeriodFrames"/>,
/// <see cref="H264BaselineEncoder.RequestIntraRefresh"/>). The defining property — and the entire
/// point of the feature — is mid-stream join: a decoder that starts at the wave's first access
/// unit, having seen no IDR and none of the encoder's reference pictures, must reconstruct the
/// picture <em>byte-exactly</em> once the wave completes. That only holds if the PPS
/// <c>constrained_intra_pred_flag</c> handling and the motion-vector restriction against the
/// refreshed region are both right; a test that merely checks intra macroblocks appear in the right
/// columns proves nothing, so these tests decode with ffmpeg and compare reconstructions.
/// QPs are deliberately off the 23/28/33/34 values the historical suites clustered on — two
/// shipped conformance bugs were masked at exactly those QPs.
/// </summary>
public sealed class H264IntraRefreshTests
{
    private const int W = 320;
    private const int H = 240;
    private const int MbW = W / 16;

    private const int TotalFrames = 22;
    private const int WaveRequestFrame = 5;
    private const int RefreshPeriod = 10; // 20 MB columns / period 10 → 2 columns per frame
    private const int WaveFrames = 10;
    private const int RecoveryFrame = WaveRequestFrame + WaveFrames - 1; // first bit-exact joiner frame

    /// <summary>
    /// The defining test. Encode motion content; start one refresh wave mid-stream; hand a decoder
    /// only the access units from the wave's first frame (no IDR, no prior references) and assert
    /// its output is byte-exact against the encoder's reconstruction from the recovery point through
    /// the end of the stream — including the frames just after wave completion, where refIdx-1
    /// still reaches partially-refreshed references. Also decodes the full stream to prove an
    /// established viewer sees byte-exact reconstruction on every frame (constrained intra
    /// prediction and the restricted vectors are conformance-critical for them too).
    /// </summary>
    [Theory]
    [InlineData(26, 1, 2)]
    [InlineData(37, 1, 2)]
    [InlineData(26, 4, 2)]
    [InlineData(14, 1, 2)] // near-lossless: P_Skip-heavy, exercises the skip-side refresh gate
    [InlineData(44, 2, 2)] // heavy quantisation: strongest deblocking, stresses the wave-front margins
    [InlineData(30, 1, 1)] // single reference: no refIdx-1 tail, slot-1 guarantee must not latch
    public void Joining_decoder_converges_byte_exact_after_one_refresh_wave(int qp, int slices, int maxRefs)
    {
        if (!FfmpegOnPath())
        {
            return;
        }

        var frames = GenerateMotionContent();
        var ys = W * H;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 1_048_576];
        var accessUnits = new byte[TotalFrames][];
        var reconY = new byte[TotalFrames][];
        var reconU = new byte[TotalFrames][];
        var reconV = new byte[TotalFrames][];

        using (var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
               {
                   QuantizationParameter = qp,
                   KeyframeIntervalFrames = int.MaxValue,
                   SliceCount = slices,
                   MaxReferenceFrames = maxRefs,
                   IntraRefreshPeriodFrames = RefreshPeriod,
               }))
        {
            enc.IntraRefreshEnabled.Should().BeTrue();
            enc.IntraRefreshWaveFrames.Should().Be(WaveFrames);
            for (var i = 0; i < TotalFrames; i++)
            {
                if (i == WaveRequestFrame)
                {
                    enc.RequestIntraRefresh();
                }

                var f = frames[i % frames.Length];
                var n = enc.EncodeFrame(
                    f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), W, W / 2, annex, forceKeyframe: i == 0);
                accessUnits[i] = annex.AsSpan(0, n).ToArray();
                reconY[i] = enc.LastReconstructedY.ToArray();
                reconU[i] = enc.LastReconstructedU.ToArray();
                reconV[i] = enc.LastReconstructedV.ToArray();
            }

            enc.IntraRefreshActive.Should().BeFalse("the wave must have completed within the stream");
            enc.TestHookFrameShared.RefreshConstraintsActive.Should().BeFalse(
                "all guarantees return to full-picture within two frames of wave completion, " +
                "so the restriction checks must be fully off again");
        }

        // The wave-start access unit must be joinable: SPS(7) + PPS(8) + recovery point SEI(6).
        var waveStartNalTypes = NalTypes(accessUnits[WaveRequestFrame]);
        waveStartNalTypes.Should().Contain([7, 8, 6],
            "a joiner needs parameter sets and the recovery point announcement in the wave-start AU");
        NalTypes(accessUnits[WaveRequestFrame + 1]).Should().NotContain(7,
            "parameter sets are repeated only at the wave start");

        // Established viewer: full-stream decode must match the encoder reconstruction byte-exactly
        // on every frame and every plane.
        var fullStream = Concat(accessUnits, 0, TotalFrames);
        var fullDecoded = FfmpegDecodeAllFrames(fullStream);
        var frameBytes = ys + 2 * uv;
        (fullDecoded.Length / frameBytes).Should().Be(TotalFrames, "full-stream decode must yield every frame");
        for (var i = 0; i < TotalFrames; i++)
        {
            AssertPlanesEqual(fullDecoded, i, frameBytes, reconY[i], reconU[i], reconV[i],
                $"full-stream frame {i} (qp={qp}, slices={slices})");
        }

        // Mid-stream joiner: decode only the AUs from the wave start. ffmpeg honours the recovery
        // point SEI and begins output at the recovery frame; everything it outputs must already be
        // byte-exact. Alignment is from the stream end (the last decoded frame is the last encoded
        // frame), so this holds regardless of how many pre-recovery frames a decoder chooses to
        // emit — but if it emitted any, they would be garbage and fail, so also assert it did not.
        var joinStream = Concat(accessUnits, WaveRequestFrame, TotalFrames - WaveRequestFrame);
        var joinDecoded = FfmpegDecodeAllFrames(joinStream);
        var joinFrames = joinDecoded.Length / frameBytes;
        var expectedJoinFrames = TotalFrames - RecoveryFrame;
        joinFrames.Should().Be(expectedJoinFrames,
            "the joiner must output exactly the frames from the recovery point (SEI honoured), " +
            "and none of the not-yet-converged frames before it");
        for (var k = 0; k < joinFrames; k++)
        {
            var encodedIndex = TotalFrames - joinFrames + k;
            encodedIndex.Should().BeGreaterThanOrEqualTo(RecoveryFrame);
            AssertPlanesEqual(joinDecoded, k, frameBytes,
                reconY[encodedIndex], reconU[encodedIndex], reconV[encodedIndex],
                $"joiner frame {encodedIndex} (qp={qp}, slices={slices}) — a mismatch here means the MV " +
                "restriction leaks unrefreshed content back across the wave boundary");
        }
    }

    /// <summary>
    /// Same input and requests must give identical bytes: the wave is counted in coded frames, so
    /// no wall-clock or scheduling input may reach the bitstream.
    /// </summary>
    [Fact]
    public void Refresh_streams_are_deterministic()
    {
        var a = EncodeRefreshStream();
        var b = EncodeRefreshStream();
        a.AsSpan().SequenceEqual(b).Should().BeTrue("identical inputs and refresh requests must produce identical bytes");
    }

    [Fact]
    public void Requesting_refresh_on_a_disabled_encoder_throws()
    {
        using var enc = new H264BaselineEncoder(W, H);
        enc.IntraRefreshEnabled.Should().BeFalse();
        var act = () => enc.RequestIntraRefresh();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{nameof(H264BaselineEncoderOptions.IntraRefreshPeriodFrames)}*");
    }

    /// <summary>
    /// A scheduled IDR is itself a full recovery: it must cancel an in-flight wave (and a queued
    /// request) rather than leave wave state dangling across the reference reset.
    /// </summary>
    [Fact]
    public void Forced_idr_cancels_an_active_wave()
    {
        var frames = GenerateMotionContent();
        var ys = W * H;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 1_048_576];
        using var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 30,
            KeyframeIntervalFrames = int.MaxValue,
            IntraRefreshPeriodFrames = RefreshPeriod,
        });

        for (var i = 0; i < 6; i++)
        {
            if (i == 2)
            {
                enc.RequestIntraRefresh();
            }

            var f = frames[i % frames.Length];
            var forceIdr = i == 0 || i == 4;
            enc.EncodeFrame(f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), W, W / 2, annex, forceKeyframe: forceIdr);
            if (i == 3)
            {
                enc.IntraRefreshActive.Should().BeTrue("the wave started at frame 2 spans 10 frames");
            }
        }

        enc.IntraRefreshActive.Should().BeFalse("the frame-4 IDR is a full recovery and cancels the wave");
    }

    /// <summary>
    /// Session wiring: a PLI during IDR cooldown produces an intra-refresh decision, and a session
    /// whose encoder has the feature enabled must act on it by starting a wave.
    /// </summary>
    [Fact]
    public void Session_starts_a_wave_when_recovery_asks_for_intra_refresh()
    {
        var frames = GenerateMotionContent();
        var ys = W * H;
        var uv = ys / 4;
        var config = new RateControlConfig
        {
            SupportedWidths = [W],
            SupportedHeights = [H],
            SupportedFps = [30],
            IdrCooldownFrames = 60,
        };
        using var session = new H264StreamingSession(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 30,
            KeyframeIntervalFrames = int.MaxValue,
            IntraRefreshPeriodFrames = RefreshPeriod,
        }, config);
        var annex = new byte[session.RecommendedOutputBufferSize];

        var stream = new MemoryStream();
        var sessionRecon = new List<(byte[] Y, byte[] U, byte[] V)>();

        H264StreamingEncodeResult Encode(int i, bool pli)
        {
            var f = frames[i % frames.Length];
            var feedback = new EncoderNetworkFeedback(
                EstimatedAvailableBitrateBps: 10_000_000,
                PacketLossRatio: 0.0,
                RoundTripTime: TimeSpan.FromMilliseconds(20),
                Jitter: TimeSpan.FromMilliseconds(2),
                PendingRtpBytes: 5_000,
                NackCount: 0,
                PictureLossIndication: pli,
                FullIntraRequest: false,
                ClientDecodeDelay: null);
            var r = session.EncodeFrame(f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), W, W / 2, annex, feedback);
            stream.Write(annex, 0, r.BytesWritten);
            var e = session.EncoderForTests;
            sessionRecon.Add((e.LastReconstructedY.ToArray(), e.LastReconstructedU.ToArray(), e.LastReconstructedV.ToArray()));
            return r;
        }

        Encode(0, pli: false);

        // First PLI: cooldown idle → IDR.
        var idrResult = Encode(1, pli: true);
        idrResult.WasIdr.Should().BeTrue("first PLI outside cooldown recovers via IDR");
        idrResult.IntraRefreshRequested.Should().BeFalse();

        // Second PLI inside the cooldown: the policy asks for intra refresh, and with the feature
        // enabled the session starts a wave instead of encoding a normal frame and shrugging.
        var refreshResult = Encode(2, pli: true);
        refreshResult.WasIdr.Should().BeFalse("the IDR cooldown must hold");
        refreshResult.IntraRefreshRequested.Should().BeTrue();
        session.EncoderForTests.IntraRefreshActive.Should().BeTrue("the session must act on the request");

        for (var i = 3; i < 3 + WaveFrames; i++)
        {
            Encode(i, pli: false);
        }

        session.EncoderForTests.IntraRefreshActive.Should().BeFalse("the wave completes after one period");

        // The session drives per-MB rate control (mb_qp_delta chains) through the wave — decode the
        // whole stream and require byte-exact reconstruction, so the refresh band and the restricted
        // vectors are proven conformant under rate control too, not only at constant QP.
        if (FfmpegOnPath())
        {
            var decoded = FfmpegDecodeAllFrames(stream.ToArray());
            var frameBytes = ys + 2 * uv;
            (decoded.Length / frameBytes).Should().Be(sessionRecon.Count, "every session frame must decode");
            for (var i = 0; i < sessionRecon.Count; i++)
            {
                AssertPlanesEqual(decoded, i, frameBytes,
                    sessionRecon[i].Y, sessionRecon[i].U, sessionRecon[i].V, $"session frame {i}");
            }
        }
    }

    /// <summary>
    /// Unaligned display size: the wave, the guarantee bounds and the MV restriction all operate on
    /// the coded (macroblock-aligned) grid while the decoder outputs the cropped display size; the
    /// joiner must still converge byte-exactly on the cropped planes.
    /// </summary>
    [Fact]
    public void Joining_decoder_converges_at_unaligned_display_size()
    {
        if (!FfmpegOnPath())
        {
            return;
        }

        const int Dw = 308;
        const int Dh = 230;
        var frames = GenerateMotionContent();
        var ys = Dw * Dh;
        var uv = Dw / 2 * (Dh / 2);
        var srcFrames = new byte[TotalFrames][];
        for (var i = 0; i < TotalFrames; i++)
        {
            // Crop the 320×240 generator content to the display size, planar I420.
            var srcFull = frames[i % frames.Length];
            var dst = new byte[ys + 2 * uv];
            for (var row = 0; row < Dh; row++)
            {
                Array.Copy(srcFull, row * W, dst, row * Dw, Dw);
            }

            for (var plane = 0; plane < 2; plane++)
            {
                var srcOff = W * H + plane * (W * H / 4);
                var dstOff = ys + plane * uv;
                for (var row = 0; row < Dh / 2; row++)
                {
                    Array.Copy(srcFull, srcOff + row * (W / 2), dst, dstOff + row * (Dw / 2), Dw / 2);
                }
            }

            srcFrames[i] = dst;
        }

        var annex = new byte[2 * 320 * 240 + 1_048_576];
        var accessUnits = new byte[TotalFrames][];
        var recon = new (byte[] Y, byte[] U, byte[] V)[TotalFrames];
        using (var enc = new H264BaselineEncoder(Dw, Dh, new H264BaselineEncoderOptions
               {
                   QuantizationParameter = 29,
                   KeyframeIntervalFrames = int.MaxValue,
                   IntraRefreshPeriodFrames = RefreshPeriod,
               }))
        {
            for (var i = 0; i < TotalFrames; i++)
            {
                if (i == WaveRequestFrame)
                {
                    enc.RequestIntraRefresh();
                }

                var f = srcFrames[i];
                var n = enc.EncodeFrame(
                    f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), Dw, Dw / 2, annex, forceKeyframe: i == 0);
                accessUnits[i] = annex.AsSpan(0, n).ToArray();
                var yPlane = new byte[ys];
                var uPlane = new byte[uv];
                var vPlane = new byte[uv];
                enc.CopyLastReconstructedTo(yPlane, uPlane, vPlane, Dw, Dw / 2);
                recon[i] = (yPlane, uPlane, vPlane);
            }
        }

        var joinStream = Concat(accessUnits, WaveRequestFrame, TotalFrames - WaveRequestFrame);
        var joinDecoded = FfmpegDecodeAllFrames(joinStream);
        var frameBytes = ys + 2 * uv;
        var joinFrames = joinDecoded.Length / frameBytes;
        joinFrames.Should().Be(TotalFrames - RecoveryFrame, "output starts at the recovery point");
        for (var k = 0; k < joinFrames; k++)
        {
            var encodedIndex = TotalFrames - joinFrames + k;
            var baseOff = k * frameBytes;
            CountMismatches(joinDecoded.AsSpan(baseOff, ys), recon[encodedIndex].Y)
                .Should().Be(0, $"cropped joiner luma, frame {encodedIndex}");
            CountMismatches(joinDecoded.AsSpan(baseOff + ys, uv), recon[encodedIndex].U)
                .Should().Be(0, $"cropped joiner U, frame {encodedIndex}");
            CountMismatches(joinDecoded.AsSpan(baseOff + ys + uv, uv), recon[encodedIndex].V)
                .Should().Be(0, $"cropped joiner V, frame {encodedIndex}");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static byte[] EncodeRefreshStream()
    {
        var frames = GenerateMotionContent();
        var ys = W * H;
        var uv = ys / 4;
        var annex = new byte[ys * 2 + 1_048_576];
        using var enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 31,
            KeyframeIntervalFrames = int.MaxValue,
            SliceCount = 2,
            IntraRefreshPeriodFrames = RefreshPeriod,
        });
        var stream = new MemoryStream();
        for (var i = 0; i < TotalFrames; i++)
        {
            if (i == WaveRequestFrame)
            {
                enc.RequestIntraRefresh();
            }

            var f = frames[i % frames.Length];
            var n = enc.EncodeFrame(f.AsSpan(0, ys), f.AsSpan(ys, uv), f.AsSpan(ys + uv, uv), W, W / 2, annex, forceKeyframe: i == 0);
            stream.Write(annex, 0, n);
        }

        return stream.ToArray();
    }

    private static void AssertPlanesEqual(
        byte[] decoded, int decodedIndex, int frameBytes,
        byte[] expectedY, byte[] expectedU, byte[] expectedV, string what)
    {
        var ys = W * H;
        var uv = ys / 4;
        var baseOff = decodedIndex * frameBytes;
        CountMismatches(decoded.AsSpan(baseOff, ys), expectedY).Should().Be(0, $"{what}: luma must be byte-exact");
        CountMismatches(decoded.AsSpan(baseOff + ys, uv), expectedU).Should().Be(0, $"{what}: U must be byte-exact");
        CountMismatches(decoded.AsSpan(baseOff + ys + uv, uv), expectedV).Should().Be(0, $"{what}: V must be byte-exact");
    }

    private static int CountMismatches(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
    {
        var bad = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                bad++;
            }
        }

        return bad;
    }

    private static byte[] Concat(byte[][] accessUnits, int start, int count)
    {
        var ms = new MemoryStream();
        for (var i = start; i < start + count; i++)
        {
            ms.Write(accessUnits[i]);
        }

        return ms.ToArray();
    }

    /// <summary>NAL unit types present in one access unit, in order (Annex B start-code scan).</summary>
    private static List<int> NalTypes(byte[] annexB)
    {
        var types = new List<int>();
        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                types.Add(annexB[i + 3] & 0x1F);
                i += 3;
            }
        }

        return types;
    }

    /// <summary>
    /// Motion content that stresses the wave boundary in the dangerous direction: a leftward
    /// texture scroll (motion compensation reads to the <em>right</em>, toward unrefreshed
    /// content), a fast bright block crossing the picture, and textured chroma so the chroma-side
    /// restriction margins are exercised too.
    /// </summary>
    private static byte[][] GenerateMotionContent()
    {
        const int Cycle = 11;
        var ys = W * H;
        var uv = ys / 4;
        var pad = 8 * Cycle + 32;
        var texW = W + pad;
        var texH = H + pad;
        var tex = new byte[texW * texH];
        var rng = new Random(97531);
        var latW = texW / 8 + 2;
        var latH = texH / 8 + 2;
        var lattice = new byte[latW * latH];
        rng.NextBytes(lattice);
        for (var y = 0; y < texH; y++)
        {
            for (var x = 0; x < texW; x++)
            {
                var v = lattice[(y / 8) * latW + x / 8];
                tex[y * texW + x] = (byte)(36 + (v * 168 / 255) + (((x / 4) + (y / 4)) & 1) * 14);
            }
        }

        var frames = new byte[Cycle][];
        for (var f = 0; f < Cycle; f++)
        {
            var frame = new byte[ys + 2 * uv];
            var yPlane = frame.AsSpan(0, ys);
            var shift = f * 8;
            for (var row = 0; row < H; row++)
            {
                tex.AsSpan((row + shift / 2) * texW + shift, W).CopyTo(yPlane.Slice(row * W, W));
            }

            const int Side = 48;
            var bx = (f * 21) % (W - Side);
            var by = H / 2 - Side / 2;
            for (var yy = 0; yy < Side; yy++)
            {
                yPlane.Slice((by + yy) * W + bx, Side).Fill(235);
            }

            var uPlane = frame.AsSpan(ys, uv);
            var vPlane = frame.AsSpan(ys + uv, uv);
            var cw = W / 2;
            var ch = H / 2;
            for (var row = 0; row < ch; row++)
            {
                for (var col = 0; col < cw; col++)
                {
                    var t = tex[(row * 2 + shift / 2) * texW + col * 2 + shift];
                    uPlane[row * cw + col] = (byte)(96 + (t >> 3));
                    vPlane[row * cw + col] = (byte)(160 - (t >> 3));
                }
            }

            frames[f] = frame;
        }

        return frames;
    }

    private static byte[] FfmpegDecodeAllFrames(byte[] annexB)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kiln-gdr-{Guid.NewGuid():N}.264");
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

    private static bool FfmpegOnPath()
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
}
