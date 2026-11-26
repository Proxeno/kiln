using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kiln.Internal.H264;
using Kiln.RateControl;

namespace Kiln.Benchmarks;

/// <summary>
/// Stage-profiler harness, passes T (end-to-end P-frame timing) and C (exact kernel call counts).
///
/// <list type="bullet">
/// <item><c>--stage-profile &lt;outdir&gt; [--quick] [--sha &lt;sha&gt;]</c>: runs the matrix, writes
/// <c>profile.json</c> and <c>profile.md</c>, and returns 1 if any row's Annex B output is not
/// byte-identical across every pass-T round and its pass-C encode.</item>
/// <item><c>--stage-profile-selftest</c>: the negative control for that identity gate — one quick
/// row, pass C decorated with a kernel whose <c>Sad16x16</c> returns inner+1. Exit 0 only if the
/// identity check reports the expected mismatch, 1 if the hashes still match.</item>
/// </list>
///
/// <para>
/// Pass T encodes each row with a fresh encoder per round (one discarded warm-up round, then 15
/// timed rounds, preset order reversed on odd rounds) and times only the P frames. Pass C installs
/// <see cref="CountingKernelSet"/> through <see cref="H264KernelSetHooks.MeasurementDecorator"/>
/// and encodes each row once. Both passes run the same fixed-QP configuration as the existing
/// harnesses: QP 28, no rate control, frame 0 IDR, every later frame P.
/// </para>
/// </summary>
internal static class H264StageProfiler
{
    private const int Qp = 28;
    private const int TimedRounds = 15;
    private const int DiscardedRounds = 1;

    /// <summary>Pass-S rounds per row; the median per stage is kept.</summary>
    private const int StageRounds = 3;

    /// <summary>
    /// Paired timers-off/timers-on rounds per row for the probe-overhead estimate. The two
    /// conditions run back-to-back inside one window, alternating order, so a load change between
    /// passes cannot masquerade as overhead.
    /// </summary>
    private const int PairedRounds = 3;

    /// <summary>Closure gate: a row is coarse above this unattributed share.</summary>
    private const double UnattributedLimitPct = 5.0;

    /// <summary>Closure gate: a row is coarse above this pass-S probe overhead.</summary>
    private const double ProbeOverheadLimitPct = 15.0;

    private static readonly string[] AllContents = ["static", "moving", "highmotion", "scenecut", "divergent"];
    private static readonly string[] QuickContents = ["moving", "divergent"];
    private static readonly (int Width, int Height)[] AllResolutions = [(1280, 720), (1920, 1080)];
    private static readonly (int Width, int Height)[] QuickResolutions = [(1280, 720)];
    private static readonly EncoderSpeedMode[] AllPresets =
        [EncoderSpeedMode.HighQuality, EncoderSpeedMode.Balanced, EncoderSpeedMode.Fast, EncoderSpeedMode.VeryFast];
    private static readonly EncoderSpeedMode[] QuickPresets =
        [EncoderSpeedMode.HighQuality, EncoderSpeedMode.VeryFast];
    private static readonly int[] AllSlices = [1, 4];
    private static readonly int[] QuickSlices = [1];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly record struct RowKey(string Content, int Width, int Height, EncoderSpeedMode Preset, int Slices);

    private sealed record Settings(
        string[] Contents,
        (int Width, int Height)[] Resolutions,
        int Frames,
        EncoderSpeedMode[] Presets,
        int[] Slices,
        int TimedRounds,
        int DiscardedRounds,
        bool SkipQuietGate,
        int Sad16x16Bias,
        bool ExpectIdentityMismatch);

    private sealed record Clip(string Content, int Width, int Height, byte[][] Frames, string Sha256);

    private sealed record EncodeResult(string BitstreamSha256, double PFrameMs);

    /// <summary>Entry point for both stage-profiler modes; returns the process exit code.</summary>
    public static int RunDispatch(string[] args)
    {
        if (args[0] == "--stage-profile-selftest")
        {
            return RunSelfTest();
        }

        if (args[0] == "--stage-profile-selftest-closure")
        {
            return RunClosureSelfTest();
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: --stage-profile <outdir> [--quick] [--sha <sha>]");
            return 1;
        }

        return RunProfile(args[1], args[2..]);
    }

