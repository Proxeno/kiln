using System.Buffers.Binary;
using System.Diagnostics;
using FluentAssertions;
using Kiln.Capture.Mp4;

namespace Kiln.Tests;

/// <summary>
/// Covers the capture sample's MP4 muxer. No camera is involved: frames are synthesized, encoded
/// with Kiln, and muxed, so these run everywhere CI does.
/// </summary>
public sealed class Mp4WriterTests
{
    private const int Width = 128;
    private const int Height = 128;
    private const int FrameCount = 30;
    private const int KeyframeInterval = 10;

    [Fact]
    public void Muxed_file_has_the_expected_box_structure_and_sample_tables()
    {
        var path = TempPath();
        try
        {
            var idrFrames = Encode(path);
            var file = File.ReadAllBytes(path);

            // Top-level boxes, in the order this writer emits them.
            var ftyp = FindBox(file, 0, file.Length, "ftyp");
            ftyp.Should().NotBeNull("every ISO base media file starts with a file type box");
            var mdat = FindBox(file, 0, file.Length, "mdat");
            mdat.Should().NotBeNull();
            var moov = FindBox(file, 0, file.Length, "moov");
            moov.Should().NotBeNull();

            moov!.Value.Offset.Should().BeGreaterThan(mdat!.Value.Offset,
                "this writer streams media data first and appends the movie box on close");

            // The .m4v brand is what makes QuickTime treat the file as video.
            ReadFourCc(file, ftyp!.Value.Offset + 8).Should().Be("M4V ");

            // Sample tables live several levels down; walk the nesting explicitly.
            var stbl = Descend(file, moov.Value, "trak", "mdia", "minf", "stbl");

            var stsz = FindBox(file, stbl.Offset + 8, stbl.End, "stsz")!.Value;
            BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsz.Offset + 16, 4))
                .Should().Be(FrameCount, "every encoded frame becomes exactly one MP4 sample");

