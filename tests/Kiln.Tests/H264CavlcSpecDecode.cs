using Kiln.Internal.H264;

namespace Kiln.Tests;

/// <summary>
/// Test-only H.264 §9.2 CAVLC block decoder for <see cref="H264CavlcResidual.WriteBlockResidual"/> round-trips.
/// </summary>
internal static class H264CavlcSpecDecode
{
    private static ReadOnlySpan<byte> ZeroLeftMap =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7,
    ];

    public sealed class BitReader(byte[] data)
    {
        private int _bytePos;
        private int _bitPos;

        /// <summary>Current absolute bit offset from the start of <paramref name="data"/>.</summary>
        public int BitPosition => _bytePos * 8 + _bitPos;

        public int ReadBit()
        {
            if (_bytePos >= data.Length)
                throw new InvalidOperationException("Read past end of buffer");
            var bit = (data[_bytePos] >> (7 - _bitPos)) & 1;
            if (++_bitPos == 8)
            {
                _bitPos = 0;
                _bytePos++;
            }

            return bit;
        }

        public int ReadBits(int n)
        {
            var v = 0;
            for (var i = 0; i < n; i++)
                v = (v << 1) | ReadBit();
            return v;
        }
    }

    /// <summary>
    /// Decode one CAVLC residual block. Does not consume RBSP trailing bits after the block.
    /// </summary>
    public static short[] DecodeBlock(BitReader br, int endIdx, int nc, bool isChromaDc)
    {
        var result = new short[endIdx + 1];

        var ncIdx = isChromaDc ? 4 : H264CavlcTables.EncNcMapTable[Math.Clamp(nc, 0, 16)];
        var maxTc = isChromaDc ? 4 : 16;
        var totalCoeff = -1;
        var trailingOnes = -1;
        var accumBits = 0;
        var accumVal = 0;
        for (var bit = 0; bit < 16 && totalCoeff < 0; bit++)
        {
            accumVal = (accumVal << 1) | br.ReadBit();
            accumBits++;
            for (var tc = 0; tc <= maxTc && totalCoeff < 0; tc++)
            {
                var maxT1 = Math.Min(tc, 3);
                for (var t1 = 0; t1 <= maxT1 && totalCoeff < 0; t1++)
                {
                    var e = H264CavlcTables.VlcCoeffToken[ncIdx][tc][t1];
                    if (e[1] == accumBits && e[0] == accumVal)
                    {
                        totalCoeff = tc;
                        trailingOnes = t1;
                    }
                }
            }
        }

        if (totalCoeff < 0)
            throw new InvalidOperationException("coeff_token VLC not found");
        if (totalCoeff == 0)
            return result;

        var levels = new int[totalCoeff];
        for (var i = 0; i < trailingOnes; i++)
            levels[i] = br.ReadBit() == 0 ? 1 : -1;

        var suffixLength = totalCoeff > 10 && trailingOnes < 3 ? 1 : 0;
        for (var i = trailingOnes; i < totalCoeff; i++)
        {
            var levelPrefix = 0;
            while (br.ReadBit() == 0)
                levelPrefix++;

            var suffixSize = suffixLength;
            if (levelPrefix == 14 && suffixLength == 0)
                suffixSize = 4;
            else if (levelPrefix >= 15)
                suffixSize = 12;

            var levelSuffix = suffixSize > 0 ? br.ReadBits(suffixSize) : 0;

            int levelCode;
            if (levelPrefix >= 15)
                levelCode = (15 << suffixLength) + levelSuffix + (suffixLength == 0 ? 15 : 0);
            else if (levelPrefix == 14 && suffixLength == 0)
                levelCode = 14 + levelSuffix;
            else
                levelCode = (levelPrefix << suffixLength) + levelSuffix;

            if (i == trailingOnes && trailingOnes < 3)
                levelCode += 2;

            var level = levelCode % 2 == 0 ? levelCode / 2 + 1 : -((levelCode + 1) / 2);
            levels[i] = level;

            // Per H.264 §9.2.2: bump 0→1, then re-check magnitude at new suffixLength.
            suffixLength += suffixLength == 0 ? 1 : 0;
            var threshold = 3 << (suffixLength - 1);
            if (suffixLength < 6 && Math.Abs(level) > threshold)
                suffixLength++;
        }

        var totalZeros = 0;
        if (totalCoeff < endIdx + 1)
        {
            if (!isChromaDc)
            {
                var tzBits = 0;
                var tzVal = 0;
                var found = false;
                for (var bit = 0; bit < 16 && !found; bit++)
                {
                    tzVal = (tzVal << 1) | br.ReadBit();
                    tzBits++;
                    var maxTz = endIdx + 1 - totalCoeff;
                    for (var tz = 0; tz <= maxTz && !found; tz++)
                    {
                        var eVal = H264CavlcTables.VlcTotalZeros[totalCoeff, tz, 0];
                        var eBits = H264CavlcTables.VlcTotalZeros[totalCoeff, tz, 1];
                        if (eBits > 0 && eBits == tzBits && eVal == tzVal)
                        {
                            totalZeros = tz;
                            found = true;
                        }
                    }
                }

                if (!found)
                    throw new InvalidOperationException($"total_zeros VLC not found for totalCoeff={totalCoeff}");
            }
            else
            {
                var tzBits = 0;
                var tzVal = 0;
                var found = false;
                for (var bit = 0; bit < 8 && !found; bit++)
                {
                    tzVal = (tzVal << 1) | br.ReadBit();
                    tzBits++;
                    var maxTz = endIdx + 1 - totalCoeff;
                    for (var tz = 0; tz <= maxTz && !found; tz++)
                    {
                        var eVal = H264CavlcTables.VlcTotalZerosChromaDc[totalCoeff, tz, 0];
                        var eBits = H264CavlcTables.VlcTotalZerosChromaDc[totalCoeff, tz, 1];
                        if (eBits > 0 && eBits == tzBits && eVal == tzVal)
                        {
                            totalZeros = tz;
                            found = true;
                        }
                    }
                }

                if (!found)
                    throw new InvalidOperationException($"chroma-DC total_zeros VLC not found for totalCoeff={totalCoeff}");
            }
        }

        var run = new int[totalCoeff];
        var zerosLeft = totalZeros;
        for (var i = 0; i < totalCoeff - 1 && zerosLeft > 0; i++)
        {
            var zl = ZeroLeftMap[Math.Clamp(zerosLeft, 0, ZeroLeftMap.Length - 1)];
            var rbBits = 0;
            var rbVal = 0;
            var found = false;
            for (var bit = 0; bit < 16 && !found; bit++)
            {
                rbVal = (rbVal << 1) | br.ReadBit();
                rbBits++;
                for (var rb = 0; rb <= zerosLeft && !found; rb++)
                {
                    var eVal = H264CavlcTables.VlcRunBefore[zl, rb, 0];
                    var eBits = H264CavlcTables.VlcRunBefore[zl, rb, 1];
                    if (eBits > 0 && eBits == rbBits && eVal == rbVal)
                    {
                        run[i] = rb;
                        found = true;
                    }
                }
            }

            if (!found)
                throw new InvalidOperationException($"run_before VLC not found at i={i} zerosLeft={zerosLeft}");
            zerosLeft -= run[i];
        }

        run[totalCoeff - 1] = zerosLeft;

        var pos = totalCoeff + totalZeros - 1;
        for (var i = 0; i < totalCoeff; i++)
        {
            result[pos] = (short)levels[i];
            if (i + 1 < totalCoeff)
                pos -= run[i] + 1;
        }

        return result;
    }

    public static byte[] EncodeBlock(ReadOnlySpan<short> coeffs, int endIdx, H264ResidualKind kind, int nc)
    {
        var bs = new H264RbspBitBuffer();
        Span<short> work = stackalloc short[16];
        coeffs[..(endIdx + 1)].CopyTo(work);
        H264CavlcResidual.WriteBlockResidual(bs, work, endIdx, kind, nc);
        bs.WriteRbspTrailingBits();
        return bs.WrittenSpan().ToArray();
    }

    public static void EncodeBlockNoTrailing(H264RbspBitBuffer bs, ReadOnlySpan<short> coeffs, int endIdx, H264ResidualKind kind, int nc)
    {
        Span<short> work = stackalloc short[16];
        coeffs[..(endIdx + 1)].CopyTo(work);
        H264CavlcResidual.WriteBlockResidual(bs, work, endIdx, kind, nc);
    }

    /// <returns> RBSP-aligned bit length.</returns>
    public static int MeasureEncodedBits(ReadOnlySpan<short> coeffs, int endIdx, H264ResidualKind kind, int nc)
    {
        var bs = new H264RbspBitBuffer();
        Span<short> work = stackalloc short[16];
        coeffs[..(endIdx + 1)].CopyTo(work);
        var before = bs.BitLength;
        H264CavlcResidual.WriteBlockResidual(bs, work, endIdx, kind, nc);
        return bs.BitLength - before;
    }
}
