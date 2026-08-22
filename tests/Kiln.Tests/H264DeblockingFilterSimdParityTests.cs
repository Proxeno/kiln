using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Verifies that the SIMD deblocking kernel (Phase 5) produces bit-exact output vs the scalar path
/// for all bS values (0–4) across representative QP / alpha / beta combinations.
/// Runs once with SIMD on and once with SIMD off; the two outputs must be identical.
/// </summary>
public sealed class H264DeblockingFilterSimdParityTests
{
    private const int Mbs = 2;
    private const int LumaSize = Mbs * 16;
    private const int ChromaSize = Mbs * 8;

    private static byte[] BuildLuma(int seed)
    {
        var rng = new Random(seed);
        var a = new byte[LumaSize * LumaSize];
        for (var i = 0; i < a.Length; i++)
        {
            a[i] = (byte)((i / LumaSize / 16) * 60 + (i % LumaSize / 16) * 30 + 80 + rng.Next(-20, 21));
        }

        return a;
    }

    private static byte[] BuildChroma(int seed)
    {
        var rng = new Random(seed);
        var a = new byte[ChromaSize * ChromaSize];
        for (var i = 0; i < a.Length; i++)
        {
            a[i] = (byte)((i / ChromaSize / 8) * 50 + (i % ChromaSize / 8) * 25 + 80 + rng.Next(-20, 21));
        }

        return a;
    }

    private static (byte[] y, byte[] u, byte[] v) RunFilter(
        byte[] srcY, byte[] srcU, byte[] srcV,
        byte[] bsH, byte[] bsV,
        int[] qpY, int[] qpUv,
        bool preferIntrinsics)
    {
        var y = (byte[])srcY.Clone();
        var u = (byte[])srcU.Clone();
        var v = (byte[])srcV.Clone();
        using (new H264IntrinsicsPreference.Scope(preferIntrinsics))
        {
            H264DeblockingFilter.Apply(
                y, LumaSize, u, v, ChromaSize, Mbs, Mbs, bsH, bsV, qpY, qpUv,
                alphaOffsetDiv2: 0, betaOffsetDiv2: 0);
        }

        return (y, u, v);
    }

    [Theory]
    [InlineData(28, 1, 0xAA01)]
    [InlineData(28, 2, 0xAA02)]
    [InlineData(28, 3, 0xAA03)]
    [InlineData(28, 4, 0xAA04)]
    [InlineData(22, 2, 0xBB22)]
    [InlineData(36, 3, 0xCC36)]
    [InlineData(40, 4, 0xDD40)]
    [InlineData(16, 1, 0xEE16)]
    [InlineData(48, 3, 0xFF48)]
    public void Uniform_bs_simd_matches_scalar_exactly(int qp, int bs, int seed)
    {
        var srcY = BuildLuma(seed);
        var srcU = BuildChroma(seed + 1);
        var srcV = BuildChroma(seed + 2);

        const int totalBs = Mbs * Mbs * 16;
        var bsH = new byte[totalBs];
        var bsV = new byte[totalBs];
        Array.Fill(bsH, (byte)bs);
        Array.Fill(bsV, (byte)bs);

        var qpY = Enumerable.Repeat(qp, Mbs * Mbs).ToArray();
        var qpUv = Enumerable.Repeat(qp, Mbs * Mbs).ToArray();

        var (simdY, simdU, simdV) = RunFilter(srcY, srcU, srcV, bsH, bsV, qpY, qpUv, preferIntrinsics: true);
        var (scalarY, scalarU, scalarV) = RunFilter(srcY, srcU, srcV, bsH, bsV, qpY, qpUv, preferIntrinsics: false);

        for (var i = 0; i < simdY.Length; i++)
        {
            simdY[i].Should().Be(scalarY[i],
                $"luma[{i}] (y={i / LumaSize}, x={i % LumaSize}) qp={qp} bs={bs}");
        }

        for (var i = 0; i < simdU.Length; i++)
        {
            simdU[i].Should().Be(scalarU[i], $"U[{i}] qp={qp} bs={bs}");
            simdV[i].Should().Be(scalarV[i], $"V[{i}] qp={qp} bs={bs}");
        }
    }

