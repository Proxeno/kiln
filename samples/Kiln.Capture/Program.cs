using System.Globalization;

namespace Kiln.Capture;

/// <summary>
/// Records from a camera into an H.264 <c>.m4v</c> file using Kiln as the encoder.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var verb = args.Length > 0 ? args[0] : "help";

        try
        {
            switch (verb)
            {
                case "list":
                    DeviceCatalog.PrintList(DeviceCatalog.Enumerate());
                    return 0;

                case "record":
                    return await RecordAsync(args).ConfigureAwait(false);

                case "help":
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;

                default:
                    Console.Error.WriteLine($"Unknown command \"{verb}\".");
                    Console.Error.WriteLine();
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RecordAsync(string[] args)
    {
        var options = ParseRecordOptions(args);

        if (!DeviceCatalog.IsEncodable(options.Width, options.Height))
        {
            Console.Error.WriteLine(
                $"error: {options.Width}x{options.Height} is not encodable. 4:2:0 chroma requires " +
                "both dimensions to be even.");
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
            Console.WriteLine();
            Console.WriteLine("Stopping...");
        };

        return await new Recorder().RunAsync(options, cancellation.Token).ConfigureAwait(false);
    }

    private static RecordOptions ParseRecordOptions(string[] args)
    {
        var options = new RecordOptions();

        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument \"{name}\".");
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Option \"{name}\" needs a value.");
            }

            var value = args[++i];
            options = name switch
            {
                "--device" => options with { DeviceIndex = ParseInt(name, value) },
                "--width" => options with { Width = ParseInt(name, value) },
                "--height" => options with { Height = ParseInt(name, value) },
                "--fps" => options with { Fps = ParseInt(name, value) },
                "--seconds" => options with { Seconds = ParseInt(name, value) },
                "--qp" => options with { Qp = ParseInt(name, value) },
                "--slices" => options with { Slices = ParseInt(name, value) },
                "--output" => options with { OutputPath = value },
                _ => throw new ArgumentException($"Unknown option \"{name}\"."),
            };
        }

        if (options.Qp is < 0 or > 51)
        {
            throw new ArgumentException($"--qp must be between 0 and 51; got {options.Qp}.");
        }

        if (options.Slices is < 1 or > 8)
        {
            throw new ArgumentException($"--slices must be between 1 and 8; got {options.Slices}.");
        }

        if (options.Seconds <= 0)
        {
            throw new ArgumentException($"--seconds must be positive; got {options.Seconds}.");
        }

        return options;
    }

    private static int ParseInt(string name, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Option \"{name}\" expects an integer; got \"{value}\".");

    private static void PrintUsage()
    {
        Console.WriteLine("kiln-capture - record a camera to H.264 .m4v with the Kiln encoder");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  kiln-capture list");
        Console.WriteLine("  kiln-capture record [options]");
        Console.WriteLine();
        Console.WriteLine("Options for record:");
        Console.WriteLine("  --device  <index>   capture device from \"list\"      (default 0)");
        Console.WriteLine("  --width   <px>      requested frame width           (default 1280)");
        Console.WriteLine("  --height  <px>      requested frame height          (default 720)");
        Console.WriteLine("  --fps     <n>       requested frame rate            (default 30)");
        Console.WriteLine("  --seconds <n>       recording length                (default 10)");
        Console.WriteLine("  --qp      <0-51>    quantization parameter          (default 26)");
        Console.WriteLine("  --slices  <1-8>     slices per frame, encoded in parallel (default 4)");
        Console.WriteLine("  --output  <path>    output file                     (default capture.m4v)");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop early; the file is finalized either way.");
    }
}
