namespace Kiln.Internal.H264;

/// <summary>H.264 RBSP byte stream — emulation prevention (EBSP) for Annex B.</summary>
internal static class H264RbspEmulation
{
    /// <summary>Emitted size ≤ <paramref name="source"/> + (source/3) + 1.</summary>
    public static int GetEmulationPreventionBufferSize(int sourceLength) =>
        sourceLength + Math.Max(1, sourceLength / 3) + 8;

    /// <summary>Write RBSP into <paramref name="dest"/> applying emulation prevention (zeros after two zeros).</summary>
    /// <returns>Bytes written.</returns>
    public static int WriteEbsp(Span<byte> dest, ReadOnlySpan<byte> rbsp)
    {
        if (dest.Length < GetEmulationPreventionBufferSize(rbsp.Length))
        {
            throw new ArgumentException("destination span too small for EBSP", nameof(dest));
        }

        var zeroRun = 0;
        var pos = 0;
        for (var i = 0; i < rbsp.Length; i++)
        {
            var b = rbsp[i];
            if (zeroRun >= 2 && b is <= 3)
            {
                dest[pos++] = 3;
                zeroRun = 0;
            }

            dest[pos++] = b;
            zeroRun = b == 0 ? zeroRun + 1 : 0;
        }

        return pos;
    }
}
