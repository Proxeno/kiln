// Phase-1 acceptance test for Composer2 task F2b/W4 (H264InterBoundaryStrength).
//
// Authoring strategy: STRATEGY A from the F2a playbook — the real test fixtures live inside
// `#if HAS_INTER_BOUNDARY_STRENGTH` blocks that reference internal types the W4 worker will
// add in `src/Kiln/Internal/H264/H264InterBoundaryStrength.cs`. With that symbol
// UNDEFINED (the state at file commit time), only the
// `InterBoundaryStrength_must_be_delivered_before_test_runs` fact is compiled, and it fails
// with a Composer2-actionable error so the W4 worker's mandatory pre-check (rule 11 of the
// F2b common preamble) lights up immediately. After the W4 worker lands the production module
// the senior's phase-3 integration step adds
// `<DefineConstants>$(DefineConstants);HAS_INTER_BOUNDARY_STRENGTH</DefineConstants>` to
// `tests/Kiln.Tests/Kiln.Tests.csproj`; the gate test then short-
// circuits to a no-op pass and the real boundary-strength fixture suite below activates.
//
// Drift-trap context (see h264_encoder_f2b_delegation_orchestration.md "The drift trap"):
//   The H.264 8.7.2.1 inter rule uses `>= 4` quarter-pel difference, NOT `> 4`. Implementations
//   that use `>` silently over-smooth tiny-MV inter MBs and silently inflate residuals for
//   exactly-1-pel MV deltas. The fixtures below therefore include the `|Δmv| == 4` boundary
//   case and the just-below `|Δmv| == 3` case as separate fixtures so a `>`-vs-`>=` mistake
//   is caught with a single failing test row.

using Xunit;

#if HAS_INTER_BOUNDARY_STRENGTH
using FluentAssertions;
using Kiln.Internal.H264;
using InterEdgeNeighbour = Kiln.Internal.H264.H264InterBoundaryStrength.InterEdgeNeighbour;
#endif

namespace Kiln.Tests;

/// <summary>
/// Phase-1 acceptance test class for the F2b/W4 deliverable
/// (<c>Kiln.Internal.H264.H264InterBoundaryStrength</c>). The class is the
/// Composer2 worker's contract: it must be green by the end of W4's diff. The set of
/// <see cref="FactAttribute"/>s changes depending on the <c>HAS_INTER_BOUNDARY_STRENGTH</c>
/// compile-time symbol — see the file-level comment for the strategy explanation.
/// </summary>
public sealed class H264InterBoundaryStrengthTests
{
#if !HAS_INTER_BOUNDARY_STRENGTH
    /// <summary>
    /// Pre-delivery gate: as long as the W4 production module is missing, this test fails with
    /// a clear, actionable message so a Composer2 worker reading the failure log knows exactly
    /// what to add. The full fixture suite (hand-crafted MB-pair bs values across every edge)
    /// lives below this method inside <c>#if HAS_INTER_BOUNDARY_STRENGTH</c> and activates
    /// after the senior toggles the build symbol in phase-3 integration.
    /// </summary>
    [Fact]
    public void InterBoundaryStrength_must_be_delivered_before_test_runs()
    {
        var t = Type.GetType("Kiln.Internal.H264.H264InterBoundaryStrength, Kiln");
        if (t is null)
        {
            Assert.Fail(
                "W4 has not been delivered: type Kiln.Internal.H264.H264InterBoundaryStrength " +
                "is missing. Implement the module per src/Kiln/Internal/H264/H264InterBoundaryStrength.cs " +
                "with the public API given in the W4 Composer2 prompt (`Compute(...)` plus the `InterEdgeNeighbour` " +
                "record struct).");
        }

        Assert.Fail(
            "W4 appears to be delivered (Kiln.Internal.H264.H264InterBoundaryStrength exists), " +
            "but the test project's `HAS_INTER_BOUNDARY_STRENGTH` build symbol is not enabled. The senior " +
            "must add `<DefineConstants>$(DefineConstants);HAS_INTER_BOUNDARY_STRENGTH</DefineConstants>` " +
            "to a `<PropertyGroup>` in `tests/Kiln.Tests/Kiln.Tests.csproj` to " +
            "activate the real H264InterBoundaryStrengthTests fixtures (see file header comment).");
    }
#else
    // -----------------------------------------------------------------------------------------
    // Real fixture suite (active when HAS_INTER_BOUNDARY_STRENGTH is defined).
    // -----------------------------------------------------------------------------------------

