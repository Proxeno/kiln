namespace Kiln.Capture.Mp4;

/// <summary>
/// Splits an Annex B access unit into its NAL units.
/// </summary>
/// <remarks>
/// Kiln emits 4-byte start codes and already applies emulation prevention, so the payload bytes
/// are EBSP — exactly what both an <c>avcC</c> parameter set record and MP4 sample data want.
/// Converting to MP4 is therefore a pure start-code to 4-byte-length rewrite with no re-escaping.
/// The walker accepts 3-byte start codes too, so it stays correct against any conforming producer.
/// </remarks>
internal static class AnnexBReader
{
    internal const byte NalTypeIdrSlice = 5;
    internal const byte NalTypeSps = 7;
    internal const byte NalTypePps = 8;
    internal const byte NalTypeAccessUnitDelimiter = 9;

    /// <summary>A single NAL unit, excluding its start code.</summary>
    internal readonly record struct Nal(int Offset, int Length, byte Type);

    /// <summary>
    /// Appends every NAL unit found in <paramref name="annexB"/> to <paramref name="into"/>.
    /// Offsets point at the NAL header byte, so the start code is already excluded.
    /// </summary>
    internal static void Split(ReadOnlySpan<byte> annexB, List<Nal> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        var pos = FindNextStart(annexB, 0);
        while (pos >= 0)
        {
            TryStartCode(annexB, pos, out var startCodeLength);
            var headerIndex = pos + startCodeLength;
            if (headerIndex >= annexB.Length)
            {
                break;
            }

            var nextStart = FindNextStart(annexB, headerIndex + 1);
            var end = nextStart < 0 ? annexB.Length : nextStart;

            into.Add(new Nal(headerIndex, end - headerIndex, (byte)(annexB[headerIndex] & 0x1F)));
            pos = nextStart;
        }
    }

    private static bool TryStartCode(ReadOnlySpan<byte> b, int i, out int codeLength)
    {
        codeLength = 0;
        if (i + 4 <= b.Length && b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1)
        {
            codeLength = 4;
            return true;
        }

        if (i + 3 <= b.Length && b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1)
        {
            codeLength = 3;
            return true;
        }

        return false;
    }

    private static int FindNextStart(ReadOnlySpan<byte> b, int fromInclusive)
    {
        for (var i = Math.Max(fromInclusive, 0); i < b.Length; i++)
        {
            if (TryStartCode(b, i, out _))
            {
                return i;
            }
        }

        return -1;
    }
}
