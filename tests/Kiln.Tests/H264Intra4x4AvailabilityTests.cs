using FluentAssertions;
using Kiln;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// First-principles gates for the Intra_4×4 neighbour-availability, candidate-set and
/// most-probable-mode machinery in <see cref="H264BaselineSliceEncoder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every expectation in this file is written out from the specification (or, where noted, from the
/// encoder's documented candidate-narrowing policy) rather than captured from the implementation, so a
/// refactor that quietly changes neighbour geometry, slice-boundary handling or MPM substitution fails
/// here instead of surfacing as a decoder desynchronisation.
/// </para>
/// <para>
/// Coordinates are geometric: <c>row</c> / <c>col</c> index the 4×4 luma blocks of a macroblock,
/// (0,0) top-left, (3,3) bottom-right — the ordering of H.264 §6.4.3's inverse 4×4 luma block scan
/// expressed as positions rather than as transmission order.
/// </para>
/// </remarks>
public sealed class H264Intra4x4AvailabilityTests
{
    // ── Specification facts the expectations are built from ────────────────────────────────────
    //
    // H.264 §8.3.1.2 — sample dependency of each Intra_4×4 prediction mode:
    //   0 Vertical             p[0..3, -1]                       → above only
    //   1 Horizontal           p[-1, 0..3]                       → left only
    //   2 DC                   defined for any availability       → neither
    //   3 Diagonal_Down_Left   p[0..7, -1]                       → above only (§8.3.1.2.1 substitutes
    //                                                              p[3,-1] for missing above-right)
    //   4 Diagonal_Down_Right  p[0..3,-1], p[-1,0..3], p[-1,-1]  → both
    //   5 Vertical_Right       as mode 4                          → both
    //   6 Horizontal_Down      as mode 4                          → both
    //   7 Vertical_Left        p[0..7, -1]                       → above only
    //   8 Horizontal_Up        p[-1, 0..3]                       → left only

    private static bool ModeNeedsAboveSamples(int mode) => mode is 0 or 3 or 4 or 5 or 6 or 7;

    private static bool ModeNeedsLeftSamples(int mode) => mode is 1 or 4 or 5 or 6 or 8;

    private const int DcMode = 2;

    private static int[] AllowedSet(bool leftMbAvailable, bool aboveMbAvailable, int row, int col)
    {
        var allowed = new List<int>();
        for (var mode = 0; mode <= 8; mode++)
        {
            if (H264BaselineSliceEncoder.IsIntra4x4ModeAllowed(mode, row, col, leftMbAvailable, aboveMbAvailable))
            {
                allowed.Add(mode);
            }
        }

        return [.. allowed];
    }

    // ── Sample availability is positional ──────────────────────────────────────────────────────

    /// <summary>
    /// §6.4.11.4 places neighbour B of a 4×4 block one block row up and neighbour A one block column
    /// left. Only the macroblock's top block row and left block column therefore reach outside the
    /// macroblock; every interior block predicts from samples reconstructed earlier in the same
    /// macroblock. A mode whose samples exist must never be filtered out for an interior block.
    /// </summary>
    [Fact]
    public void Interior_blocks_never_depend_on_neighbouring_macroblocks()
    {
        foreach (var leftMb in new[] { false, true })
        {
            foreach (var aboveMb in new[] { false, true })
            {
                for (var row = 2; row <= 3; row++)
                {
                    for (var col = 1; col <= 3; col++)
                    {
                        var allowed = AllowedSet(leftMb, aboveMb, row, col);
                        allowed.Should().Contain(DcMode, "DC is always defined (§8.3.1.2.4)");
                        allowed.Should().Contain(0, $"block ({row},{col}) takes its above samples from inside the macroblock");
                        allowed.Should().Contain(4, $"block ({row},{col}) has both neighbours inside the macroblock");
                    }
                }
            }
        }
    }

