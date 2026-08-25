using System;
using BenchmarkDotNet.Attributes;
using Kiln;
using Kiln.Internal.H264;

namespace Kiln.Benchmarks;

/// <summary>
/// Resolution × slice-count sweep answering "how does per-frame encode cost scale with pixel count,
/// and how much does <see cref="H264BaselineEncoderOptions.SliceCount"/> parallelism recover?".
/// Steady-state P-frames and IDR frames are measured separately; every configuration encodes the
/// same deterministic camera-like content (seeded value-noise background with global scroll motion,
/// ±2 sensor noise per frame, and a moving high-contrast square), scaled to each resolution.
/// </summary>
/// <remarks>
/// LevelIdc is pinned at 40 in every configuration so the exact same options construct on older
/// commits (whose default Level 3.1 rejected 1080p) — the level byte only changes the SPS, never
/// the encode cost. Run e.g.:
/// <c>dotnet run -c Release --project bench/Kiln.Benchmarks -- --filter "*H264ResolutionSliceSweepBenchmarks*" --job short --iterationCount 10 --warmupCount 3 --exporters json</c>
/// </remarks>
[MinColumn, MeanColumn, MedianColumn, MaxColumn, StdDevColumn]
public class H264ResolutionSliceSweepBenchmarks
{
    private const int FrameCycle = 8;
    private const int Qp = 28;

    [Params("640x480", "1280x720", "1920x1080")]
    public string Resolution { get; set; } = "1280x720";

    [Params(1, 2, 4, 8)]
    public int SliceCount { get; set; }

    private int _w;
    private int _h;
    private int _ys;
    private int _uv;
    private byte[][] _frames = null!;
    private byte[] _annex = null!;
    private H264BaselineEncoder _pEncoder = null!;
    private H264BaselineEncoder _idrEncoder = null!;
    private int _pIndex;
    private int _idrIndex;

    [GlobalSetup]
    public void Setup()
    {
        var parts = Resolution.Split('x');
        _w = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        _h = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        _ys = _w * _h;
        _uv = _ys / 4;
        _frames = GenerateFrames(_w, _h);

        H264BaselineEncoderOptions Opts() => new()
        {
            QuantizationParameter = Qp,
            KeyframeIntervalFrames = int.MaxValue,
            LevelIdc = 40,
            SliceCount = SliceCount,
        };

        _pEncoder = new H264BaselineEncoder(_w, _h, Opts());
        _idrEncoder = new H264BaselineEncoder(_w, _h, Opts());
        _annex = new byte[_ys * 2 + 1_048_576];

        // Prime: IDR + two P so the P benchmark starts from a warmed reference/DPB.
        Encode(_pEncoder, 0, forceKeyframe: true);
        Encode(_pEncoder, 1, forceKeyframe: false);
        Encode(_pEncoder, 2, forceKeyframe: false);
        Encode(_idrEncoder, 0, forceKeyframe: true);
        _pIndex = 3;
        _idrIndex = 1;

        Console.WriteLine(
            $"// sweep setup: {_w}x{_h} slices={SliceCount} kernels={H264KernelSet.CreateBest().GetType().Name} " +
            $"procCount={Environment.ProcessorCount}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pEncoder?.Dispose();
        _idrEncoder?.Dispose();
    }

    [Benchmark(Description = "Steady P-frame")]
    public int Encode_steady_P() => Encode(_pEncoder, _pIndex++, forceKeyframe: false);

    [Benchmark(Description = "IDR frame (incl. SPS/PPS)")]
    public int Encode_IDR() => Encode(_idrEncoder, _idrIndex++, forceKeyframe: true);

    private int Encode(H264BaselineEncoder enc, int frameIndex, bool forceKeyframe)
    {
        var f = _frames[frameIndex % FrameCycle];
        return enc.EncodeFrame(
            f.AsSpan(0, _ys), f.AsSpan(_ys, _uv), f.AsSpan(_ys + _uv, _uv),
            _w, _w / 2, _annex, forceKeyframe);
    }

    /// <summary>
    /// Deterministic camera-like content: a smoothed value-noise background texture scrolled 2 px
    /// left per frame (global motion), ±2 per-pixel sensor noise re-seeded per frame index, and a
    /// 96×96 bright square moving 16 px per frame. Identical for every (resolution, slice) cell at
    /// a given resolution; scaled texture, same recipe, across resolutions.
    /// </summary>
    private static byte[][] GenerateFrames(int w, int h)
    {
        var ys = w * h;
        var uv = ys / 4;
        var texW = w + 2 * FrameCycle;
        var tex = new byte[texW * h];
        var rng = new Random(20260824);
        // Coarse value-noise lattice (16 px cells) with bilinear upsampling — cheap and band-limited
        // enough that motion search sees trackable structure rather than white noise.
        var latW = texW / 16 + 2;
        var latH = h / 16 + 2;
        var lattice = new byte[latW * latH];
        rng.NextBytes(lattice);
        for (var y = 0; y < h; y++)
        {
            var ly = y / 16;
            var fy = (y & 15) / 16.0;
            for (var x = 0; x < texW; x++)
            {
                var lx = x / 16;
                var fx = (x & 15) / 16.0;
                var v00 = lattice[ly * latW + lx];
                var v10 = lattice[ly * latW + lx + 1];
                var v01 = lattice[(ly + 1) * latW + lx];
                var v11 = lattice[(ly + 1) * latW + lx + 1];
                var v = (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
                tex[y * texW + x] = (byte)(48 + v * 160.0 / 255.0);
            }
        }

        var frames = new byte[FrameCycle][];
        for (var f = 0; f < FrameCycle; f++)
        {
            var frame = new byte[ys + 2 * uv];
            var yPlane = frame.AsSpan(0, ys);
            var uPlane = frame.AsSpan(ys, uv);
            var vPlane = frame.AsSpan(ys + uv, uv);
            var shift = f * 2;
            for (var row = 0; row < h; row++)
            {
                tex.AsSpan(row * texW + shift, w).CopyTo(yPlane.Slice(row * w, w));
            }

            // Sensor noise: deterministic per frame index, ±2 luma.
            var noise = new Random(777_000 + f);
            for (var i = 0; i < ys; i++)
            {
                yPlane[i] = (byte)Math.Clamp(yPlane[i] + noise.Next(-2, 3), 0, 255);
            }

            // Moving square (object motion on top of global scroll).
            var side = Math.Min(96, Math.Min(w, h) / 4);
            var bx = (f * 16) % Math.Max(1, w - side);
            var by = h / 2 - side / 2;
            for (var yy = 0; yy < side; yy++)
            {
                yPlane.Slice((by + yy) * w + bx, side).Fill(235);
            }

            uPlane.Fill(118);
            vPlane.Fill(138);
            var cNoise = new Random(888_000 + f);
            for (var i = 0; i < uv; i++)
            {
                uPlane[i] = (byte)Math.Clamp(uPlane[i] + cNoise.Next(-1, 2), 0, 255);
                vPlane[i] = (byte)Math.Clamp(vPlane[i] + cNoise.Next(-1, 2), 0, 255);
            }

            frames[f] = frame;
        }

        return frames;
    }
}
