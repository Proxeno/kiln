namespace Kiln.AbHarness;

/// <summary>
/// Deterministic synthetic I420 clips, generated in-process from fixed seeds so the same source
/// produces the same frames on every build under test. Three content kinds:
/// <list type="bullet">
/// <item><c>static</c>: one textured frame, repeated (no motion).</item>
/// <item><c>moving</c>: a value-noise background panned 3 px/frame, plus a bright square moving
/// 16 px/frame and deterministic per-frame sensor noise.</item>
/// <item><c>divergent</c>: the top and bottom halves of the background scroll in opposite
/// directions, two objects move vertically in opposite directions, plus sensor noise.</item>
/// </list>
/// </summary>
internal static class Corpus
{
    /// <summary>Frames per clip in a full run (IDR every 60, so only frame 0 is an IDR).</summary>
    public const int FrameCount = 60;

    /// <summary>Frames per clip in a quick run.</summary>
    public const int QuickFrameCount = 10;

    private const int StaticSeed = 20_260_824;
    private const int DivergentSeed = 20_260_825;
    private const int PanPixelsPerFrame = 3;
    private const int DivergentStepPixelsPerFrame = 6;

    public static byte[][] Generate(string content, int width, int height, int frameCount) => content switch
    {
        "static" => GenerateStatic(width, height, frameCount),
        "moving" => GenerateMoving(width, height, frameCount),
        "divergent" => GenerateDivergent(width, height, frameCount),
        _ => throw new ArgumentException($"Unknown content kind: {content}", nameof(content)),
    };

    private static byte[][] GenerateStatic(int width, int height, int frameCount)
    {
        var texW = width + (PanPixelsPerFrame * (frameCount - 1));
        var texture = ValueNoise(StaticSeed, texW, height);
        var frame = BuildMovingFrame(texture, texW, width, height, 0);
        var frames = new byte[frameCount][];
        Array.Fill(frames, frame);
        return frames;
    }

    private static byte[][] GenerateMoving(int width, int height, int frameCount)
    {
        var texW = width + (PanPixelsPerFrame * (frameCount - 1));
        var texture = ValueNoise(StaticSeed, texW, height);
        var frames = new byte[frameCount][];
        for (var f = 0; f < frameCount; f++)
        {
            frames[f] = BuildMovingFrame(texture, texW, width, height, f);
        }

        return frames;
    }

    private static byte[][] GenerateDivergent(int width, int height, int frameCount)
    {
        var margin = DivergentStepPixelsPerFrame * (frameCount - 1);
        var texW = width + (2 * margin);
        var texture = ValueNoise(DivergentSeed, texW, height);
        var frames = new byte[frameCount][];
        for (var f = 0; f < frameCount; f++)
        {
            frames[f] = BuildDivergentFrame(texture, texW, width, height, margin, f);
        }

        return frames;
    }

    /// <summary>
    /// Coarse value-noise lattice (16 px cells) with bilinear upsampling. Cheap and band-limited
    /// enough that motion search sees trackable structure rather than white noise.
    /// </summary>
    private static byte[] ValueNoise(int seed, int texW, int texH)
    {
        var rng = new Random(seed);
        var latW = (texW / 16) + 2;
        var latH = (texH / 16) + 2;
        var lattice = new byte[latW * latH];
        rng.NextBytes(lattice);

        var tex = new byte[texW * texH];
        for (var y = 0; y < texH; y++)
        {
            var ly = y / 16;
            var fy = (y & 15) / 16.0;
            for (var x = 0; x < texW; x++)
            {
                var lx = x / 16;
                var fx = (x & 15) / 16.0;
                var v00 = lattice[(ly * latW) + lx];
                var v10 = lattice[(ly * latW) + lx + 1];
                var v01 = lattice[((ly + 1) * latW) + lx];
                var v11 = lattice[((ly + 1) * latW) + lx + 1];
                var v = ((v00 * (1 - fx)) + (v10 * fx)) * (1 - fy)
                    + (((v01 * (1 - fx)) + (v11 * fx)) * fy);
                tex[(y * texW) + x] = (byte)(48 + (v * 160.0 / 255.0));
            }
        }

        return tex;
    }

