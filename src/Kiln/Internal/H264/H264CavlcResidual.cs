namespace Kiln.Internal.H264;

internal enum H264ResidualKind : byte
{
    Luma4X4 = 0,
    ChromaDc = 1,
    /// <summary>Chroma 4×4 AC (15 coefficients, DC side-coded); same CAVLC tables as luma.</summary>
    ChromaAc = 2,
    /// <summary>
    /// Luma 4×4 DC block of an Intra_16×16 macroblock (16 coefficients, post-Hadamard) per H.264 9.2.1
    /// nC selection treating it as a luma block. Senior stub for Junior-D-cavlc; the existing else-branch
    /// in <see cref="H264CavlcResidual.WriteBlockResidual"/> routes this kind through the luma tables, which
    /// is the correct table choice for Intra_16×16 luma DC; the junior verifies and tightens with goldens.
    /// </summary>
    Luma16x16Dc = 3,
    /// <summary>
    /// Luma 4×4 AC block of an Intra_16×16 macroblock (15 coefficients; coeff 0 carried by the DC block).
    /// Caller layout follows the existing <see cref="ChromaAc"/> convention — the 15 AC coefficients are
    /// packed into a 15-element span starting at index 0, with <c>endIdx = 14</c>. Same luma CAVLC tables
    /// as <see cref="Luma4X4"/> per H.264 9.2.1.
    /// </summary>
    Luma16x16Ac = 4,
}