    /// <summary>
    /// DC (mode 2) is defined for every availability combination (§8.3.1.2.4 substitutes
    /// 1 &lt;&lt; (BitDepth − 1) when nothing is available), so it must survive as a candidate for every
    /// block of every macroblock — otherwise a macroblock at the picture corner would have no legal
    /// candidate at all.
    /// </summary>
    [Fact]
    public void Dc_is_always_a_candidate()
    {
        foreach (var leftMb in new[] { false, true })
        {
            foreach (var aboveMb in new[] { false, true })
            {
                for (var row = 0; row < 4; row++)
                {
                    for (var col = 0; col < 4; col++)
                    {
                        H264BaselineSliceEncoder.IsIntra4x4ModeAllowed(DcMode, row, col, leftMb, aboveMb)
                            .Should().BeTrue($"DC must be available at ({row},{col}) with left={leftMb}, above={aboveMb}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// The hard specification constraint: a candidate may never require samples that do not exist.
    /// This is asserted independently of the encoder's extra narrowing — narrowing may remove modes,
    /// it may never <em>add</em> one whose samples are missing.
    /// </summary>
    [Fact]
    public void No_candidate_ever_requires_missing_samples()
    {
        foreach (var leftMb in new[] { false, true })
        {
            foreach (var aboveMb in new[] { false, true })
            {
                for (var row = 0; row < 4; row++)
                {
                    for (var col = 0; col < 4; col++)
                    {
                        var aboveSamples = row > 0 || aboveMb;
                        var leftSamples = col > 0 || leftMb;
                        foreach (var mode in AllowedSet(leftMb, aboveMb, row, col))
                        {
                            if (ModeNeedsAboveSamples(mode))
                            {
                                aboveSamples.Should().BeTrue(
                                    $"mode {mode} at ({row},{col}) reads p[.., -1] (left={leftMb}, above={aboveMb})");
                            }

                            if (ModeNeedsLeftSamples(mode))
                            {
                                leftSamples.Should().BeTrue(
                                    $"mode {mode} at ({row},{col}) reads p[-1, ..] (left={leftMb}, above={aboveMb})");
                            }
                        }
                    }
                }
            }
        }
    }

    // ── Full 9-mode × 16-position sweeps for the four availability combinations ────────────────
    //
    // Expected sets below are written out by hand. Modes 0/3/7 need above samples, 1/8 need left
    // samples, 4/5/6 need both, 2 needs nothing (§8.3.1.2); on top of that the encoder narrows its
    // candidate set along picture / slice edges as documented on IsIntra4x4ModeAllowed:
    //   • with no above macroblock the left-only modes (1, 8) are dropped for the whole macroblock,
    //     and the above-using modes are additionally dropped at (1,0) and (1,1);
    //   • with no left macroblock the left-only modes are dropped at (0,2), (2,0) and (2,2).

    private static readonly int[] All9 = [0, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly int[] NoLeftOnly = [0, 2, 3, 4, 5, 6, 7];
    private static readonly int[] AboveOnlyPlusDc = [0, 2, 3, 7];
    private static readonly int[] DcOnly = [DcMode];

    /// <summary>Interior macroblock: both neighbours present, no narrowing, every mode is a candidate.</summary>
    [Fact]
    public void Sweep_interior_macroblock_allows_all_nine_modes_everywhere()
    {
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                AllowedSet(leftMbAvailable: true, aboveMbAvailable: true, row, col)
                    .Should().Equal(All9, $"block ({row},{col})");
            }
        }
    }

    /// <summary>
    /// Top macroblock row of the picture, away from the left edge (also the shape of the top-right
    /// corner macroblock): no above macroblock, left macroblock present.
    /// </summary>
    [Fact]
    public void Sweep_no_above_macroblock()
    {
        int[][] expected =
        [
            // row 0 — above samples do not exist at all.
            DcOnly, DcOnly, DcOnly, DcOnly,
            // row 1 — (1,0) and (1,1) additionally narrowed; (1,2)/(1,3) keep the above-using modes.
            DcOnly, DcOnly, NoLeftOnly, NoLeftOnly,
            // rows 2 and 3 — left-only modes stay dropped for the whole macroblock.
            NoLeftOnly, NoLeftOnly, NoLeftOnly, NoLeftOnly,
            NoLeftOnly, NoLeftOnly, NoLeftOnly, NoLeftOnly,
        ];

        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                AllowedSet(leftMbAvailable: true, aboveMbAvailable: false, row, col)
                    .Should().Equal(expected[row * 4 + col], $"block ({row},{col})");
            }
        }
    }

    /// <summary>
    /// Left picture edge below the first macroblock row (the shape of the bottom-left corner
    /// macroblock): no left macroblock, above macroblock present.
    /// </summary>
    [Fact]
    public void Sweep_no_left_macroblock()
    {
        int[][] expected =
        [
            AboveOnlyPlusDc, All9, NoLeftOnly, All9,
            AboveOnlyPlusDc, All9, All9,       All9,
            AboveOnlyPlusDc, All9, NoLeftOnly, All9,
            AboveOnlyPlusDc, All9, All9,       All9,
        ];

        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                AllowedSet(leftMbAvailable: false, aboveMbAvailable: true, row, col)
                    .Should().Equal(expected[row * 4 + col], $"block ({row},{col})");
            }
        }
    }

    /// <summary>
    /// Top-left corner macroblock of the picture — and, identically, the first macroblock of any
    /// non-first slice's first macroblock row at column 0: neither neighbour exists.
    /// </summary>
    [Fact]
    public void Sweep_no_neighbouring_macroblocks_at_all()
    {
        int[][] expected =
        [
            DcOnly,          DcOnly, DcOnly,     DcOnly,
            DcOnly,          DcOnly, NoLeftOnly, NoLeftOnly,
            AboveOnlyPlusDc, NoLeftOnly, NoLeftOnly, NoLeftOnly,
            AboveOnlyPlusDc, NoLeftOnly, NoLeftOnly, NoLeftOnly,
        ];

        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                AllowedSet(leftMbAvailable: false, aboveMbAvailable: false, row, col)
                    .Should().Equal(expected[row * 4 + col], $"block ({row},{col})");
            }
        }
    }

    // ── Macroblock-level neighbour availability, including slice boundaries ────────────────────

    /// <summary>
    /// §6.4.11.4 with §6.4.4: the left neighbour is unavailable only at the left picture edge, and the
    /// above neighbour is unavailable at the top picture edge <em>and</em> on the first macroblock row
    /// of every non-first slice, because a macroblock in another slice is not available.
    /// </summary>
    [Theory]
    // Single slice covering the whole picture (firstMbRowInSlice = 0).
    [InlineData(0, 0, 0, false, false)]   // top-left corner of the picture
    [InlineData(19, 0, 0, true, false)]   // top-right corner of the picture
    [InlineData(0, 14, 0, false, true)]   // bottom-left corner of the picture
    [InlineData(19, 14, 0, true, true)]   // bottom-right corner of the picture
    [InlineData(7, 3, 0, true, true)]     // interior
    // Second slice starting at macroblock row 8.
    [InlineData(0, 8, 8, false, false)]   // first macroblock of the slice
    [InlineData(7, 8, 8, true, false)]    // first row of the slice: above is in the previous slice
    [InlineData(7, 9, 8, true, true)]     // second row of the slice: above is inside this slice
    public void Mb_neighbour_availability(int mbx, int mby, int firstMbRowInSlice, bool expectLeft, bool expectAbove)
    {
        H264BaselineSliceEncoder.IsLeftMbAvailable(mbx).Should().Be(expectLeft);
        H264BaselineSliceEncoder.IsAboveMbAvailable(mby, firstMbRowInSlice).Should().Be(expectAbove);
    }

    /// <summary>
    /// The first macroblock row of a non-first slice must behave exactly like the top row of the
    /// picture: no above macroblock, therefore no candidate that reads above samples in block row 0.
    /// A regression here produces a stream that only decodes correctly when the decoder happens to
    /// have the previous slice's reconstruction, i.e. it breaks slice independence.
    /// </summary>
    [Fact]
    public void First_row_of_a_non_first_slice_has_no_above_candidates()
    {
        const int firstMbRowInSlice = 8;
        var aboveMb = H264BaselineSliceEncoder.IsAboveMbAvailable(mby: firstMbRowInSlice, firstMbRowInSlice);
        aboveMb.Should().BeFalse();

        for (var col = 0; col < 4; col++)
        {
            AllowedSet(leftMbAvailable: true, aboveMbAvailable: aboveMb, row: 0, col)
                .Should().Equal(DcOnly, $"block (0,{col}) of the slice's first macroblock row");
        }
    }

    // ── Most-probable-mode derivation (§8.3.1.1) ───────────────────────────────────────────────

    private static sbyte[] BuildContext(
        bool leftAvailable, bool leftIsIntra4x4, byte[] leftBoundary,
        bool aboveAvailable, bool aboveIsIntra4x4, byte[] aboveBoundary)
    {
        var ctx = new sbyte[25];
        H264BaselineSliceEncoder.FillIntra4x4ModeContext(
            ctx,
            leftAvailable, leftIsIntra4x4, leftBoundary,
            aboveAvailable, aboveIsIntra4x4, aboveBoundary);
        return ctx;
    }

    private static int Mpm(sbyte[] ctx, int row, int col)
    {
        var slot = H264BaselineSliceEncoder.LumaCtxSlot(row, col);
        return H264Intra4X4Prediction.NeighborPredMode(ctx[slot - 1], ctx[slot - 5]);
    }

    /// <summary>
    /// Boundary storage must expose exactly the blocks a later macroblock reads (§6.4.11.4): the MB to
    /// the right sees this macroblock's column 3, the MB below sees its row 3. Round-tripping 16
    /// distinct modes through storage and back into a neighbour's context proves the layout and both
    /// index derivations agree.
    /// </summary>
    [Fact]
    public void Boundary_storage_round_trips_the_blocks_neighbours_actually_read()
    {
        // 16 modes laid out so that mode == row * 4 + col is unique per block; values stay within 0..8
        // by using (row * 4 + col) % 9.
        var grid = new sbyte[25];
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                grid[H264BaselineSliceEncoder.LumaCtxSlot(row, col)] = (sbyte)((row * 4 + col) % 9);
            }
        }

        var boundary = new byte[H264BaselineSliceEncoder.IntraModeBoundaryStride];
        H264BaselineSliceEncoder.StoreIntra4x4ModeBoundary(grid, boundary);

        // Used as the LEFT neighbour: A of (row, 0) must be the stored macroblock's (row, 3).
        var asLeft = BuildContext(true, true, boundary, false, false, []);
        for (var row = 0; row < 4; row++)
        {
            var slot = H264BaselineSliceEncoder.LumaCtxSlot(row, 0);
            asLeft[slot - 1].Should().Be(grid[H264BaselineSliceEncoder.LumaCtxSlot(row, 3)],
                $"A neighbour of (row {row}, col 0) is the left macroblock's (row {row}, col 3)");
        }

        // Used as the ABOVE neighbour: B of (0, col) must be the stored macroblock's (3, col).
        var asAbove = BuildContext(false, false, [], true, true, boundary);
        for (var col = 0; col < 4; col++)
        {
            var slot = H264BaselineSliceEncoder.LumaCtxSlot(0, col);
            asAbove[slot - 5].Should().Be(grid[H264BaselineSliceEncoder.LumaCtxSlot(3, col)],
                $"B neighbour of (row 0, col {col}) is the above macroblock's (row 3, col {col})");
        }
    }

    /// <summary>§8.3.1.1: when either neighbour is unavailable, predIntra4x4PredMode is DC.</summary>
    [Fact]
    public void Mpm_is_dc_when_a_neighbour_is_unavailable()
    {
        byte[] neighbour = [5, 5, 5, 5, 5, 5, 5];

        // Corner macroblock: neither neighbour.
        Mpm(BuildContext(false, false, [], false, false, []), 0, 0).Should().Be(DcMode);

        // Left present with mode 5, above missing → still DC.
        Mpm(BuildContext(true, true, neighbour, false, false, []), 0, 0).Should().Be(DcMode);

        // Above present with mode 5, left missing → still DC.
        Mpm(BuildContext(false, false, [], true, true, neighbour), 0, 0).Should().Be(DcMode);
    }

    /// <summary>§8.3.1.1: with both neighbours available, predIntra4x4PredMode is Min(A, B).</summary>
    [Theory]
    [InlineData(0, 8, 0)]
    [InlineData(8, 0, 0)]
    [InlineData(4, 4, 4)]
    [InlineData(7, 3, 3)]
    [InlineData(1, 6, 1)]
    public void Mpm_is_min_of_both_available_neighbours(byte leftMode, byte aboveMode, int expected)
    {
        var left = new byte[7];
        var above = new byte[7];
        Array.Fill(left, leftMode);
        Array.Fill(above, aboveMode);

        Mpm(BuildContext(true, true, left, true, true, above), 0, 0).Should().Be(expected);
    }

    /// <summary>
    /// §8.3.1.1 with <c>constrained_intra_pred_flag</c> equal to 0: a neighbouring macroblock that
    /// exists but is not coded in an Intra_4×4 mode — an inter macroblock in a P-slice, or an
    /// Intra_16×16 macroblock — is <em>available</em> and contributes DC, which is materially different
    /// from being unavailable when the other neighbour is present.
    /// </summary>
    [Fact]
    public void Inter_neighbour_contributes_dc_rather_than_being_unavailable()
    {
        byte[] intraNeighbour = [5, 5, 5, 5, 5, 5, 5];

        // Above is an inter macroblock (present, not Intra_4×4) → contributes DC (2).
        // Left is Intra_4×4 with mode 5 → Min(5, 2) = 2.
        var withInterAbove = BuildContext(true, true, intraNeighbour, true, aboveIsIntra4x4: false, []);
        withInterAbove[H264BaselineSliceEncoder.LumaCtxSlot(-1, 0)].Should().Be(2);
        Mpm(withInterAbove, 0, 0).Should().Be(2);

        // The same neighbour being absent instead of inter also gives DC at (0,0) — but the two differ
        // where the surviving neighbour is smaller than DC: with an inter above and a left mode of 0,
        // Min(0, 2) = 0, whereas an absent above forces DC.
        var interAboveSmallLeft = BuildContext(true, true, [0, 0, 0, 0, 0, 0, 0], true, false, []);
        Mpm(interAboveSmallLeft, 0, 0).Should().Be(0);

        var absentAboveSmallLeft = BuildContext(true, true, [0, 0, 0, 0, 0, 0, 0], false, false, []);
        Mpm(absentAboveSmallLeft, 0, 0).Should().Be(DcMode);
    }

    /// <summary>
    /// Blocks inside the macroblock take their MPM from blocks of the same macroblock, so the halo must
    /// not leak into interior positions: with both neighbours filled with mode 5, block (1,1)'s A and B
    /// are still whatever the macroblock itself wrote there.
    /// </summary>
    [Fact]
    public void Interior_mpm_reads_the_macroblocks_own_blocks()
    {
        byte[] neighbour = [5, 5, 5, 5, 5, 5, 5];
        var ctx = BuildContext(true, true, neighbour, true, true, neighbour);

        ctx[H264BaselineSliceEncoder.LumaCtxSlot(1, 0)] = 1; // A of (1,1)
        ctx[H264BaselineSliceEncoder.LumaCtxSlot(0, 1)] = 7; // B of (1,1)

        Mpm(ctx, 1, 1).Should().Be(1);
    }

    // ── CAVLC nC derivation (§9.2.1) ───────────────────────────────────────────────────────────

    /// <summary>
    /// §9.2.1: with both neighbours available nC = (nA + nB + 1) &gt;&gt; 1; with exactly one available it is
    /// that neighbour's total coefficient count; with neither it is 0. nC selects the coeff_token VLC
    /// table, so an error here makes the stream undecodable rather than merely suboptimal.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 5, 4)]
    [InlineData(5, 3, 4)]
    [InlineData(1, 0, 1)]
    [InlineData(16, 16, 16)]
    [InlineData(7, -1, 7)]
    [InlineData(-1, 7, 7)]
    [InlineData(-1, -1, 0)]
    [InlineData(0, -1, 0)]
    public void Nc_matches_clause_9_2_1(int nA, int nB, int expected)
    {
        H264BaselineSliceEncoder.DeriveCoeffTokenNc(nA, nB).Should().Be(expected);
    }

    /// <summary>Rounding in §9.2.1 is "round half up" on the average, for every reachable pair.</summary>
    [Fact]
    public void Nc_rounds_half_up_over_the_whole_reachable_range()
    {
        for (var nA = 0; nA <= 16; nA++)
        {
            for (var nB = 0; nB <= 16; nB++)
            {
                H264BaselineSliceEncoder.DeriveCoeffTokenNc(nA, nB)
                    .Should().Be((nA + nB + 1) / 2, $"nA={nA}, nB={nB}");
            }
        }
    }

    // ── End-to-end: the encoder-side conformance self-check must never fire ────────────────────

    /// <summary>
    /// Encoding real content through the corner and slice-boundary cases above must never trip the
    /// encoder's internal check that every signalled Intra_4×4 mode has the samples it needs. The tiny
    /// resolutions make the picture almost entirely edge macroblocks; SliceCount &gt; 1 additionally puts
    /// a slice boundary mid-picture.
    /// </summary>
    [Theory]
    [InlineData(32, 32, 1)]
    [InlineData(32, 32, 2)]
    [InlineData(48, 32, 2)]
    [InlineData(80, 64, 4)]
    public void Encoding_edge_heavy_pictures_never_trips_the_conformance_self_check(int w, int h, int sliceCount)
    {
        var ySize = w * h;
        var uvSize = ySize / 4;
        var src = new byte[ySize + uvSize * 2];
        // High-frequency content so directional modes actually win the RD comparison.
        for (var i = 0; i < ySize; i++) src[i] = (byte)((i * 37 + (i / w) * 91) % 251);
        for (var i = ySize; i < src.Length; i++) src[i] = (byte)((i * 17) % 233);

        var buf = new byte[ySize * 6 + 1_000_000];
        foreach (var qp in new[] { 18, 28, 40 })
        {
            using var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
            {
                QuantizationParameter = qp,
                SliceCount = sliceCount,
            });

            var act = () => enc.EncodeFrame(
                src.AsSpan(0, ySize),
                src.AsSpan(ySize, uvSize),
                src.AsSpan(ySize + uvSize, uvSize),
                w,
                w / 2,
                buf);

            act.Should().NotThrow($"{w}×{h} qp{qp} sliceCount={sliceCount}");
        }
    }
}