    /// <summary>Pans the background 3 px left per frame and overlays a 16 px/frame moving square.</summary>
    private static byte[] BuildMovingFrame(byte[] texture, int texW, int width, int height, int frame)
    {
        var ys = width * height;
        var uv = ys / 4;
        var buffer = new byte[ys + (2 * uv)];
        var yPlane = buffer.AsSpan(0, ys);
        var uPlane = buffer.AsSpan(ys, uv);
        var vPlane = buffer.AsSpan(ys + uv, uv);

        CopyPanned(texture, texW, yPlane, width, height, PanPixelsPerFrame * frame, PanPixelsPerFrame * frame);
        AddLumaNoise(yPlane, 777_000 + frame);

        var side = Math.Min(96, Math.Min(width, height) / 4);
        var bx = (frame * 16) % Math.Max(1, width - side);
        var by = (height / 2) - (side / 2);
        for (var yy = 0; yy < side; yy++)
        {
            yPlane.Slice(((by + yy) * width) + bx, side).Fill(235);
        }

        FillChroma(uPlane, vPlane, 888_000 + frame);
        return buffer;
    }

    /// <summary>
    /// Top half scrolls one way and the bottom half the other, with two objects moving vertically
    /// in opposite directions — several independent motions in one picture.
    /// </summary>
    private static byte[] BuildDivergentFrame(byte[] texture, int texW, int width, int height, int margin, int frame)
    {
        var ys = width * height;
        var uv = ys / 4;
        var buffer = new byte[ys + (2 * uv)];
        var yPlane = buffer.AsSpan(0, ys);
        var uPlane = buffer.AsSpan(ys, uv);
        var vPlane = buffer.AsSpan(ys + uv, uv);

        var step = DivergentStepPixelsPerFrame * frame;
        CopyPanned(texture, texW, yPlane, width, height, margin - step, step);
        AddLumaNoise(yPlane, 777_100 + frame);

        var side = Math.Min(96, Math.Min(width, height) / 4);
        var byDown = ((height / 4) + (step * 2)) % Math.Max(1, height - side);
        var byUp = (((3 * height / 4) - (step * 2)) + height) % Math.Max(1, height - side);
        for (var yy = 0; yy < side; yy++)
        {
            yPlane.Slice(((byDown + yy) * width) + (width / 4), side).Fill(235);
            yPlane.Slice(((byUp + yy) * width) + (3 * width / 4), side).Fill(20);
        }

        FillChroma(uPlane, vPlane, 888_100 + frame);
        return buffer;
    }

    /// <summary>Copies a window from the texture, using <paramref name="topShift"/> for the top
    /// half and <paramref name="bottomShift"/> for the bottom half.</summary>
    private static void CopyPanned(
        byte[] texture, int texW, Span<byte> yPlane, int width, int height, int topShift, int bottomShift)
    {
        var half = height / 2;
        for (var row = 0; row < height; row++)
        {
            var shift = row < half ? topShift : bottomShift;
            texture.AsSpan((row * texW) + shift, width).CopyTo(yPlane.Slice(row * width, width));
        }
    }

    private static void AddLumaNoise(Span<byte> yPlane, int seed)
    {
        var noise = new Random(seed);
        for (var i = 0; i < yPlane.Length; i++)
        {
            yPlane[i] = (byte)Math.Clamp(yPlane[i] + noise.Next(-2, 3), 0, 255);
        }
    }

    private static void FillChroma(Span<byte> uPlane, Span<byte> vPlane, int seed)
    {
        uPlane.Fill(118);
        vPlane.Fill(138);
        var noise = new Random(seed);
        for (var i = 0; i < uPlane.Length; i++)
        {
            uPlane[i] = (byte)Math.Clamp(uPlane[i] + noise.Next(-1, 2), 0, 255);
            vPlane[i] = (byte)Math.Clamp(vPlane[i] + noise.Next(-1, 2), 0, 255);
        }
    }
}
