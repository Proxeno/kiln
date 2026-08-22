using System;
using BenchmarkDotNet.Attributes;
using Kiln;
using Kiln.Internal.H264;

namespace Kiln.Benchmarks;

/// <summary>
/// A/B: steady-state P-frame encode cost with experimental Phase 2b enabled vs disabled.
/// Uses single-slice tiling; slice-count amplification is isolated via the VideoToolbox-comparison benchmarks (out of scope here).
/// </summary>
/// <remarks>
/// In-process profiling (SampleProfiler Speedscope — use BDN <c>*Method*(Param: value)*</c> filter syntax), e.g.:
/// <c>dotnet trace collect --providers Microsoft-DotNETCore-SampleProfiler --format speedscope -o trace.speedscope.json --duration 00:00:00:35 --
/// dotnet …/Kiln.Benchmarks.dll -i --job short --iterationcount 300 --warmupcount 3
/// --filter '*Encode_primmed_P(EnableIntraInPFallback: True)*'</c>
/// </remarks>
[MinColumn, MeanColumn, MaxColumn]
public class H264PInterPhase2bAblationBenchmarks
{
    /// <summary>true = production default (intra-vs-inter competition in P-slices); false = pure inter.</summary>
    [Params(false, true)]
    public bool EnableIntraInPFallback { get; set; }

    private const int W = 1280;
    private const int H = 720;
    private readonly int _ys = W * H;
    private readonly int _uv = W * H / 4;

    private byte[] _i420A = null!;
    private byte[] _i420B = null!;
    private byte[] _annex = null!;
    private H264BaselineEncoder _enc = null!;
    private int _frameToggle;

    [GlobalSetup(Target = nameof(Encode_primmed_P))]
    public void PerCaseSetup()
    {
        _i420A = new byte[_ys + 2 * _uv];
        _i420B = new byte[_ys + 2 * _uv];
        _annex = new byte[_ys * 2 + 512_000];
        FillSyntheticMotionFrame(_i420A, frameIndex: 0);
        FillSyntheticMotionFrame(_i420B, frameIndex: 1);
        ResetPrimedEncoder();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        ResetPrimedEncoder();
    }

    [GlobalCleanup(Target = nameof(Encode_primmed_P))]
    public void PerCaseCleanup()
    {
        _enc?.Dispose();
        _enc = null!;
        Console.WriteLine(H264PInterDiagnostics.BuildPhase2bRdReport());
        H264PInterDiagnostics.DisablePhase2bManual = false;
        H264PInterDiagnostics.CollectPhaseCounts = false;
        H264PInterDiagnostics.CollectPhase2bRdAccounting = false;
        H264PInterDiagnostics.ResetPhaseCounts();
        H264PInterDiagnostics.ResetPhase2bRdAccounting();
    }

    [Benchmark(Description = "Steady P-frame 1280×720, SliceCount=1; Phase 2b on/off")]
    public int Encode_primmed_P()
    {
        var frame = (_frameToggle++ & 1) == 0 ? _i420A : _i420B;
        return _enc.EncodeFrame(
            frame.AsSpan(0, _ys),
            frame.AsSpan(_ys, _uv),
            frame.AsSpan(_ys + _uv, _uv),
            W,
            W / 2,
            _annex,
            forceKeyframe: false);
    }

    private static void Prime(H264BaselineEncoder enc, byte[] i420A, byte[] i420B, int w, int ys, int uv, byte[] annex)
    {
        _ = enc.EncodeFrame(i420A.AsSpan(0, ys), i420A.AsSpan(ys, uv),
            i420A.AsSpan(ys + uv, uv), w, w / 2, annex, forceKeyframe: true);
        _ = enc.EncodeFrame(i420B.AsSpan(0, ys), i420B.AsSpan(ys, uv),
            i420B.AsSpan(ys + uv, uv), w, w / 2, annex, forceKeyframe: false);
    }

    private void FillSyntheticMotionFrame(byte[] i420, int frameIndex)
    {
        var y = i420.AsSpan(0, _ys);
        var u = i420.AsSpan(_ys, _uv);
        var v = i420.AsSpan(_ys + _uv, _uv);
        y.Fill(40);
        u.Fill(128);
        v.Fill(128);

        const int bw = 96;
        const int bh = 96;
        var bx = (frameIndex * 24) % Math.Max(1, W - bw);
        var by = (H / 2) - (bh / 2);
        for (var yy = 0; yy < bh; yy++)
        {
            var row = (by + yy) * W + bx;
            y.Slice(row, bw).Fill(220);
        }
    }

    private void ResetPrimedEncoder()
    {
        H264PInterDiagnostics.DisablePhase2bManual = false;
        H264PInterDiagnostics.CollectPhaseCounts = true;
        H264PInterDiagnostics.CollectPhase2bRdAccounting = true;
        H264PInterDiagnostics.ResetPhaseCounts();
        H264PInterDiagnostics.ResetPhase2bRdAccounting();
        _frameToggle = 0;
        _enc?.Dispose();
        _enc = new H264BaselineEncoder(W, H, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = 10_000,
            PreferRealtimeLatencyTuning = false,
            LightweightDeblocking = false,
            SliceCount = 1,
            EnableIntraInPFallback = EnableIntraInPFallback,
        });

        Prime(_enc, _i420A, _i420B, W, _ys, _uv, _annex);
    }
}
