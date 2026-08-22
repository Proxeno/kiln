using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Acceptance test for Junior-C: every (mode, x, y, fixture) sample produced by
/// <see cref="H264Intra4X4Prediction.Predict"/> must be bit-exact to a spec-derived reference.
/// Modes 0..3 (Vertical, Horizontal, DC, Diagonal_Down_Left) are already known-good and act as a
/// regression guard; modes 4..8 are the focus of Junior-C's work and where the previous session
/// observed silent encoder/decoder drift.
/// </summary>
/// <remarks>
/// The reference is a port of the H.264 8.3.1.2 prediction formulas. <see cref="ReferencePredict"/>
/// is the oracle and is written for clarity, not speed; it must never call the production code under
/// test. Failures are reported per (mode, x, y) so a single sample bug pinpoints the exact spec
/// branch to fix (e.g. the <c>zVR == -1</c> top-left corner blend in 8.3.1.2.6).
/// </remarks>
public sealed class H264Intra4x4PredictorParityTests
{
    /// <summary>
    /// Spec-derived reference for the 9 H.264 4×4 intra prediction modes (8.3.1.2.1 .. 8.3.1.2.9).
    /// Inputs match the production API: <paramref name="topRow"/> is 9 bytes layered as
    /// [TL, T0, T1, T2, T3, T4, T5, T6, T7] (caller has already replicated T4..T7 from T3 if the
    /// top-right block is unavailable); <paramref name="leftCol"/> is L0..L3.
    /// </summary>
    private static void ReferencePredict(
        int mode,
        ReadOnlySpan<byte> topRow,
        ReadOnlySpan<byte> leftCol,
        bool topAvail,
        bool leftAvail,
        Span<byte> dst16)
    {
        if (mode is 0 or 3 or 7 && !topAvail)
        {
            mode = 2;
        }
        else if (mode is 1 or 8 && !leftAvail)
        {
            mode = 2;
        }
        else if (mode is 4 or 5 or 6 && (!topAvail || !leftAvail))
        {
            mode = 2;
        }

        var tl = topRow[0];
        Span<byte> t = stackalloc byte[8];
        for (var i = 0; i < 8; i++)
        {
            t[i] = topRow[1 + i];
        }

        Span<byte> l = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            l[i] = leftCol[i];
        }