    /// <summary>Build a 16-element block array where every 4×4 block has the same (refIdx, mvX, mvY).</summary>
    private static InterEdgeNeighbour[] UniformBlocks(int refIdx, int mvX, int mvY)
    {
        var blocks = new InterEdgeNeighbour[16];
        for (var i = 0; i < 16; i++)
        {
            blocks[i] = new InterEdgeNeighbour(refIdx, mvX, mvY);
        }
        return blocks;
    }

    /// <summary>Build a 4-element neighbour row/col with uniform fields.</summary>
    private static InterEdgeNeighbour[] UniformNeighbour(int refIdx, int mvX, int mvY)
    {
        var n = new InterEdgeNeighbour[4];
        for (var i = 0; i < 4; i++)
        {
            n[i] = new InterEdgeNeighbour(refIdx, mvX, mvY);
        }
        return n;
    }

    /// <summary>
    /// (inter same ref, same MV) → bs=0 for every segment of every edge type. This is the
    /// canonical "no deblocking needed across this edge" case.
    /// </summary>
    [Fact]
    public void Compute_inter_same_ref_same_mv_produces_bs_zero_everywhere()
    {
        var thisMb = UniformBlocks(refIdx: 0, mvX: 4, mvY: 8);
        var above = UniformNeighbour(refIdx: 0, mvX: 4, mvY: 8);
        var left = UniformNeighbour(refIdx: 0, mvX: 4, mvY: 8);
        var bsH = new byte[16];
        var bsV = new byte[16];

        H264InterBoundaryStrength.Compute(thisMb, above, left, bsH, bsV);

        for (var i = 0; i < 16; i++)
        {
            bsH[i].Should().Be(0, $"horizontal edge segment {i} between identical inter blocks must be 0");
            bsV[i].Should().Be(0, $"vertical edge segment {i} between identical inter blocks must be 0");
        }
    }

    /// <summary>
    /// H.264 8.7.2.1: when both partitions are inter with the same ref and MV, non-zero transform
    /// coefficients on either side of an internal edge force <c>bs = 2</c> (stronger than MV-only <c>1</c>).
    /// </summary>
    [Fact]
    public void Compute_inter_nonzero_coeffs_on_one_block_yields_bs_two_on_touching_edges()
    {
        var thisMb = UniformBlocks(refIdx: 0, mvX: 0, mvY: 0);
        thisMb[6] = new InterEdgeNeighbour(RefIdx: 0, MvXQpel: 0, MvYQpel: 0, NonZeroCoeffs: true);

        var bsH = new byte[16];
        var bsV = new byte[16];
        H264InterBoundaryStrength.Compute(thisMb, ReadOnlySpan<InterEdgeNeighbour>.Empty,
            ReadOnlySpan<InterEdgeNeighbour>.Empty, bsH, bsV);

        bsH[1 * 4 + 2].Should().Be(2, "horizontal edge between row0/1 seg2 touches nonzero block 6");
        bsH[2 * 4 + 2].Should().Be(2, "horizontal edge between row1/2 seg2 touches block 6");
        bsV[2 * 4 + 1].Should().Be(2, "vertical edge col1/2 seg1 spans blocks 5 and 6");
        bsV[3 * 4 + 1].Should().Be(2, "vertical edge col2/3 seg1 spans blocks 6 and 7");
        bsH[1 * 4 + 0].Should().Be(0, "unaffected segment without nonzero mismatch");
        bsV[2 * 4 + 0].Should().Be(0, "vertical seg0 row0 does not touch block 6");
    }

    /// <summary>
    /// H.264 8.7.2.1: MV component difference <c>≥ 4</c> qpel is evaluated <em>before</em> non-zero coeffs;
    /// residual blocks still yield <c>bs = 1</c>, not <c>2</c>, when <c>|Δmv| ≥ 4</c> across the edge.
    /// </summary>
    [Fact]
    public void Compute_large_mv_delta_takes_precedence_over_nonzero_coeffs()
    {
        var thisMb = UniformBlocks(refIdx: 0, mvX: 0, mvY: 0);
        // Same geometry as Δmv.x = 4 case: divergent MV on block 6 vs neighbours at MV 0, plus coded residual on 6.
        thisMb[6] = new InterEdgeNeighbour(RefIdx: 0, MvXQpel: 4, MvYQpel: 0, NonZeroCoeffs: true);

        var bsH = new byte[16];
        var bsV = new byte[16];
        H264InterBoundaryStrength.Compute(thisMb, ReadOnlySpan<InterEdgeNeighbour>.Empty,
            ReadOnlySpan<InterEdgeNeighbour>.Empty, bsH, bsV);

        bsH[1 * 4 + 2].Should().Be(1, "MV rule precedes coeffs: must be bs=1, not bs=2");
        bsH[2 * 4 + 2].Should().Be(1);
        bsV[2 * 4 + 1].Should().Be(1);
        bsV[3 * 4 + 1].Should().Be(1);
    }

