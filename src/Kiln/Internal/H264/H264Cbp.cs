namespace Kiln.Internal.H264;

/// <summary>
/// Maps 6-bit coded_block_pattern values to the Exp-Golomb code number (ue(v)) for both the
/// Intra_4x4 and Inter macroblock prediction modes.
/// </summary>
/// <remarks>
/// The two forward tables below are the ITU-T H.264 (ISO/IEC 14496-10) Table 9-4 assignment of
/// codeNum to values of coded_block_pattern, for ChromaArrayType equal to 1 or 2 — the
/// "Intra_4x4, Intra_8x8" column and the "Inter" column respectively. Per clause 9.1.2 the
/// syntax element coded_block_pattern is mapped from the parsed codeNum through that table, so an
/// encoder writes ue(v) of the codeNum at which the desired coded_block_pattern appears; the
/// inverse permutations built here perform exactly that lookup.
/// </remarks>
internal static class H264Cbp
{
    /// <summary>
    /// ITU-T H.264 Table 9-4, Intra_4x4 column (ChromaArrayType 1 or 2): codeNum → coded_block_pattern.
    /// </summary>
    private static ReadOnlySpan<byte> CodeNumToCbpIntra4x4 =>
    [
        47, 31, 15, 0, 23, 27, 29, 30, 7, 11, 13, 14, 39, 43, 45, 46,
        16, 3, 5, 10, 12, 19, 21, 26, 28, 35, 37, 42, 44, 1, 2, 4,
        8, 17, 18, 20, 24, 6, 9, 22, 25, 32, 33, 34, 36, 40, 38, 41,
    ];

    /// <summary>
    /// ITU-T H.264 Table 9-4, Inter column (ChromaArrayType 1 or 2): codeNum → coded_block_pattern.
    /// Distinct from the Intra_4x4 column; coded_block_pattern 0 sits at codeNum 0 (a single '1' bit),
    /// which is what makes an all-zero inter macroblock cheap to signal.
    /// </summary>
    private static ReadOnlySpan<byte> CodeNumToCbpInter =>
    [
        0, 16, 1, 2, 4, 8, 32, 3, 5, 10, 12, 15, 47, 7, 11, 13,
        14, 6, 9, 31, 35, 37, 42, 44, 33, 34, 36, 40, 39, 43, 45, 46,
        17, 18, 20, 24, 19, 21, 26, 28, 23, 27, 29, 30, 22, 25, 38, 41,
    ];

    private static readonly int[] IntraCbpToCodeNum = BuildInverse(CodeNumToCbpIntra4x4);
    private static readonly int[] InterCbpToCodeNum = BuildInverse(CodeNumToCbpInter);

    private static int[] BuildInverse(ReadOnlySpan<byte> table)
    {
        var inv = new int[48];
        Array.Fill(inv, 0);
        for (var codeNum = 0; codeNum < 48; codeNum++)
        {
            var cbp = table[codeNum];
            inv[cbp] = codeNum;
        }

        return inv;
    }

    public static int IntraCbpCodeNum(byte intraCbp6) =>
        IntraCbpToCodeNum[intraCbp6 > 47 ? 0 : intraCbp6];

    /// <summary>Inverse of <see cref="CodeNumToCbpInter"/>; used by the P-slice MB writer to encode coded_block_pattern (ITU-T H.264 clause 7.3.5.1).</summary>
    public static int InterCbpCodeNum(byte interCbp6) =>
        InterCbpToCodeNum[interCbp6 > 47 ? 0 : interCbp6];
}
