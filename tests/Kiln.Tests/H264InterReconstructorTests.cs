// Phase-1 acceptance test for Composer2 task F2b/W3 (H264InterReconstructor).
//
// Authoring strategy: STRATEGY A from the F2a playbook — the real test fixtures live inside
// `#if HAS_INTER_RECONSTRUCTOR` blocks that reference internal types the W3 worker will add
// in `src/Kiln/Internal/H264/H264InterReconstructor.cs`. With that symbol UNDEFINED
// (the state at file commit time), only the
// `InterReconstructor_must_be_delivered_before_test_runs` fact is compiled, and it fails with
// a Composer2-actionable error so the W3 worker's mandatory pre-check (rule 11 of the F2b
// common preamble) lights up immediately. After the W3 worker lands the production module,
// the senior's phase-3 integration step adds
// `<DefineConstants>$(DefineConstants);HAS_INTER_RECONSTRUCTOR</DefineConstants>` to
// `tests/Kiln.Tests/Kiln.Tests.csproj`; the gate test then short-
// circuits to a no-op pass and the real per-(qpel-position × block-size) parity suite below
// activates.
//
// Drift-trap context (see h264_encoder_f2b_delegation_orchestration.md "The drift trap"):
//   The wrapper's only job is to translate (mbX, mbY, mvX_qpel, mvY_qpel) into the
//   (srcOriginX, srcOriginY, xFrac, yFrac) pair the underlying interpolators consume — but
//   the math has at least three off-by-one traps the spec is silent on (signed shift vs
//   integer division for the integer part, halo vs picture coords, chroma MV decomposition
//   per H.264 8.4.1.4). Every miss decodes through ffmpeg cleanly while bleeding 0.5–2 dB
//   per GOP. The fixtures below therefore assert byte-for-byte equality between the wrapper
//   output and a direct call to `H264QpelLumaInterp.Interpolate` /
//   `H264BilinearChromaInterp.Interpolate` (which are the bit-exact-tested reference
//   functions) at every (xFrac, yFrac) on every supported block size.

using Xunit;

#if HAS_INTER_RECONSTRUCTOR
using FluentAssertions;
using Kiln.Internal.H264;
#endif

namespace Kiln.Tests;

/// <summary>
/// Phase-1 acceptance test class for the F2b/W3 deliverable
/// (<c>Kiln.Internal.H264.H264InterReconstructor</c>). The class is the Composer2
/// worker's contract: it must be green by the end of W3's diff. The set of <see cref="FactAttribute"/>s
/// changes depending on the <c>HAS_INTER_RECONSTRUCTOR</c> compile-time symbol — see the file-level
/// comment for the strategy explanation.
/// </summary>
public sealed class H264InterReconstructorTests
{
#if !HAS_INTER_RECONSTRUCTOR
    /// <summary>
    /// Pre-delivery gate: as long as the W3 production module is missing, this test fails with
    /// a clear, actionable message so a Composer2 worker reading the failure log knows exactly
    /// what to add. The full fixture suite (per-qpel-position parity at every block size) lives
    /// below this method inside <c>#if HAS_INTER_RECONSTRUCTOR</c> and activates after the
    /// senior toggles the build symbol in phase-3 integration.
    /// </summary>
    [Fact]
    public void InterReconstructor_must_be_delivered_before_test_runs()
    {
        var t = Type.GetType("Kiln.Internal.H264.H264InterReconstructor, Kiln");
        if (t is null)
        {
            Assert.Fail(
                "W3 has not been delivered: type Kiln.Internal.H264.H264InterReconstructor " +
                "is missing. Implement the module per src/Kiln/Internal/H264/H264InterReconstructor.cs " +
                "with the public API given in the W3 Composer2 prompt (`ReconstructLuma`, `ReconstructChroma`).");
        }

        Assert.Fail(
            "W3 appears to be delivered (Kiln.Internal.H264.H264InterReconstructor exists), " +
            "but the test project's `HAS_INTER_RECONSTRUCTOR` build symbol is not enabled. The senior " +
            "must add `<DefineConstants>$(DefineConstants);HAS_INTER_RECONSTRUCTOR</DefineConstants>` " +
            "to a `<PropertyGroup>` in `tests/Kiln.Tests/Kiln.Tests.csproj` to " +
            "activate the real H264InterReconstructorTests fixtures (see file header comment).");
    }
#else
    // -----------------------------------------------------------------------------------------
    // Real fixture suite (active when HAS_INTER_RECONSTRUCTOR is defined).
    // -----------------------------------------------------------------------------------------