    /// <summary>
    /// (inter different ref) → bs=1 for every external edge segment that bridges into the
    /// neighbour. Internal edges within thisMb are still bs=0 since all blocks share a ref.
    /// </summary>
    [Fact]
    public void Compute_inter_different_ref_produces_bs_one_on_external_edges_only()
    {
        var thisMb = UniformBlocks(refIdx: 0, mvX: 0, mvY: 0);
        var above = UniformNeighbour(refIdx: 1, mvX: 0, mvY: 0);
        var left = UniformNeighbour(refIdx: 1, mvX: 0, mvY: 0);
        var bsH = new byte[16];
        var bsV = new byte[16];

        H264InterBoundaryStrength.Compute(thisMb, above, left, bsH, bsV);

        for (var seg = 0; seg < 4; seg++)
        {
            bsH[0 * 4 + seg].Should().Be(1, $"top external edge segment {seg} (different ref) must be 1");
            bsV[0 * 4 + seg].Should().Be(1, $"left external edge segment {seg} (different ref) must be 1");
        }
        for (var edge = 1; edge < 4; edge++)
        {
            for (var seg = 0; seg < 4; seg++)
            {
                bsH[edge * 4 + seg].Should().Be(0,
                    $"internal horizontal edge {edge} seg {seg} (same ref/MV inside MB) must be 0");
                bsV[edge * 4 + seg].Should().Be(0,
                    $"internal vertical edge {edge} seg {seg} (same ref/MV inside MB) must be 0");
            }
        }
    }

    /// <summary>
    /// Boundary case 1: |Δmv.x| = 4 (= 1 full pel) at a single internal segment must trigger
    /// bs=1 on that segment and bs=0 elsewhere. The H.264 8.7.2.1 inter rule is `>= 4`, not
    /// `> 4` — a `>` implementation would misclassify this as bs=0.
    /// </summary>
    [Fact]
    public void Compute_internal_edge_at_exact_mv_delta_4_triggers_bs_one()
    {
        var thisMb = UniformBlocks(refIdx: 0, mvX: 0, mvY: 0);
        // Place a divergent MV at the second row, third column (raster index 4*1+2 = 6) so the
        // horizontal edge between row 0 and row 1, segment 2 must flip to bs=1.
        thisMb[6] = new InterEdgeNeighbour(RefIdx: 0, MvXQpel: 4, MvYQpel: 0);

        var bsH = new byte[16];
        var bsV = new byte[16];
        H264InterBoundaryStrength.Compute(thisMb, ReadOnlySpan<InterEdgeNeighbour>.Empty,
            ReadOnlySpan<InterEdgeNeighbour>.Empty, bsH, bsV);

        // Horizontal edge=1 (between row 0 and row 1), seg=2 spans block 2 (above) vs block 6 (below)
        // — Δmv.x = |0 - 4| = 4, exactly the spec threshold; bs must be 1.
        bsH[1 * 4 + 2].Should().Be(1, "Δmv.x of exactly 4 qpel must trigger bs=1 (spec uses >=, not >)");
        // Adjacent segments at the same edge use Δmv.x = 0 → bs=0.
        bsH[1 * 4 + 0].Should().Be(0);
        bsH[1 * 4 + 1].Should().Be(0);
        bsH[1 * 4 + 3].Should().Be(0);
        // Horizontal edge=2 also touches block 6 (now as the "above" block vs block 10): same Δ=4.
        bsH[2 * 4 + 2].Should().Be(1, "edge below the divergent block also sees Δmv.x = 4");
        // Vertical edges adjacent to block 6 (col 2 vs col 1, col 2 vs col 3) also see Δmv.x = 4.
        bsV[1 * 4 + 1].Should().Be(0,
            "vertical edge=1 seg=1 spans col 0 vs col 1 of row 1, neither is the divergent block 6, must be 0");
        bsV[2 * 4 + 1].Should().Be(1,
            "vertical edge=2 seg=1 spans col 1 (block 5, MV 0) vs col 2 (block 6, MV 4) → Δ=4 → bs=1");
        bsV[3 * 4 + 1].Should().Be(1,
            "vertical edge=3 seg=1 spans col 2 (block 6, MV 4) vs col 3 (block 7, MV 0) → Δ=4 → bs=1");
    }

