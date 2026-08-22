using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

public sealed class H264EncodeIntrinsicsParityTests
{
    private static bool AllSimdPathsAvailable => H264IntrinsicsPreference.AllEncoderSimdAvailable;

    private static H264BaselineEncoderOptions TestOptions() =>
        new()
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = 10_000,
            PreferHardwareIntrinsics = true,
        };

    [Fact]
    public void AnnexB_identical_for_simd_vs_scalar_flat_gray_idr()
    {
        if (!AllSimdPathsAvailable)
        {
            return;
        }

        const int w = 16;
        const int h = 16;
        var yBytes = w * h;
        var cBytes = w * h / 4;
        var buffer = new byte[yBytes + 2 * cBytes];
        buffer.AsSpan().Fill(128);

        var a = EncodeOneIdr(buffer, w, h, preferIntrinsics: true);
        var b = EncodeOneIdr(buffer, w, h, preferIntrinsics: false);
        a.Should().Equal(b);
    }

    [Fact]
    public void AnnexB_identical_for_simd_vs_scalar_stripes_idr()
    {
        if (!AllSimdPathsAvailable)
        {
            return;
        }

        const int w = 16;
        const int h = 16;
        var (ySize, uvSize, buffer) = AllocI420(w, h);
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                buffer[row * w + col] = (byte)((col / 4 + row / 4) & 1);
            }
        }

        buffer.AsSpan(ySize, uvSize).Fill(128);
        buffer.AsSpan(ySize + uvSize, uvSize).Fill(128);

        var a = EncodeOneIdr(buffer, w, h, true);
        var b = EncodeOneIdr(buffer, w, h, false);
        a.Should().Equal(b);
    }

    [Fact]
    public void AnnexB_identical_for_simd_vs_scalar_pseudorandom_idr()
    {
        if (!AllSimdPathsAvailable)
        {
            return;
        }

        const int w = 16;
        const int h = 16;
        var (_, _, buffer) = AllocI420(w, h);
        var rng = new Random(31);
        rng.NextBytes(buffer);

        var a = EncodeOneIdr(buffer, w, h, true);
        var b = EncodeOneIdr(buffer, w, h, false);
        a.Should().Equal(b);
    }

    [Fact]
    public void AnnexB_identical_for_simd_vs_scalar_after_idr_p_frame()
    {
        if (!AllSimdPathsAvailable)
        {
            return;
        }

        const int w = 16;
        const int h = 16;
        var (_, _, buffer) = AllocI420(w, h);
        var rng = new Random(77);
        rng.NextBytes(buffer);

        var optsSimd = TestOptions();
        var optsScalar = TestOptions();
        optsScalar.PreferHardwareIntrinsics = false;

        var eSimd = new H264BaselineEncoder(w, h, optsSimd);
        var eScalar = new H264BaselineEncoder(w, h, optsScalar);
        var outSimd = new byte[128_000];
        var outScalar = new byte[128_000];

        var ySize = w * h;
        var uvSize = w * h / 4;
        var ySimd = buffer.AsSpan(0, ySize);
        var uSimd = buffer.AsSpan(ySize, uvSize);
        var vSimd = buffer.AsSpan(ySize + uvSize, uvSize);
        var nIdrSimd = eSimd.EncodeFrame(ySimd, uSimd, vSimd, w, w / 2, outSimd, true);
        var nIdrScalar = eScalar.EncodeFrame(ySimd, uSimd, vSimd, w, w / 2, outScalar, true);
        nIdrSimd.Should().Be(nIdrScalar);
        outSimd.AsSpan(0, nIdrSimd).SequenceEqual(outScalar.AsSpan(0, nIdrScalar)).Should().BeTrue();

        rng.NextBytes(buffer);
        var yP = buffer.AsSpan(0, ySize);
        var uP = buffer.AsSpan(ySize, uvSize);
        var vP = buffer.AsSpan(ySize + uvSize, uvSize);
        var nPSimd = eSimd.EncodeFrame(yP, uP, vP, w, w / 2, outSimd, false);
        var nPScalar = eScalar.EncodeFrame(yP, uP, vP, w, w / 2, outScalar, false);
        nPSimd.Should().Be(nPScalar);
        outSimd.AsSpan(0, nPSimd).SequenceEqual(outScalar.AsSpan(0, nPScalar)).Should().BeTrue();
    }

    [Fact]
    public void AnnexB_identical_when_simd_fused_transform_bundle_pref_flag_enabled()
    {
        if (!AllSimdPathsAvailable || !H264TransformBundle.IsSimdBundleSupported)
        {
            return;
        }

        var saved = H264TransformBundle.PreferSimdBundleByDefault;
        try
        {
            H264TransformBundle.PreferSimdBundleByDefault = true;

            const int w = 16;
            const int h = 16;
            var yBytes = w * h;
            var cBytes = w * h / 4;
            var buffer = new byte[yBytes + 2 * cBytes];
            buffer.AsSpan().Fill(128);

            var a = EncodeOneIdr(buffer, w, h, preferIntrinsics: true);
            var b = EncodeOneIdr(buffer, w, h, preferIntrinsics: false);
            a.Should().Equal(b);
        }
        finally
        {
            H264TransformBundle.PreferSimdBundleByDefault = saved;
        }
    }

    private static (int ySize, int uvSize, byte[] buffer) AllocI420(int w, int h)
    {
        var ySize = w * h;
        var uvSize = w * h / 4;
        return (ySize, uvSize, new byte[ySize + 2 * uvSize]);
    }

    private static byte[] EncodeOneIdr(byte[] i420, int w, int h, bool preferIntrinsics)
    {
        var opts = TestOptions();
        opts.PreferHardwareIntrinsics = preferIntrinsics;
        var enc = new H264BaselineEncoder(w, h, opts);
        var dest = new byte[128_000];
        var ySize = w * h;
        var uvSize = w * h / 4;
        var y = i420.AsSpan(0, ySize);
        var u = i420.AsSpan(ySize, uvSize);
        var v = i420.AsSpan(ySize + uvSize, uvSize);
        var n = enc.EncodeFrame(y, u, v, w, w / 2, dest, forceKeyframe: true);
        return dest.AsSpan(0, n).ToArray();
    }
}
