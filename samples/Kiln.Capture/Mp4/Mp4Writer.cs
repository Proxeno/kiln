namespace Kiln.Capture.Mp4;

/// <summary>
/// A minimal progressive MP4 / M4V writer for a single H.264 video track.
/// </summary>
/// <remarks>
/// <para>
/// Layout is <c>ftyp</c>, then <c>mdat</c> with sample data streamed straight to disk, then
/// <c>moov</c> built from the sample table accumulated in memory. The <c>mdat</c> size is
/// reserved up front and patched by seeking back on <see cref="Dispose"/>.
/// </para>
/// <para>
/// <c>.m4v</c> is ordinary MP4 with Apple's extension, so the only difference from <c>.mp4</c>
/// is the <c>M4V </c> brand advertised in <c>ftyp</c>.
/// </para>
/// </remarks>
internal sealed class Mp4Writer : IDisposable
{
    /// <summary>Media timescale in ticks per second. 90 kHz is the usual choice for H.264.</summary>
    private const uint MediaTimescale = 90_000;

    /// <summary>Movie-level timescale, in ticks per second.</summary>
    private const uint MovieTimescale = 1_000;

    private readonly FileStream _file;
    private readonly int _width;
    private readonly int _height;
    private readonly List<uint> _sampleSizes = [];
    private readonly List<uint> _sampleDurations = [];
    private readonly List<uint> _syncSamples = [];
    private readonly List<AnnexBReader.Nal> _nals = [];

    private byte[]? _sps;
    private byte[]? _pps;
    private long _mdatStart;
    private long _mdatPayloadStart;
    private long _previousTimestampTicks = -1;
    private bool _disposed;

    internal Mp4Writer(string path, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        _width = width;
        _height = height;
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        WriteFileTypeBox();

        // Reserve the mdat header; the size is patched once the payload length is known.
        _mdatStart = _file.Position;
        WriteBigEndianUInt32(0);
        _file.Write("mdat"u8);
        _mdatPayloadStart = _file.Position;
    }

    /// <summary>Number of samples (coded frames) written so far.</summary>
    internal int SampleCount => _sampleSizes.Count;

