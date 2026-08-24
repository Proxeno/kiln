using System.Globalization;
using FlashCap;

namespace Kiln.Capture;

/// <summary>
/// Enumerates the machine's capture devices and picks a device format Kiln can encode.
/// </summary>
internal static class DeviceCatalog
{
    /// <summary>
    /// Preference order for capture formats. Native YUV formats come first because they convert
    /// to I420 with a repack and a vertical average; RGB costs a full colour-space transform.
    /// </summary>
    private static readonly PixelFormats[] FormatPreference =
    [
        PixelFormats.YUYV,
        PixelFormats.UYVY,
        PixelFormats.NV12,
        PixelFormats.RGB32,
        PixelFormats.ARGB32,
        PixelFormats.RGB24,
    ];

    internal static IReadOnlyList<CaptureDeviceDescriptor> Enumerate() =>
        [.. new CaptureDevices().EnumerateDescriptors()
            .Where(d => d.Characteristics.Length > 0)];

    /// <summary>Prints every device and the distinct frame sizes it offers.</summary>
    internal static void PrintList(IReadOnlyList<CaptureDeviceDescriptor> devices)
    {
        if (devices.Count == 0)
        {
            Console.WriteLine("No video capture devices found.");
            return;
        }

        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            Console.WriteLine($"[{i}] {device.Name}");
            Console.WriteLine($"    backend: {device.DeviceType}");
            Console.WriteLine($"    id:      {device.Identity}");

            var sizes = device.Characteristics
                .Where(c => FormatPreference.Contains(c.PixelFormat))
                .GroupBy(c => (c.Width, c.Height))
                .OrderBy(g => g.Key.Width * g.Key.Height)
                .ToList();

            if (sizes.Count == 0)
            {
                Console.WriteLine("    formats: none this sample can decode (compressed-only device)");
                Console.WriteLine();
                continue;
            }

            Console.WriteLine("    formats:");
            foreach (var group in sizes)
            {
                var (width, height) = group.Key;
                var formats = string.Join(", ", group.Select(c => c.PixelFormat).Distinct().Order());
                var fps = group.Max(c => c.FramesPerSecond.Numerator / (double)c.FramesPerSecond.Denominator);
                var encodable = IsEncodable(width, height) ? "           " : " (not x16) ";
                Console.WriteLine(
                    $"      {width,5} x {height,-5}{encodable} up to {fps,6:0.##} fps   [{formats}]");
            }

            Console.WriteLine();
        }

        Console.WriteLine("Sizes marked \"(not x16)\" cannot be encoded: Kiln requires both dimensions");
        Console.WriteLine("to be multiples of 16.");
    }

    /// <summary>Kiln's constructor rejects any dimension that is not a multiple of 16.</summary>
    internal static bool IsEncodable(int width, int height) => (width & 15) == 0 && (height & 15) == 0;

    /// <summary>
    /// Chooses the device format closest to the requested size and frame rate, preferring formats
    /// this sample can convert cheaply.
    /// </summary>
    internal static VideoCharacteristics Select(CaptureDeviceDescriptor device, int width, int height, int fps)
    {
        ArgumentNullException.ThrowIfNull(device);

        var candidates = device.Characteristics
            .Where(c => c.Width == width && c.Height == height)
            .Where(c => FormatPreference.Contains(c.PixelFormat))
            .ToList();

        if (candidates.Count == 0)
        {
            var offered = device.Characteristics
                .Where(c => FormatPreference.Contains(c.PixelFormat))
                .Select(c => $"{c.Width}x{c.Height}")
                .Distinct()
                .Order(StringComparer.Ordinal);

            throw new InvalidOperationException(
                $"\"{device.Name}\" does not offer {width}x{height} in a format this sample supports. " +
                $"Available: {string.Join(", ", offered)}. Run \"list\" for details.");
        }

        // Rank by format preference first, then by how close the frame rate is to the request.
        return candidates
            .OrderBy(c => Array.IndexOf(FormatPreference, c.PixelFormat))
            .ThenBy(c => Math.Abs((c.FramesPerSecond.Numerator / (double)c.FramesPerSecond.Denominator) - fps))
            .First();
    }

    internal static string Describe(VideoCharacteristics characteristics)
    {
        ArgumentNullException.ThrowIfNull(characteristics);
        var fps = characteristics.FramesPerSecond.Numerator / (double)characteristics.FramesPerSecond.Denominator;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{characteristics.Width}x{characteristics.Height} {characteristics.PixelFormat} @ {fps:0.##} fps");
    }
}
