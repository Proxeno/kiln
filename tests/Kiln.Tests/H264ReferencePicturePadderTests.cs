// Phase-1 acceptance test for Composer2 task F2b/W1 (H264ReferencePicturePadder).
//
// Authoring strategy: STRATEGY A from the F2a playbook — the real test fixtures live inside
// `#if HAS_REFERENCE_PICTURE_PADDER` blocks that reference internal types the W1 worker will
// add in `src/Kiln/Internal/H264/H264ReferencePicturePadder.cs`. With that symbol
// UNDEFINED (the state at file commit time), only the
// `ReferencePicturePadder_must_be_delivered_before_test_runs` fact is compiled, and it fails
// with a Composer2-actionable error so the W1 worker's mandatory pre-check (rule 11 of the
// F2b common preamble) lights up immediately. After the W1 worker lands the production module
// the senior's phase-3 integration step adds
// `<DefineConstants>$(DefineConstants);HAS_REFERENCE_PICTURE_PADDER</DefineConstants>` to
// `tests/Kiln.Tests/Kiln.Tests.csproj`; the gate test then short-circuits
// to a no-op pass and the real per-(x,y) fixture suite below activates.
//
// Drift-trap context (see h264_encoder_f2b_delegation_orchestration.md "The drift trap"):
//   The padded reference picture is the substrate for ME and the qpel/chroma sub-pel filters;
//   a single off-by-one in border replication at the corners silently misaligns every MV that
//   lands at or near the picture boundary, decodes cleanly, and bleeds 0.5–2 dB of PSNR per
//   GOP. The fixtures below therefore assert per-(x,y) bit-exact parity against an inline
//   `Clamp(x - halo, 0, w-1) + Clamp(y - halo, 0, h-1)*srcStride` oracle, exercising every
//   corner / edge / interior region across multiple (w, h, halo) shapes including the
//   production luma (320×240, halo=16) and chroma (160×120, halo=8) sizes.

using Xunit;

#if HAS_REFERENCE_PICTURE_PADDER
using FluentAssertions;
using Kiln.Internal.H264;
#endif

namespace Kiln.Tests;

/// <summary>
/// Phase-1 acceptance test class for the F2b/W1 deliverable
/// (<c>Kiln.Internal.H264.H264ReferencePicturePadder</c>). The class is the
/// Composer2 worker's contract: it must be green by the end of W1's diff. The set of
/// <see cref="FactAttribute"/>s changes depending on the <c>HAS_REFERENCE_PICTURE_PADDER</c>
/// compile-time symbol — see the file-level comment for the strategy explanation.
/// </summary>
public sealed class H264ReferencePicturePadderTests
{
#if !HAS_REFERENCE_PICTURE_PADDER
    /// <summary>
    /// Pre-delivery gate: as long as the W1 production module is missing, this test fails with
    /// a clear, actionable message so a Composer2 worker reading the failure log knows exactly
    /// what to add. The full fixture suite (per-(x,y) corner/edge/interior parity at multiple
    /// sizes) lives below this method inside <c>#if HAS_REFERENCE_PICTURE_PADDER</c> and
    /// activates after the senior toggles the build symbol in phase-3 integration.
    /// </summary>
    [Fact]
    public void ReferencePicturePadder_must_be_delivered_before_test_runs()
    {
        var t = Type.GetType("Kiln.Internal.H264.H264ReferencePicturePadder, Kiln");
        if (t is null)
        {
            Assert.Fail(
                "W1 has not been delivered: type Kiln.Internal.H264.H264ReferencePicturePadder " +
                "is missing. Implement the module per src/Kiln/Internal/H264/H264ReferencePicturePadder.cs " +
                "with the public API given in the W1 Composer2 prompt " +
                "(static `Pad(ReadOnlySpan<byte> src, int srcStride, int srcWidth, int srcHeight, int halo, " +
                "Span<byte> dst, int dstStride)`).");
        }

        Assert.Fail(
            "W1 appears to be delivered (Kiln.Internal.H264.H264ReferencePicturePadder exists), " +
            "but the test project's `HAS_REFERENCE_PICTURE_PADDER` build symbol is not enabled. The senior " +
            "must add `<DefineConstants>$(DefineConstants);HAS_REFERENCE_PICTURE_PADDER</DefineConstants>` " +
            "to a `<PropertyGroup>` in `tests/Kiln.Tests/Kiln.Tests.csproj` to " +
            "activate the real H264ReferencePicturePadderTests fixtures (see file header comment).");
    }
#else
    // -----------------------------------------------------------------------------------------
    // Real fixture suite (active when HAS_REFERENCE_PICTURE_PADDER is defined).
    // -----------------------------------------------------------------------------------------

