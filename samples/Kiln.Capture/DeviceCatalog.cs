using System.Globalization;
using FlashCap;

namespace Kiln.Capture;

/// <summary>
/// Enumerates the machine's capture devices and picks a device format Kiln can encode.
/// </summary>
internal static class DeviceCatalog
{
    /// <summary>
    /// Preference order for capture formats. Native YUV formats normally come first because they
    /// convert to I420 with a repack and a vertical average, whereas RGB costs a full colour-space
    /// transform.
    /// </summary>
    private static readonly PixelFormats[] YuvFirst =
    [
        PixelFormats.YUYV,
        PixelFormats.UYVY,
        PixelFormats.NV12,
        PixelFormats.RGB32,
        PixelFormats.ARGB32,
        PixelFormats.RGB24,
    ];

    /// <summary>
    /// Preference order for macOS. FlashCap 1.12.0's AVFoundation backend returns corrupt frames
    /// for the YUV characteristics — the buffer is sized for the requested resolution but holds
    /// the native frame, so the picture comes out horizontally duplicated with wrong chroma. Its
    /// RGB32 path returns the native frame intact, so prefer that and pay for the conversion.
    /// </summary>
    private static readonly PixelFormats[] RgbFirst =
    [
        PixelFormats.RGB32,
        PixelFormats.ARGB32,
        PixelFormats.RGB24,
    ];

    private static PixelFormats[] PreferenceFor(DeviceTypes deviceType) =>
        deviceType == DeviceTypes.AVFoundation ? RgbFirst : YuvFirst;

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

            var preference = PreferenceFor(device.DeviceType);
            var sizes = device.Characteristics
                .Where(c => preference.Contains(c.PixelFormat))
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
                var encodable = IsEncodable(width, height) ? "         " : " (odd!)  ";
                Console.WriteLine(
                    $"      {width,5} x {height,-5}{encodable} up to {fps,6:0.##} fps   [{formats}]");
            }

            Console.WriteLine();
        }

        Console.WriteLine("Sizes marked \"(odd!)\" cannot be encoded: 4:2:0 chroma requires even dimensions.");
    }

    /// <summary>
    /// Kiln encodes any even dimensions, padding up to the macroblock grid and signalling the
    /// difference as SPS frame cropping. Odd sizes are unrepresentable in 4:2:0.
    /// </summary>
    internal static bool IsEncodable(int width, int height) => (width & 1) == 0 && (height & 1) == 0;

    /// <summary>
    /// Chooses the device format closest to the requested size and frame rate, preferring formats
    /// this sample can convert cheaply.
    /// </summary>
    internal static VideoCharacteristics Select(CaptureDeviceDescriptor device, int width, int height, int fps)
    {
        ArgumentNullException.ThrowIfNull(device);

        var preference = PreferenceFor(device.DeviceType);
        var candidates = device.Characteristics
            .Where(c => c.Width == width && c.Height == height)
            .Where(c => preference.Contains(c.PixelFormat))
            .ToList();

        if (candidates.Count == 0)
        {
            var offered = device.Characteristics
                .Where(c => preference.Contains(c.PixelFormat))
                .Select(c => $"{c.Width}x{c.Height}")
                .Distinct()
                .Order(StringComparer.Ordinal);

            throw new InvalidOperationException(
                $"\"{device.Name}\" does not offer {width}x{height} in a format this sample supports. " +
                $"Available: {string.Join(", ", offered)}. Run \"list\" for details.");
        }

        // Rank by format preference first, then by how close the frame rate is to the request.
        return candidates
            .OrderBy(c => Array.IndexOf(preference, c.PixelFormat))
            .ThenBy(c => Math.Abs((c.FramesPerSecond.Numerator / (double)c.FramesPerSecond.Denominator) - fps))
            .First();
    }

    /// <summary>Every distinct frame size the device advertises, for frame-geometry recovery.</summary>
    internal static IReadOnlyList<(int Width, int Height)> AdvertisedSizes(CaptureDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return [.. device.Characteristics.Select(c => (c.Width, c.Height)).Distinct()];
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