    /// <summary>Parses <c>--stage-profile</c> arguments and returns the process exit code.</summary>
    private static int RunProfile(string outDir, string[] extraArgs)
    {
        var quick = false;
        var sha = "unknown";
        for (var i = 0; i < extraArgs.Length; i++)
        {
            switch (extraArgs[i])
            {
                case "--quick":
                    quick = true;
                    break;
                case "--sha":
                    if (i + 1 >= extraArgs.Length)
                    {
                        Console.Error.WriteLine("--sha needs a value.");
                        return 1;
                    }

                    sha = extraArgs[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown --stage-profile argument: {extraArgs[i]}");
                    return 1;
            }
        }

        var rawArgs = string.Join(' ', ["--stage-profile", outDir, .. extraArgs]);
        var settings = quick
            ? new Settings(QuickContents, QuickResolutions, 8, QuickPresets, QuickSlices,
                TimedRounds, DiscardedRounds, SkipQuietGate: false, Sad16x16Bias: 0, ExpectIdentityMismatch: false)
            : new Settings(AllContents, AllResolutions, 24, AllPresets, AllSlices,
                TimedRounds, DiscardedRounds, SkipQuietGate: false, Sad16x16Bias: 0, ExpectIdentityMismatch: false);

        Directory.CreateDirectory(outDir);
        return Execute(settings, outDir, sha, rawArgs);
    }

    /// <summary>
    /// Negative control for the identity gate: one quick row, sabotaged pass C. Exit 0 means the
    /// check caught the changed bitstream (the expected outcome); exit 1 means it did not.
    /// </summary>
    public static int RunSelfTest()
    {
        var settings = new Settings(
            Contents: ["moving"],
            Resolutions: [(1280, 720)],
            Frames: 8,
            Presets: [EncoderSpeedMode.HighQuality],
            Slices: [1],
            TimedRounds: 1,
            DiscardedRounds: 0,
            SkipQuietGate: true,
            Sad16x16Bias: 1,
            ExpectIdentityMismatch: true);
        Console.WriteLine("// stage-profile-selftest: one quick row, pass C Sad16x16 returns inner+1");
        return Execute(settings, outDir: null, sha: "unknown", rawArgs: "--stage-profile-selftest");
    }

    /// <summary>
    /// Closure negative control: one quick row, the Residual timer's notification subtracted from the
    /// covered total before the closure computation (a test-only simulation of a suppressed stage).
    /// The row MUST then be coarse with <c>unattributed_pct &gt; 5</c>. Exit 0 only if it is, and only
    /// if the pass-S bitstream is still identical to a non-instrumented encode.
    /// </summary>
    public static int RunClosureSelfTest()
    {
        var settings = new Settings(
            Contents: ["moving"],
            Resolutions: [(1280, 720)],
            Frames: 8,
            Presets: [EncoderSpeedMode.VeryFast],
            Slices: [1],
            TimedRounds: 1,
            DiscardedRounds: 0,
            SkipQuietGate: true,
            Sad16x16Bias: 0,
            ExpectIdentityMismatch: false);
        Console.WriteLine("// stage-profile-selftest-closure: one quick row, Residual excluded from closure");
        var clips = BuildClips(settings);
        var clip = clips[0];
        var annex = new byte[(clip.Width * clip.Height * 2) + 1_048_576];
        var options = CreateOptions(EncoderSpeedMode.VeryFast, 1);

        var plain = EncodeClip(options, clip, annex);

        StageEncodeResult staged;
        H264PInterDiagnostics.CollectStageProfile = true;
        try
        {
            staged = EncodeStageClip(options, clip, annex);
        }
        finally
        {
            H264PInterDiagnostics.CollectStageProfile = false;
            H264PInterDiagnostics.SnapshotStageTicks(reset: true);
        }

        var identity = string.Equals(plain.BitstreamSha256, staged.BitstreamSha256, StringComparison.Ordinal);

        var macroblocksPerFrame = ((clip.Width + 15) / 16) * ((clip.Height + 15) / 16);
        var totalMacroblocks = (double)macroblocksPerFrame * clip.Frames.Length;
        var e2e = new ProfileE2e(staged.PFrameMs, staged.PFrameMs, staged.PFrameMs,
            staged.PFrameMs, staged.PFrameMs, 1);
        var stages = BuildStageProfile(
            new RowKey(clip.Content, clip.Width, clip.Height, EncoderSpeedMode.VeryFast, 1),
            e2e,
            [staged.StageTicks],
            [staged.PFrameMs],
            [],
            (0, 0),
            (0, 0),
            new Dictionary<string, double>(StringComparer.Ordinal),
            totalMacroblocks,
            roundHot: false,
            pairedProbeOverheadPct: double.NaN,
            suppressResidual: true);
        var coarse = stages.UnattributedPct > UnattributedLimitPct
            || (stages.ProbeOverheadReliable && stages.ProbeOverheadPct > ProbeOverheadLimitPct);
        Console.WriteLine(
            $"closure: identity={(identity ? "equal" : "MISMATCH")} " +
            $"unattributed={stages.UnattributedPct:F2}% probeOverhead=" +
            $"{(stages.ProbeOverheadReliable ? $"{stages.ProbeOverheadPct:F2}%" : "n/a")} coarse={coarse}");

        if (!identity)
        {
            Console.WriteLine("selftest-closure: FAIL pass-S bitstream differs from a plain encode");
            return 1;
        }

        if (stages.UnattributedPct > UnattributedLimitPct)
        {
            Console.WriteLine("selftest-closure: PASS residual suppression made the row coarse");
            return 0;
        }

        Console.WriteLine("selftest-closure: FAIL residual suppression did not push unattributed above 5%");
        return 1;
    }

    private static int Execute(Settings settings, string? outDir, string sha, string rawArgs)
    {
        // Resolved before any decorator is installed, so the name is the real ISA kernel set.
        var kernelSetName = H264KernelSet.CreateBest().GetType().Name;
        Console.WriteLine(
            $"// stage-profile: kernels={kernelSetName} contents={string.Join(',', settings.Contents)} " +
            $"frames={settings.Frames} presets={string.Join(',', settings.Presets)} " +
            $"slices={string.Join(',', settings.Slices)}");

        var clips = BuildClips(settings);
        var maxPixels = settings.Resolutions.Max(r => r.Width * r.Height);
        var annex = new byte[(maxPixels * 2) + 1_048_576];

        var roundTimes = new Dictionary<RowKey, List<double>>();
        var hashes = new Dictionary<RowKey, List<string>>();
        var rowCoarse = new Dictionary<RowKey, bool>();
        var quietLog = new List<ProfileQuietRound>();
        var identityMismatch = false;

        // ---- Pass T: timing ----------------------------------------------------------------
        var totalRounds = settings.TimedRounds + settings.DiscardedRounds;
        foreach (var clip in clips)
        {
            foreach (var slices in settings.Slices)
            {
                var cellCoarse = false;
                for (var round = 0; round < totalRounds; round++)
                {
                    var threshold = 1.5 + slices;
                    var quiet = settings.SkipQuietGate
                        ? new QuietGate.Result(double.NaN, 0.0, QuietGate.Outcome.Immediate, threshold)
                        : QuietGate.WaitForQuiet(threshold, maxWaitSeconds: 30.0);
                    quietLog.Add(new ProfileQuietRound(
                        clip.Content, clip.Width, clip.Height, slices, round + 1,
                        Math.Round(quiet.Threshold, 3),
                        Math.Round(quiet.LoadBefore, 3),
                        Math.Round(quiet.WaitSeconds, 3),
                        quiet.Outcome.ToString()));
                    Console.WriteLine(
                        $"gate {clip.Content} {clip.Width}x{clip.Height} s{slices} round {round + 1}/{totalRounds}: " +
                        $"load={FormatLoad(quiet.LoadBefore)} wait={quiet.WaitSeconds:F1}s {quiet.Outcome}");
                    if (quiet.Outcome == QuietGate.Outcome.RanHot)
                    {
                        cellCoarse = true;
                    }

                    var reverse = (round & 1) == 1;
                    for (var orderIndex = 0; orderIndex < settings.Presets.Length; orderIndex++)
                    {
                        var preset = settings.Presets[
                            reverse ? settings.Presets.Length - 1 - orderIndex : orderIndex];
                        var key = new RowKey(clip.Content, clip.Width, clip.Height, preset, slices);
                        var result = EncodeClip(CreateOptions(preset, slices), clip, annex);
                        if (!roundTimes.TryGetValue(key, out var rowTimes))
                        {
                            rowTimes = [];
                            roundTimes[key] = rowTimes;
                        }

                        if (!hashes.TryGetValue(key, out var rowHashes))
                        {
                            rowHashes = [];
                            hashes[key] = rowHashes;
                        }

                        if (round >= settings.DiscardedRounds)
                        {
                            rowTimes.Add(result.PFrameMs);
                        }

                        rowHashes.Add(result.BitstreamSha256);
                    }
                }

                foreach (var preset in settings.Presets)
                {
                    rowCoarse[new RowKey(clip.Content, clip.Width, clip.Height, preset, slices)] = cellCoarse;
                }
            }
        }

        foreach (var (key, rowHashes) in hashes)
        {
            if (!VerifyIdentity(Label(key), rowHashes))
            {
                if (!settings.ExpectIdentityMismatch)
                {
                    return 1;
                }

                identityMismatch = true;
            }
        }

        // ---- Pass S: stage timings --------------------------------------------------------
        var stageTicksByRow = new Dictionary<RowKey, List<long[]>>();
        var stageWallByRow = new Dictionary<RowKey, List<double>>();
        H264PInterDiagnostics.CollectStageProfile = true;
        try
        {
            for (var stageRound = 0; stageRound < StageRounds; stageRound++)
            {
                foreach (var clip in clips)
                {
                    foreach (var slices in settings.Slices)
                    {
                        foreach (var preset in settings.Presets)
                        {
                            var key = new RowKey(clip.Content, clip.Width, clip.Height, preset, slices);
                            var result = EncodeStageClip(CreateOptions(preset, slices), clip, annex);

                            var rowHashes = hashes[key];
                            rowHashes.Add(result.BitstreamSha256);
                            if (!VerifyIdentity(Label(key), rowHashes))
                            {
                                identityMismatch = true;
                                if (!settings.ExpectIdentityMismatch)
                                {
                                    return 1;
                                }
                            }

                            if (!stageTicksByRow.TryGetValue(key, out var ticks))
                            {
                                ticks = [];
                                stageTicksByRow[key] = ticks;
                            }

                            ticks.Add(result.StageTicks);
                            if (!stageWallByRow.TryGetValue(key, out var walls))
                            {
                                walls = [];
                                stageWallByRow[key] = walls;
                            }

                            walls.Add(result.PFrameMs);
                        }
                    }
                }
            }
        }
        finally
        {
            H264PInterDiagnostics.CollectStageProfile = false;
            H264PInterDiagnostics.SnapshotStageTicks(reset: true);
        }

        // ---- Pass C: kernel call counts (total and per stage) ------------------------------
        var counts = new Dictionary<RowKey, IReadOnlyDictionary<string, long>>();
        var stageCounts = new Dictionary<RowKey, IReadOnlyList<CountingKernelSet.StageCall>>();
        var meSearchCounts = new Dictionary<RowKey, (long HexSearches, long ExhaustiveFallbacks)>();
        var meCandidateCounts = new Dictionary<RowKey, (long Integer, long Subpel)>();
        H264KernelSetHooks.MeasurementDecorator = k => new CountingKernelSet(k, settings.Sad16x16Bias);
        H264PInterDiagnostics.CollectStageProfile = true;
        H264PInterDiagnostics.CollectPhaseCounts = true;
        try
        {
            foreach (var clip in clips)
            {
                foreach (var slices in settings.Slices)
                {
                    foreach (var preset in settings.Presets)
                    {
                        var key = new RowKey(clip.Content, clip.Width, clip.Height, preset, slices);
                        using var encoder = new H264BaselineEncoder(
                            clip.Width, clip.Height, CreateOptions(preset, slices));
                        CountingKernelSet.Reset();
                        H264PInterDiagnostics.ResetPhaseCounts();
                        H264PInterDiagnostics.SnapshotStageTicks(reset: true);
                        H264PInterDiagnostics.SnapshotMeCandidates(reset: true);
                        var result = EncodeWith(encoder, clip, annex);
                        counts[key] = CountingKernelSet.Snapshot();
                        stageCounts[key] = CountingKernelSet.SnapshotByStage();
                        meSearchCounts[key] = H264PInterDiagnostics.ReadMeSearchCounts();
                        meCandidateCounts[key] = H264PInterDiagnostics.SnapshotMeCandidates(reset: false);

                        var rowHashes = hashes[key];
                        rowHashes.Add(result.BitstreamSha256);
                        if (VerifyIdentity(Label(key), rowHashes))
                        {
                            Console.WriteLine($"identity equal: {Label(key)} sha256={result.BitstreamSha256}");
                        }
                        else
                        {
                            identityMismatch = true;
                            if (!settings.ExpectIdentityMismatch)
                            {
                                return 1;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            H264KernelSetHooks.MeasurementDecorator = null;
            H264PInterDiagnostics.CollectStageProfile = false;
            H264PInterDiagnostics.CollectPhaseCounts = false;
            H264PInterDiagnostics.SnapshotStageTicks(reset: true);
        }

        if (settings.ExpectIdentityMismatch)
        {
            if (!identityMismatch)
            {
                Console.WriteLine("selftest: identity check did NOT notice the sabotaged kernel");
                return 1;
            }

            // Gap assertion: pass C must see integer and sub-pel candidate evaluations. Zero means
            // the counters are not wired up, which makes the "Int cand/MB" / "Subpel cand/MB"
            // columns meaningless.
            foreach (var (key, candidates) in meCandidateCounts)
            {
                Console.WriteLine(
                    $"selftest: {Label(key)} int-candidates={candidates.Integer} " +
                    $"subpel-candidates={candidates.Subpel}");
                if (candidates.Integer == 0 || candidates.Subpel == 0)
                {
                    Console.WriteLine(
                        "selftest: FAIL integer/sub-pel candidate counters are zero on the moving clip");
                    return 1;
                }
            }

            Console.WriteLine("selftest: identity check reported the expected mismatch");
            return 0;
        }

        // ---- Pass O: probe overhead from paired rounds -------------------------------------
        // The old estimate compared pass-S wall time with pass-T wall time from a different
        // window, so on a loaded box it came out negative. Timers-off and timers-on now run
        // back-to-back for the same row, alternating order each pair, and the median ratio is
        // the overhead. Rows whose quiet gate ran hot are reported as unmeasured downstream.
        var pairedOffByRow = new Dictionary<RowKey, List<double>>();
        var pairedOnByRow = new Dictionary<RowKey, List<double>>();
        for (var pair = 0; pair < PairedRounds; pair++)
        {
            foreach (var clip in clips)
            {
                foreach (var slices in settings.Slices)
                {
                    foreach (var preset in settings.Presets)
                    {
                        var key = new RowKey(clip.Content, clip.Width, clip.Height, preset, slices);
                        if (!pairedOffByRow.TryGetValue(key, out var offTimes))
                        {
                            offTimes = [];
                            pairedOffByRow[key] = offTimes;
                        }

                        if (!pairedOnByRow.TryGetValue(key, out var onTimes))
                        {
                            onTimes = [];
                            pairedOnByRow[key] = onTimes;
                        }

                        var options = CreateOptions(preset, slices);
                        if ((pair & 1) == 0)
                        {
                            offTimes.Add(MeasurePFrameMs(clip, options, annex, timersOn: false));
                            onTimes.Add(MeasurePFrameMs(clip, options, annex, timersOn: true));
                        }
                        else
                        {
                            onTimes.Add(MeasurePFrameMs(clip, options, annex, timersOn: true));
                            offTimes.Add(MeasurePFrameMs(clip, options, annex, timersOn: false));
                        }
                    }
                }
            }
        }

        var pairedOverheadByRow = new Dictionary<RowKey, double>();
        foreach (var (key, offTimes) in pairedOffByRow)
        {
            var offMedian = Median(offTimes);
            var onMedian = Median(pairedOnByRow[key]);
            pairedOverheadByRow[key] = double.IsNaN(offMedian) || double.IsNaN(onMedian) || offMedian <= 0
                ? double.NaN
                : ((onMedian / offMedian) - 1.0) * 100.0;
        }

        // ---- Results -----------------------------------------------------------------------
        var unitCosts = KernelUnitCost.Measure();
        var rows = BuildRows(
            settings, roundTimes, hashes, rowCoarse, counts, stageTicksByRow, stageWallByRow,
            stageCounts, meSearchCounts, meCandidateCounts, pairedOverheadByRow, unitCosts,
            suppressResidualForClosure: false);
        var provenance = new ProfileProvenance(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            $"{RuntimeInformation.OSDescription} | {RuntimeInformation.OSArchitecture} | " +
            $"process {RuntimeInformation.ProcessArchitecture} | {Environment.ProcessorCount} processors",
            $"{RuntimeInformation.FrameworkDescription} | Environment.Version {Environment.Version}",
            sha,
            kernelSetName,
            rawArgs);
        var document = new ProfileDocument(
            provenance,
            clips.Select(c => new ProfileClip(c.Content, c.Width, c.Height, c.Frames.Length, c.Sha256)).ToList(),
            rows,
            quietLog,
            unitCosts,
            KernelUnitCost.Tier);

        if (outDir is not null)
        {
            var jsonPath = Path.Combine(outDir, "profile.json");
            var mdPath = Path.Combine(outDir, "profile.md");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine);
            File.WriteAllText(mdPath, BuildMarkdown(document, settings));
            Console.WriteLine($"wrote {jsonPath}");
            Console.WriteLine($"wrote {mdPath}");
        }

        return 0;
    }

    private static List<Clip> BuildClips(Settings settings)
    {
        var clips = new List<Clip>();
        foreach (var content in settings.Contents)
        {
            foreach (var (width, height) in settings.Resolutions)
            {
                var pool = GeneratePool(content, width, height);
                var frames = new byte[settings.Frames][];
                using var clipHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (var i = 0; i < settings.Frames; i++)
                {
                    var frame = pool[i % pool.Length];
                    frames[i] = frame;
                    clipHash.AppendData(frame);
                }

                var clip = new Clip(
                    content, width, height, frames,
                    Convert.ToHexString(clipHash.GetHashAndReset()));
                clips.Add(clip);
                Console.WriteLine($"clip {content} {width}x{height} frames={settings.Frames} sha256={clip.Sha256}");
            }
        }

        return clips;
    }

    /// <summary>The five existing content generators; this harness adds no new ones.</summary>
    private static byte[][] GeneratePool(string content, int width, int height) => content switch
    {
        "static" => H264SliceSweepQuickHarness.GenerateStatic(width, height),
        "moving" => H264ResolutionSliceSweepBenchmarks.GenerateFrames(width, height),
        "highmotion" => H264SliceSweepQuickHarness.GenerateHighMotion(width, height),
        "scenecut" => H264SliceSweepQuickHarness.GenerateSceneCut(width, height),
        "divergent" => H264PerfProbe.GenerateDivergentFrames(width, height),
        _ => throw new ArgumentException($"Unknown content class '{content}'.", nameof(content)),
    };

    /// <summary>Fixed-QP configuration used by the existing harnesses: no rate control, IDR frame 0, P after.</summary>
    private static H264BaselineEncoderOptions CreateOptions(EncoderSpeedMode preset, int slices) => new()
    {
        QuantizationParameter = Qp,
        KeyframeIntervalFrames = int.MaxValue,
        LevelIdc = 40,
        SliceCount = slices,
        SpeedMode = preset,
    };

    private static EncodeResult EncodeClip(H264BaselineEncoderOptions options, Clip clip, byte[] annex)
    {
        using var encoder = new H264BaselineEncoder(clip.Width, clip.Height, options);
        return EncodeWith(encoder, clip, annex);
    }

    /// <summary>
    /// Pass-S encode: same bitstream as <see cref="EncodeWith"/>, but the per-stage tick totals cover
    /// the P frames only (the IDR's contributions are dropped at the same point pass T starts its
    /// stopwatch) and the wall time is the P-frame time across this round.
    /// </summary>
    private static StageEncodeResult EncodeStageClip(H264BaselineEncoderOptions options, Clip clip, byte[] annex)
    {
        using var encoder = new H264BaselineEncoder(clip.Width, clip.Height, options);
        var width = clip.Width;
        var height = clip.Height;
        var ySize = width * height;
        var uvSize = ySize / 4;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var idr = clip.Frames[0];
        var written = encoder.EncodeFrame(
            idr.AsSpan(0, ySize), idr.AsSpan(ySize, uvSize), idr.AsSpan(ySize + uvSize, uvSize),
            width, width / 2, annex, forceKeyframe: true);
        hash.AppendData(annex, 0, written);

        // Pass T times only the P frames; discard the IDR's stage ticks for the same reason.
        H264PInterDiagnostics.SnapshotStageTicks(reset: true);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 1; i < clip.Frames.Length; i++)
        {
            var frame = clip.Frames[i];
            written = encoder.EncodeFrame(
                frame.AsSpan(0, ySize), frame.AsSpan(ySize, uvSize), frame.AsSpan(ySize + uvSize, uvSize),
                width, width / 2, annex, forceKeyframe: false);
            hash.AppendData(annex, 0, written);
        }

        stopwatch.Stop();
        var ticks = H264PInterDiagnostics.SnapshotStageTicks(reset: true);
        return new StageEncodeResult(
            Convert.ToHexString(hash.GetHashAndReset()),
            stopwatch.Elapsed.TotalMilliseconds / (clip.Frames.Length - 1),
            ticks);
    }

    private sealed record StageEncodeResult(string BitstreamSha256, double PFrameMs, long[] StageTicks);

    /// <summary>
    /// Encodes the whole clip once. Only the P frames are timed (the stopwatch starts after the
    /// IDR); the SHA-256 covers every emitted Annex B byte, IDR included.
    /// </summary>
    private static EncodeResult EncodeWith(H264BaselineEncoder encoder, Clip clip, byte[] annex)
    {
        var width = clip.Width;
        var height = clip.Height;
        var ySize = width * height;
        var uvSize = ySize / 4;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var idr = clip.Frames[0];
        var written = encoder.EncodeFrame(
            idr.AsSpan(0, ySize), idr.AsSpan(ySize, uvSize), idr.AsSpan(ySize + uvSize, uvSize),
            width, width / 2, annex, forceKeyframe: true);
        hash.AppendData(annex, 0, written);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 1; i < clip.Frames.Length; i++)
        {
            var frame = clip.Frames[i];
            written = encoder.EncodeFrame(
                frame.AsSpan(0, ySize), frame.AsSpan(ySize, uvSize), frame.AsSpan(ySize + uvSize, uvSize),
                width, width / 2, annex, forceKeyframe: false);
            hash.AppendData(annex, 0, written);
        }

        stopwatch.Stop();
        return new EncodeResult(
            Convert.ToHexString(hash.GetHashAndReset()),
            stopwatch.Elapsed.TotalMilliseconds / (clip.Frames.Length - 1));
    }

    /// <summary>
    /// One paired-round encode with stage timers explicitly off or on, returning P-frame ms. A new
    /// encoder is built per measurement so the paired rounds see the same construction and warm-up
    /// state as pass T.
    /// </summary>
    private static double MeasurePFrameMs(
        Clip clip, H264BaselineEncoderOptions options, byte[] annex, bool timersOn)
    {
        H264PInterDiagnostics.CollectStageProfile = timersOn;
        try
        {
            using var encoder = new H264BaselineEncoder(clip.Width, clip.Height, options);
            return EncodeWith(encoder, clip, annex).PFrameMs;
        }
        finally
        {
            H264PInterDiagnostics.CollectStageProfile = false;
        }
    }

    private static readonly H264ProfileStage[] PerSliceStages =
    [
        H264ProfileStage.SkipPrecheck,
        H264ProfileStage.MeInteger,
        H264ProfileStage.MeSubpel,
        H264ProfileStage.MeSubpartition,
        H264ProfileStage.IntraDecision,
        H264ProfileStage.InterPredBuild,
        H264ProfileStage.Residual,
        H264ProfileStage.Entropy,
        H264ProfileStage.Deblock,
        H264ProfileStage.RateControl,
    ];

    private static List<ProfileRow> BuildRows(
        Settings settings,
        Dictionary<RowKey, List<double>> roundTimes,
        Dictionary<RowKey, List<string>> hashes,
        Dictionary<RowKey, bool> rowCoarse,
        Dictionary<RowKey, IReadOnlyDictionary<string, long>> counts,
        Dictionary<RowKey, List<long[]>> stageTicksByRow,
        Dictionary<RowKey, List<double>> stageWallByRow,
        Dictionary<RowKey, IReadOnlyList<CountingKernelSet.StageCall>> stageCounts,
        Dictionary<RowKey, (long HexSearches, long ExhaustiveFallbacks)> meSearchCounts,
        Dictionary<RowKey, (long Integer, long Subpel)> meCandidateCounts,
        Dictionary<RowKey, double> pairedProbeOverheadPct,
        IReadOnlyDictionary<string, double> unitCosts,
        bool suppressResidualForClosure)
    {
        var rows = new List<ProfileRow>();
        foreach (var content in settings.Contents)
        {
            foreach (var (width, height) in settings.Resolutions)
            {
                var macroblocksPerFrame = ((width + 15) / 16) * ((height + 15) / 16);
                var totalMacroblocks = (double)macroblocksPerFrame * settings.Frames;
                foreach (var preset in settings.Presets)
                {
                    foreach (var slices in settings.Slices)
                    {
                        var key = new RowKey(content, width, height, preset, slices);
                        var e2e = BuildE2e(roundTimes[key]);
                        var kernels = counts[key]
                            .OrderByDescending(kv => kv.Value)
                            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                            .Select(kv => new ProfileKernel(
                                kv.Key,
                                kv.Value,
                                Math.Round(kv.Value / totalMacroblocks, 4, MidpointRounding.AwayFromZero)))
                            .ToList();
                        var stages = BuildStageProfile(
                            key, e2e, stageTicksByRow[key], stageWallByRow[key], stageCounts[key],
                            meSearchCounts[key], meCandidateCounts[key], unitCosts, totalMacroblocks,
                            rowCoarse[key],
                            pairedProbeOverheadPct.GetValueOrDefault(key, double.NaN),
                            suppressResidualForClosure);
                        var coarse = rowCoarse[key] || stages.UnattributedPct > UnattributedLimitPct
                            || (stages.ProbeOverheadReliable
                                && stages.ProbeOverheadPct > ProbeOverheadLimitPct);
                        rows.Add(new ProfileRow(
                            content, width, height, preset.ToString(), slices, Qp, settings.Frames,
                            hashes[key][0], e2e, coarse, kernels, stages));
                        Console.WriteLine(
                            $"row {Label(key)}: median={e2e.Median:F2} ms/P " +
                            $"p10={e2e.P10:F2} p90={e2e.P90:F2} " +
                            $"CI95=[{e2e.Ci95Lo:F2}, {e2e.Ci95Hi:F2}] coarse={coarse} " +
                            $"unattributed={stages.UnattributedPct:F2}% probeOverhead=" +
                            $"{(stages.ProbeOverheadReliable ? $"{stages.ProbeOverheadPct:F2}%" : "n/a")}");
                    }
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Builds one row's pass-S stage table: median ticks per stage over the pass-S rounds, then
    /// share of measured thread time, modelled kernel share, glue, and the end-to-end speedup
    /// ceiling. With <paramref name="suppressResidual"/> the Residual stage is subtracted from the
    /// covered total (the closure negative control), so it shows up as unattributed instead.
    /// <paramref name="pairedProbeOverheadPct"/> is the median timers-on/timers-off ratio from the
    /// paired rounds, or NaN when it was not measured.
    /// </summary>
    private static ProfileStages BuildStageProfile(
        RowKey key,
        ProfileE2e e2e,
        List<long[]> roundTicks,
        List<double> roundWalls,
        IReadOnlyList<CountingKernelSet.StageCall> stageCalls,
        (long HexSearches, long ExhaustiveFallbacks) meSearch,
        (long Integer, long Subpel) meCandidates,
        IReadOnlyDictionary<string, double> unitCosts,
        double totalMacroblocks,
        bool roundHot,
        double pairedProbeOverheadPct,
        bool suppressResidual)
    {
        var median = MedianTicks(roundTicks);
        var sliceTotalNs = TicksToNs(median[(int)H264ProfileStage.SliceTotal]);
        var prologueNs = TicksToNs(median[(int)H264ProfileStage.FramePrologue]);
        var nalGatherNs = TicksToNs(median[(int)H264ProfileStage.NalGather]);
        var totalNs = sliceTotalNs + prologueNs + nalGatherNs;

        var coveredNs = 0.0;
        foreach (var stage in PerSliceStages)
        {
            if (suppressResidual && stage == H264ProfileStage.Residual)
                continue;
            coveredNs += TicksToNs(median[(int)stage]);
        }

        var unattributedPct = sliceTotalNs > 0 ? (sliceTotalNs - coveredNs) / sliceTotalNs * 100.0 : 0.0;

        var stages = new List<ProfileStageStat>();
        foreach (var stage in Enum.GetValues<H264ProfileStage>())
        {
            if (stage is H264ProfileStage.SliceTotal or H264ProfileStage.Count)
                continue;
            var stageNs = TicksToNs(median[(int)stage]);
            if (stageNs <= 0 && stage != H264ProfileStage.Residual)
                continue;

            var kernelNs = 0.0;
            foreach (var call in stageCalls)
            {
                if (call.Stage == stage && unitCosts.TryGetValue(call.Member, out var unitNs))
                    kernelNs += call.Calls * unitNs;
            }

            var glueNs = stageNs - kernelNs;
            var sharePct = totalNs > 0 ? stageNs / totalNs * 100.0 : 0.0;
            var kernelPct = totalNs > 0 ? kernelNs / totalNs * 100.0 : 0.0;
            var gluePct = totalNs > 0 ? glueNs / totalNs * 100.0 : 0.0;
            var ceiling = totalNs > 0 && kernelNs < totalNs ? 1.0 / (1.0 - (kernelNs / totalNs)) : double.PositiveInfinity;
            stages.Add(new ProfileStageStat(
                stage.ToString(),
                Math.Round(sharePct, 3, MidpointRounding.AwayFromZero),
                Math.Round(kernelPct, 3, MidpointRounding.AwayFromZero),
                Math.Round(gluePct, 3, MidpointRounding.AwayFromZero),
                ceiling,
                Math.Round(stageNs / 1_000_000.0, 4, MidpointRounding.AwayFromZero),
                Math.Round(kernelNs / 1_000_000.0, 4, MidpointRounding.AwayFromZero),
                Math.Round(glueNs / 1_000_000.0, 4, MidpointRounding.AwayFromZero)));
        }

        stages.Sort((a, b) => b.SharePct.CompareTo(a.SharePct));

        var wallMedian = Median(roundWalls);
        // Only a paired, quiet-window measurement with a non-negative result is reported as real
        // overhead. Anything else stays unmeasured (-1) rather than printing a negative number.
        var probeOverheadMeasured = !double.IsNaN(pairedProbeOverheadPct);
        var probeOverheadReliable = probeOverheadMeasured && !roundHot && pairedProbeOverheadPct >= 0;
        var probeOverheadPct = probeOverheadMeasured
            ? Math.Round(pairedProbeOverheadPct, 3, MidpointRounding.AwayFromZero)
            : -1.0;

        double StageCalls(H264ProfileStage stage)
        {
            var total = 0.0;
            foreach (var call in stageCalls)
            {
                if (call.Stage == stage)
                    total += call.Calls;
            }

            return totalMacroblocks > 0 ? total / totalMacroblocks : 0.0;
        }

        var me = new ProfileMe(
            Math.Round(totalMacroblocks > 0 ? meCandidates.Integer / totalMacroblocks : 0.0, 4, MidpointRounding.AwayFromZero),
            Math.Round(totalMacroblocks > 0 ? meCandidates.Subpel / totalMacroblocks : 0.0, 4, MidpointRounding.AwayFromZero),
            Math.Round(StageCalls(H264ProfileStage.MeSubpartition), 4, MidpointRounding.AwayFromZero),
            Math.Round(totalMacroblocks > 0 ? meSearch.HexSearches / totalMacroblocks : 0.0, 4, MidpointRounding.AwayFromZero),
            Math.Round(totalMacroblocks > 0 ? meSearch.ExhaustiveFallbacks / totalMacroblocks : 0.0, 4, MidpointRounding.AwayFromZero));

        return new ProfileStages(
            Math.Round(unattributedPct, 3, MidpointRounding.AwayFromZero),
            probeOverheadPct,
            probeOverheadReliable,
            double.IsNaN(wallMedian) ? -1 : wallMedian,
            roundHot,
            KernelUnitCost.Tier,
            stages,
            me);
    }

    /// <summary>Per-stage median ticks across rounds; with one round the sole sample is returned.</summary>
    private static long[] MedianTicks(List<long[]> rounds)
    {
        var count = (int)H264ProfileStage.Count;
        var result = new long[count];
        if (rounds.Count == 0)
            return result;
        var scratch = new long[rounds.Count];
        for (var stage = 0; stage < count; stage++)
        {
            for (var r = 0; r < rounds.Count; r++)
                scratch[r] = rounds[r][stage];
            Array.Sort(scratch);
            result[stage] = scratch[scratch.Length / 2];
        }

        return result;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return double.NaN;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private static double TicksToNs(long ticks) => ticks * (1_000_000_000.0 / Stopwatch.Frequency);

    private static ProfileE2e BuildE2e(List<double> roundTimes)
    {
        var values = roundTimes.ToArray();
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var (ci95Lo, ci95Hi) = BootstrapMedianCi(values);
        return new ProfileE2e(
            Math.Round(Percentile(sorted, 0.50), 3, MidpointRounding.AwayFromZero),
            Math.Round(Percentile(sorted, 0.10), 3, MidpointRounding.AwayFromZero),
            Math.Round(Percentile(sorted, 0.90), 3, MidpointRounding.AwayFromZero),
            Math.Round(ci95Lo, 3, MidpointRounding.AwayFromZero),
            Math.Round(ci95Hi, 3, MidpointRounding.AwayFromZero),
            values.Length);
    }

    /// <summary>95% percentile bootstrap interval of the median; 2,000 resamples, seed 20261126.</summary>
    private static (double Lo, double Hi) BootstrapMedianCi(double[] values)
    {
        const int Resamples = 2000;
        var random = new Random(20261126);
        var sample = new double[values.Length];
        var medians = new double[Resamples];
        for (var r = 0; r < Resamples; r++)
        {
            for (var i = 0; i < sample.Length; i++)
            {
                sample[i] = values[random.Next(values.Length)];
            }

            Array.Sort(sample);
            medians[r] = Percentile(sample, 0.50);
        }

        Array.Sort(medians);
        return (Percentile(medians, 0.025), Percentile(medians, 0.975));
    }

    /// <summary>Linear-interpolated percentile of an ascending-sorted array.</summary>
    private static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var rank = fraction * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (rank - lower));
    }

    /// <summary>
    /// The correctness gate: every hash for one row (all pass-T rounds plus the pass-C encode) must
    /// be identical. Prints the row and both hashes on a mismatch.
    /// </summary>
    private static bool VerifyIdentity(string label, IReadOnlyList<string> rowHashes)
    {
        var reference = rowHashes[0];
        for (var i = 1; i < rowHashes.Count; i++)
        {
            if (!string.Equals(rowHashes[i], reference, StringComparison.Ordinal))
            {
                Console.WriteLine(
                    $"identity MISMATCH: {label}: hash[{i}]={rowHashes[i]} vs hash[0]={reference}");
                return false;
            }
        }

        return true;
    }

    private static string Label(RowKey key)
        => $"{key.Content} {key.Width}x{key.Height} {key.Preset} s{key.Slices}";

    private static string FormatLoad(double load)
        => double.IsNaN(load) ? "n/a" : load.ToString("F2", CultureInfo.InvariantCulture);

    private static string BuildMarkdown(ProfileDocument document, Settings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# H.264 stage profile");
        sb.AppendLine();
        sb.AppendLine("Passes T (end-to-end P-frame time), C (kernel call counts) and S (disjoint stage " +
            "attribution; kernel share is modelled from batch-timed unit costs, glue is the remainder).");
        sb.AppendLine();
        sb.AppendLine($"- Generated (UTC): {document.Provenance.GeneratedUtc}");
        sb.AppendLine($"- Runtime: {document.Provenance.Runtime}");
        sb.AppendLine($"- .NET: {document.Provenance.DotnetVersion}");
        sb.AppendLine($"- Kiln git sha: {document.Provenance.KilnGitSha}");
        sb.AppendLine($"- Kernel set: {document.Provenance.KernelSet}");
        sb.AppendLine($"- Args: `{document.Provenance.Args}`");
        sb.AppendLine(
            $"- Quiet rounds: Immediate {document.QuietLog.Count(q => q.Outcome == "Immediate")}, " +
            $"Waited {document.QuietLog.Count(q => q.Outcome == "Waited")}, " +
            $"RanHot {document.QuietLog.Count(q => q.Outcome == "RanHot")} " +
            "(per-round detail in profile.json)");
        sb.AppendLine();

        sb.AppendLine("## Clip hashes (all frames concatenated, SHA-256)");
        sb.AppendLine();
        foreach (var clip in document.Clips)
        {
            sb.AppendLine($"- `{clip.Content}_{clip.Width}x{clip.Height}` ({clip.Frames} frames): `{clip.Sha256}`");
        }

        sb.AppendLine();
        sb.AppendLine("## Pass T: end-to-end P-frame time");
        foreach (var preset in settings.Presets)
        {
            var presetName = preset.ToString();
            sb.AppendLine();
            sb.AppendLine($"### {presetName}");
            sb.AppendLine();
            sb.AppendLine("| Content | Resolution | Slices | ms/P frame (median) | p10 | p90 | 95% CI of median | Rounds | Coarse |");
            sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | --- | ---: | --- |");
            foreach (var row in document.Rows.Where(r => r.Preset == presetName))
            {
                var e2e = row.E2eMsPerFrame;
                sb.AppendLine(
                    $"| {row.Content} | {row.Width}x{row.Height} | {row.Slices} | {e2e.Median:F2} | " +
                    $"{e2e.P10:F2} | {e2e.P90:F2} | [{e2e.Ci95Lo:F2}, {e2e.Ci95Hi:F2}] | " +
                    $"{e2e.Rounds} | {(row.Coarse ? "yes" : "no")} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Pass S: stage attribution");
        sb.AppendLine();
        sb.AppendLine(
            "Per stage: share of measured thread time, modelled kernel share, glue (stage minus " +
            "kernel), and the end-to-end speedup ceiling if that stage's kernels were free. " +
            "Stages are disjoint; unattributed is SliceTotal minus the per-slice stages.");
        foreach (var preset in settings.Presets)
        {
            var presetName = preset.ToString();
            sb.AppendLine();
            sb.AppendLine($"### {presetName}");
            sb.AppendLine();
            sb.AppendLine(
                "| Content | Resolution | Slices | Unattributed % | Probe overhead % | Int cand/MB | " +
                "Subpel cand/MB | Subpart cand/MB | Hex/MB | Exhaustive/MB | Coarse |");
            sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
            foreach (var row in document.Rows.Where(r => r.Preset == presetName))
            {
                var s = row.Stages;
                var overhead = s.ProbeOverheadReliable
                    ? s.ProbeOverheadPct.ToString("F2", CultureInfo.InvariantCulture)
                    : s.RoundHot ? "n/a (hot)" : "n/a";
                sb.AppendLine(
                    $"| {row.Content} | {row.Width}x{row.Height} | {row.Slices} | {s.UnattributedPct:F2} | " +
                    $"{overhead} | {s.Me.IntegerCandidatesPerMb:F2} | {s.Me.SubpelCandidatesPerMb:F2} | " +
                    $"{s.Me.SubpartitionCandidatesPerMb:F2} | {s.Me.HexSearchesPerMb:F2} | " +
                    $"{s.Me.ExhaustiveFallbacksPerMb:F2} | {(row.Coarse ? "yes" : "no")} |");
            }

            sb.AppendLine();
            sb.AppendLine("| Content | Resolution | Slices | Stage | Share % | Kernel % | Glue % | Ceiling x | Stage ms |");
            sb.AppendLine("| --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: |");
            foreach (var row in document.Rows.Where(r => r.Preset == presetName))
            {
                foreach (var stage in row.Stages.Stages)
                {
                    var ceiling = double.IsPositiveInfinity(stage.CeilingX)
                        ? "inf"
                        : stage.CeilingX.ToString("F2", CultureInfo.InvariantCulture);
                    sb.AppendLine(
                        $"| {row.Content} | {row.Width}x{row.Height} | {row.Slices} | {stage.Stage} | " +
                        $"{stage.SharePct:F2} | {stage.KernelPct:F2} | {stage.GluePct:F2} | {ceiling} | " +
                        $"{stage.StageMs:F3} |");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"## Kernel unit costs ({document.KernelUnitCostTier})");
        sb.AppendLine();
        sb.AppendLine($"| Kernel | ns/call |");
        sb.AppendLine("| --- | ---: |");
        foreach (var (member, ns) in document.KernelUnitCostsNs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"| {member} | {ns:F3} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Pass C: top 10 kernels by calls per MB");
        foreach (var preset in settings.Presets)
        {
            var presetName = preset.ToString();
            var presetRows = document.Rows.Where(r => r.Preset == presetName).ToList();
            var totals = new Dictionary<string, long>(StringComparer.Ordinal);
            var totalMacroblocks = 0.0;
            foreach (var row in presetRows)
            {
                var macroblocksPerFrame = ((row.Width + 15) / 16) * ((row.Height + 15) / 16);
                totalMacroblocks += (double)macroblocksPerFrame * row.Frames;
                foreach (var kernel in row.Kernels)
                {
                    totals[kernel.Member] = totals.GetValueOrDefault(kernel.Member) + kernel.Calls;
                }
            }

            var top = totals
                .Select(kv => (Member: kv.Key, Calls: kv.Value, PerMb: kv.Value / totalMacroblocks))
                .OrderByDescending(x => x.PerMb)
                .ThenBy(x => x.Member, StringComparer.Ordinal)
                .Take(10)
                .ToList();
            sb.AppendLine();
            sb.AppendLine($"### {presetName}");
            sb.AppendLine();
            sb.AppendLine("| Rank | Kernel | Calls | Calls/MB |");
            sb.AppendLine("| ---: | --- | ---: | ---: |");
            for (var i = 0; i < top.Count; i++)
            {
                sb.AppendLine($"| {i + 1} | {top[i].Member} | {top[i].Calls} | {top[i].PerMb:F2} |");
            }
        }

        return sb.ToString();
    }
}

/// <summary>Provenance block of <c>profile.json</c>.</summary>
internal sealed record ProfileProvenance(
    string GeneratedUtc,
    string Runtime,
    string DotnetVersion,
    string KilnGitSha,
    string KernelSet,
    string Args);

/// <summary>SHA-256 of one generated source clip (all frames concatenated).</summary>
internal sealed record ProfileClip(string Content, int Width, int Height, int Frames, string Sha256);

/// <summary>End-to-end timing summary for one row: ms per P frame across the timed pass-T rounds.</summary>
internal sealed record ProfileE2e(double Median, double P10, double P90, double Ci95Lo, double Ci95Hi, int Rounds);

/// <summary>One kernel member's call count for one row, plus calls per macroblock over the clip.</summary>
internal sealed record ProfileKernel(string Member, long Calls, double CallsPerMb);

/// <summary>One (content, resolution, preset, slices) row.</summary>
internal sealed record ProfileRow(
    string Content,
    int Width,
    int Height,
    string Preset,
    int Slices,
    int Qp,
    int Frames,
    string BitstreamSha256,
    ProfileE2e E2eMsPerFrame,
    bool Coarse,
    IReadOnlyList<ProfileKernel> Kernels,
    ProfileStages Stages);

/// <summary>One stage's share of measured thread time plus its modelled kernel/glue split.</summary>
internal sealed record ProfileStageStat(
    string Stage,
    double SharePct,
    double KernelPct,
    double GluePct,
    double CeilingX,
    double StageMs,
    double KernelMs,
    double GlueMs);

/// <summary>Motion-search candidate mix for one row, from the pass-C per-stage counts.</summary>
internal sealed record ProfileMe(
    double IntegerCandidatesPerMb,
    double SubpelCandidatesPerMb,
    double SubpartitionCandidatesPerMb,
    double HexSearchesPerMb,
    double ExhaustiveFallbacksPerMb);

/// <summary>Pass-S summary for one row.</summary>
internal sealed record ProfileStages(
    double UnattributedPct,
    double ProbeOverheadPct,
    bool ProbeOverheadReliable,
    double PassSWallMs,
    bool RoundHot,
    string KernelCostTier,
    IReadOnlyList<ProfileStageStat> Stages,
    ProfileMe Me);

/// <summary>One quiet-gate probe, logged before each pass-T round.</summary>
internal sealed record ProfileQuietRound(
    string Content,
    int Width,
    int Height,
    int Slices,
    int Round,
    double Threshold,
    double LoadBefore,
    double WaitSeconds,
    string Outcome);

/// <summary>Top-level shape of <c>profile.json</c>.</summary>
internal sealed record ProfileDocument(
    ProfileProvenance Provenance,
    IReadOnlyList<ProfileClip> Clips,
    IReadOnlyList<ProfileRow> Rows,
    IReadOnlyList<ProfileQuietRound> QuietLog,
    IReadOnlyDictionary<string, double> KernelUnitCostsNs,
    string KernelUnitCostTier);
