using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Kiln.Internal.H264;

/// <summary>
/// Interpolated-string handler for <see cref="H264PInterDiagnostics.TraceMbDecision"/> that skips
/// all formatting (and evaluation of the interpolation-hole expressions) unless the macroblock is
/// actually being traced. Without this, every call site materialised its message string per
/// macroblock even with tracing disabled — measured at ~2 MiB of string garbage per 1080p P-frame,
/// enough to drive periodic gen2 pauses in steady-state encoding.
/// </summary>
[InterpolatedStringHandler]
internal ref struct H264MbTraceInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _handler;
    private readonly bool _enabled;

    public H264MbTraceInterpolatedStringHandler(
        int literalLength, int formattedCount, int frameNum, int mbX, int mbY, out bool shouldAppend)
    {
        _enabled = H264PInterDiagnostics.ShouldTraceMb(frameNum, mbX, mbY);
        shouldAppend = _enabled;
        _handler = _enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>True when this macroblock matched a trace target and the message was formatted.</summary>
    public readonly bool IsEnabled => _enabled;

    public void AppendLiteral(string value) => _handler.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => _handler.AppendFormatted(value);

    public void AppendFormatted<T>(T value, string? format) => _handler.AppendFormatted(value, format);

    /// <summary>Only valid when <see cref="IsEnabled"/>; the compiler never appends otherwise.</summary>
    public string ToStringAndClear() => _handler.ToStringAndClear();
}

