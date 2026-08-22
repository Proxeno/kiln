using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Kiln;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Phase-1 acceptance test for Composer2 task F3 (intra-4×4 SAD lambda plumbing).
/// </summary>
/// <remarks>
/// <para>
/// Authored BEFORE the Composer2 worker adds <c>H264BaselineEncoderOptions.Intra4x4SadLambda</c>.
/// The property is therefore reached via reflection so this file compiles cleanly against the
/// production code as it exists today; once F3 lands the reflection lookups succeed and the
/// assertions run against the real plumbing.
/// </para>
/// <para>
/// Drift-trap motivation: the most likely Composer2 failure mode for a "thread an option through"
/// task is a silent no-op — the property is added to the options object but never propagates past
/// the encoder constructor (e.g. a missed argument in the <c>new H264BaselineSliceEncoder(...)</c>
/// call). This test catches that by encoding the same frame at the same QP with three different
/// lambda values and asserting the resulting Annex-B byte streams are not all identical, which is
/// only possible if the override actually reaches the slice encoder's <c>_intra4x4LambdaSad</c>
/// field. The complementary assertion that <c>null</c> is byte-for-byte identical to "property
/// never set" guards against the inverse failure mode where the property defaults to a non-null
/// value somewhere in the chain.
/// </para>
/// </remarks>
public sealed class H264LambdaPlumbingTests
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Qp = 28;

    /// <summary>
    /// Reflective lookup of the not-yet-implemented property. Returns <c>null</c> until F3 lands;
    /// each individual <see cref="FactAttribute"/> below fails fast with a clear message in that case
    /// so a Composer2 worker reading the test output knows exactly which property to add.
    /// </summary>
    private static PropertyInfo? Intra4x4SadLambdaProperty =>
        typeof(H264BaselineEncoderOptions).GetProperty(
            "Intra4x4SadLambda",
            BindingFlags.Public | BindingFlags.Instance);

    [Fact]
    public void Intra4x4SadLambda_property_must_be_present_on_encoder_options()
    {
        var prop = Intra4x4SadLambdaProperty;
        if (prop is null)
        {
            Assert.Fail(
                "F3 has not been delivered: H264BaselineEncoderOptions.Intra4x4SadLambda property is missing. " +
                "Add an `int? Intra4x4SadLambda { get; set; }` property modelled on ChromaDcRdLambda.");
        }

        prop!.PropertyType.Should().Be(typeof(int?),
            "the SAD-domain lambda field in the slice encoder is `int`, so the override must be a nullable int.");
        prop.CanRead.Should().BeTrue();
        prop.CanWrite.Should().BeTrue();
    }

    /// <summary>
    /// Three encodes at identical QP with distinct lambda overrides must not all produce the same
    /// Annex-B byte stream. If they do, the override is a no-op and Intra4×4 mode selection still
    /// runs against the QP-derived default.
    /// </summary>
    [Fact]
    public void Encoding_at_different_lambda_values_changes_byte_stream()
    {
        EnsureFixtureAndPropertyOrFail(out var y, out var u, out var v);

        var streamA = EncodeOnce(y, u, v, lambdaOverride: null);
        var streamB = EncodeOnce(y, u, v, lambdaOverride: 0);
        var streamC = EncodeOnce(y, u, v, lambdaOverride: 64);

        var aEqB = streamA.AsSpan().SequenceEqual(streamB);
        var aEqC = streamA.AsSpan().SequenceEqual(streamC);
        var bEqC = streamB.AsSpan().SequenceEqual(streamC);

        (aEqB && aEqC && bEqC).Should().BeFalse(
            "at least two of the three encodes (λ=null, λ=0, λ=64) must produce different byte streams. " +
            "If all three are identical, Intra4x4SadLambda is set on the options object but never " +
            "reaches H264BaselineSliceEncoder._intra4x4LambdaSad — most likely the encoder constructor " +
            "or slice-encoder constructor was not updated to thread the override through.");
    }

    /// <summary>
    /// Setting <c>Intra4x4SadLambda = null</c> must produce the exact same byte stream as never touching
    /// the property at all. This protects against a default that is silently non-null somewhere in the
    /// initialisation chain (e.g. a property auto-initialised to <c>0</c> instead of <c>null</c>).
    /// </summary>
    [Fact]
    public void Null_lambda_override_is_byte_identical_to_property_unset()
    {
        EnsureFixtureAndPropertyOrFail(out var y, out var u, out var v);

        var streamUnset = EncodeOnce(y, u, v, lambdaOverride: null, applyOverride: false);
        var streamNull = EncodeOnce(y, u, v, lambdaOverride: null, applyOverride: true);

        streamUnset.Should().Equal(streamNull,
            "Intra4x4SadLambda = null must be a true no-op: the byte stream produced when the property " +
            "is never set must equal the byte stream when it is explicitly assigned null. If they differ, " +
            "the override path is taking precedence over the QP-derived default even when the override is " +
            "absent — typically a missing null-coalesce in the slice encoder.");
    }

    /// <summary>
    /// Each of the three encodes (λ=null, λ=0, λ=64) must round-trip through ffmpeg with no decode
    /// errors. Skipped (early return) if ffmpeg is not on PATH so CI without ffmpeg does not break.
    /// </summary>
    [Fact]
    public void All_lambda_values_produce_streams_that_decode_cleanly_through_ffmpeg()
    {
        EnsureFixtureAndPropertyOrFail(out var y, out var u, out var v);

        if (!TryVerifyFfmpegOnPath())
        {
            return;
        }

        var streams = new (string label, byte[] bytes)[]
        {
            ("Intra4x4SadLambda=null", EncodeOnce(y, u, v, lambdaOverride: null)),
            ("Intra4x4SadLambda=0", EncodeOnce(y, u, v, lambdaOverride: 0)),
            ("Intra4x4SadLambda=64", EncodeOnce(y, u, v, lambdaOverride: 64)),
        };

        foreach (var (label, bytes) in streams)
        {
            var (_, stderr) = DecodeYuv420ToNull(bytes);
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(stderr,
                $"plumbing variant {label} must decode without ffmpeg errors");
        }
    }

    /// <summary>
    /// Loads the 320×240 fixture and ensures the property exists; calls <see cref="Assert.Fail"/>
    /// with a Composer2-actionable message otherwise.
    /// </summary>
    private static void EnsureFixtureAndPropertyOrFail(out byte[] y, out byte[] u, out byte[] v)
    {
        if (Intra4x4SadLambdaProperty is null)
        {
            Assert.Fail(
                "F3 has not been delivered: H264BaselineEncoderOptions.Intra4x4SadLambda property is missing. " +
                "Add an `int? Intra4x4SadLambda { get; set; }` property and thread it through the encoder " +
                "constructor into H264BaselineSliceEncoder._intra4x4LambdaSad via a null-coalesce.");
        }

        var fixturePath = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "H264Golden", $"frame_{Width}x{Height}.i420");
        if (!File.Exists(fixturePath))
        {
            Assert.Fail(
                $"Fixture missing: expected {fixturePath}. The lambda plumbing test depends on the same " +
                $"committed I420 fixture used by the lambda sweep harness; restore it before re-running.");
        }

        var i420 = File.ReadAllBytes(fixturePath);
        var ySize = Width * Height;
        var uvSize = ySize / 4;
        if (i420.Length < ySize + 2 * uvSize)
        {
            Assert.Fail(
                $"Fixture {fixturePath} is shorter than {ySize + 2 * uvSize} bytes; expected I420 layout " +
                $"for {Width}×{Height}.");
        }

        y = i420.AsSpan(0, ySize).ToArray();
        u = i420.AsSpan(ySize, uvSize).ToArray();
        v = i420.AsSpan(ySize + uvSize, uvSize).ToArray();
    }

    /// <summary>
    /// Encodes one frame and returns the raw Annex-B byte stream. Reflectively sets the (not-yet-existing
    /// at file-authoring time) <c>Intra4x4SadLambda</c> property when <paramref name="applyOverride"/> is
    /// <c>true</c>; when it is <c>false</c> the property is never touched, mirroring "ordinary caller"
    /// behaviour from before F3 was delivered.
    /// </summary>
    private static byte[] EncodeOnce(
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> u,
        ReadOnlySpan<byte> v,
        int? lambdaOverride,
        bool applyOverride = true)
    {
        var opts = new H264BaselineEncoderOptions
        {
            QuantizationParameter = Qp,
            KeyframeIntervalFrames = 60,
            PreferHardwareIntrinsics = true,
        };

        if (applyOverride)
        {
            var prop = Intra4x4SadLambdaProperty
                ?? throw new InvalidOperationException(
                    "Intra4x4SadLambda property must exist by the time EncodeOnce runs; caller should have " +
                    "already invoked EnsureFixtureAndPropertyOrFail.");
            prop.SetValue(opts, lambdaOverride);
        }

        var annex = new byte[Width * Height * 2 + 512_000];
        using var enc = new H264BaselineEncoder(Width, Height, opts);
        var n = enc.EncodeFrame(y, u, v, Width, Width / 2, annex, forceKeyframe: false);
        return annex.AsSpan(0, n).ToArray();
    }

    /// <summary>
    /// Pipes the Annex-B stream through <c>ffmpeg ... -f null -</c> and returns its stderr. Used purely
    /// for the decode-error substring check; the decoded raw output is discarded.
    /// </summary>
    private static (int exitCode, string stderr) DecodeYuv420ToNull(byte[] annex)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"proxeno-lambda-plumbing-{Guid.NewGuid():N}.h264");
        try
        {
            File.WriteAllBytes(tmp, annex);

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("h264");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            using var p = Process.Start(psi);
            if (p is null)
            {
                return (-1, "failed to start ffmpeg");
            }

            // Drain stdout to /dev/null so the child cannot block on a full pipe; we only care about stderr.
            _ = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                return (-1, $"timeout; partial stderr: {err}");
            }

            return (p.ExitCode, err);
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>True when <c>ffmpeg -version</c> exits 0; false otherwise. Used to skip ffmpeg-dependent
    /// assertions on environments without ffmpeg installed.</summary>
    private static bool TryVerifyFfmpegOnPath()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            return p?.WaitForExit(10_000) == true && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
