using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Acceptance test for Junior-D-pred. Every of the 256 sample positions in every of the 4
/// Intra_16×16 luma prediction modes (Vertical, Horizontal, DC, Plane) must be bit-exact to a
/// spec-derived reference, across multiple availability and edge-pattern fixtures. Plane mode
/// (8.3.3.4) is the highest drift-risk because it touches edge samples in three different ways.
/// </summary>
public sealed class H264Intra16x16PredictorParityTests
{
    private static void ReferencePredict(
        int mode,
        ReadOnlySpan<byte> topRow,
        bool topAvail,
        ReadOnlySpan<byte> leftCol,
        bool leftAvail,
        byte topLeft,
        bool topLeftAvail,
        Span<byte> dst256)
    {
        switch (mode)
        {
            case 0:
                for (var y = 0; y < 16; y++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        dst256[y * 16 + x] = topRow[x];
                    }
                }

                return;
            case 1:
                for (var y = 0; y < 16; y++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        dst256[y * 16 + x] = leftCol[y];
                    }
                }

                return;
            case 2:
            {
                int dc;
                if (topAvail && leftAvail)
                {
                    var s = 0;
                    for (var i = 0; i < 16; i++)
                    {
                        s += topRow[i];
                        s += leftCol[i];
                    }

                    dc = (s + 16) >> 5;
                }
                else if (topAvail)
                {
                    var s = 0;
                    for (var i = 0; i < 16; i++)
                    {
                        s += topRow[i];
                    }

                    dc = (s + 8) >> 4;
                }
                else if (leftAvail)
                {
                    var s = 0;
                    for (var i = 0; i < 16; i++)
                    {
                        s += leftCol[i];
                    }

                    dc = (s + 8) >> 4;
                }
                else
                {
                    dc = 128;
                }

                dst256.Fill((byte)Math.Clamp(dc, 0, 255));
                return;
            }

            case 3:
            {
                var p = new int[33];
                p[0] = topLeftAvail ? topLeft : 0;
                for (var i = 0; i < 16; i++)
                {
                    p[1 + i] = topRow[i];
                }

                for (var i = 0; i < 16; i++)
                {
                    p[17 + i] = leftCol[i];
                }

                var hSum = 0;
                for (var i = 0; i < 8; i++)
                {
                    hSum += (i + 1) * (p[1 + 8 + i] - p[1 + 6 - i]);
                }

                var vSum = 0;
                for (var j = 0; j < 8; j++)
                {
                    // §8.3.3.4: V uses p[−1, 6−y']; for y'=7 that is p[−1,−1] = topLeft (p[0]).
                    // Per H.264 §8.3.3 Intra_16x16 plane prediction (src[−stride−1] for the last term), not p[16].
                    var lower = j < 7 ? p[17 + 6 - j] : p[0];
                    vSum += (j + 1) * (p[17 + 8 + j] - lower);
                }

                var b = (5 * hSum + 32) >> 6;
                var c = (5 * vSum + 32) >> 6;
                var a = 16 * (p[17 + 15] + p[1 + 15]);
                for (var y = 0; y < 16; y++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        var pred = (a + b * (x - 7) + c * (y - 7) + 16) >> 5;
                        dst256[y * 16 + x] = (byte)Math.Clamp(pred, 0, 255);
                    }
                }

                return;
            }
        }
    }

    public static IEnumerable<object[]> Fixtures()
    {
        yield return ["uniform_128", Top(128), Left(128), (byte)128, true, true, true];
        yield return ["uniform_0", Top(0), Left(0), (byte)0, true, true, true];
        yield return ["uniform_255", Top(255), Left(255), (byte)255, true, true, true];
        yield return [
            "horizontal_gradient",
            Range(0, 16, x => (byte)(x * 16 + 8)),
            Left(128), (byte)128, true, true, true,
        ];
        yield return [
            "vertical_gradient",
            Top(128),
            Range(0, 16, y => (byte)(y * 16 + 8)),
            (byte)128, true, true, true,
        ];
        yield return [
            "plane_diagonal",
            Range(0, 16, x => (byte)Math.Clamp(64 + x * 8, 0, 255)),
            Range(0, 16, y => (byte)Math.Clamp(64 + y * 8, 0, 255)),
            (byte)64, true, true, true,
        ];
        yield return ["top_only", Top(200), Left(0), (byte)200, true, false, true];
        yield return ["left_only", Top(0), Left(80), (byte)80, false, true, true];
        yield return ["no_neighbors", Top(0), Left(0), (byte)0, false, false, false];
        yield return [
            "rng_seeded",
            SeedBytes(unchecked((int)0xCAFEBABE), 16),
            SeedBytes(unchecked((int)0xCAFEBABE) + 1, 16),
            (byte)0x77, true, true, true,
        ];
        yield return [
            "edge_high_contrast",
            new byte[] { 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0 },
            new byte[] { 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255 },
            (byte)128, true, true, true,
        ];
        yield return [
            "plane_overflow_check",
            Range(0, 16, x => (byte)(255 - x * 16)),
            Range(0, 16, y => (byte)(255 - y * 16)),
            (byte)255, true, true, true,
        ];
    }

    private static byte[] Top(byte v)
    {
        var a = new byte[16];
        Array.Fill(a, v);
        return a;
    }

    private static byte[] Left(byte v) => Top(v);

    private static byte[] Range(int start, int count, Func<int, byte> f)
    {
        var a = new byte[count];
        for (var i = 0; i < count; i++)
        {
            a[i] = f(start + i);
        }

        return a;
    }

    private static byte[] SeedBytes(int seed, int n)
    {
        var rng = new Random(seed);
        var a = new byte[n];
        rng.NextBytes(a);
        return a;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_mode_every_position_matches_spec_reference(
        string fixtureName,
        byte[] topRow,
        byte[] leftCol,
        byte topLeft,
        bool topAvail,
        bool leftAvail,
        bool topLeftAvail)
    {
        Span<byte> got = stackalloc byte[256];
        Span<byte> expected = stackalloc byte[256];

        for (var mode = 0; mode <= 3; mode++)
        {
            if (mode == 0 && !topAvail)
            {
                continue;
            }

            if (mode == 1 && !leftAvail)
            {
                continue;
            }

            if (mode == 3 && (!topAvail || !leftAvail || !topLeftAvail))
            {
                continue;
            }

            got.Clear();
            expected.Clear();

            H264Intra16x16Prediction.Predict(
                mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, got);
            ReferencePredict(
                mode, topRow, topAvail, leftCol, leftAvail, topLeft, topLeftAvail, expected);

            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var pos = y * 16 + x;
                    got[pos].Should().Be(expected[pos],
                        $"fixture '{fixtureName}' mode={mode} pos=(x={x},y={y})");
                }
            }
        }
    }
}
