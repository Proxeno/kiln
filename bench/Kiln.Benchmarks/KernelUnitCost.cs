using System.Diagnostics;
using System.Text;
using Kiln.Internal.H264;

namespace Kiln.Benchmarks;

/// <summary>
/// Per-kernel modelled unit cost for the stage profiler's <c>kernel_ns_modelled</c> column.
///
/// <para>
/// Every <see cref="IH264KernelSet"/> member pass C recorded is batch-timed on representative
/// inputs taken from the 720p <c>moving</c> clip: <see cref="Trials"/> trials of
/// <see cref="CallsPerTrial"/> calls, each trial's total divided by the call count, and the
/// minimum trial kept. Single calls are never timed — at 20k calls per trial the timer overhead
/// is amortised far below one call. The result is a <em>modelled</em> cost (tier
/// <see cref="Tier"/>), not a measured end-to-end attribution: it says what the stage would cost
/// if every recorded call cost this much on this machine.
/// </para>
/// </summary>
internal static class KernelUnitCost
{
    /// <summary>Timed batches per member; the minimum batch average is kept.</summary>
    public const int Trials = 7;

    /// <summary>Calls per timed batch.</summary>
    public const int CallsPerTrial = 20_000;

    /// <summary>Cost tier label written into the profile.</summary>
    public const string Tier = "modelled";

    /// <summary>Times every supported member and returns ns per call, keyed by member name.</summary>
    public static IReadOnlyDictionary<string, double> Measure()
    {
        var kernels = H264KernelSet.CreateBest();
        // The 720p moving clip's I420 frame: Y plane then U then V, each row-major.
        var frame = H264ResolutionSliceSweepBenchmarks.GenerateFrames(1280, 720)[1];
        const int width = 1280;

        var costs = new Dictionary<string, double>(StringComparer.Ordinal);

        void Time(string member, Action call)
        {
            // Warm up the JIT and caches once, outside every timed batch.
            for (var i = 0; i < 2_000; i++)
                call();

            var bestNsPerCall = double.MaxValue;
            for (var trial = 0; trial < Trials; trial++)
            {
                var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < CallsPerTrial; i++)
                    call();
                var elapsed = Stopwatch.GetTimestamp() - start;
                var nsPerCall = elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / CallsPerTrial;
                if (nsPerCall < bestNsPerCall)
                    bestNsPerCall = nsPerCall;
            }

            costs[member] = bestNsPerCall;
        }

        // Two 16×16 luma blocks (stride 16) at different spots in the frame.
        var blockA = frame.AsSpan((64 * width) + 64, 16 * 16).ToArray();
        var blockB = frame.AsSpan((96 * width) + 96, 16 * 16).ToArray();
        var stride16 = 16;

        var manySrc = blockA;
        var manyPreds = new byte[9 * 16];
        for (var i = 0; i < manyPreds.Length; i++)
            manyPreds[i] = (byte)(i * 7);
        var manyOut = new int[9];

        var src16 = blockA;
        var pred16 = blockB;
        var coeffOut = new short[16];
        var recDst = new byte[64];

        var topRow = new byte[16];
        var leftCol = new byte[16];
        var dst16 = new byte[16];
        var dst256 = new byte[256];

        var residual = new short[16];
        for (var i = 0; i < residual.Length; i++)
            residual[i] = (short)(i - 8);
        var dctOut = new int[16];

        var quantBlock = new int[16];
        for (var i = 0; i < quantBlock.Length; i++)
            quantBlock[i] = (i * 37) + 1;

        // One macroblock of deblocking: 16×16 luma, 8×8 chroma, every internal edge active.
        var dbY = new byte[16 * 16];
        var dbU = new byte[16 * 16];
        var dbV = new byte[16 * 16];
        var bsH = new byte[16];
        var bsV = new byte[16];
        Array.Fill(bsH, (byte)1);
        Array.Fill(bsV, (byte)1);
        var qpY = new[] { 28 };
        var qpUv = new[] { 28 };

        var interpLumaDst = new byte[16 * 16];
        var interpChromaDst = new byte[8 * 8];
        var gather16 = new byte[16];
        var gather64 = new byte[64];

