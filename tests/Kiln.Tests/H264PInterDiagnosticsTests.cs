using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

public sealed class H264PInterDiagnosticsTests
{
    [Fact]
    public void Phase_counters_P_slice_sum_to_macroblocks_per_frame_after_IDR()
    {
        const int w = 320;
        const int h = 240;
        var ySize = w * h;
        var uvSize = ySize / 4;
        var i420 = new byte[ySize + 2 * uvSize];
        new Random(unchecked((int)0xC001D00D)).NextBytes(i420);

        H264PInterDiagnostics.CollectPhaseCounts = true;
        H264PInterDiagnostics.DisablePhase2bManual = false;
        H264PInterDiagnostics.ResetPhaseCounts();

        var annex = new byte[w * h * 2 + 512_000];
        try
        {
            var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
            {
                QuantizationParameter = 28,
                KeyframeIntervalFrames = 60,
                PreferHardwareIntrinsics = true,
                SliceCount = 1,
                EnableIntraInPFallback = true,
            });

            _ = enc.EncodeFrame(
                i420.AsSpan(0, ySize),
                i420.AsSpan(ySize, uvSize),
                i420.AsSpan(ySize + uvSize, uvSize),
                w,
                w / 2,
                annex,
                forceKeyframe: true);

            H264PInterDiagnostics.ResetPhaseCounts();
            _ = enc.EncodeFrame(
                i420.AsSpan(0, ySize),
                i420.AsSpan(ySize, uvSize),
                i420.AsSpan(ySize + uvSize, uvSize),
                w,
                w / 2,
                annex,
                forceKeyframe: false);

            var mbW = (w + 15) / 16;
            var mbH = (h + 15) / 16;
            var mbPerFrame = mbW * mbH;

            var (p1, p2, p2b) = H264PInterDiagnostics.ReadPhaseCounts();
            (p1 + p2).Should().Be(mbPerFrame, "every inter-capable MB hits Phase1 or Phase2 exclusively");
            p2b.Should().BeLessThanOrEqualTo(p2, "Phase2b intra wins cannot exceed Phase2 ME entries");
        }
        finally
        {
            H264PInterDiagnostics.CollectPhaseCounts = false;
            H264PInterDiagnostics.DisablePhase2bManual = false;
            H264PInterDiagnostics.ResetPhaseCounts();
        }
    }

    [Fact]
    public void DisablePhase2bManual_never_reports_Phase2b_intra_wins()
    {
        const int w = 320;
        const int h = 240;
        var ySize = w * h;
        var uvSize = ySize / 4;
        var i420 = new byte[ySize + 2 * uvSize];
        new Random(77).NextBytes(i420);
        var annex = new byte[w * h * 2 + 512_000];

        try
        {
            H264PInterDiagnostics.DisablePhase2bManual = true;
            H264PInterDiagnostics.CollectPhaseCounts = true;
            H264PInterDiagnostics.ResetPhaseCounts();

            var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
            {
                QuantizationParameter = 28,
                KeyframeIntervalFrames = 60,
                PreferHardwareIntrinsics = true,
                SliceCount = 1,
                EnableIntraInPFallback = true,
            });

            _ = enc.EncodeFrame(i420.AsSpan(0, ySize), i420.AsSpan(ySize, uvSize),
                i420.AsSpan(ySize + uvSize, uvSize), w, w / 2, annex, forceKeyframe: true);

            _ = enc.EncodeFrame(i420.AsSpan(0, ySize), i420.AsSpan(ySize, uvSize),
                i420.AsSpan(ySize + uvSize, uvSize), w, w / 2, annex, forceKeyframe: false);

            var (_, _, p2b) = H264PInterDiagnostics.ReadPhaseCounts();
            p2b.Should().Be(0);
        }
        finally
        {
            H264PInterDiagnostics.CollectPhaseCounts = false;
            H264PInterDiagnostics.DisablePhase2bManual = false;
            H264PInterDiagnostics.ResetPhaseCounts();
        }
    }

    [Fact]
    public void Intra_in_p_fallback_disabled_reports_no_intra_wins()
    {
        const int w = 320;
        const int h = 240;
        var ySize = w * h;
        var uvSize = ySize / 4;
        var i420 = new byte[ySize + 2 * uvSize];
        new Random(991).NextBytes(i420);
        var annex = new byte[w * h * 2 + 512_000];

        try
        {
            H264PInterDiagnostics.DisablePhase2bManual = false;
            H264PInterDiagnostics.CollectPhaseCounts = true;
            H264PInterDiagnostics.ResetPhaseCounts();

            using var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
            {
                QuantizationParameter = 28,
                KeyframeIntervalFrames = 60,
                PreferHardwareIntrinsics = true,
                SliceCount = 1,
                EnableIntraInPFallback = false,
            });

            _ = enc.EncodeFrame(i420.AsSpan(0, ySize), i420.AsSpan(ySize, uvSize),
                i420.AsSpan(ySize + uvSize, uvSize), w, w / 2, annex, forceKeyframe: true);
            _ = enc.EncodeFrame(i420.AsSpan(0, ySize), i420.AsSpan(ySize, uvSize),
                i420.AsSpan(ySize + uvSize, uvSize), w, w / 2, annex, forceKeyframe: false);

            var (_, _, p2b) = H264PInterDiagnostics.ReadPhaseCounts();
            p2b.Should().Be(0);
        }
        finally
        {
            H264PInterDiagnostics.CollectPhaseCounts = false;
            H264PInterDiagnostics.DisablePhase2bManual = false;
            H264PInterDiagnostics.ResetPhaseCounts();
        }
    }

    [Fact]
    public void Phase2b_rd_accounting_collects_per_macroblock_candidate_metrics()
    {
        const int w = 320;
        const int h = 240;
        var ySize = w * h;
        var uvSize = ySize / 4;
        var i420 = new byte[ySize + 2 * uvSize];
        new Random(1234).NextBytes(i420);
        var annex = new byte[w * h * 2 + 512_000];

        try
        {
            H264PInterDiagnostics.DisablePhase2bManual = false;
            H264PInterDiagnostics.CollectPhase2bRdAccounting = true;
            H264PInterDiagnostics.ResetPhase2bRdAccounting();

            using var enc = new H264BaselineEncoder(w, h, new H264BaselineEncoderOptions
            {
                QuantizationParameter = 28,
                KeyframeIntervalFrames = 60,
                PreferHardwareIntrinsics = true,
                SliceCount = 1,
                EnableIntraInPFallback = true,
            });

            _ = enc.EncodeFrame(i420.AsSpan(0, ySize), i420.AsSpan(ySize, uvSize),
                i420.AsSpan(ySize + uvSize, uvSize), w, w / 2, annex, forceKeyframe: true);
            var pFrame = i420.ToArray();
            for (var i = 0; i < ySize; i += 97)
            {
                pFrame[i] ^= 0x2F;
            }

            _ = enc.EncodeFrame(pFrame.AsSpan(0, ySize), pFrame.AsSpan(ySize, uvSize),
                pFrame.AsSpan(ySize + uvSize, uvSize), w, w / 2, annex, forceKeyframe: false);

            var rd = H264PInterDiagnostics.ReadPhase2bRdAccounting();
            rd.EvaluatedMacroblocks.Should().BeGreaterThan(0);
            (rd.ChosenInterCount + rd.ChosenIntraCount).Should().Be(rd.EvaluatedMacroblocks);
            rd.SumInterDistortion.Should().BeGreaterThan(0);
            rd.SumIntraDistortion.Should().BeGreaterThan(0);
            rd.SumInterBits.Should().BeGreaterThan(0);
            rd.SumIntraBits.Should().BeGreaterThan(0);
        }
        finally
        {
            H264PInterDiagnostics.DisablePhase2bManual = false;
            H264PInterDiagnostics.CollectPhase2bRdAccounting = false;
            H264PInterDiagnostics.ResetPhase2bRdAccounting();
        }
    }
}
