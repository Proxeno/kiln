# Kiln.Capture

A console app that enumerates the machine's video capture devices and records one to a playable
`.m4v` file, using Kiln as the encoder.

```
dotnet run --project samples/Kiln.Capture -- list
dotnet run --project samples/Kiln.Capture -- record --seconds 10 --output capture.m4v
```

```
Usage:
  kiln-capture list
  kiln-capture record [options]

Options for record:
  --device  <index>   capture device from "list"      (default 0)
  --width   <px>      requested frame width           (default 1280)
  --height  <px>      requested frame height          (default 720)
  --fps     <n>       requested frame rate            (default 30)
  --seconds <n>       recording length                (default 10)
  --qp      <0-51>    quantization parameter          (default 26)
  --slices  <1-8>     slices per frame, encoded in parallel (default 4)
  --output  <path>    output file                     (default capture.m4v)
```

## What this demonstrates

The whole pipeline — capture, colour conversion, H.264 encode, MP4 mux — runs in managed code with
no native binaries. [FlashCap](https://github.com/kekyo/FlashCap) (Apache-2.0) provides camera access
by P/Invoking the OS APIs directly on macOS (AVFoundation), Linux (V4L2) and Windows
(DirectShow / Media Foundation); everything downstream of it is in this repository.

`src/Kiln` takes no dependency on FlashCap. The reference lives only in this sample.

## How it fits together

| File | Role |
|---|---|
| `Program.cs` | Argument parsing, `list` and `record` verbs |
| `DeviceCatalog.cs` | Device enumeration and capture-format selection |
| `CapturedFrameFormat.cs` | Works out the layout frames are *actually* delivered in |
| `FrameConverter.cs` | YUYV / UYVY / NV12 / RGB → planar I420 |
| `H264Levels.cs` | Picks a `level_idc` that admits the frame size |
| `Mp4/AnnexBReader.cs` | Splits Annex B access units into NAL units |
| `Mp4/BoxWriter.cs` | Big-endian ISO base media file format box writer |
| `Mp4/Mp4Writer.cs` | The muxer: `ftyp` / `mdat` / `moov` |

Kiln emits Annex B only, so producing a container is the sample's job: SPS and PPS are parsed back
out of the first IDR access unit into the `avcC` record, start codes are rewritten as 4-byte NAL
lengths, and the sample tables (`stts`, `stss`, `stsc`, `stsz`, `stco`) are accumulated in memory
while media data streams to disk.

The muxer is covered by `tests/Kiln.Tests/Mp4WriterTests.cs`, which runs without a camera.

## macOS: camera permission

A plain `dotnet run` binary is not a bundled `.app`, so it inherits the *terminal's* camera
permission rather than requesting its own. If frames come back black or the recording captures
nothing, grant your terminal access under **System Settings → Privacy & Security → Camera**.

## Known upstream issues (FlashCap 1.12.0, macOS/AVFoundation)

The AVFoundation backend is new in FlashCap 1.11.0 and misreports what it delivers. This sample
works around it, which is why `CapturedFrameFormat` exists rather than the code simply trusting the
frame header:

1. **The requested frame size is ignored.** Asking for 1280x720 returns the device's native
   1920x1080 regardless.
2. **The bitmap header reports the requested size, not the delivered one.** The header is
   cross-checked against the payload length, and the real geometry is recovered by matching the
   pixel count against the sizes the device advertises.
3. **Row order is misreported.** The header marks the image bottom-up while the rows are top-down.
   When the header's dimensions are already proven wrong, its orientation is not trusted either.
4. **Channel order is misreported.** `RGB32` is really `0RGB`, with the padding byte first. The
   sample probes for the constant lane to work out which byte is which.
5. **The YUV characteristics return corrupt frames.** Requesting `YUYV` or `UYVY` yields a buffer
   sized for the requested resolution that holds the native frame, so the picture comes out
   horizontally duplicated with wrong chroma. `DeviceCatalog` therefore prefers RGB formats on
   AVFoundation and YUV formats everywhere else.

`PixelBuffer.Timestamp` also is not filled in dependably, so arrival is stamped from the sample's
own clock — a wrong value there corrupts the container's `stts` table.

## Performance note

Real camera content is far heavier to encode than the synthetic near-static frames in
`bench/Kiln.Benchmarks`. Expect roughly 10 ms/frame at 720p and 35-60 ms/frame at 1080p on Apple
silicon with `--slices 4`. When encoding cannot keep up with the camera the newest frame wins and
the older one is counted as dropped, so latency stays bounded; the summary reports the count.