    private const int LumaPicWidth = 64;
    private const int LumaPicHeight = 64;
    private const int LumaHalo = 16;
    private const int LumaMbX = 32;
    private const int LumaMbY = 32;

    private const int ChromaPicWidth = 32;
    private const int ChromaPicHeight = 32;
    private const int ChromaHalo = 8;
    private const int ChromaMbX = 16;
    private const int ChromaMbY = 16;

    private const int RngSeed = 0x0F2B_0003;

    /// <summary>Builds a deterministic random plane of size <paramref name="w"/> × <paramref name="h"/>.</summary>
    private static byte[] BuildPlane(int w, int h, int seed = RngSeed)
    {
        var rng = new Random(seed);
        var bytes = new byte[w * h];
        rng.NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Inline copy of the H.264 8.4.2.1 clamp-to-border oracle (matches the W1 prompt's Inline
    /// 1). Used in the `#else` fall-back branch when W1 is not yet delivered, but here in the
    /// W3 fixture we always have W1 available (W3 is integrated after W1 by spec); we still
    /// keep this hand oracle so the test does NOT depend on the production padder's correctness
    /// for the wrapper-vs-direct parity assertion.
    /// </summary>
    private static byte[] HandPad(ReadOnlySpan<byte> src, int srcW, int srcH, int halo)
    {
        var dstW = srcW + 2 * halo;
        var dstH = srcH + 2 * halo;
        var dst = new byte[dstW * dstH];
        for (var y = 0; y < dstH; y++)
        {
            for (var x = 0; x < dstW; x++)
            {
                var sx = Math.Clamp(x - halo, 0, srcW - 1);
                var sy = Math.Clamp(y - halo, 0, srcH - 1);
                dst[y * dstW + x] = src[sy * srcW + sx];
            }
        }

        return dst;
    }

    public static IEnumerable<object[]> LumaQpelCases()
    {
        // Cartesian product (blockSize ∈ {4, 8, 16}) × (xFrac ∈ 0..3) × (yFrac ∈ 0..3) per the
        // F2b orchestration doc's "16 luma qpel positions × N block sizes" requirement. mvIntX
        // and mvIntY are non-zero so the integer-pel translation path is exercised separately
        // from the fractional one (a wrapper that confuses the two would still pass at MV=(0,0)).
        var blockSizes = new[] { 4, 8, 16 };
        var mvInts = new[] { (0, 0), (3, 2), (-3, -2), (1, -1) };
        foreach (var bs in blockSizes)
        {
            foreach (var (mvIntX, mvIntY) in mvInts)
            {
                for (var xFrac = 0; xFrac < 4; xFrac++)
                {
                    for (var yFrac = 0; yFrac < 4; yFrac++)
                    {
                        yield return new object[] { bs, mvIntX, mvIntY, xFrac, yFrac };
                    }
                }
            }
        }
    }

    /// <summary>
    /// For every (block size, integer MV, qpel xFrac, qpel yFrac), assert the W3 wrapper's
    /// reconstructed block equals what <see cref="H264QpelLumaInterp.Interpolate"/> returns
    /// when called directly with the equivalent (srcOriginX, srcOriginY, xFrac, yFrac). The
    /// assertion is per-sample: a single mismatched byte fails with the exact (i, j) so the
    /// worker can pinpoint the off-by-one.
    /// </summary>
    [Theory]
    [MemberData(nameof(LumaQpelCases))]
    public void ReconstructLuma_matches_direct_qpel_interpolator(int blockSize, int mvIntX, int mvIntY, int xFrac, int yFrac)
    {
        var src = BuildPlane(LumaPicWidth, LumaPicHeight);
        var padded = HandPad(src, LumaPicWidth, LumaPicHeight, LumaHalo);
        var paddedStride = LumaPicWidth + 2 * LumaHalo;

        var mvxQpel = mvIntX * 4 + xFrac;
        var mvyQpel = mvIntY * 4 + yFrac;

        var wrapped = new byte[blockSize * blockSize];
        H264InterReconstructor.ReconstructLuma(
            padded, paddedRefStride: paddedStride, haloLuma: LumaHalo,
            mbX: LumaMbX, mbY: LumaMbY,
            mvX_qpel: mvxQpel, mvY_qpel: mvyQpel,
            blockWidth: blockSize, blockHeight: blockSize,
            wrapped, dstStride: blockSize);

        // Direct call equivalent: the integer-pel reference position in PADDED coords =
        // (mbX + mvIntX) + halo, (mbY + mvIntY) + halo. The qpel fractional part is unchanged.
        var direct = new byte[blockSize * blockSize];
        H264QpelLumaInterp.Interpolate(
            padded, srcStride: paddedStride,
            srcOriginX: LumaMbX + mvIntX + LumaHalo,
            srcOriginY: LumaMbY + mvIntY + LumaHalo,
            xFrac: xFrac, yFrac: yFrac,
            blockWidth: blockSize, blockHeight: blockSize,
            direct, dstStride: blockSize);

        for (var i = 0; i < blockSize; i++)
        {
            for (var j = 0; j < blockSize; j++)
            {
                wrapped[i * blockSize + j].Should().Be(direct[i * blockSize + j],
                    $"luma sample at ({j}, {i}) for blockSize={blockSize}, mvInt=({mvIntX},{mvIntY}), " +
                    $"qpel=({xFrac},{yFrac}) must match a direct H264QpelLumaInterp.Interpolate call");
            }
        }
    }

    public static IEnumerable<object[]> ChromaEighthPelCases()
    {
        // (blockSize ∈ {4, 8}) × (xFrac ∈ 0..7) × (yFrac ∈ 0..7) per the orchestration doc's
        // "64 chroma 1/8-pel positions" requirement. mvIntX/mvIntY are picked to hit positive
        // and negative integer-pel offsets in chroma coords.
        var blockSizes = new[] { 4, 8 };
        var mvInts = new[] { (0, 0), (2, 1), (-2, -1) };
        foreach (var bs in blockSizes)
        {
            foreach (var (mvIntX, mvIntY) in mvInts)
            {
                for (var xFrac = 0; xFrac < 8; xFrac++)
                {
                    for (var yFrac = 0; yFrac < 8; yFrac++)
                    {
                        yield return new object[] { bs, mvIntX, mvIntY, xFrac, yFrac };
                    }
                }
            }
        }
    }

    /// <summary>
    /// Chroma counterpart: per H.264 8.4.1.4, chroma MV components are decomposed from the LUMA
    /// MV by `mvIntC = lumaMvQpel >> 3` and `xFracC = lumaMvQpel & 7`. The wrapper takes a
    /// luma qpel MV and must produce the same output as a direct
    /// <see cref="H264BilinearChromaInterp.Interpolate"/> call with the equivalent chroma
    /// origin and 1/8-pel fractional. Tested across all 64 (xFrac, yFrac) combinations × 2
    /// block sizes × 3 integer-pel offsets.
    /// </summary>
    [Theory]
    [MemberData(nameof(ChromaEighthPelCases))]
    public void ReconstructChroma_matches_direct_bilinear_after_luma_to_chroma_mv_decomposition(
        int blockSize, int mvIntChromaX, int mvIntChromaY, int xFrac, int yFrac)
    {
        var src = BuildPlane(ChromaPicWidth, ChromaPicHeight, seed: RngSeed ^ 0x1C);
        var padded = HandPad(src, ChromaPicWidth, ChromaPicHeight, ChromaHalo);
        var paddedStride = ChromaPicWidth + 2 * ChromaHalo;

        // Luma qpel MV that decomposes into the desired (chroma int, chroma 1/8-pel) per
        // 8.4.1.4: lumaMvQpel = (chromaInt << 3) + xFrac.
        var lumaMvX = (mvIntChromaX << 3) + xFrac;
        var lumaMvY = (mvIntChromaY << 3) + yFrac;

        var wrapped = new byte[blockSize * blockSize];
        H264InterReconstructor.ReconstructChroma(
            padded, paddedRefStride: paddedStride, haloChroma: ChromaHalo,
            mbCx: ChromaMbX, mbCy: ChromaMbY,
            lumaMvX_qpel: lumaMvX, lumaMvY_qpel: lumaMvY,
            blockWidth: blockSize, blockHeight: blockSize,
            wrapped, dstStride: blockSize);

        var direct = new byte[blockSize * blockSize];
        H264BilinearChromaInterp.Interpolate(
            padded, srcStride: paddedStride,
            srcOriginX: ChromaMbX + mvIntChromaX + ChromaHalo,
            srcOriginY: ChromaMbY + mvIntChromaY + ChromaHalo,
            xFrac: xFrac, yFrac: yFrac,
            blockWidth: blockSize, blockHeight: blockSize,
            direct, dstStride: blockSize);

        for (var i = 0; i < blockSize; i++)
        {
            for (var j = 0; j < blockSize; j++)
            {
                wrapped[i * blockSize + j].Should().Be(direct[i * blockSize + j],
                    $"chroma sample at ({j}, {i}) for blockSize={blockSize}, " +
                    $"chromaInt=({mvIntChromaX},{mvIntChromaY}), 1/8-pel=({xFrac},{yFrac}) " +
                    $"must match a direct H264BilinearChromaInterp.Interpolate call");
            }
        }
    }

    public static IEnumerable<object[]> LumaToChromaMvDecompositionCases()
    {
        // Hand-constructed (lumaQpelX, lumaQpelY, expectedChromaIntX, expectedChromaIntY,
        // expectedChroma8pelX, expectedChroma8pelY) tuples spanning positive/negative integer
        // and qpel cases per H.264 8.4.1.4. Worked out by hand from `mvIntC = lumaMv >> 3`
        // (arithmetic shift) and `xFracC = lumaMv & 7`:
        //   +9 = (1, 1)        -1 = (-1, 7)        17 = (2, 1)
        //   -9 = (-2, 7)        8 = (1, 0)        -8 = (-1, 0)
        // The negative cases catch a `mvIntC = lumaMv / 8` implementation that rounds toward
        // zero — those would compute (0, -1) instead of (-1, 7) for lumaMv=-1, silently
        // misaligning chroma reads at every negative MV.
        yield return new object[] { 9, 9, 1, 1, 1, 1 };
        yield return new object[] { -1, -1, -1, -1, 7, 7 };
        yield return new object[] { 17, 0, 2, 0, 1, 0 };
        yield return new object[] { -9, -9, -2, -2, 7, 7 };
        yield return new object[] { 8, -8, 1, -1, 0, 0 };
        yield return new object[] { 0, 0, 0, 0, 0, 0 };
    }

    /// <summary>
    /// Verifies the chroma MV decomposition end-to-end by constructing a hand-tabulated luma
    /// qpel MV (positive and negative) and asserting the wrapper produces the same output as
    /// the direct chroma-interpolator call with the spec-exact (mvIntC, frac) pair derived by
    /// arithmetic shift. Catches the most common bug at this layer: using `/8` (round toward
    /// zero) instead of `>>3` (floor) for negative MVs.
    /// </summary>
    [Theory]
    [MemberData(nameof(LumaToChromaMvDecompositionCases))]
    public void ReconstructChroma_decomposes_negative_luma_mv_with_arithmetic_shift_per_8_4_1_4(
        int lumaMvX, int lumaMvY, int expectedChromaIntX, int expectedChromaIntY,
        int expectedChromaFracX, int expectedChromaFracY)
    {
        var src = BuildPlane(ChromaPicWidth, ChromaPicHeight, seed: RngSeed ^ 0x2C);
        var padded = HandPad(src, ChromaPicWidth, ChromaPicHeight, ChromaHalo);
        var paddedStride = ChromaPicWidth + 2 * ChromaHalo;

        const int blockSize = 8;

        var wrapped = new byte[blockSize * blockSize];
        H264InterReconstructor.ReconstructChroma(
            padded, paddedRefStride: paddedStride, haloChroma: ChromaHalo,
            mbCx: ChromaMbX, mbCy: ChromaMbY,
            lumaMvX_qpel: lumaMvX, lumaMvY_qpel: lumaMvY,
            blockWidth: blockSize, blockHeight: blockSize,
            wrapped, dstStride: blockSize);

        var direct = new byte[blockSize * blockSize];
        H264BilinearChromaInterp.Interpolate(
            padded, srcStride: paddedStride,
            srcOriginX: ChromaMbX + expectedChromaIntX + ChromaHalo,
            srcOriginY: ChromaMbY + expectedChromaIntY + ChromaHalo,
            xFrac: expectedChromaFracX, yFrac: expectedChromaFracY,
            blockWidth: blockSize, blockHeight: blockSize,
            direct, dstStride: blockSize);

        for (var i = 0; i < blockSize; i++)
        {
            for (var j = 0; j < blockSize; j++)
            {
                wrapped[i * blockSize + j].Should().Be(direct[i * blockSize + j],
                    $"luma MV ({lumaMvX}, {lumaMvY}) qpel must decompose to chroma " +
                    $"int=({expectedChromaIntX}, {expectedChromaIntY}) frac=({expectedChromaFracX}, " +
                    $"{expectedChromaFracY}) per H.264 8.4.1.4 (arithmetic shift, not integer division); " +
                    $"sample ({j}, {i}) diverges");
            }
        }
    }
#endif
}