/// <summary>
/// Optional diagnostics for P-slice macroblock costing in <see cref="H264BaselineSliceEncoder"/>.
/// Enables phase counters (test/benchmark instrumentation) and A/B disables for Phase 2b (§7.3.5 intra-in-P scoring after ME).
/// </summary>
/// <remarks>
/// Set <see cref="DisablePhase2bManual"/> from benchmarks/tests, or set environment variable
/// <c>KILN_H264_DIAG_DISABLE_P_INTER_PHASE2B=1</c> once per process (read at type init; no per-MB env read).
/// </remarks>
internal static class H264PInterDiagnostics
{
    private static readonly bool EnvDisablePhase2b = string.Equals(
        Environment.GetEnvironmentVariable("KILN_H264_DIAG_DISABLE_P_INTER_PHASE2B"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool EnvCollectPhase2Timing = string.Equals(
        Environment.GetEnvironmentVariable("KILN_H264_P_INTER_TIMING"),
        "1",
        StringComparison.Ordinal);

    private static long s_phase1Skip;
    private static long s_phase2Entered;
    private static long s_phase2bIntraWin;
    private static long s_meHexSearches;
    private static long s_meBudgetTier1;
    private static long s_meBudgetTier2;
    private static long s_meBudgetTier3;
    private static long s_meExhaustiveFallbacks;
    private static long s_temporalProbeWideGate;
    private static long s_temporalProbeAttempts;
    private static long s_temporalProbePasses;

    private static long s_phase2bEvalCount;
    private static long s_phase2bChooseInterCount;
    private static long s_phase2bChooseIntraCount;
    private static long s_phase2bInterDistortionSum;
    private static long s_phase2bInterBitsSum;
    private static long s_phase2bInterRdSum;
    private static long s_phase2bIntraDistortionSum;
    private static long s_phase2bIntraBitsSum;
    private static long s_phase2bIntraRdSum;
    private static long s_phase2TimingCount;
    private static long s_phase2TimingTotalTicks;
    private static long s_phase2TimingMeTicks;
    private static long s_phase2TimingPredTicks;
    private static long s_phase2TimingLumaTicks;
    private static long s_phase2TimingChromaTicks;
    private static long s_phase2TimingWriteTicks;
    private static readonly ConcurrentQueue<string> s_mbTraceLines = new();
    private static int s_mbTraceLineCount;
    private const int MaxMbTraceLines = 4096;

    private readonly record struct MbTraceTarget(int FrameNum, int MbX, int MbY);
    private static readonly MbTraceTarget[] EnvTraceMbTargets = ParseTraceMbTargets();
    private static volatile MbTraceTarget[] s_runtimeTraceMbTargets = [];

    /// <summary>
    /// Add a runtime trace target (frameNum=-1 for all frames). Takes effect immediately.
    /// </summary>
    public static void AddRuntimeTraceMbTarget(int frameNum, int mbX, int mbY)
    {
        var existing = s_runtimeTraceMbTargets;
        var updated = new MbTraceTarget[existing.Length + 1];
        existing.CopyTo(updated, 0);
        updated[^1] = new MbTraceTarget(frameNum, mbX, mbY);
        s_runtimeTraceMbTargets = updated;
    }

    /// <summary>Clears all runtime trace targets added via <see cref="AddRuntimeTraceMbTarget"/>.</summary>
    public static void ClearRuntimeTraceMbTargets() => s_runtimeTraceMbTargets = [];

    /// <summary>
    /// Manual kill switch for diagnostics/bench runs. Keep false in normal operation; use
    /// <see cref="Kiln.H264BaselineEncoderOptions.EnableExperimentalPhase2b"/> for explicit opt-in.
    /// </summary>
    public static bool DisablePhase2bManual { get; set; }

    /// <summary>When true, <see cref="NotifyPhase1Skip"/> / <see cref="NotifyPhase2Entered"/> / <see cref="NotifyPhase2bIntraWin"/> count (Interlocked; safe for parallel slice encoders).</summary>
    public static bool CollectPhaseCounts { get; set; }

    /// <summary>
    /// Measurement-only override for the per-slice sub-partition search budget divisor
    /// (<c>H264BaselineSliceEncoder.SubPartBudgetMbDivisor</c>). <c>null</c> (production) uses the
    /// built-in constant; <c>0</c> disables the budget; a negative value restores the legacy fixed
    /// 32-MB-per-slice-encoder budget; a positive value divides the slice MB count. Used by the
    /// budget sweep harness — never set in production code.
    /// </summary>
    public static int? SubPartBudgetDivisorOverride { get; set; }

    /// <summary>
    /// Manual A/B kill switch for the temporal-seed probe that keeps a failed P_Skip from widening
    /// the ME range when the co-located previous-frame MV already explains the motion.
    /// Keep false in normal operation; benchmarks/tests set it to measure the pre-probe behaviour
    /// interleaved in the same process.
    /// </summary>
    public static bool DisableTemporalSeedProbe { get; set; }

    /// <summary>
    /// Manual A/B kill switch for the rate-aware margin ref1 must clear to beat ref0 in the P-slice
    /// reference competition. Keep false in normal operation; benchmarks/tests set it to
    /// measure the raw-SAD tie-break behaviour interleaved in the same process.
    /// </summary>
    public static bool DisableRef1TieMargin { get; set; }

    /// <summary>
    /// When true, collects per-MB Phase2b candidate RD accounting (inter vs intra estimated D/R/J).
    /// </summary>
    public static bool CollectPhase2bRdAccounting { get; set; }

    public static bool CollectPhase2Timing { get; set; }

    /// <summary>
    /// Measurement-only frame-phase wall-clock accounting for the multi-slice orchestrator:
    /// serial prologue/epilogue versus the parallel slice region, and slice load imbalance.
    /// Keep false in normal operation; the perf probe enables it around bounded runs.
    /// </summary>
    public static bool CollectFramePhases { get; set; }

    /// <summary>
    /// Measurement-only A/B kill switch for the effort-balanced slice partition: when true the
    /// multi-slice orchestrator keeps the historical equal-height row split every frame.
    /// Keep false in normal operation.
    /// </summary>
    public static bool DisableSlicePartitionBalance { get; set; }

    /// <summary>
    /// Measurement-only A/B kill switch for the unified sub-partition search's quadrant-level
    /// SAD-lower-bound gate (skipping a quadrant's SATD atoms when no partition shape using that
    /// quadrant can strictly improve). Bitstream-identical either way; keep false in normal operation.
    /// </summary>
    public static bool DisableUnifiedQuadrantGate { get; set; }

    private static long s_fpFrames;
    private static long s_fpBeginFrameTicks;
    private static long s_fpParallelWallTicks;
    private static long s_fpNalGatherTicks;
    private static long s_fpPadRotateTicks;
    private static long s_fpSliceSumTicks;
    private static long s_fpSliceMaxTicks;
    private static long s_fpSliceCopyTicks;
    private static long s_fpSliceDeblockTicks;
    private static readonly long[] s_fpSliceTicksByIndex = new long[16];
    private const int MaxRowStats = 1024;
    private static readonly long[] s_rowTicks = new long[MaxRowStats];
    private static readonly long[] s_rowSkip = new long[MaxRowStats];
    private static readonly long[] s_rowInter16 = new long[MaxRowStats];
    private static readonly long[] s_rowInterSub = new long[MaxRowStats];
    private static readonly long[] s_rowIntra = new long[MaxRowStats];
    private static readonly long[] s_rowEffort = new long[MaxRowStats];
    private static long s_rowStatFrames;

    /// <summary>Clears the frame-phase accumulators recorded via <see cref="NotifyFramePhases"/>.</summary>
    public static void ResetFramePhases()
    {
        Interlocked.Exchange(ref s_fpFrames, 0);
        Interlocked.Exchange(ref s_fpBeginFrameTicks, 0);
        Interlocked.Exchange(ref s_fpParallelWallTicks, 0);
        Interlocked.Exchange(ref s_fpNalGatherTicks, 0);
        Interlocked.Exchange(ref s_fpPadRotateTicks, 0);
        Interlocked.Exchange(ref s_fpSliceSumTicks, 0);
        Interlocked.Exchange(ref s_fpSliceMaxTicks, 0);
        Interlocked.Exchange(ref s_fpSliceCopyTicks, 0);
        Interlocked.Exchange(ref s_fpSliceDeblockTicks, 0);
        Array.Clear(s_fpSliceTicksByIndex);
    }

    /// <summary>Records one multi-slice frame's phase ticks (orchestrator thread, once per frame).</summary>
    internal static void NotifyFramePhases(
        long beginFrameTicks,
        long parallelWallTicks,
        long nalGatherTicks,
        long padRotateTicks,
        long sliceSumTicks,
        long sliceMaxTicks)
    {
        if (!CollectFramePhases)
            return;
        Interlocked.Increment(ref s_fpFrames);
        Interlocked.Add(ref s_fpBeginFrameTicks, beginFrameTicks);
        Interlocked.Add(ref s_fpParallelWallTicks, parallelWallTicks);
        Interlocked.Add(ref s_fpNalGatherTicks, nalGatherTicks);
        Interlocked.Add(ref s_fpPadRotateTicks, padRotateTicks);
        Interlocked.Add(ref s_fpSliceSumTicks, sliceSumTicks);
        Interlocked.Add(ref s_fpSliceMaxTicks, sliceMaxTicks);
    }

    /// <summary>Records one slice's total ticks under its slice index (orchestrator thread).</summary>
    internal static void NotifySliceIndexTicks(int sliceIndex, long ticks)
    {
        if (!CollectFramePhases || (uint)sliceIndex >= (uint)s_fpSliceTicksByIndex.Length)
            return;
        Interlocked.Add(ref s_fpSliceTicksByIndex[sliceIndex], ticks);
    }

    /// <summary>Records one slice's source-copy and deblock ticks (slice worker, once per slice).</summary>
    internal static void NotifySlicePhases(long copyTicks, long deblockTicks)
    {
        if (!CollectFramePhases)
            return;
        Interlocked.Add(ref s_fpSliceCopyTicks, copyTicks);
        Interlocked.Add(ref s_fpSliceDeblockTicks, deblockTicks);
    }

    /// <summary>Accumulates one MB row's wall ticks and outcome mix (see the slice encoder's per-row flush).</summary>
    internal static void NotifyRowStats(int mbRow, long ticks, long effort, int skip, int inter16, int interSub, int intra)
    {
        if (!CollectFramePhases || (uint)mbRow >= MaxRowStats)
            return;
        Interlocked.Add(ref s_rowTicks[mbRow], ticks);
        Interlocked.Add(ref s_rowEffort[mbRow], effort);
        Interlocked.Add(ref s_rowSkip[mbRow], skip);
        Interlocked.Add(ref s_rowInter16[mbRow], inter16);
        Interlocked.Add(ref s_rowInterSub[mbRow], interSub);
        Interlocked.Add(ref s_rowIntra[mbRow], intra);
        if (mbRow == 0)
            Interlocked.Increment(ref s_rowStatFrames);
    }

    /// <summary>Per-row average time and MB outcome mix over the frames recorded since the last reset.</summary>
    public static string BuildRowStatsReport(bool reset = false)
    {
        var frames = Volatile.Read(ref s_rowStatFrames);
        if (frames <= 0)
            return "Row stats: no rows recorded.";
        var sb = new StringBuilder(4096);
        sb.Append("Row stats (avg per frame over n=").Append(frames).AppendLine("): row: us | effort | skip/16x16/subPart/intra");
        for (var r = 0; r < MaxRowStats; r++)
        {
            var t = Volatile.Read(ref s_rowTicks[r]);
            if (t == 0)
                continue;
            var us = t * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency / frames;
            sb.Append("  ").Append(r).Append(": ").Append(us.ToString("F0")).Append(" | ")
                .Append(((double)s_rowEffort[r] / frames).ToString("F0")).Append(" | ")
                .Append(((double)s_rowSkip[r] / frames).ToString("F1")).Append('/')
                .Append(((double)s_rowInter16[r] / frames).ToString("F1")).Append('/')
                .Append(((double)s_rowInterSub[r] / frames).ToString("F1")).Append('/')
                .Append(((double)s_rowIntra[r] / frames).ToString("F1")).AppendLine();
        }

        if (reset)
        {
            Interlocked.Exchange(ref s_rowStatFrames, 0);
            Array.Clear(s_rowTicks);
            Array.Clear(s_rowSkip);
            Array.Clear(s_rowInter16);
            Array.Clear(s_rowInterSub);
            Array.Clear(s_rowIntra);
            Array.Clear(s_rowEffort);
        }

        return sb.ToString();
    }

    /// <summary>Per-frame averages of the phase ticks recorded since the last reset.</summary>
    public static string BuildFramePhaseReport(bool reset = false)
    {
        var frames = Volatile.Read(ref s_fpFrames);
        if (frames <= 0)
            return "Frame-phase timing: no frames recorded.";

        static double Ms(long ticks, long frames) =>
            ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames;
        var begin = Ms(Volatile.Read(ref s_fpBeginFrameTicks), frames);
        var par = Ms(Volatile.Read(ref s_fpParallelWallTicks), frames);
        var gather = Ms(Volatile.Read(ref s_fpNalGatherTicks), frames);
        var pad = Ms(Volatile.Read(ref s_fpPadRotateTicks), frames);
        var sliceSum = Ms(Volatile.Read(ref s_fpSliceSumTicks), frames);
        var sliceMax = Ms(Volatile.Read(ref s_fpSliceMaxTicks), frames);
        var copy = Ms(Volatile.Read(ref s_fpSliceCopyTicks), frames);
        var deblock = Ms(Volatile.Read(ref s_fpSliceDeblockTicks), frames);
        var sb = new StringBuilder(320);
        sb.Append("Frame phases (ms/frame, n=").Append(frames).AppendLine("):");
        sb.Append("  beginFrame=").Append(begin.ToString("F3"))
            .Append(" parallelWall=").Append(par.ToString("F3"))
            .Append(" nalGather=").Append(gather.ToString("F3"))
            .Append(" padRotate=").Append(pad.ToString("F3")).AppendLine();
        sb.Append("  sliceSum=").Append(sliceSum.ToString("F3"))
            .Append(" sliceMax=").Append(sliceMax.ToString("F3"))
            .Append(" forkJoin=").Append((par - sliceMax).ToString("F3"))
            .Append(" sliceCopySum=").Append(copy.ToString("F3"))
            .Append(" sliceDeblockSum=").Append(deblock.ToString("F3"));
        sb.AppendLine().Append("  perSlice:");
        for (var i = 0; i < s_fpSliceTicksByIndex.Length; i++)
        {
            var t = Volatile.Read(ref s_fpSliceTicksByIndex[i]);
            if (t == 0)
                continue;
            sb.Append(" [").Append(i).Append("]=").Append(Ms(t, frames).ToString("F2"));
        }

        if (reset)
            ResetFramePhases();
        return sb.ToString();
    }

    public static bool IsPhase2TimingEnabled =>
        CollectPhase2Timing || EnvCollectPhase2Timing || H264MotionSatdDagDiagnostics.IsEnabled;

    public static bool ShouldDisablePhase2b() => DisablePhase2bManual || EnvDisablePhase2b;

    public static bool ShouldTraceMb(int frameNum, int mbX, int mbY)
    {
        static bool Check(MbTraceTarget[] targets, int frameNum, int mbX, int mbY)
        {
            foreach (var target in targets)
            {
                if (target.MbX != mbX || target.MbY != mbY) continue;
                if (target.FrameNum < 0 || target.FrameNum == frameNum) return true;
            }
            return false;
        }
        return Check(EnvTraceMbTargets, frameNum, mbX, mbY)
            || Check(s_runtimeTraceMbTargets, frameNum, mbX, mbY);
    }

    public static void TraceMbDecision(
        int frameNum,
        int codedFrameIndex,
        int mbX,
        int mbY,
        [InterpolatedStringHandlerArgument(nameof(frameNum), nameof(mbX), nameof(mbY))] H264MbTraceInterpolatedStringHandler message)
    {
        // The handler already evaluated ShouldTraceMb; when it said no, the compiler skipped every
        // append (and the hole expressions), so the disabled path allocates nothing at all.
        if (!message.IsEnabled)
            return;
        var line = $"[H264PInter MBTrace] frameNum={frameNum} codedFrame={codedFrameIndex} mb=({mbX},{mbY}) {message.ToStringAndClear()}";
        s_mbTraceLines.Enqueue(line);
        var newCount = Interlocked.Increment(ref s_mbTraceLineCount);
        while (newCount > MaxMbTraceLines && s_mbTraceLines.TryDequeue(out _))
            newCount = Interlocked.Decrement(ref s_mbTraceLineCount);
    }

    public static string BuildMbTraceReportAndReset()
    {
        if (Volatile.Read(ref s_mbTraceLineCount) == 0)
            return string.Empty;
        var sb = new StringBuilder(1024);
        while (s_mbTraceLines.TryDequeue(out var line))
        {
            sb.AppendLine(line);
            Interlocked.Decrement(ref s_mbTraceLineCount);
        }
        return sb.ToString();
    }

    public static void ResetPhaseCounts()
    {
        Interlocked.Exchange(ref s_phase1Skip, 0);
        Interlocked.Exchange(ref s_phase2Entered, 0);
        Interlocked.Exchange(ref s_phase2bIntraWin, 0);
        Interlocked.Exchange(ref s_meHexSearches, 0);
        Interlocked.Exchange(ref s_meExhaustiveFallbacks, 0);
        Interlocked.Exchange(ref s_meBudgetTier1, 0);
        Interlocked.Exchange(ref s_meBudgetTier2, 0);
        Interlocked.Exchange(ref s_meBudgetTier3, 0);
        Interlocked.Exchange(ref s_temporalProbeWideGate, 0);
        Interlocked.Exchange(ref s_temporalProbeAttempts, 0);
        Interlocked.Exchange(ref s_temporalProbePasses, 0);
    }

    public static void ResetPhase2bRdAccounting()
    {
        Interlocked.Exchange(ref s_phase2bEvalCount, 0);
        Interlocked.Exchange(ref s_phase2bChooseInterCount, 0);
        Interlocked.Exchange(ref s_phase2bChooseIntraCount, 0);
        Interlocked.Exchange(ref s_phase2bInterDistortionSum, 0);
        Interlocked.Exchange(ref s_phase2bInterBitsSum, 0);
        Interlocked.Exchange(ref s_phase2bInterRdSum, 0);
        Interlocked.Exchange(ref s_phase2bIntraDistortionSum, 0);
        Interlocked.Exchange(ref s_phase2bIntraBitsSum, 0);
        Interlocked.Exchange(ref s_phase2bIntraRdSum, 0);
    }

    public static void ResetPhase2Timing()
    {
        Interlocked.Exchange(ref s_phase2TimingCount, 0);
        Interlocked.Exchange(ref s_phase2TimingTotalTicks, 0);
        Interlocked.Exchange(ref s_phase2TimingMeTicks, 0);
        Interlocked.Exchange(ref s_phase2TimingPredTicks, 0);
        Interlocked.Exchange(ref s_phase2TimingLumaTicks, 0);
        Interlocked.Exchange(ref s_phase2TimingChromaTicks, 0);
        Interlocked.Exchange(ref s_phase2TimingWriteTicks, 0);
    }

    public static (long Phase1Skip, long Phase2Entered, long Phase2bIntraWin) ReadPhaseCounts() =>
        (
            Volatile.Read(ref s_phase1Skip),
            Volatile.Read(ref s_phase2Entered),
            Volatile.Read(ref s_phase2bIntraWin));

    /// <summary>
    /// Integer-pel ME invocation counters (gated by <see cref="CollectPhaseCounts"/>): hex seed
    /// searches started, and how many escalated to the exhaustive-window fallback. These are the
    /// direct measure of how much motion-search work slicing creates.
    /// </summary>
    /// <summary>
    /// Macroblocks encoded at each ME effort-budget degradation tier (gated by
    /// <see cref="CollectPhaseCounts"/>); all zero when the budget is off or never binds.
    /// </summary>
    public static (long Tier1, long Tier2, long Tier3) ReadMeBudgetTierCounts() =>
        (
            Volatile.Read(ref s_meBudgetTier1),
            Volatile.Read(ref s_meBudgetTier2),
            Volatile.Read(ref s_meBudgetTier3));

    public static (long HexSearches, long ExhaustiveFallbacks) ReadMeSearchCounts() =>
        (
            Volatile.Read(ref s_meHexSearches),
            Volatile.Read(ref s_meExhaustiveFallbacks));

    /// <summary>
    /// Temporal-seed probe counters (gated by <see cref="CollectPhaseCounts"/>): macroblocks that
    /// hit the sadSkip &gt; 2048 widening gate, how many had a distinct temporal seed to probe, and
    /// how many probes passed (tightening the range instead of widening).
    /// </summary>
    public static (long WideGate, long Attempts, long Passes) ReadTemporalProbeCounts() =>
        (
            Volatile.Read(ref s_temporalProbeWideGate),
            Volatile.Read(ref s_temporalProbeAttempts),
            Volatile.Read(ref s_temporalProbePasses));

    public readonly record struct Phase2bRdSnapshot(
        long EvaluatedMacroblocks,
        long ChosenInterCount,
        long ChosenIntraCount,
        long SumInterDistortion,
        long SumInterBits,
        long SumInterRd,
        long SumIntraDistortion,
        long SumIntraBits,
        long SumIntraRd);

    public static Phase2bRdSnapshot ReadPhase2bRdAccounting() => new(
        EvaluatedMacroblocks: Volatile.Read(ref s_phase2bEvalCount),
        ChosenInterCount: Volatile.Read(ref s_phase2bChooseInterCount),
        ChosenIntraCount: Volatile.Read(ref s_phase2bChooseIntraCount),
        SumInterDistortion: Volatile.Read(ref s_phase2bInterDistortionSum),
        SumInterBits: Volatile.Read(ref s_phase2bInterBitsSum),
        SumInterRd: Volatile.Read(ref s_phase2bInterRdSum),
        SumIntraDistortion: Volatile.Read(ref s_phase2bIntraDistortionSum),
        SumIntraBits: Volatile.Read(ref s_phase2bIntraBitsSum),
        SumIntraRd: Volatile.Read(ref s_phase2bIntraRdSum));

    public static string BuildPhase2bRdReport()
    {
        var s = ReadPhase2bRdAccounting();
        if (s.EvaluatedMacroblocks <= 0)
            return "Phase2b RD report: no candidate macroblocks evaluated.";

        static double Avg(long sum, long n) => n <= 0 ? 0.0 : (double)sum / n;
        var n = s.EvaluatedMacroblocks;
        var avgInterDist = Avg(s.SumInterDistortion, n);
        var avgInterBits = Avg(s.SumInterBits, n);
        var avgInterRd = Avg(s.SumInterRd, n);
        var avgIntraDist = Avg(s.SumIntraDistortion, n);
        var avgIntraBits = Avg(s.SumIntraBits, n);
        var avgIntraRd = Avg(s.SumIntraRd, n);
        var intraChoosePct = (double)s.ChosenIntraCount * 100.0 / n;

        var sb = new StringBuilder(320);
        sb.Append("Phase2b RD report: n=").Append(n)
            .Append(", chooseIntra=").Append(s.ChosenIntraCount)
            .Append(" (").Append(intraChoosePct.ToString("F1")).Append("%)")
            .Append(", chooseInter=").Append(s.ChosenInterCount).AppendLine();
        sb.Append("  avgInter: D=").Append(avgInterDist.ToString("F1"))
            .Append(" R=").Append(avgInterBits.ToString("F1"))
            .Append(" J=").Append(avgInterRd.ToString("F1")).AppendLine();
        sb.Append("  avgIntra: D=").Append(avgIntraDist.ToString("F1"))
            .Append(" R=").Append(avgIntraBits.ToString("F1"))
            .Append(" J=").Append(avgIntraRd.ToString("F1")).AppendLine();
        sb.Append("  delta (intra-inter): D=").Append((avgIntraDist - avgInterDist).ToString("F1"))
            .Append(" R=").Append((avgIntraBits - avgInterBits).ToString("F1"))
            .Append(" J=").Append((avgIntraRd - avgInterRd).ToString("F1"));
        return sb.ToString();
    }

    public static string BuildPhase2TimingReport(bool reset = false)
    {
        var count = Volatile.Read(ref s_phase2TimingCount);
        if (count <= 0)
            return "P-Inter Phase2 timing: no macroblocks recorded.";

        var totalTicks = Volatile.Read(ref s_phase2TimingTotalTicks);
        var meTicks = Volatile.Read(ref s_phase2TimingMeTicks);
        var predTicks = Volatile.Read(ref s_phase2TimingPredTicks);
        var lumaTicks = Volatile.Read(ref s_phase2TimingLumaTicks);
        var chromaTicks = Volatile.Read(ref s_phase2TimingChromaTicks);
        var writeTicks = Volatile.Read(ref s_phase2TimingWriteTicks);
        var accountedTicks = meTicks + predTicks + lumaTicks + chromaTicks + writeTicks;
        var otherTicks = Math.Max(0, totalTicks - accountedTicks);

        static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        static string AvgMs(long ticks, long count) => (Ms(ticks) / count).ToString("F4");
        static string Pct(long ticks, long total) => total <= 0 ? "0.0%" : ((double)ticks * 100.0 / total).ToString("F1") + "%";

        var sb = new StringBuilder(512);
        sb.Append("P-Inter Phase2 timing: mb=").Append(count)
            .Append(" totalMs=").Append(Ms(totalTicks).ToString("F1"))
            .Append(" avgMs=").Append(AvgMs(totalTicks, count)).AppendLine();
        sb.Append("  ME avgMs=").Append(AvgMs(meTicks, count)).Append(" pct=").Append(Pct(meTicks, totalTicks))
            .Append(" pred avgMs=").Append(AvgMs(predTicks, count)).Append(" pct=").Append(Pct(predTicks, totalTicks))
            .Append(" luma avgMs=").Append(AvgMs(lumaTicks, count)).Append(" pct=").Append(Pct(lumaTicks, totalTicks)).AppendLine();
        sb.Append("  chroma avgMs=").Append(AvgMs(chromaTicks, count)).Append(" pct=").Append(Pct(chromaTicks, totalTicks))
            .Append(" write avgMs=").Append(AvgMs(writeTicks, count)).Append(" pct=").Append(Pct(writeTicks, totalTicks))
            .Append(" other avgMs=").Append(AvgMs(otherTicks, count)).Append(" pct=").Append(Pct(otherTicks, totalTicks));

        if (reset)
            ResetPhase2Timing();
        return sb.ToString();
    }

    internal static void NotifyPhase1Skip()
    {
        if (CollectPhaseCounts)
        {
            Interlocked.Increment(ref s_phase1Skip);
        }
    }

    internal static void NotifyPhase2Entered()
    {
        if (CollectPhaseCounts)
        {
            Interlocked.Increment(ref s_phase2Entered);
        }
    }

    internal static void NotifyPhase2bIntraWin()
    {
        if (CollectPhaseCounts)
        {
            Interlocked.Increment(ref s_phase2bIntraWin);
        }
    }

    internal static void NotifyMeHexSearch()
    {
        if (CollectPhaseCounts)
        {
            Interlocked.Increment(ref s_meHexSearches);
        }
    }

    /// <summary>
    /// Records a P-inter macroblock encoded under ME effort-budget pressure (gated by
    /// <see cref="CollectPhaseCounts"/>): tier 1 = fallback skipped + radius 8, tier 2 = single
    /// reference + radius 4 + narrowed window, tier 3 = 16x16-only search.
    /// </summary>
    internal static void NotifyMeBudgetTier(int tier)
    {
        if (CollectPhaseCounts)
        {
            switch (tier)
            {
                case 1: Interlocked.Increment(ref s_meBudgetTier1); break;
                case 2: Interlocked.Increment(ref s_meBudgetTier2); break;
                default: Interlocked.Increment(ref s_meBudgetTier3); break;
            }
        }
    }

    internal static void NotifyMeExhaustiveFallback()
    {
        if (CollectPhaseCounts)
        {
            Interlocked.Increment(ref s_meExhaustiveFallbacks);
        }
    }

    internal static void NotifyTemporalProbe(bool attempted, bool passed)
    {
        if (CollectPhaseCounts)
        {
            Interlocked.Increment(ref s_temporalProbeWideGate);
            if (attempted)
            {
                Interlocked.Increment(ref s_temporalProbeAttempts);
            }

            if (passed)
            {
                Interlocked.Increment(ref s_temporalProbePasses);
            }
        }
    }

    internal static void NotifyPhase2bCandidateRd(
        int interDistortion,
        int interBits,
        int interRd,
        int intraDistortion,
        int intraBits,
        int intraRd,
        bool chooseIntra)
    {
        if (!CollectPhase2bRdAccounting)
            return;

        Interlocked.Increment(ref s_phase2bEvalCount);
        if (chooseIntra)
            Interlocked.Increment(ref s_phase2bChooseIntraCount);
        else
            Interlocked.Increment(ref s_phase2bChooseInterCount);

        Interlocked.Add(ref s_phase2bInterDistortionSum, interDistortion);
        Interlocked.Add(ref s_phase2bInterBitsSum, interBits);
        Interlocked.Add(ref s_phase2bInterRdSum, interRd);
        Interlocked.Add(ref s_phase2bIntraDistortionSum, intraDistortion);
        Interlocked.Add(ref s_phase2bIntraBitsSum, intraBits);
        Interlocked.Add(ref s_phase2bIntraRdSum, intraRd);
    }

    internal static void NotifyPhase2Timing(
        long totalTicks,
        long meTicks,
        long predTicks,
        long lumaTicks,
        long chromaTicks,
        long writeTicks)
    {
        if (!IsPhase2TimingEnabled)
            return;

        Interlocked.Increment(ref s_phase2TimingCount);
        Interlocked.Add(ref s_phase2TimingTotalTicks, totalTicks);
        Interlocked.Add(ref s_phase2TimingMeTicks, meTicks);
        Interlocked.Add(ref s_phase2TimingPredTicks, predTicks);
        Interlocked.Add(ref s_phase2TimingLumaTicks, lumaTicks);
        Interlocked.Add(ref s_phase2TimingChromaTicks, chromaTicks);
        Interlocked.Add(ref s_phase2TimingWriteTicks, writeTicks);
    }

    private static MbTraceTarget[] ParseTraceMbTargets()
    {
        var raw = Environment.GetEnvironmentVariable("KILN_H264_DIAG_TRACE_MB");
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var groups = raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var targets = new List<MbTraceTarget>(groups.Length);
        foreach (var group in groups)
        {
            var parts = group.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                continue;

            if (!int.TryParse(parts[0], out var frameNum))
                continue;
            if (!int.TryParse(parts[1], out var mbX))
                continue;
            if (!int.TryParse(parts[2], out var mbY))
                continue;

            targets.Add(new MbTraceTarget(frameNum, mbX, mbY));
        }

        return targets.ToArray();
    }
}