    /// <summary>
    /// Boundary case 2: |Δmv.x| = 3 (just under threshold) MUST NOT trigger bs=1 — the spec is
    /// strictly `>= 4`. A buggy `> 3` implementation would coincidentally pass this OR a
    /// `> 4` implementation would coincidentally pass this; only the pair (Δ=3 → 0, Δ=4 → 1)
    /// pins down the correct comparator.
    /// </summary>
    [Fact]
    public void Compute_internal_edge_at_mv_delta_3_does_not_trigger_bs_one()
    {
        var thisMb = UniformBlocks(refIdx: 0, mvX: 0, mvY: 0);
        thisMb[6] = new InterEdgeNeighbour(RefIdx: 0, MvXQpel: 3, MvYQpel: 0);

        var bsH = new byte[16];
        var bsV = new byte[16];
        H264InterBoundaryStrength.Compute(thisMb, ReadOnlySpan<InterEdgeNeighbour>.Empty,
            ReadOnlySpan<InterEdgeNeighbour>.Empty, bsH, bsV);

        bsH[1 * 4 + 2].Should().Be(0, "Δmv.x of 3 qpel is below the spec's >=4 threshold → bs=0");
        bsH[2 * 4 + 2].Should().Be(0);
        bsV[2 * 4 + 1].Should().Be(0);
        bsV[3 * 4 + 1].Should().Be(0);
    }

    /// <summary>
    /// |Δmv.y| = 5 — confirms the spec rule applies symmetrically to the y component (a wrapper
    /// that only checked the x component would silently miss vertical motion drift).
    /// </summary>
    [Fact]
    public void Compute_internal_edge_at_mv_delta_y_5_triggers_bs_one()
    {
        var thisMb = UniformBlocks(refIdx: 0, mvX: 0, mvY: 0);
        thisMb[6] = new InterEdgeNeighbour(RefIdx: 0, MvXQpel: 0, MvYQpel: 5);

        var bsH = new byte[16];
        var bsV = new byte[16];
        H264InterBoundaryStrength.Compute(thisMb, ReadOnlySpan<InterEdgeNeighbour>.Empty,
            ReadOnlySpan<InterEdgeNeighbour>.Empty, bsH, bsV);

        bsH[1 * 4 + 2].Should().Be(1, "|Δmv.y|=5 ≥ 4 → bs=1 on the affected horizontal edge segment");
        bsH[2 * 4 + 2].Should().Be(1);
        bsV[2 * 4 + 1].Should().Be(1);
        bsV[3 * 4 + 1].Should().Be(1);
    }

    /// <summary>
    /// Intra neighbour (refIdx == -1) must produce bs=1 from this function. The slice encoder
    /// later overwrites with bs=3/4 for the proper intra cases per H.264 8.7.2.1, but the
    /// inter-only path must surface the "different ref" condition for the upper layer.
    /// </summary>
    [Fact]
    public void Compute_intra_neighbour_with_refIdx_minus_one_yields_bs_one()
    {
        var thisMb = UniformBlocks(refIdx: 0, mvX: 0, mvY: 0);
        var above = UniformNeighbour(refIdx: -1, mvX: 0, mvY: 0);
        var left = UniformNeighbour(refIdx: -1, mvX: 0, mvY: 0);

        var bsH = new byte[16];
        var bsV = new byte[16];
        H264InterBoundaryStrength.Compute(thisMb, above, left, bsH, bsV);

        for (var seg = 0; seg < 4; seg++)
        {
            bsH[0 * 4 + seg].Should().Be(1, $"intra above neighbour (refIdx=-1) ⇒ bs=1 on top edge seg {seg}");
            bsV[0 * 4 + seg].Should().Be(1, $"intra left neighbour (refIdx=-1) ⇒ bs=1 on left edge seg {seg}");
        }
    }