        Time(nameof(IH264KernelSet.Sad16x16),
            () => kernels.Sad16x16(blockA, stride16, blockB, stride16));
        Time(nameof(IH264KernelSet.Sad16x8),
            () => kernels.Sad16x8(blockA, stride16, blockB, stride16));
        Time(nameof(IH264KernelSet.Sad8x16),
            () => kernels.Sad8x16(blockA, stride16, blockB, stride16));
        Time(nameof(IH264KernelSet.Sad8x8),
            () => kernels.Sad8x8(blockA, 8, blockB, 8));
        Time(nameof(IH264KernelSet.SadMany4x4),
            () => kernels.SadMany4x4(manySrc, manyPreds, manyOut, 9));
        Time(nameof(IH264KernelSet.SadIntra16x16),
            () => kernels.SadIntra16x16(blockA, blockB, stride16));
        // Chroma: 8×8 U and V blocks, stride 8.
        Time(nameof(IH264KernelSet.SadChromaPair),
            () => kernels.SadChromaPair(blockA, blockB, blockB, blockA));

        Time(nameof(IH264KernelSet.Ssd16x16),
            () => kernels.Ssd16x16(blockA, stride16, blockB, stride16));
        Time(nameof(IH264KernelSet.Ssd8x8),
            () => kernels.Ssd8x8(blockA, 8, blockB, 8));
        Time(nameof(IH264KernelSet.VarianceMb16x16),
            () => kernels.VarianceMb16x16(blockA, stride16));

        Time(nameof(IH264KernelSet.Satd16x16),
            () => kernels.Satd16x16(blockA, stride16, blockB, stride16));
        Time(nameof(IH264KernelSet.Satd16x8),
            () => kernels.Satd16x8(blockA, stride16, blockB, stride16));
        Time(nameof(IH264KernelSet.Satd8x16),
            () => kernels.Satd8x16(blockA, stride16, blockB, stride16));
        Time(nameof(IH264KernelSet.Satd8x8),
            () => kernels.Satd8x8(blockA, 8, blockB, 8));
        Time(nameof(IH264KernelSet.SatdMany4x4),
            () => kernels.SatdMany4x4(manySrc, manyPreds, manyOut, 9));

        Time(nameof(IH264KernelSet.Predict4x4),
            () => kernels.Predict4x4(2, topRow, leftCol, topAvail: true, leftAvail: true, dst16));
        Time(nameof(IH264KernelSet.PredictIntra16x16),
            () => kernels.PredictIntra16x16(2, topRow, topAvail: true, leftCol, leftAvail: true, topLeft: 0, topLeftAvail: true, dst256));

        Time(nameof(IH264KernelSet.EncodeResidual4x4),
            () => kernels.EncodeResidual4x4(src16, pred16, 28, coeffOut, recDst, 16));

        Time(nameof(IH264KernelSet.ApplyDeblock),
            () => kernels.ApplyDeblock(dbY, 16, dbU, dbV, 8, 1, 1, bsH, bsV, qpY, qpUv, 0, 0));

        Time(nameof(IH264KernelSet.InterpolateLuma),
            () => kernels.InterpolateLuma(frame, width, 64, 64, 1, 1, 16, 16, interpLumaDst, 16));
        Time(nameof(IH264KernelSet.InterpolateChroma),
            () => kernels.InterpolateChroma(frame, width, 64, 64, 1, 1, 8, 8, interpChromaDst, 8));

        Time(nameof(IH264KernelSet.GatherSrcBlock4x4),
            () => kernels.GatherSrcBlock4x4(frame, (64 * width) + 64, width, gather16));
        Time(nameof(IH264KernelSet.GatherChroma8x8),
            () => kernels.GatherChroma8x8(frame, width, 8, 8, gather64));

        Time(nameof(IH264KernelSet.ForwardDct4x4),
            () => kernels.ForwardDct4x4(residual, dctOut));
        Time(nameof(IH264KernelSet.Quant4x4),
            () => kernels.Quant4x4(quantBlock, 28));

        return costs;
    }

    /// <summary>Renders the unit-cost table for the profile markdown.</summary>
    public static string RenderMarkdown(IReadOnlyDictionary<string, double> costs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Kernel unit costs ({Tier}): {Trials} trials × {CallsPerTrial:N0} calls, minimum trial.");
        sb.AppendLine();
        sb.AppendLine("| Kernel | ns/call (modelled) |");
        sb.AppendLine("| --- | ---: |");
        foreach (var (member, ns) in costs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"| {member} | {ns:F3} |");
        }

        return sb.ToString();
    }
}