        switch (mode)
        {
            case 0:
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        dst16[y * 4 + x] = t[x];
                    }
                }

                return;
            case 1:
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        dst16[y * 4 + x] = l[y];
                    }
                }

                return;
            case 2:
            {
                var sum = 0;
                var n = 0;
                if (topAvail)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        sum += t[i];
                        n++;
                    }
                }

                if (leftAvail)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        sum += l[i];
                        n++;
                    }
                }

                var dc = (byte)(n == 0 ? 128 : (sum + (n >> 1)) / n);
                dst16.Fill(dc);
                return;
            }

            case 3:
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        int v;
                        if (x == 3 && y == 3)
                        {
                            v = (t[6] + 3 * t[7] + 2) >> 2;
                        }
                        else
                        {
                            v = (t[x + y] + 2 * t[x + y + 1] + t[x + y + 2] + 2) >> 2;
                        }

                        dst16[y * 4 + x] = (byte)v;
                    }
                }

                return;
            case 4:
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        int v;
                        if (x > y)
                        {
                            v = (PTopOrTl(x - y - 2, tl, t) + 2 * PTopOrTl(x - y - 1, tl, t) + t[x - y] + 2) >> 2;
                        }
                        else if (x < y)
                        {
                            v = (PLeftOrTl(y - x - 2, tl, l) + 2 * PLeftOrTl(y - x - 1, tl, l) + l[y - x] + 2) >> 2;
                        }
                        else
                        {
                            v = (t[0] + 2 * tl + l[0] + 2) >> 2;
                        }

                        dst16[y * 4 + x] = (byte)v;
                    }
                }

                return;
            case 5:
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var zVR = 2 * x - y;
                        int v;
                        switch (zVR)
                        {
                            case 0:
                            case 2:
                            case 4:
                            case 6:
                            {
                                var idx = x - (y >> 1);
                                v = (PTopOrTl(idx - 1, tl, t) + t[idx] + 1) >> 1;
                                break;
                            }

                            case 1:
                            case 3:
                            case 5:
                            {
                                var idx = x - (y >> 1);
                                v = (PTopOrTl(idx - 2, tl, t) + 2 * PTopOrTl(idx - 1, tl, t) + t[idx] + 2) >> 2;
                                break;
                            }

                            case -1:
                                v = (l[0] + 2 * tl + t[0] + 2) >> 2;
                                break;
                            case -2:
                                v = (l[1] + 2 * l[0] + tl + 2) >> 2;
                                break;
                            default:
                                v = (l[2] + 2 * l[1] + l[0] + 2) >> 2;
                                break;
                        }

                        dst16[y * 4 + x] = (byte)v;
                    }
                }

                return;
            case 6:
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var zHD = 2 * y - x;
                        int v;
                        switch (zHD)
                        {
                            case 0:
                            case 2:
                            case 4:
                            case 6:
                            {
                                var idx = y - (x >> 1);
                                v = (PLeftOrTl(idx - 1, tl, l) + l[idx] + 1) >> 1;
                                break;
                            }

                            case 1:
                            case 3:
                            case 5:
                            {
                                var idx = y - (x >> 1);
                                v = (PLeftOrTl(idx - 2, tl, l) + 2 * PLeftOrTl(idx - 1, tl, l) + l[idx] + 2) >> 2;
                                break;
                            }

                            case -1:
                                v = (l[0] + 2 * tl + t[0] + 2) >> 2;
                                break;
                            case -2:
                                v = (t[1] + 2 * t[0] + tl + 2) >> 2;
                                break;
                            default:
                                v = (t[2] + 2 * t[1] + t[0] + 2) >> 2;
                                break;
                        }

                        dst16[y * 4 + x] = (byte)v;
                    }
                }

                return;
            case 7:
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var idx = x + (y >> 1);
                        int v;
                        if ((y & 1) == 0)
                        {
                            v = (t[idx] + t[idx + 1] + 1) >> 1;
                        }
                        else
                        {
                            v = (t[idx] + 2 * t[idx + 1] + t[idx + 2] + 2) >> 2;
                        }

                        dst16[y * 4 + x] = (byte)v;
                    }
                }

                return;
            case 8:
                for (var y = 0; y < 4; y++)
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var zHU = x + 2 * y;
                        int v;
                        if (zHU is 0 or 2 or 4)
                        {
                            var idx = y + (x >> 1);
                            v = (l[idx] + l[idx + 1] + 1) >> 1;
                        }
                        else if (zHU is 1 or 3)
                        {
                            var idx = y + (x >> 1);
                            v = (l[idx] + 2 * l[idx + 1] + l[idx + 2] + 2) >> 2;
                        }
                        else if (zHU == 5)
                        {
                            v = (l[2] + 3 * l[3] + 2) >> 2;
                        }
                        else
                        {
                            v = l[3];
                        }

                        dst16[y * 4 + x] = (byte)v;
                    }
                }

                return;
        }
    }

    private static int PTopOrTl(int idx, byte tl, ReadOnlySpan<byte> t) => idx < 0 ? tl : t[idx];

    private static int PLeftOrTl(int idx, byte tl, ReadOnlySpan<byte> l) => idx < 0 ? tl : l[idx];

    public static IEnumerable<object[]> Fixtures()
    {
        yield return [
            "uniform_128", Repeat9((byte)128), Repeat4((byte)128), true, true,
        ];
        yield return [
            "uniform_0", Repeat9((byte)0), Repeat4((byte)0), true, true,
        ];
        yield return [
            "uniform_255", Repeat9((byte)255), Repeat4((byte)255), true, true,
        ];
        yield return [
            "horizontal_gradient",
            new byte[] { 16, 24, 48, 64, 80, 96, 112, 128, 144 },
            new byte[] { 32, 40, 48, 56 },
            true, true,
        ];
        yield return [
            "vertical_step",
            new byte[] { 200, 200, 200, 200, 200, 200, 200, 200, 200 },
            new byte[] { 50, 50, 200, 200 },
            true, true,
        ];
        yield return [
            "diag_descend",
            new byte[] { 250, 200, 150, 100, 50, 50, 50, 50, 50 },
            new byte[] { 200, 150, 100, 50 },
            true, true,
        ];
        yield return [
            "high_contrast_corner",
            new byte[] { 0, 255, 255, 255, 255, 255, 255, 255, 255 },
            new byte[] { 255, 0, 0, 0 },
            true, true,
        ];
        yield return [
            "low_contrast_alt",
            new byte[] { 130, 132, 128, 134, 126, 136, 124, 138, 122 },
            new byte[] { 131, 129, 133, 127 },
            true, true,
        ];
        yield return [
            "tr_replicated_from_t3",
            new byte[] { 100, 110, 120, 130, 140, 140, 140, 140, 140 },
            new byte[] { 90, 95, 100, 105 },
            true, true,
        ];
        yield return [
            "no_top",
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            new byte[] { 100, 110, 120, 130 },
            false, true,
        ];
        yield return [
            "no_left",
            new byte[] { 200, 100, 110, 120, 130, 140, 150, 160, 170 },
            new byte[] { 0, 0, 0, 0 },
            true, false,
        ];
        yield return [
            "no_neighbors",
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            new byte[] { 0, 0, 0, 0 },
            false, false,
        ];
        yield return [
            "asymmetric_top_only_gradient",
            new byte[] { 70, 80, 90, 100, 110, 120, 130, 140, 150 },
            new byte[] { 0, 0, 0, 0 },
            true, false,
        ];
        yield return [
            "asymmetric_left_only_gradient",
            new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            new byte[] { 70, 80, 90, 100 },
            false, true,
        ];
        yield return [
            "edge_with_tl_corner_spike",
            new byte[] { 200, 50, 50, 50, 50, 50, 50, 50, 50 },
            new byte[] { 50, 50, 50, 50 },
            true, true,
        ];
        yield return [
            "row_step",
            new byte[] { 64, 64, 64, 64, 64, 192, 192, 192, 192 },
            new byte[] { 64, 64, 192, 192 },
            true, true,
        ];
        yield return [
            "boundary_blend",
            new byte[] { 50, 100, 100, 100, 100, 100, 100, 100, 100 },
            new byte[] { 100, 100, 100, 100 },
            true, true,
        ];
        yield return [
            "rng_seeded_a",
            SeededTop(0xC0FFEE),
            SeededLeft(0xC0FFEE + 1),
            true, true,
        ];
        yield return [
            "rng_seeded_b",
            SeededTop(0xBADCAFE),
            SeededLeft(0xBADCAFE + 1),
            true, true,
        ];
    }

    private static byte[] Repeat9(byte v)
    {
        var a = new byte[9];
        Array.Fill(a, v);
        return a;
    }

    private static byte[] Repeat4(byte v)
    {
        var a = new byte[4];
        Array.Fill(a, v);
        return a;
    }

    private static byte[] SeededTop(int seed)
    {
        var rng = new Random(seed);
        var a = new byte[9];
        rng.NextBytes(a);
        return a;
    }

    private static byte[] SeededLeft(int seed)
    {
        var rng = new Random(seed);
        var a = new byte[4];
        rng.NextBytes(a);
        return a;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_mode_every_position_matches_spec_reference(
        string fixtureName,
        byte[] topRow,
        byte[] leftCol,
        bool topAvail,
        bool leftAvail)
    {
        Span<byte> got = stackalloc byte[16];
        Span<byte> expected = stackalloc byte[16];

        for (var mode = 0; mode <= 8; mode++)
        {
            got.Clear();
            expected.Clear();

            H264Intra4X4Prediction.Predict(mode, topRow, leftCol, topAvail, leftAvail, got);
            ReferencePredict(mode, topRow, leftCol, topAvail, leftAvail, expected);

            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    var pos = y * 4 + x;
                    got[pos].Should().Be(expected[pos],
                        $"fixture '{fixtureName}' mode={mode} pos=(x={x},y={y}) " +
                        $"topAvail={topAvail} leftAvail={leftAvail}");
                }
            }
        }
    }
}
