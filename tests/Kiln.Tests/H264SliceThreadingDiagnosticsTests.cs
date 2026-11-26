using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

/// <summary>
/// Pass-Th hook: slice-threading accounting must record per-slice busy windows and per-row wall
/// ticks, and must never change one byte of the bitstream.
/// </summary>
public sealed class H264SliceThreadingDiagnosticsTests
{
    private const int W = 320;
    private const int H = 240;
    private const int Frames = 4;
    private const int SliceCount = 4;

    [Fact]
    public void CollectSliceThreading_records_slice_windows_and_rows_without_changing_the_bitstream()
    {
        var (ySize, uvSize) = (W * H, W * H / 4);
        var frames = new byte[Frames][];
        for (var f = 0; f < Frames; f++)
        {
            var frame = new byte[ySize + (2 * uvSize)];
            new Random(100 + f).NextBytes(frame);
            frames[f] = frame;
        }

        var annex = new byte[(W * H * 2) + 1_048_576];
        var options = new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = int.MaxValue,
            SliceCount = SliceCount,
        };

        var plain = Encode(frames, options, annex);

        List<H264PInterDiagnostics.SliceThreadingFrame> samples;
        string hooked;
        H264PInterDiagnostics.ResetSliceThreading();
        H264PInterDiagnostics.CollectSliceThreading = true;
        try
        {
            hooked = Encode(frames, options, annex);
        }
        finally
        {
            H264PInterDiagnostics.CollectSliceThreading = false;
        }

        samples = H264PInterDiagnostics.DrainSliceThreadingFrames();
        H264PInterDiagnostics.ResetSliceThreading();

        hooked.Should().Be(plain, "timers only: the threaded bitstream must be byte-identical");
        samples.Should().NotBeEmpty("every multi-slice frame records one threading sample");

        var mbH = (H + 15) / 16;
        foreach (var sample in samples)
        {
            sample.Slices.Should().HaveCount(SliceCount);
            sample.JoinTicks.Should().BeGreaterThan(sample.DispatchTicks, "the frame has a positive wall time");
            sample.RowTicks.Should().HaveCount(mbH);
            sample.RowEffort.Should().HaveCount(mbH);

            var sumBusy = 0L;
            foreach (var slice in sample.Slices)
            {
                slice.EndTicks.Should().BeGreaterThan(slice.StartTicks, "each slice task has a positive busy time");
                sumBusy += slice.EndTicks - slice.StartTicks;
            }

            // Busy windows nest inside the dispatch-to-join interval, so their sum cannot exceed
            // sliceCount * frameWall and the idle share is non-negative.
            sumBusy.Should().BeLessThanOrEqualTo(SliceCount * (sample.JoinTicks - sample.DispatchTicks));
            sample.RowTicks.Should().Contain(x => x > 0, "row timing is captured at row boundaries");
        }
    }

    [Fact]
    public void Slice_threading_hook_records_nothing_when_off()
    {
        var (ySize, uvSize) = (W * H, W * H / 4);
        var frames = new byte[2][];
        for (var f = 0; f < frames.Length; f++)
        {
            var frame = new byte[ySize + (2 * uvSize)];
            new Random(7 + f).NextBytes(frame);
            frames[f] = frame;
        }

        var annex = new byte[(W * H * 2) + 1_048_576];
        var options = new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = int.MaxValue,
            SliceCount = SliceCount,
        };

        H264PInterDiagnostics.ResetSliceThreading();
        H264PInterDiagnostics.CollectSliceThreading = false;
        _ = Encode(frames, options, annex);

        H264PInterDiagnostics.DrainSliceThreadingFrames().Should().BeEmpty("the gate is off");
    }

    private static string Encode(byte[][] frames, H264BaselineEncoderOptions options, byte[] annex)
    {
        var ySize = W * H;
        var uvSize = ySize / 4;
        using var encoder = new H264BaselineEncoder(W, H, options);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        for (var i = 0; i < frames.Length; i++)
        {
            var frame = frames[i];
            var written = encoder.EncodeFrame(
                frame.AsSpan(0, ySize), frame.AsSpan(ySize, uvSize), frame.AsSpan(ySize + uvSize, uvSize),
                W, W / 2, annex, forceKeyframe: i == 0);
            hash.AppendData(annex, 0, written);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
