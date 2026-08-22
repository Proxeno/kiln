using FluentAssertions;
using Kiln;
using Xunit;

namespace Kiln.Tests;

/// <summary>Phase 3: adaptive quantization, rate target, and per-MB λ plumbing (default path parity).</summary>
public sealed class H264AdaptiveQpTests
{
    private const int Width = 320;
    private const int Height = 240;

    private static string I420FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "H264Golden", $"frame_{Width}x{Height}.i420");

    private static void LoadFixtureOrFail(out byte[] y, out byte[] u, out byte[] v)
    {
        var path = I420FixturePath;
        if (!File.Exists(path))
        {
            Assert.Fail($"Missing fixture: {path}");
        }

        var i420 = File.ReadAllBytes(path);
        var ySize = Width * Height;
        var uvSize = ySize / 4;
        i420.Length.Should().Be(ySize + 2 * uvSize);
        y = i420.AsSpan(0, ySize).ToArray();
        u = i420.AsSpan(ySize, uvSize).ToArray();
        v = i420.AsSpan(ySize + uvSize, uvSize).ToArray();
    }

    [Fact]
    public void AqOff_OutputMatchesBaseline()
    {
        LoadFixtureOrFail(out var y, out var u, out var v);
        var cap = y.Length * 2 + 512_000;
        var a = new byte[cap];
        var b = new byte[cap];

        int na, nb;
        using (var encA = new H264BaselineEncoder(Width, Height, new H264BaselineEncoderOptions
               {
                   QuantizationParameter = 28,
                   AdaptiveQuantStrength = 0.0,
                   TargetBitsPerFrame = 0,
               }))
        {
            na = encA.EncodeFrame(y, u, v, Width, Width / 2, a, forceKeyframe: true);
        }

        using (var encB = new H264BaselineEncoder(Width, Height, new H264BaselineEncoderOptions
               {
                   QuantizationParameter = 28,
               }))
        {
            nb = encB.EncodeFrame(y, u, v, Width, Width / 2, b, forceKeyframe: true);
        }

        na.Should().Be(nb);
        a.AsSpan(0, na).SequenceEqual(b.AsSpan(0, nb)).Should().BeTrue(
            "AdaptiveQuantStrength=0 and TargetBitsPerFrame=0 must match default options byte-for-byte.");
    }

    [Fact]
    public void AqOn_OutputDiffersFromBaseline()
    {
        LoadFixtureOrFail(out var y, out var u, out var v);
        var cap = y.Length * 2 + 512_000;
        var baseline = new byte[cap];
        var aq = new byte[cap];

        int nBase, nAq;
        using (var enc = new H264BaselineEncoder(Width, Height, new H264BaselineEncoderOptions { QuantizationParameter = 28 }))
        {
            nBase = enc.EncodeFrame(y, u, v, Width, Width / 2, baseline, forceKeyframe: true);
        }

        using (var enc = new H264BaselineEncoder(Width, Height, new H264BaselineEncoderOptions
               {
                   QuantizationParameter = 28,
                   AdaptiveQuantStrength = 1.0,
               }))
        {
            nAq = enc.EncodeFrame(y, u, v, Width, Width / 2, aq, forceKeyframe: true);
        }

        nAq.Should().BePositive();
        baseline.AsSpan(0, nBase).SequenceEqual(aq.AsSpan(0, nAq)).Should().BeFalse(
            "natural imagery at AQ=1.0 should diverge from AQ=off for at least one access unit.");
    }

    [Fact]
    public void RateTarget_QpVariesAcrossMbs()
    {
        LoadFixtureOrFail(out var y, out var u, out var v);
        var cap = y.Length * 2 + 512_000;
        var annex = new byte[cap];

        using var enc = new H264BaselineEncoder(Width, Height, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            TargetBitsPerFrame = 80_000,
        });
        var written = enc.EncodeFrame(y, u, v, Width, Width / 2, annex, forceKeyframe: true);
        written.Should().BePositive();

        var qp = enc.TestHookLastEncodedQpY;
        qp.Length.Should().Be((Width / 16) * (Height / 16));
        qp.ToArray().Distinct().Count().Should().BeGreaterThan(1);
    }

    [Fact]
    public void PerMbLambda_ZeroAq_ByteIdenticalToBaseline()
    {
        LoadFixtureOrFail(out var y, out var u, out var v);
        var cap = y.Length * 2 + 512_000;
        var first = new byte[cap];
        var second = new byte[cap];

        var opts = new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            AdaptiveQuantStrength = 0,
            TargetBitsPerFrame = 0,
        };

        int n1, n2;
        using (var enc = new H264BaselineEncoder(Width, Height, opts))
        {
            n1 = enc.EncodeFrame(y, u, v, Width, Width / 2, first, forceKeyframe: true);
        }

        using (var enc = new H264BaselineEncoder(Width, Height, opts))
        {
            n2 = enc.EncodeFrame(y, u, v, Width, Width / 2, second, forceKeyframe: true);
        }

        n1.Should().Be(n2);
        first.AsSpan(0, n1).SequenceEqual(second.AsSpan(0, n2)).Should().BeTrue(
            "two runs with AQ off and no rate target must match (per-MB λ uses qpThisMb == slice QP).");
    }
}
