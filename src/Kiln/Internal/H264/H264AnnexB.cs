namespace Kiln.Internal.H264;

internal static class H264AnnexB
{
    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    /// <summary>Append one NAL unit: start code + 1-byte header + EBSP(RBSP).</summary>
    public static int AppendNal(
        Span<byte> dest,
        byte nalRefIdc,
        byte nalUnitType,
        ReadOnlySpan<byte> rbspBytes,
        Span<byte> ebspScratch)
    {
        var nri = (byte)((nalRefIdc & 3) << 5);
        var nalHeader = (byte)(nri | (nalUnitType & 0x1F));
        var ebspLen = H264RbspEmulation.WriteEbsp(ebspScratch, rbspBytes);
        var total = StartCode.Length + 1 + ebspLen;
        if (dest.Length < total)
        {
            throw new ArgumentException("destination span too small for NAL unit", nameof(dest));
        }

        StartCode.CopyTo(dest);
        dest[StartCode.Length] = nalHeader;
        ebspScratch[..ebspLen].CopyTo(dest[(StartCode.Length + 1)..]);
        return total;
    }
}