    [Theory]
    [InlineData(28, 0x1234)]
    [InlineData(36, 0x5678)]
    [InlineData(22, 0xABCD)]
    public void Mixed_bs_per_segment_simd_matches_scalar_exactly(int qp, int seed)
    {
        var srcY = BuildLuma(seed);
        var srcU = BuildChroma(seed + 1);
        var srcV = BuildChroma(seed + 2);

        // Build bs arrays with varied values per segment (0,1,2,3,4 cycling)
        const int totalBs = Mbs * Mbs * 16;
        var bsH = new byte[totalBs];
        var bsV = new byte[totalBs];
        for (var i = 0; i < totalBs; i++)
        {
            bsH[i] = (byte)(i % 5);
            bsV[i] = (byte)((i + 2) % 5);
        }

        var qpY = Enumerable.Repeat(qp, Mbs * Mbs).ToArray();
        var qpUv = Enumerable.Repeat(qp, Mbs * Mbs).ToArray();

        var (simdY, simdU, simdV) = RunFilter(srcY, srcU, srcV, bsH, bsV, qpY, qpUv, preferIntrinsics: true);
        var (scalarY, scalarU, scalarV) = RunFilter(srcY, srcU, srcV, bsH, bsV, qpY, qpUv, preferIntrinsics: false);

        for (var i = 0; i < simdY.Length; i++)
        {
            simdY[i].Should().Be(scalarY[i],
                $"luma[{i}] qp={qp} (mixed bs per segment)");
        }

        for (var i = 0; i < simdU.Length; i++)
        {
            simdU[i].Should().Be(scalarU[i], $"U[{i}] qp={qp}");
            simdV[i].Should().Be(scalarV[i], $"V[{i}] qp={qp}");
        }
    }

    [Theory]
    [InlineData(28, 0xDEAD)]
    [InlineData(40, 0xBEEF)]
    public void Zero_bs_simd_is_identity(int qp, int seed)
    {
        var srcY = BuildLuma(seed);
        var srcU = BuildChroma(seed + 1);
        var srcV = BuildChroma(seed + 2);

        const int totalBs = Mbs * Mbs * 16;
        var bsH = new byte[totalBs];
        var bsV = new byte[totalBs];
        var qpY = Enumerable.Repeat(qp, Mbs * Mbs).ToArray();
        var qpUv = Enumerable.Repeat(qp, Mbs * Mbs).ToArray();

        var (simdY, simdU, simdV) = RunFilter(srcY, srcU, srcV, bsH, bsV, qpY, qpUv, preferIntrinsics: true);

        simdY.Should().Equal(srcY, "bs=0 everywhere: SIMD path must be a no-op on Y.");
        simdU.Should().Equal(srcU, "bs=0 everywhere: SIMD path must be a no-op on U.");
        simdV.Should().Equal(srcV, "bs=0 everywhere: SIMD path must be a no-op on V.");
    }

    [Fact]
    public void Chroma_deblock_averages_neighbor_qp_at_mb_boundary()
    {
        var srcY = BuildLuma(0xA001);
        var srcU = BuildChroma(0xA002);
        var srcV = BuildChroma(0xA003);

        const int totalBs = Mbs * Mbs * 16;
        var bsH = new byte[totalBs];
        var bsV = new byte[totalBs];
        Array.Fill(bsH, (byte)2);
        Array.Fill(bsV, (byte)2);
        var qpY = Enumerable.Repeat(28, Mbs * Mbs).ToArray();

        var qpMixed = new[] { 22, 38, 22, 38 };
        var qpUniformNeighbor = new[] { 38, 38, 22, 38 };

        var mixed = RunFilter(srcY, srcU, srcV, bsH, bsV, qpY, qpMixed, preferIntrinsics: false);
        var uniformNeighbor = RunFilter(srcY, srcU, srcV, bsH, bsV, qpY, qpUniformNeighbor, preferIntrinsics: false);

        var boundaryDiffers = false;
        for (var y = 0; y < ChromaSize; y++)
        {
            var idx = y * ChromaSize + 8;
            if (mixed.u[idx] != uniformNeighbor.u[idx])
            {
                boundaryDiffers = true;
                break;
            }
        }

        boundaryDiffers.Should().BeTrue(
            "vertical chroma MB boundary must use averaged qPav when left/right MB QPs differ");
    }

    [Fact]
    public void Simd_supported_on_this_platform()
    {
        // Informational: confirm the SIMD path is actually exercised on this build host.
        H264DeblockingFilterSimd.IsSupported.Should().BeTrue(
            "SIMD deblocking should be available on all supported build hosts (x64/AArch64).");
    }
}