    /// <summary>
    /// Appends one access unit as an MP4 sample.
    /// </summary>
    /// <param name="annexB">A complete Annex B access unit as produced by <c>EncodeFrame</c>.</param>
    /// <param name="isIdr">Whether this access unit is an IDR, for the sync sample table.</param>
    /// <param name="timestamp">Capture timestamp, used to derive per-sample durations.</param>
    internal void WriteSample(ReadOnlySpan<byte> annexB, bool isIdr, TimeSpan timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _nals.Clear();
        AnnexBReader.Split(annexB, _nals);

        // SPS/PPS live in the avcC record, not in sample data. Capture them from the first IDR
        // and drop them (along with any access unit delimiter) from every sample.
        foreach (var nal in _nals)
        {
            switch (nal.Type)
            {
                case AnnexBReader.NalTypeSps:
                    _sps ??= annexB.Slice(nal.Offset, nal.Length).ToArray();
                    break;
                case AnnexBReader.NalTypePps:
                    _pps ??= annexB.Slice(nal.Offset, nal.Length).ToArray();
                    break;
                default:
                    break;
            }
        }

        if (_sps is null || _pps is null)
        {
            throw new InvalidOperationException(
                "The first sample must carry SPS and PPS; Kiln emits both ahead of every IDR access unit.");
        }

        var sampleBytes = 0u;
        foreach (var nal in _nals)
        {
            if (nal.Type is AnnexBReader.NalTypeSps or AnnexBReader.NalTypePps
                or AnnexBReader.NalTypeAccessUnitDelimiter)
            {
                continue;
            }

            WriteBigEndianUInt32((uint)nal.Length);
            _file.Write(annexB.Slice(nal.Offset, nal.Length));
            sampleBytes += 4u + (uint)nal.Length;
        }

        if (sampleBytes == 0)
        {
            throw new InvalidOperationException("Access unit contained no coded slice NAL units.");
        }

        if (_file.Position - _mdatPayloadStart > uint.MaxValue)
        {
            throw new InvalidOperationException(
                "Media data exceeds 4 GiB, which this writer's 32-bit mdat box cannot describe. Record a shorter clip.");
        }

        var ticks = (long)(timestamp.TotalSeconds * MediaTimescale);
        if (_previousTimestampTicks >= 0)
        {
            // The duration of the *previous* sample is only known once this one arrives.
            var delta = ticks - _previousTimestampTicks;
            _sampleDurations.Add(delta > 0 ? (uint)delta : 1u);
        }

        _previousTimestampTicks = ticks;
        _sampleSizes.Add(sampleBytes);

        if (isIdr)
        {
            // stss holds 1-based sample indices.
            _syncSamples.Add((uint)_sampleSizes.Count);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        using (_file)
        {
            if (_sampleSizes.Count > 0)
            {
                Finish();
            }
        }
    }

    private void Finish()
    {
        // The final sample has no successor to measure against; give it the average of the rest.
        var averageDuration = _sampleDurations.Count > 0
            ? (uint)(_sampleDurations.Sum(d => (long)d) / _sampleDurations.Count)
            : MediaTimescale / 30;
        _sampleDurations.Add(averageDuration);

        var mdatEnd = _file.Position;
        var mdatSize = mdatEnd - _mdatStart;

        _file.Position = _mdatStart;
        WriteBigEndianUInt32(checked((uint)mdatSize));
        _file.Position = mdatEnd;

        var moov = BuildMovieBox(firstSampleOffset: checked((uint)_mdatPayloadStart));
        _file.Write(moov);
    }

    private void WriteFileTypeBox()
    {
        var writer = new BoxWriter();
        using (writer.Box("ftyp"))
        {
            writer.FourCc("M4V ");   // major brand: Apple .m4v
            writer.UInt32(1);        // minor version
            writer.FourCc("M4V ");
            writer.FourCc("mp42");
            writer.FourCc("isom");
            writer.FourCc("avc1");
        }

        _file.Write(writer.ToArray());
    }

    private byte[] BuildMovieBox(uint firstSampleOffset)
    {
        var totalMediaTicks = _sampleDurations.Sum(d => (long)d);
        var movieDuration = (uint)(totalMediaTicks * MovieTimescale / MediaTimescale);

        var writer = new BoxWriter();
        using (writer.Box("moov"))
        {
            WriteMovieHeaderBox(writer, movieDuration);

            using (writer.Box("trak"))
            {
                WriteTrackHeaderBox(writer, movieDuration);

                using (writer.Box("mdia"))
                {
                    WriteMediaHeaderBox(writer, (uint)totalMediaTicks);
                    WriteHandlerBox(writer);

                    using (writer.Box("minf"))
                    {
                        WriteVideoMediaHeaderBox(writer);
                        WriteDataInformationBox(writer);
                        WriteSampleTableBox(writer, firstSampleOffset);
                    }
                }
            }
        }

        return writer.ToArray();
    }

    private static void WriteMovieHeaderBox(BoxWriter writer, uint duration)
    {
        using (writer.Box("mvhd"))
        {
            writer.FullBoxHeader(0, 0);
            writer.UInt32(0);              // creation time
            writer.UInt32(0);              // modification time
            writer.UInt32(MovieTimescale);
            writer.UInt32(duration);
            writer.UInt32(0x0001_0000);    // rate 1.0
            writer.UInt16(0x0100);         // volume 1.0
            writer.Zeros(2 + 8);           // reserved
            WriteUnityMatrix(writer);
            writer.Zeros(24);              // pre_defined
            writer.UInt32(2);              // next track id
        }
    }

    private void WriteTrackHeaderBox(BoxWriter writer, uint duration)
    {
        using (writer.Box("tkhd"))
        {
            // flags 0x7 = enabled | in movie | in preview
            writer.FullBoxHeader(0, 0x7);
            writer.UInt32(0);          // creation time
            writer.UInt32(0);          // modification time
            writer.UInt32(1);          // track id
            writer.Zeros(4);           // reserved
            writer.UInt32(duration);
            writer.Zeros(8);           // reserved
            writer.UInt16(0);          // layer
            writer.UInt16(0);          // alternate group
            writer.UInt16(0);          // volume (0 for video)
            writer.Zeros(2);           // reserved
            WriteUnityMatrix(writer);
            writer.UInt32((uint)_width << 16);   // 16.16 fixed point
            writer.UInt32((uint)_height << 16);
        }
    }

    private static void WriteMediaHeaderBox(BoxWriter writer, uint duration)
    {
        using (writer.Box("mdhd"))
        {
            writer.FullBoxHeader(0, 0);
            writer.UInt32(0);          // creation time
            writer.UInt32(0);          // modification time
            writer.UInt32(MediaTimescale);
            writer.UInt32(duration);
            writer.UInt16(0x55C4);     // language 'und', packed ISO-639-2/T
            writer.UInt16(0);          // pre_defined
        }
    }

    private static void WriteHandlerBox(BoxWriter writer)
    {
        using (writer.Box("hdlr"))
        {
            writer.FullBoxHeader(0, 0);
            writer.UInt32(0);          // pre_defined
            writer.FourCc("vide");
            writer.Zeros(12);          // reserved
            writer.Bytes("VideoHandler\0"u8);
        }
    }

    private static void WriteVideoMediaHeaderBox(BoxWriter writer)
    {
        using (writer.Box("vmhd"))
        {
            writer.FullBoxHeader(0, 1);
            writer.UInt16(0);          // graphics mode: copy
            writer.Zeros(6);           // opcolor
        }
    }

    private static void WriteDataInformationBox(BoxWriter writer)
    {
        using (writer.Box("dinf"))
        {
            using (writer.Box("dref"))
            {
                writer.FullBoxHeader(0, 0);
                writer.UInt32(1);      // entry count
                using (writer.Box("url "))
                {
                    // flags bit 0 = media data is in this same file
                    writer.FullBoxHeader(0, 1);
                }
            }
        }
    }

    private void WriteSampleTableBox(BoxWriter writer, uint firstSampleOffset)
    {
        using (writer.Box("stbl"))
        {
            WriteSampleDescriptionBox(writer);
            WriteTimeToSampleBox(writer);
            WriteSyncSampleBox(writer);
            WriteSampleToChunkBox(writer);
            WriteSampleSizeBox(writer);
            WriteChunkOffsetBox(writer, firstSampleOffset);
        }
    }

    private void WriteSampleDescriptionBox(BoxWriter writer)
    {
        using (writer.Box("stsd"))
        {
            writer.FullBoxHeader(0, 0);
            writer.UInt32(1);          // entry count

            using (writer.Box("avc1"))
            {
                writer.Zeros(6);       // reserved
                writer.UInt16(1);      // data reference index
                writer.UInt16(0);      // pre_defined
                writer.UInt16(0);      // reserved
                writer.Zeros(12);      // pre_defined
                writer.UInt16((ushort)_width);
                writer.UInt16((ushort)_height);
                writer.UInt32(0x0048_0000);   // horizontal resolution 72 dpi
                writer.UInt32(0x0048_0000);   // vertical resolution 72 dpi
                writer.UInt32(0);             // reserved
                writer.UInt16(1);             // frame count

                // compressorname: a 32-byte fixed field holding a leading-length Pascal string.
                var name = "Kiln"u8;
                writer.UInt8((byte)name.Length);
                writer.Bytes(name);
                writer.Zeros(31 - name.Length);

                writer.UInt16(0x0018);        // depth: colour, no alpha
                writer.UInt16(0xFFFF);        // pre_defined = -1

                WriteAvcConfigurationBox(writer);
            }
        }
    }

    private void WriteAvcConfigurationBox(BoxWriter writer)
    {
        var sps = _sps!;
        var pps = _pps!;

        using (writer.Box("avcC"))
        {
            // AVCDecoderConfigurationRecord, ISO/IEC 14496-15 s5.2.4.1.1.
            // Bytes 1..3 of the SPS NAL are profile_idc, the constraint set flags and level_idc.
            writer.UInt8(1);           // configurationVersion
            writer.UInt8(sps[1]);      // AVCProfileIndication
            writer.UInt8(sps[2]);      // profile_compatibility
            writer.UInt8(sps[3]);      // AVCLevelIndication
            writer.UInt8(0xFF);        // 6 reserved bits | lengthSizeMinusOne = 3 (4-byte lengths)
            writer.UInt8(0xE1);        // 3 reserved bits | numOfSequenceParameterSets = 1
            writer.UInt16((ushort)sps.Length);
            writer.Bytes(sps);
            writer.UInt8(1);           // numOfPictureParameterSets
            writer.UInt16((ushort)pps.Length);
            writer.Bytes(pps);
        }
    }

    private void WriteTimeToSampleBox(BoxWriter writer)
    {
        // Run-length encode equal consecutive durations.
        var entries = new List<(uint Count, uint Delta)>();
        foreach (var duration in _sampleDurations)
        {
            if (entries.Count > 0 && entries[^1].Delta == duration)
            {
                entries[^1] = (entries[^1].Count + 1, duration);
            }
            else
            {
                entries.Add((1, duration));
            }
        }

        using (writer.Box("stts"))
        {
            writer.FullBoxHeader(0, 0);
            writer.UInt32((uint)entries.Count);
            foreach (var (count, delta) in entries)
            {
                writer.UInt32(count);
                writer.UInt32(delta);
            }
        }
    }

    private void WriteSyncSampleBox(BoxWriter writer)
    {
        // Omitting stss entirely would declare every sample a sync sample, which is wrong for P-frames.
        using (writer.Box("stss"))
        {
            writer.FullBoxHeader(0, 0);
            writer.UInt32((uint)_syncSamples.Count);
            foreach (var index in _syncSamples)
            {
                writer.UInt32(index);
            }
        }
    }

    private void WriteSampleToChunkBox(BoxWriter writer)
    {
        using (writer.Box("stsc"))
        {
            writer.FullBoxHeader(0, 0);
            writer.UInt32(1);                              // entry count
            writer.UInt32(1);                              // first chunk
            writer.UInt32((uint)_sampleSizes.Count);       // samples per chunk
            writer.UInt32(1);                              // sample description index
        }
    }

    private void WriteSampleSizeBox(BoxWriter writer)
    {
        using (writer.Box("stsz"))
        {
            writer.FullBoxHeader(0, 0);
            writer.UInt32(0);                              // 0 = sizes vary, table follows
            writer.UInt32((uint)_sampleSizes.Count);
            foreach (var size in _sampleSizes)
            {
                writer.UInt32(size);
            }
        }
    }

    private static void WriteChunkOffsetBox(BoxWriter writer, uint firstSampleOffset)
    {
        using (writer.Box("stco"))
        {
            writer.FullBoxHeader(0, 0);
            writer.UInt32(1);                              // entry count: one chunk
            writer.UInt32(firstSampleOffset);
        }
    }

    /// <summary>Writes the 3x3 unity transformation matrix used by mvhd and tkhd.</summary>
    private static void WriteUnityMatrix(BoxWriter writer)
    {
        writer.UInt32(0x0001_0000);
        writer.UInt32(0);
        writer.UInt32(0);
        writer.UInt32(0);
        writer.UInt32(0x0001_0000);
        writer.UInt32(0);
        writer.UInt32(0);
        writer.UInt32(0);
        writer.UInt32(0x4000_0000);
    }

    private void WriteBigEndianUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        buffer[0] = (byte)(value >> 24);
        buffer[1] = (byte)(value >> 16);
        buffer[2] = (byte)(value >> 8);
        buffer[3] = (byte)value;
        _file.Write(buffer);
    }
}
