using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kiln;
using Kiln.RateControl;

namespace Kiln.AbHarness;

/// <summary>One cell of the encode matrix.</summary>
internal sealed record Cell(
    string Id,
    string Content,
    int Width,
    int Height,
    EncoderSpeedMode Preset,
    int Slices,
    int Frames);

/// <summary>One measured cell, serialized as a single JSON line.</summary>
internal sealed record CellResult(
    string Cell,
    double MsPerFrame,
    int FramesEncoded,
    long Bytes,
    string Sha256);

/// <summary>
/// A/B encode harness: encodes a deterministic synthetic matrix once per invocation and emits one
/// JSON line per cell. Everything goes through the public encoder API so the same source builds
/// against any commit that ships <see cref="H264BaselineEncoder"/>.
/// </summary>
internal static class Program
{
    private static readonly string[] Contents = ["static", "moving", "divergent"];
    private static readonly (int Width, int Height)[] AllResolutions = [(1280, 720), (1920, 1080)];
    private static readonly (int Width, int Height)[] QuickResolutions = [(1280, 720)];
    private static readonly EncoderSpeedMode[] AllPresets =
        [EncoderSpeedMode.HighQuality, EncoderSpeedMode.Balanced, EncoderSpeedMode.Fast, EncoderSpeedMode.VeryFast];
    private static readonly EncoderSpeedMode[] QuickPresets = [EncoderSpeedMode.HighQuality];
    private static readonly int[] Slices = [1, 8];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static bool _quick;

    public static int Main(string[] args)
    {
        _quick = Environment.GetEnvironmentVariable("KILN_AB_QUICK") == "1";
        var filtered = new List<string>(args.Length);
        foreach (var arg in args)
        {
            if (arg == "--quick")
            {
                _quick = true;
            }
            else
            {
                filtered.Add(arg);
            }
        }

        args = [.. filtered];
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }

        switch (args[0])
        {
            case "--list":
                foreach (var cell in BuildMatrix())
                {
                    Console.WriteLine(cell.Id);
                }

                return 0;
            case "--run":
                return Run(args[1..]);
            case "--help":
            case "-h":
                Usage();
                return 0;
            default:
                Usage();
                return 2;
        }
    }

    private static void Usage()
    {
        Console.Error.WriteLine(
            "Usage: Kiln.AbHarness --list");
        Console.Error.WriteLine(
            "       Kiln.AbHarness --run <cellId|all> [--rounds N] --out <file.jsonl>");
        Console.Error.WriteLine(
            "Add --quick (or set KILN_AB_QUICK=1) for the reduced matrix (720p, HighQuality, 10 frames).");
    }

    private static List<Cell> BuildMatrix()
    {
        var resolutions = _quick ? QuickResolutions : AllResolutions;
        var presets = _quick ? QuickPresets : AllPresets;
        var frames = _quick ? Corpus.QuickFrameCount : Corpus.FrameCount;

        var cells = new List<Cell>();
        foreach (var content in Contents)
        {
            foreach (var (width, height) in resolutions)
            {
                foreach (var preset in presets)
                {
                    foreach (var slices in Slices)
                    {
                        var id = $"{content}-{width}x{height}-{preset}-s{slices}";
                        cells.Add(new Cell(id, content, width, height, preset, slices, frames));
                    }
                }
            }
        }

        return cells;
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }

        var target = args[0];
        var rounds = 1;
        string? outPath = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rounds":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out rounds)
                        || rounds < 1)
                    {
                        Console.Error.WriteLine("--rounds needs a positive integer.");
                        return 2;
                    }

                    break;
                case "--out":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--out needs a path.");
                        return 2;
                    }

                    outPath = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 2;
            }
        }

        if (outPath is null)
        {
            Console.Error.WriteLine("--out is required.");
            return 2;
        }

        var selected = SelectCells(BuildMatrix(), target);
        if (selected is null)
        {
            return 2;
        }

        var fullOutPath = Path.GetFullPath(outPath);
        var outDir = Path.GetDirectoryName(fullOutPath);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var writer = new StreamWriter(fullOutPath, append: true, utf8NoBom) { AutoFlush = true };

        foreach (var group in selected.GroupBy(c => (c.Content, c.Width, c.Height)))
        {
            var frames = Corpus.Generate(group.Key.Content, group.Key.Width, group.Key.Height, group.First().Frames);
            foreach (var cell in group)
            {
                // One discarded warm-up pass so JIT and buffer growth are not counted.
                _ = EncodePass(cell, frames);

                var results = new (double Ms, long Bytes, string Sha)[rounds];
                for (var r = 0; r < rounds; r++)
                {
                    results[r] = EncodePass(cell, frames);
                }

                var sha = results[0].Sha;
                for (var r = 1; r < rounds; r++)
                {
                    if (results[r].Sha != sha)
                    {
                        Console.Error.WriteLine($"Timed passes disagree on the bitstream for {cell.Id}.");
                        return 1;
                    }
                }

                var ms = Median(results.Select(r => r.Ms).ToArray());
                var record = new CellResult(cell.Id, ms, cell.Frames, results[0].Bytes, sha);
                writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
                Console.Error.WriteLine(
                    $"{cell.Id} {ms.ToString("F3", CultureInfo.InvariantCulture)} ms/frame " +
                    $"{results[0].Bytes} bytes sha={sha[..12]}");
            }

            // Let the previous content's frame pool go before building the next one.
            GC.Collect();
        }

        return 0;
    }

    private static List<Cell>? SelectCells(List<Cell> matrix, string target)
    {
        if (target == "all")
        {
            return matrix;
        }

        var cell = matrix.FirstOrDefault(c => c.Id == target);
        if (cell is null)
        {
            Console.Error.WriteLine($"Unknown cell: {target} (try --list)");
            return null;
        }

        return [cell];
    }

    private static (double Ms, long Bytes, string Sha) EncodePass(Cell cell, byte[][] frames)
    {
        var options = new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = 60,
            SliceCount = cell.Slices,
            SpeedMode = cell.Preset,
        };

        using var encoder = new H264BaselineEncoder(cell.Width, cell.Height, options);
        var annex = new byte[encoder.RecommendedOutputBufferSize];
        var ys = cell.Width * cell.Height;
        var uv = ys / 4;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < frames.Length; i++)
        {
            var frame = frames[i];
            var written = encoder.EncodeFrame(
                frame.AsSpan(0, ys),
                frame.AsSpan(ys, uv),
                frame.AsSpan(ys + uv, uv),
                cell.Width,
                cell.Width / 2,
                annex,
                forceKeyframe: i == 0);
            total += written;
            hash.AppendData(annex.AsSpan(0, written));
        }

        stopwatch.Stop();
        var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        return (stopwatch.Elapsed.TotalMilliseconds / frames.Length, total, sha);
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        var n = values.Length;
        return (n & 1) == 1 ? values[n / 2] : (values[(n / 2) - 1] + values[n / 2]) / 2.0;
    }
}
