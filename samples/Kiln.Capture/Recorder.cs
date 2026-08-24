using System.Diagnostics;
using FlashCap;
using Kiln.Capture.Mp4;

namespace Kiln.Capture;

/// <summary>
/// Drives capture, encode and mux for one recording.
/// </summary>
/// <remarks>
/// The capture callback runs on FlashCap's own thread and must not block, so it only copies the
/// frame into a hand-off buffer. Encoding happens on the calling thread. If encoding ever falls
/// behind the camera the newest frame wins and the older one is counted as dropped, which keeps
/// latency bounded rather than letting a queue grow without limit.
/// </remarks>
internal sealed class Recorder
{
    private readonly object _gate = new();
    private byte[] _pending = [];
    private byte[] _spare = [];
    private byte[] _work = [];
    private int _pendingLength;
    private TimeSpan _pendingTimestamp;
    private bool _hasPending;
    private bool _stopped;

    private int _dropped;
    private int _captured;

    internal async Task<int> RunAsync(RecordOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var devices = DeviceCatalog.Enumerate();
        if (devices.Count == 0)
        {
            Console.Error.WriteLine("No video capture devices found.");
            return 1;
        }

        if (options.DeviceIndex < 0 || options.DeviceIndex >= devices.Count)
        {
            Console.Error.WriteLine(
                $"Device index {options.DeviceIndex} is out of range; {devices.Count} device(s) found. Run \"list\".");
            return 1;
        }

        var device = devices[options.DeviceIndex];
        var characteristics = DeviceCatalog.Select(device, options.Width, options.Height, options.Fps);

        Console.WriteLine($"Device : {device.Name} ({device.DeviceType})");
        Console.WriteLine($"Format : {DeviceCatalog.Describe(characteristics)}");
        Console.WriteLine($"Output : {options.OutputPath}");
        Console.WriteLine($"Encode : H.264 baseline, QP {options.Qp}, IDR every {options.Fps * 2} frames");
        Console.WriteLine();

        var converter = new FrameConverter(characteristics.PixelFormat, options.Width, options.Height);

        using var encoder = new H264BaselineEncoder(options.Width, options.Height, new H264BaselineEncoderOptions
        {
            QuantizationParameter = options.Qp,
            KeyframeIntervalFrames = options.Fps * 2,
            SliceCount = 1,
        });

        var annexB = new byte[(options.Width * options.Height * 2) + 512_000];
        using var mp4 = new Mp4Writer(options.OutputPath, options.Width, options.Height);

        // TranscodeFormats.DoNotTranscode keeps FlashCap out of the pixel path so we receive the
        // device's native bytes and convert them ourselves.
        using var captureDevice = await device.OpenAsync(
            characteristics,
            TranscodeFormats.DoNotTranscode,
            OnFrameArrived,
            cancellationToken).ConfigureAwait(false);

        var totalEncodeMs = 0.0;
        var encodedBytes = 0L;
        var stopwatch = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(options.Seconds);

        await captureDevice.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (stopwatch.Elapsed < deadline && !cancellationToken.IsCancellationRequested)
            {
                if (!TryTakeFrame(out var length, out var timestamp))
                {
                    continue;
                }

                converter.Convert(_work.AsSpan(0, length));

                var encodeStart = Stopwatch.GetTimestamp();
                var written = encoder.EncodeFrame(
                    converter.Y,
                    converter.U,
                    converter.V,
                    converter.StrideY,
                    converter.StrideUv,
                    annexB);
                totalEncodeMs += Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds;
                encodedBytes += written;

                mp4.WriteSample(annexB.AsSpan(0, written), encoder.LastFrameWasIdr, timestamp);

                if (mp4.SampleCount % 15 == 0)
                {
                    Console.Write(
                        $"\r  {mp4.SampleCount,5} frames  {stopwatch.Elapsed.TotalSeconds,5:0.0}s  " +
                        $"{encodedBytes / 1024.0,7:0.0} KiB");
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _stopped = true;
                Monitor.PulseAll(_gate);
            }

            await captureDevice.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var frames = mp4.SampleCount;
        mp4.Dispose();

        Console.WriteLine();
        Console.WriteLine();

        if (frames == 0)
        {
            Console.Error.WriteLine("No frames were captured.");
            if (OperatingSystem.IsMacOS())
            {
                Console.Error.WriteLine(
                    "On macOS a console app inherits the terminal's camera permission. Grant your " +
                    "terminal access under System Settings > Privacy & Security > Camera.");
            }

            return 1;
        }

        var elapsed = stopwatch.Elapsed.TotalSeconds;
        Console.WriteLine($"  frames encoded : {frames} ({frames / elapsed:0.0} fps average)");
        Console.WriteLine($"  frames dropped : {_dropped}");
        Console.WriteLine($"  encode time    : {totalEncodeMs / frames:0.00} ms/frame");
        Console.WriteLine($"  output size    : {encodedBytes / 1024.0:0.0} KiB");
        Console.WriteLine($"  bitrate        : {encodedBytes * 8 / elapsed / 1000:0.0} kbps");
        Console.WriteLine($"  written to     : {Path.GetFullPath(options.OutputPath)}");

        return 0;
    }

    /// <summary>Capture callback. Runs on FlashCap's thread; keep it to a copy and a swap.</summary>
    private void OnFrameArrived(PixelBufferScope scope)
    {
        var image = scope.Buffer.ReferImage();
        var timestamp = scope.Buffer.Timestamp;

        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (_spare.Length < image.Count)
            {
                _spare = new byte[image.Count];
            }

            image.AsSpan().CopyTo(_spare);

            if (_hasPending)
            {
                _dropped++;
            }

            (_spare, _pending) = (_pending, _spare);
            _pendingLength = image.Count;
            _pendingTimestamp = timestamp;
            _hasPending = true;
            _captured++;
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>
    /// Waits for the next frame and swaps it into the work buffer. Returns false on timeout so the
    /// caller can re-check its deadline.
    /// </summary>
    private bool TryTakeFrame(out int length, out TimeSpan timestamp)
    {
        lock (_gate)
        {
            if (!_hasPending && !_stopped)
            {
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(100));
            }

            if (!_hasPending)
            {
                length = 0;
                timestamp = default;
                return false;
            }

            (_work, _pending) = (_pending, _work);
            length = _pendingLength;
            timestamp = _pendingTimestamp;
            _hasPending = false;
            return true;
        }
    }
}

/// <summary>Parsed options for the <c>record</c> verb.</summary>
internal sealed record RecordOptions
{
    internal int DeviceIndex { get; init; }

    internal int Width { get; init; } = 1280;

    internal int Height { get; init; } = 720;

    internal int Fps { get; init; } = 30;

    internal int Seconds { get; init; } = 10;

    internal int Qp { get; init; } = 26;

    internal string OutputPath { get; init; } = "capture.m4v";
}