/// <summary>4x4 and chroma-DC CAVLC residual writer (baseline).</summary>
internal static class H264CavlcResidual
{
    private static ReadOnlySpan<byte> ZeroLeftMap =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7,
    ];

    /// <summary>Total coefficient count for CAVLC (same as used when writing coeff_token).</summary>
    public static int TotalCoefficients(ReadOnlySpan<short> coeffLevel, int lastIndex)
    {
        Span<short> level = stackalloc short[16];
        Span<byte> run = stackalloc byte[16];
        CavlcParamCal(coeffLevel, lastIndex, level, run, out var totalCoeffs, out _);
        return totalCoeffs;
    }

    public static void CavlcParamCal(ReadOnlySpan<short> coeffLevel, int lastIndex, Span<short> level, Span<byte> run, out int totalCoeffs, out int totalZeros)
    {
        totalZeros = 0;
        totalCoeffs = 0;
        var iLast = lastIndex;
        while (iLast >= 0 && coeffLevel[iLast] == 0)
        {
            iLast--;
        }

        while (iLast >= 0)
        {
            var countZero = 0;
            level[totalCoeffs] = coeffLevel[iLast];
            iLast--;
            while (iLast >= 0 && coeffLevel[iLast] == 0)
            {
                countZero++;
                iLast--;
            }

            totalZeros += countZero;
            run[totalCoeffs++] = (byte)countZero;
        }
    }

    /// <summary>
    /// Chroma DC only: coeff_token through run_before, after <see cref="CavlcParamCal"/>.
    /// When <paramref name="bs"/> is null, returns the accumulated bit count (for <see cref="CountChromaDcResidualBits"/>).
    /// </summary>
    private static int EmitChromaDcResidualBody(
        H264RbspBitBuffer? bs,
        Span<short> level,
        Span<byte> run,
        int totalCoeffs,
        int totalZeros,
        int endIdx)
    {
        var trailing = 0;
        uint sign = 0;
        var countT = totalCoeffs > 3 ? 3 : totalCoeffs;
        for (var i = 0; i < countT; i++)
        {
            if (Math.Abs(level[i]) == 1)
            {
                trailing++;
                sign <<= 1;
                if (level[i] < 0)
                {
                    sign |= 1;
                }
            }
            else
            {
                break;
            }
        }

        var coeffTok = H264CavlcTables.VlcCoeffToken[4][totalCoeffs][trailing];
        var nBits = (int)coeffTok[1];
        var iVal = (uint)coeffTok[0];
        var acc = 0;
        if (totalCoeffs == 0)
        {
            if (bs != null)
            {
                bs.WriteBits(nBits, iVal);
            }
            else
            {
                acc = nBits;
            }

            return acc;
        }

        nBits += trailing;
        iVal = (iVal << trailing) + sign;
        if (bs != null)
        {
            bs.WriteBits(nBits, iVal);
        }
        else
        {
            acc += nBits;
        }

        var suffixLength = totalCoeffs > 10 && trailing < 3 ? 1 : 0;
        for (var i = trailing; i < totalCoeffs; i++)
        {
            var coeffVal = level[i];
            var iLevelCode = (coeffVal - 1) << 1;
            var uiSign = (uint)(iLevelCode >> 31);
            iLevelCode = (iLevelCode ^ (int)uiSign) + ((int)uiSign << 1);
            if (i == trailing && trailing < 3)
            {
                iLevelCode -= 2;
            }

            var iLevelPrefix = iLevelCode >> suffixLength;
            var iLevelSuffixSize = suffixLength;
            var iLevelSuffix = iLevelCode - (iLevelPrefix << suffixLength);

            if (iLevelPrefix is >= 14 and < 30 && suffixLength == 0)
            {
                iLevelPrefix = 14;
                iLevelSuffix = iLevelCode - iLevelPrefix;
                iLevelSuffixSize = 4;
            }
            else if (iLevelPrefix >= 15)
            {
                iLevelPrefix = 15;
                iLevelSuffix = iLevelCode - (iLevelPrefix << suffixLength);
                if (iLevelSuffix >> 11 != 0)
                {
                    throw new InvalidOperationException("CAVLC level overflow");
                }

                if (suffixLength == 0)
                {
                    iLevelSuffix -= 15;
                }

                iLevelSuffixSize = 12;
            }

            nBits = iLevelPrefix + 1 + iLevelSuffixSize;
            iVal = (uint)((1 << iLevelSuffixSize) | iLevelSuffix);
            if (bs != null)
            {
                bs.WriteBits(nBits, iVal);
            }
            else
            {
                acc += nBits;
            }

            suffixLength += suffixLength == 0 ? 1 : 0;
            var threshold = 3 << (suffixLength - 1);
            if (suffixLength < 6 && (coeffVal > threshold || coeffVal < -threshold))
            {
                suffixLength++;
            }
        }

        if (totalCoeffs < endIdx + 1)
        {
            var tz = H264CavlcTables.VlcTotalZerosChromaDc[totalCoeffs, totalZeros, 0];
            var tzN = H264CavlcTables.VlcTotalZerosChromaDc[totalCoeffs, totalZeros, 1];
            if (bs != null)
            {
                bs.WriteBits(tzN, tz);
            }
            else
            {
                acc += tzN;
            }
        }

        var zerosLeft = totalZeros;
        for (var i = 0; i + 1 < totalCoeffs && zerosLeft > 0; i++)
        {
            var zl = ZeroLeftMap[zerosLeft];
            var rb = H264CavlcTables.VlcRunBefore[zl, run[i], 0];
            var rbN = H264CavlcTables.VlcRunBefore[zl, run[i], 1];
            if (bs != null)
            {
                bs.WriteBits(rbN, rb);
            }
            else
            {
                acc += rbN;
            }

            zerosLeft -= run[i];
        }

        return acc;
    }

    public static void WriteBlockResidual(
        H264RbspBitBuffer bs,
        Span<short> coeffLevel,
        int endIdx,
        H264ResidualKind kind,
        int nc)
    {
        // Fast-path: no non-zero coeffs (common for skip groups / quantized-flat blocks).
        var hasNonZero = false;
        for (var z = 0; z <= endIdx; z++)
        {
            if (coeffLevel[z] != 0)
            {
                hasNonZero = true;
                break;
            }
        }

        ReadOnlySpan<byte> coeffTok0;
        if (kind == H264ResidualKind.ChromaDc)
        {
            coeffTok0 = H264CavlcTables.VlcCoeffToken[4][0][0];
        }
        else
        {
            var ncIdxZero = H264CavlcTables.EncNcMapTable[Math.Clamp(nc, 0, 16)];
            coeffTok0 = H264CavlcTables.VlcCoeffToken[ncIdxZero][0][0];
        }

        if (!hasNonZero)
        {
            var nBitsZero = (int)coeffTok0[1];
            var iValZero = (uint)coeffTok0[0];
            bs.WriteBits(nBitsZero, iValZero);
            return;
        }

        Span<short> level = stackalloc short[16];
        Span<byte> run = stackalloc byte[16];

        CavlcParamCal(coeffLevel, endIdx, level, run, out var totalCoeffs, out var totalZeros);

        if (kind == H264ResidualKind.ChromaDc)
        {
            EmitChromaDcResidualBody(bs, level, run, totalCoeffs, totalZeros, endIdx);
            return;
        }

        var trailing = 0;
        uint sign = 0;
        var countT = totalCoeffs > 3 ? 3 : totalCoeffs;
        for (var i = 0; i < countT; i++)
        {
            if (Math.Abs(level[i]) == 1)
            {
                trailing++;
                sign <<= 1;
                if (level[i] < 0)
                {
                    sign |= 1;
                }
            }
            else
            {
                break;
            }
        }

        // Luma 4×4, Luma16x16Dc, Luma16x16Ac, and chroma AC all route through the luma coeff_token VLC tables
        // per H.264 9.2.1; nC is derived from block neighbours and mapped to a VLC table index.
        var ncIdx = H264CavlcTables.EncNcMapTable[Math.Clamp(nc, 0, 16)];
        var coeffTok = H264CavlcTables.VlcCoeffToken[ncIdx][totalCoeffs][trailing];

        var nBits = (int)coeffTok[1];
        var iVal = (uint)coeffTok[0];
        if (totalCoeffs == 0)
        {
            bs.WriteBits(nBits, iVal);
            return;
        }

        nBits += trailing;
        iVal = (iVal << trailing) + sign;
        bs.WriteBits(nBits, iVal);

        var suffixLength = totalCoeffs > 10 && trailing < 3 ? 1 : 0;
        for (var i = trailing; i < totalCoeffs; i++)
        {
            var coeffVal = level[i];
            var iLevelCode = (coeffVal - 1) << 1;
            var uiSign = (uint)(iLevelCode >> 31);
            iLevelCode = (iLevelCode ^ (int)uiSign) + ((int)uiSign << 1);
            if (i == trailing && trailing < 3)
            {
                iLevelCode -= 2;
            }

            var iLevelPrefix = iLevelCode >> suffixLength;
            var iLevelSuffixSize = suffixLength;
            var iLevelSuffix = iLevelCode - (iLevelPrefix << suffixLength);

            if (iLevelPrefix is >= 14 and < 30 && suffixLength == 0)
            {
                iLevelPrefix = 14;
                iLevelSuffix = iLevelCode - iLevelPrefix;
                iLevelSuffixSize = 4;
            }
            else if (iLevelPrefix >= 15)
            {
                iLevelPrefix = 15;
                iLevelSuffix = iLevelCode - (iLevelPrefix << suffixLength);
                if (iLevelSuffix >> 11 != 0)
                {
                    throw new InvalidOperationException("CAVLC level overflow");
                }

                if (suffixLength == 0)
                {
                    iLevelSuffix -= 15;
                }

                iLevelSuffixSize = 12;
            }

            nBits = iLevelPrefix + 1 + iLevelSuffixSize;
            iVal = (uint)((1 << iLevelSuffixSize) | iLevelSuffix);
            bs.WriteBits(nBits, iVal);

            // ITU-T H.264 clause 9.2.2 (level information) states the update as two sequential rules:
            // when suffixLength is 0 it is first set to 1, and only then is |level| compared against
            // 3 << (suffixLength - 1) — i.e. the threshold is evaluated at the NEW suffixLength.
            suffixLength += suffixLength == 0 ? 1 : 0;
            var threshold = 3 << (suffixLength - 1);
            if (suffixLength < 6 && (coeffVal > threshold || coeffVal < -threshold))
            {
                suffixLength++;
            }
        }

        if (totalCoeffs < endIdx + 1)
        {
            var tz = H264CavlcTables.VlcTotalZeros[totalCoeffs, totalZeros, 0];
            var tzN = H264CavlcTables.VlcTotalZeros[totalCoeffs, totalZeros, 1];
            bs.WriteBits(tzN, tz);
        }

        var zerosLeft = totalZeros;
        for (var i = 0; i + 1 < totalCoeffs && zerosLeft > 0; i++)
        {
            var zl = ZeroLeftMap[zerosLeft];
            var rb = H264CavlcTables.VlcRunBefore[zl, run[i], 0];
            var rbN = H264CavlcTables.VlcRunBefore[zl, run[i], 1];
            bs.WriteBits(rbN, rb);
            zerosLeft -= run[i];
        }
    }

    /// <summary>Exact CAVLC bit count for a chroma-DC block (same layout as <see cref="WriteBlockResidual"/> for <see cref="H264ResidualKind.ChromaDc"/>).</summary>
    public static int CountChromaDcResidualBits(
        ReadOnlySpan<short> coeffLevel,
        Span<short> level,
        Span<byte> run)
    {
        const int endIdx = 3;
        var hasNonZero = false;
        for (var z = 0; z <= endIdx; z++)
        {
            if (coeffLevel[z] != 0)
            {
                hasNonZero = true;
                break;
            }
        }

        if (!hasNonZero)
        {
            var coeffTok0 = H264CavlcTables.VlcCoeffToken[4][0][0];
            return (int)coeffTok0[1];
        }

        CavlcParamCal(coeffLevel, endIdx, level, run, out var totalCoeffs, out var totalZeros);
        return EmitChromaDcResidualBody(null, level, run, totalCoeffs, totalZeros, endIdx);
    }

    /// <summary>Per-coefficient trace for <see cref="WriteBlockResidual"/> level-encoding loop.</summary>
    internal sealed record CavlcCoeffTrace(
        int CoeffIdx,
        int CoeffVal,
        int LevelCode,
        int LevelPrefix,
        int LevelSuffix,
        int SuffixSize,
        int NBits,
        int SuffixLengthBefore,
        int SuffixLengthAfter);

    /// <summary>
    /// Captures suffixLength evolution for each coded level — mirrors <see cref="WriteBlockResidual"/>
    /// (diagnostics / pinning tests).
    /// </summary>
    internal static void TraceBlockResidualLevelSteps(ReadOnlySpan<short> coeffLevel, int endIdx, ICollection<CavlcCoeffTrace> traces)
    {
        traces.Clear();
        var hasNonZero = false;
        for (var z = 0; z <= endIdx; z++)
        {
            if (coeffLevel[z] != 0)
            {
                hasNonZero = true;
                break;
            }
        }

        if (!hasNonZero)
        {
            return;
        }

        Span<short> level = stackalloc short[16];
        Span<byte> run = stackalloc byte[16];

        CavlcParamCal(coeffLevel, endIdx, level, run, out var totalCoeffs, out _);
        var trailing = 0;
        var countT = totalCoeffs > 3 ? 3 : totalCoeffs;
        for (var i = 0; i < countT; i++)
        {
            if (Math.Abs(level[i]) == 1)
            {
                trailing++;
            }
            else
            {
                break;
            }
        }

        if (totalCoeffs == 0)
        {
            return;
        }

        var suffixLength = totalCoeffs > 10 && trailing < 3 ? 1 : 0;
        for (var i = trailing; i < totalCoeffs; i++)
        {
            var coeffVal = level[i];
            var iLevelCode = (coeffVal - 1) << 1;
            var uiSign = (uint)(iLevelCode >> 31);
            iLevelCode = (iLevelCode ^ (int)uiSign) + ((int)uiSign << 1);
            if (i == trailing && trailing < 3)
            {
                iLevelCode -= 2;
            }

            var iLevelPrefix = iLevelCode >> suffixLength;
            var iLevelSuffixSize = suffixLength;
            var iLevelSuffix = iLevelCode - (iLevelPrefix << suffixLength);

            if (iLevelPrefix is >= 14 and < 30 && suffixLength == 0)
            {
                iLevelPrefix = 14;
                iLevelSuffix = iLevelCode - iLevelPrefix;
                iLevelSuffixSize = 4;
            }
            else if (iLevelPrefix >= 15)
            {
                iLevelPrefix = 15;
                iLevelSuffix = iLevelCode - (iLevelPrefix << suffixLength);
                if (iLevelSuffix >> 11 != 0)
                {
                    throw new InvalidOperationException("CAVLC level overflow");
                }

                if (suffixLength == 0)
                {
                    iLevelSuffix -= 15;
                }

                iLevelSuffixSize = 12;
            }

            var nBits = iLevelPrefix + 1 + iLevelSuffixSize;
            var suffixBefore = suffixLength;

            suffixLength += suffixLength == 0 ? 1 : 0;
            var threshold = 3 << (suffixLength - 1);
            if (suffixLength < 6 && (coeffVal > threshold || coeffVal < -threshold))
            {
                suffixLength++;
            }

            traces.Add(new CavlcCoeffTrace(
                i,
                coeffVal,
                iLevelCode,
                iLevelPrefix,
                iLevelSuffix,
                iLevelSuffixSize,
                nBits,
                suffixBefore,
                suffixLength));
        }
    }
}
