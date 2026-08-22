using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Route A uses per-raster MF = floor(Table 7–3 / 2) to pair with the forward column-pass ×2 butterfly.
/// Integer truncation means quant levels can differ from a naïve <c>|W|×MF_spec</c> formula by ≤1 at some edges;
/// this file locks the structural invariants plus a bounded drift check.
/// </summary>
public sealed class H264QuantMfVsSpecParityTests
{
    [Fact]
    public void Halved_mf_tables_match_floor_of_spec_mf_columns()
    {
        for (var i = 0; i < 6; i++)
        {
            (H264BlockTransform.FullMfAa[i] >> 1).Should().Be(H264BlockTransform.MfHalvedAa[i]);
            (H264BlockTransform.FullMfAb[i] >> 1).Should().Be(H264BlockTransform.MfHalvedAb[i]);
            (H264BlockTransform.FullMfBb[i] >> 1).Should().Be(H264BlockTransform.MfHalvedBb[i]);
        }
    }

    [Fact]
    public void Kiln_quant_vs_naive_spec_formula_stays_within_one_level_for_singleton_nonzero()
    {
        Span<int> block = stackalloc int[16];
        for (var qp = 0; qp <= 51; qp++)
        {
            var qbits = 15 + (qp / 6);
            var add = 1 << (qbits - 1);
            var qpRem = qp % 6;
            for (var k = 0; k < 16; k++)
            {
                var fullMf = H264BlockTransform.FullMfForRasterIndex(qpRem, k);
                for (var mag = 1; mag <= 2000; mag += 11)
                {
                    foreach (var sign in new[] { -1, 1 })
                    {
                        var wSpec = sign * mag;
                        block.Clear();
                        block[k] = wSpec;
                        H264BlockTransform.Quant4X4Scalar(block, qp);
                        var proxLevel = block[k];
                        var naiveSpec = sign * ((mag * fullMf + add) >> qbits);
                        var d = Math.Abs(proxLevel - naiveSpec);
                        d.Should().BeLessThanOrEqualTo(1,
                            $"qp={qp} k={k} mag={mag} prox={proxLevel} naiveSpec={naiveSpec} fullMf={fullMf}");
                    }
                }
            }
        }
    }
}