            var stss = FindBox(file, stbl.Offset + 8, stbl.End, "stss")!.Value;
            var syncCount = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stss.Offset + 12, 4));
            var syncSamples = new List<uint>();
            for (var i = 0; i < syncCount; i++)
            {
                syncSamples.Add(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stss.Offset + 16 + (i * 4), 4)));
            }

            syncSamples.Should().Equal(idrFrames,
                "the sync sample table must list exactly the IDR frames, as 1-based indices");

            // stts entries must account for every sample.
            var stts = FindBox(file, stbl.Offset + 8, stbl.End, "stts")!.Value;
            var sttsEntries = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stts.Offset + 12, 4));
            var totalSamples = 0u;
            for (var i = 0; i < sttsEntries; i++)
            {
                totalSamples += BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stts.Offset + 16 + (i * 8), 4));
            }

            totalSamples.Should().Be(FrameCount, "the time-to-sample table must cover every sample");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AvcC_record_round_trips_the_encoder_parameter_sets()
    {
        var path = TempPath();
        try
        {
            Encode(path);
            var file = File.ReadAllBytes(path);

            var moov = FindBox(file, 0, file.Length, "moov")!.Value;
            var stbl = Descend(file, moov, "trak", "mdia", "minf", "stbl");
            var stsd = FindBox(file, stbl.Offset + 8, stbl.End, "stsd")!.Value;
            var avc1 = FindBox(file, stsd.Offset + 16, stsd.End, "avc1")!.Value;

            // avc1 has a 78-byte VisualSampleEntry body before its child boxes.
            var avcC = FindBox(file, avc1.Offset + 8 + 78, avc1.End, "avcC")!.Value;
            var p = avcC.Offset + 8;

            file[p].Should().Be(1, "configurationVersion is always 1");
            file[p + 4].Should().Be(0xFF, "lengthSizeMinusOne must be 3, signalling 4-byte NAL lengths");
            file[p + 5].Should().Be(0xE1, "exactly one SPS is expected");

            var spsLength = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(p + 6, 2));
            var sps = file.AsSpan(p + 8, spsLength);
            (sps[0] & 0x1F).Should().Be(7, "the stored parameter set must be an SPS NAL");

            // Bytes 1..3 of the SPS are mirrored into the configuration record.
            file[p + 1].Should().Be(sps[1], "AVCProfileIndication mirrors the SPS profile_idc");
            file[p + 2].Should().Be(sps[2], "profile_compatibility mirrors the SPS constraint flags");
            file[p + 3].Should().Be(sps[3], "AVCLevelIndication mirrors the SPS level_idc");

            var ppsCountOffset = p + 8 + spsLength;
            file[ppsCountOffset].Should().Be(1, "exactly one PPS is expected");
            var ppsLength = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(ppsCountOffset + 1, 2));
            (file[ppsCountOffset + 3] & 0x1F).Should().Be(8, "the stored parameter set must be a PPS NAL");
            ppsLength.Should().BeGreaterThan(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sample_data_carries_no_inline_parameter_sets()
    {
        var path = TempPath();
        try
        {
            Encode(path);
            var file = File.ReadAllBytes(path);
            var mdat = FindBox(file, 0, file.Length, "mdat")!.Value;

            // Walk the length-prefixed NAL units in mdat; SPS/PPS belong in avcC, not here.
            var p = mdat.Offset + 8;
            var seen = 0;
            while (p + 4 <= mdat.End)
            {
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p, 4));
                length.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(mdat.End - p - 4,
                    "each NAL length prefix must stay inside mdat, proving the lengths are consistent");

                var nalType = file[p + 4] & 0x1F;
                nalType.Should().NotBe(7, "SPS must not be duplicated into sample data");
                nalType.Should().NotBe(8, "PPS must not be duplicated into sample data");

                seen++;
                p += 4 + length;
            }

            p.Should().Be(mdat.End, "the NAL walk must land exactly on the end of mdat");
            seen.Should().BeGreaterThanOrEqualTo(FrameCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Muxed_file_decodes_cleanly_in_ffmpeg()
    {
        if (!FfmpegAvailable())
        {
            return;
        }

        var path = TempPath();
        try
        {
            Encode(path);

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            using var process = Process.Start(psi);
            process.Should().NotBeNull();
            var stderr = process!.StandardError.ReadToEnd();
            process.WaitForExit(60_000).Should().BeTrue("ffmpeg should finish promptly");

            // A container-level failure (bad box sizes, wrong avcC) shows up as a non-zero exit,
            // while bitstream damage shows up on stderr; check both.
            process.ExitCode.Should().Be(0, "ffmpeg must be able to demux and decode the file: {0}", stderr);
            H264FfmpegDecodeAssertions.AssertStderrHasNoDecodeErrors(stderr, "the muxed file must decode cleanly");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Encodes a synthetic clip into <paramref name="path"/>, returning the 1-based IDR indices.</summary>
    private static List<uint> Encode(string path)
    {
        using var encoder = new H264BaselineEncoder(Width, Height, new H264BaselineEncoderOptions
        {
            QuantizationParameter = 28,
            KeyframeIntervalFrames = KeyframeInterval,
            SliceCount = 1,
        });

        var y = new byte[Width * Height];
        var u = new byte[Width / 2 * (Height / 2)];
        var v = new byte[Width / 2 * (Height / 2)];
        var annexB = new byte[(Width * Height * 2) + 512_000];
        var idrFrames = new List<uint>();

        using var writer = new Mp4Writer(path, Width, Height);
        for (var frame = 0; frame < FrameCount; frame++)
        {
            FillMovingPattern(y, u, v, frame);

            var written = encoder.EncodeFrame(y, u, v, Width, Width / 2, annexB);
            var timestamp = TimeSpan.FromSeconds(frame / 30.0);
            writer.WriteSample(annexB.AsSpan(0, written), encoder.LastFrameWasIdr, timestamp);

            if (encoder.LastFrameWasIdr)
            {
                idrFrames.Add((uint)(frame + 1));
            }
        }

        return idrFrames;
    }

    /// <summary>A translating pattern, so P-frames carry real motion rather than encoding as all-skip.</summary>
    private static void FillMovingPattern(byte[] y, byte[] u, byte[] v, int frame)
    {
        var shift = frame * 3;
        for (var row = 0; row < Height; row++)
        {
            for (var col = 0; col < Width; col++)
            {
                y[(row * Width) + col] = (byte)((((row + shift) >> 3) ^ ((col + shift) >> 3)) * 255);
            }
        }

        var chromaWidth = Width / 2;
        for (var row = 0; row < Height / 2; row++)
        {
            for (var col = 0; col < chromaWidth; col++)
            {
                u[(row * chromaWidth) + col] = (byte)(128 + ((col + shift) & 31) - 16);
                v[(row * chromaWidth) + col] = (byte)(128 + ((row + shift) & 31) - 16);
            }
        }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"kiln-mp4-{Guid.NewGuid():N}.m4v");

    private readonly record struct BoxSpan(int Offset, int Size)
    {
        public int End => Offset + Size;
    }

    /// <summary>Scans one nesting level for a box of the given type.</summary>
    private static BoxSpan? FindBox(byte[] file, int from, int to, string type)
    {
        var p = from;
        while (p + 8 <= to)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p, 4));
            if (size < 8 || p + size > to)
            {
                return null;
            }

            if (ReadFourCc(file, p + 4) == type)
            {
                return new BoxSpan(p, size);
            }

            p += size;
        }

        return null;
    }

    /// <summary>Walks a chain of nested box types, failing the test if any link is missing.</summary>
    private static BoxSpan Descend(byte[] file, BoxSpan container, params string[] path)
    {
        var current = container;
        foreach (var type in path)
        {
            var next = FindBox(file, current.Offset + 8, current.End, type);
            next.Should().NotBeNull("box \"{0}\" must exist inside the movie box hierarchy", type);
            current = next!.Value;
        }

        return current;
    }

    private static string ReadFourCc(byte[] file, int offset) =>
        string.Create(4, (file, offset), static (chars, state) =>
        {
            for (var i = 0; i < 4; i++)
            {
                chars[i] = (char)state.file[state.offset + i];
            }
        });

    private static bool FfmpegAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(10_000))
            {
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