    private const int RngSeed = 0x0F2B_0001;

    /// <summary>
    /// Builds a deterministic random plane of size <paramref name="width"/> × <paramref name="height"/>
    /// (row-stride == width, tightly packed). Seeded so failures reproduce verbatim.
    /// </summary>
    private static byte[] BuildPlane(int width, int height, int seed = RngSeed)
    {
        var rng = new Random(seed);
        var bytes = new byte[width * height];
        rng.NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Inline oracle from the W1 prompt's "Inline 1": for any (x, y) in the padded plane (where
    /// (0, 0) is the top-left corner of the halo, so the source's (0, 0) lives at (halo, halo)),
    /// the output sample is <c>src[Clamp(y - halo, 0, h-1) * srcStride + Clamp(x - halo, 0, w-1)]</c>.
    /// </summary>
    private static byte OracleSample(
        ReadOnlySpan<byte> src, int srcStride, int srcWidth, int srcHeight,
        int halo, int x, int y)
    {
        var sx = Math.Clamp(x - halo, 0, srcWidth - 1);
        var sy = Math.Clamp(y - halo, 0, srcHeight - 1);
        return src[sy * srcStride + sx];
    }

    public static IEnumerable<object[]> SizeHaloCases()
    {
        // (srcWidth, srcHeight, halo). Includes the small/medium luma sizes from the prompt
        // plus the production luma (320×240, halo=16) and 4:2:0 chroma (160×120, halo=8).
        yield return new object[] { 16, 16, 4 };
        yield return new object[] { 32, 32, 16 };
        yield return new object[] { 320, 240, 16 };
        yield return new object[] { 160, 120, 8 };
    }

    /// <summary>
    /// Per-(x, y) bit-exact parity against the inline clamp-to-border oracle across the entire
    /// padded plane (corners + edges + interior in one sweep). Asserting per (x, y) means a
    /// failure pinpoints the exact sample formula that diverged rather than reporting an
    /// aggregate diff.
    /// </summary>
    [Theory]
    [MemberData(nameof(SizeHaloCases))]
    public void Pad_matches_clamp_to_border_oracle_at_every_padded_position(int srcWidth, int srcHeight, int halo)
    {
        var src = BuildPlane(srcWidth, srcHeight);
        var dstWidth = srcWidth + 2 * halo;
        var dstHeight = srcHeight + 2 * halo;
        var dst = new byte[dstWidth * dstHeight];

        H264ReferencePicturePadder.Pad(
            src, srcStride: srcWidth, srcWidth: srcWidth, srcHeight: srcHeight,
            halo: halo,
            dst, dstStride: dstWidth);

        for (var y = 0; y < dstHeight; y++)
        {
            for (var x = 0; x < dstWidth; x++)
            {
                var expected = OracleSample(src, srcWidth, srcWidth, srcHeight, halo, x, y);
                dst[y * dstWidth + x].Should().Be(expected,
                    $"padded sample at (x={x}, y={y}) for ({srcWidth}×{srcHeight}, halo={halo}) " +
                    $"must equal Clamp({x - halo}, 0, {srcWidth - 1}) + Clamp({y - halo}, 0, {srcHeight - 1}) " +
                    $"of the source");
            }
        }
    }

    /// <summary>
    /// Targeted corner sweep: re-asserts every sample inside each of the four halo×halo corner
    /// regions for the production luma size. Redundant with the full-plane sweep but a
    /// failing run gives the worker a much more focused error log ("top-left corner sample
    /// (3, 7) wrong" instead of "one of 80,000 samples wrong").
    /// </summary>
    [Theory]
    [InlineData(320, 240, 16)]
    [InlineData(160, 120, 8)]
    public void Pad_corner_regions_replicate_corner_source_sample(int srcWidth, int srcHeight, int halo)
    {
        var src = BuildPlane(srcWidth, srcHeight);
        var dstWidth = srcWidth + 2 * halo;
        var dstHeight = srcHeight + 2 * halo;
        var dst = new byte[dstWidth * dstHeight];

        H264ReferencePicturePadder.Pad(
            src, srcStride: srcWidth, srcWidth: srcWidth, srcHeight: srcHeight,
            halo: halo,
            dst, dstStride: dstWidth);

        var srcTL = src[0];
        var srcTR = src[srcWidth - 1];
        var srcBL = src[(srcHeight - 1) * srcWidth + 0];
        var srcBR = src[(srcHeight - 1) * srcWidth + (srcWidth - 1)];

        for (var y = 0; y < halo; y++)
        {
            for (var x = 0; x < halo; x++)
            {
                dst[y * dstWidth + x].Should().Be(srcTL, $"top-left corner at ({x}, {y}) replicates src[0,0]");
                dst[y * dstWidth + (dstWidth - 1 - x)].Should().Be(srcTR, $"top-right corner replicates src[w-1,0]");
                dst[(dstHeight - 1 - y) * dstWidth + x].Should().Be(srcBL, $"bottom-left corner replicates src[0,h-1]");
                dst[(dstHeight - 1 - y) * dstWidth + (dstWidth - 1 - x)].Should().Be(srcBR,
                    $"bottom-right corner replicates src[w-1,h-1]");
            }
        }
    }

    /// <summary>
    /// Halo coverage smoke: every (x, y) in the padded coordinate range must be a defined sample
    /// (the oracle agrees), not garbage / uninitialized memory. This pairs with the W1 drift-trap
    /// row "halo size too small" — even if the worker's per-sample formula is correct, dropping
    /// any halo row/column would surface here as a stale-byte mismatch.
    /// </summary>
    [Theory]
    [MemberData(nameof(SizeHaloCases))]
    public void Pad_defines_every_sample_in_halo_neighbourhood(int srcWidth, int srcHeight, int halo)
    {
        var src = BuildPlane(srcWidth, srcHeight, seed: RngSeed ^ 0x5A);
        var dstWidth = srcWidth + 2 * halo;
        var dstHeight = srcHeight + 2 * halo;

        // Pre-fill destination with a sentinel byte that should never naturally appear at every
        // (x, y) — if any halo position survives unwritten, at least one sample will mismatch
        // the oracle (which produces the random source's clamped border value).
        var dst = new byte[dstWidth * dstHeight];
        Array.Fill(dst, (byte)0xCD);

        H264ReferencePicturePadder.Pad(
            src, srcStride: srcWidth, srcWidth: srcWidth, srcHeight: srcHeight,
            halo: halo,
            dst, dstStride: dstWidth);

        // Translate the W1 prompt's "halo coverage" range [-halo, w+halo) × [-halo, h+halo) into
        // padded-buffer coords by adding halo: x ∈ [0, dstWidth), y ∈ [0, dstHeight). Equivalent
        // to the full-sweep test, but its failure message names the halo neighbourhood explicitly.
        for (var y = 0; y < dstHeight; y++)
        {
            for (var x = 0; x < dstWidth; x++)
            {
                var expected = OracleSample(src, srcWidth, srcWidth, srcHeight, halo, x, y);
                dst[y * dstWidth + x].Should().Be(expected,
                    $"padded buffer must define a sample at every (x, y) in the halo neighbourhood; " +
                    $"({x}, {y}) was unwritten or wrong for ({srcWidth}×{srcHeight}, halo={halo})");
            }
        }
    }

    /// <summary>
    /// Validates that the pad routine honours a wider source stride than width (callers may pass
    /// a sub-view of a larger plane). The padded output dstStride is independently sized to
    /// dstWidth here; the implementation must read with srcStride and write with dstStride.
    /// </summary>
    [Fact]
    public void Pad_honours_independent_src_and_dst_strides()
    {
        const int srcWidth = 24;
        const int srcHeight = 24;
        const int halo = 4;
        const int srcStride = 40;
        const int dstWidth = srcWidth + 2 * halo;
        const int dstHeight = srcHeight + 2 * halo;
        const int dstStride = dstWidth + 7;

        var rng = new Random(RngSeed ^ 0xA5);
        var src = new byte[srcStride * srcHeight];
        rng.NextBytes(src);

        var dst = new byte[dstStride * dstHeight];

        H264ReferencePicturePadder.Pad(
            src, srcStride: srcStride, srcWidth: srcWidth, srcHeight: srcHeight,
            halo: halo,
            dst, dstStride: dstStride);

        for (var y = 0; y < dstHeight; y++)
        {
            for (var x = 0; x < dstWidth; x++)
            {
                var sx = Math.Clamp(x - halo, 0, srcWidth - 1);
                var sy = Math.Clamp(y - halo, 0, srcHeight - 1);
                var expected = src[sy * srcStride + sx];
                dst[y * dstStride + x].Should().Be(expected,
                    $"with srcStride={srcStride} and dstStride={dstStride}, padded sample at ({x}, {y}) " +
                    $"must clamp to src[{sx}, {sy}]");
            }
        }
    }
#endif
}