    /// <summary>
    /// Picture-boundary case: when the above neighbour row is empty (this MB sits on the top
    /// edge of the picture) the top external edge segments must be filled with bs=0. The slice
    /// encoder skips deblocking those edges anyway, but the function must produce a definite
    /// value rather than reading garbage / throwing.
    /// </summary>
    [Fact]
    public void Compute_top_picture_boundary_yields_bs_zero_on_top_edge()
    {
        var thisMb = UniformBlocks(refIdx: 0, mvX: 7, mvY: 7);
        var left = UniformNeighbour(refIdx: 0, mvX: 7, mvY: 7);

        var bsH = new byte[16];
        var bsV = new byte[16];
        H264InterBoundaryStrength.Compute(thisMb, ReadOnlySpan<InterEdgeNeighbour>.Empty, left, bsH, bsV);

        for (var seg = 0; seg < 4; seg++)
        {
            bsH[0 * 4 + seg].Should().Be(0,
                $"top picture boundary (above row empty) ⇒ bs=0 on top external edge seg {seg}");
        }
    }

    /// <summary>
    /// Same as above but for the left picture boundary.
    /// </summary>
    [Fact]
    public void Compute_left_picture_boundary_yields_bs_zero_on_left_edge()
    {
        var thisMb = UniformBlocks(refIdx: 0, mvX: 7, mvY: 7);
        var above = UniformNeighbour(refIdx: 0, mvX: 7, mvY: 7);

        var bsH = new byte[16];
        var bsV = new byte[16];
        H264InterBoundaryStrength.Compute(thisMb, above, ReadOnlySpan<InterEdgeNeighbour>.Empty, bsH, bsV);

        for (var seg = 0; seg < 4; seg++)
        {
            bsV[0 * 4 + seg].Should().Be(0,
                $"left picture boundary (left col empty) ⇒ bs=0 on left external edge seg {seg}");
        }
    }

    /// <summary>
    /// Slot-layout sanity: a documented bs[edge*4 + segment] layout must place a divergent
    /// internal vertical edge at the predicted index. Hand-construct a setup where ONLY the
    /// boundary between column 1 and column 2 of row 0 has a Δmv ≥ 4, then assert exactly
    /// `bsVertical[2 * 4 + 0] == 1` and every other slot is 0.
    /// </summary>
    [Fact]
    public void Compute_documented_slot_layout_places_divergent_vertical_edge_at_expected_index()
    {
        var thisMb = UniformBlocks(refIdx: 0, mvX: 0, mvY: 0);
        // Only block (row=0, col=2) gets a divergent MV — affects vertical edge=2 seg=0 (col 1 vs col 2)
        // and vertical edge=3 seg=0 (col 2 vs col 3) for that row only.
        thisMb[0 * 4 + 2] = new InterEdgeNeighbour(RefIdx: 0, MvXQpel: 8, MvYQpel: 0);

        var bsH = new byte[16];
        var bsV = new byte[16];
        H264InterBoundaryStrength.Compute(thisMb,
            ReadOnlySpan<InterEdgeNeighbour>.Empty, ReadOnlySpan<InterEdgeNeighbour>.Empty,
            bsH, bsV);

        bsV[2 * 4 + 0].Should().Be(1, "vertical edge=2 seg=0 spans block (0,1)→(0,2) which now has Δmv.x=8");
        bsV[3 * 4 + 0].Should().Be(1, "vertical edge=3 seg=0 spans block (0,2)→(0,3) which has Δmv.x=8");
        // Every other vertical slot is 0 (col-0 boundary is picture edge, internal cols 1 and other rows
        // share MV).
        for (var i = 0; i < 16; i++)
        {
            if (i == 2 * 4 + 0 || i == 3 * 4 + 0)
            {
                continue;
            }
            bsV[i].Should().Be(0, $"vertical slot {i} (edge={i / 4}, seg={i % 4}) must be 0");
        }
        // Horizontal edges: block 2 differs from blocks 6 in row 1 → bsH[1*4+2] = 1.
        bsH[1 * 4 + 2].Should().Be(1, "horizontal edge=1 seg=2 spans block 2 → block 6 with Δmv.x=8");
        for (var i = 0; i < 16; i++)
        {
            if (i == 1 * 4 + 2)
            {
                continue;
            }
            bsH[i].Should().Be(0, $"horizontal slot {i} (edge={i / 4}, seg={i % 4}) must be 0");
        }
    }
#endif
}
