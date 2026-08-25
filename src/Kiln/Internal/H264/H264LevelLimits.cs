namespace Kiln.Internal.H264;

/// <summary>
/// Annex A (Table A-1) level limits for the H.264 levels that Kiln may signal.
/// Only the frame-size (MaxFS) constraint is enforced here; MaxMBPS, MaxBR, MaxCPB
/// and HRD conformance are documented as intentionally not enforced (no VUI HRD
/// present — see <c>24-annex-e-vui-hrd-presence.md</c>).
/// </summary>
/// <remarks>
/// Validation is best-effort: if <c>levelIdc</c> is unknown to this table, the check
/// is skipped (logged implicitly by the absence of an exception). This preserves
/// forward-compatibility with future levels without breaking the encoder.
/// </remarks>
internal static class H264LevelLimits
{
    // level_idc value → maximum frame size in macroblocks (MaxFS), Annex A Table A-1.
    // level_idc is the raw 8-bit value written to the SPS (10 = L1.0, 11 = L1.1, …).
    private static readonly (byte LevelIdc, int MaxFs)[] Table =
    [
        (10, 99),    // Level 1.0
        (11, 396),   // Level 1.1
        (12, 396),   // Level 1.2
        (13, 396),   // Level 1.3
        (20, 396),   // Level 2.0
        (21, 792),   // Level 2.1
        (22, 1620),  // Level 2.2
        (30, 1620),  // Level 3.0
        (31, 3600),  // Level 3.1 — first level supporting 1280×720 (3600 MBs)
        (32, 5120),  // Level 3.2
        (40, 8192),  // Level 4.0
        (41, 8192),  // Level 4.1
        (42, 8704),  // Level 4.2
        (50, 22080), // Level 5.0
        (51, 36864), // Level 5.1
        (52, 36864), // Level 5.2
    ];

    /// <summary>
    /// Validates that <paramref name="mbW"/> × <paramref name="mbH"/> (total macroblocks) does
    /// not exceed the <c>MaxFS</c> limit for <paramref name="levelIdc"/> per Annex A Table A-1.
    /// </summary>
    /// <param name="levelIdc">Raw SPS <c>level_idc</c> byte (e.g. 0x1F = 31 for Level 3.1).</param>
    /// <param name="mbW">Picture width in macroblocks.</param>
    /// <param name="mbH">Picture height in macroblocks.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the frame size exceeds the advertised level's <c>MaxFS</c>.
    /// When <paramref name="levelIdc"/> is not in the table the check is skipped silently.
    /// </exception>
    public static void ValidateFrameSize(byte levelIdc, int mbW, int mbH)
    {
        var totalMbs = mbW * mbH;
        foreach (var (lvl, maxFs) in Table)
        {
            if (lvl == levelIdc)
            {
                if (totalMbs > maxFs)
                {
                    var minimumLevel = MinimumLevelForFrameSize(mbW, mbH);
                    var remedy = minimumLevel != 0
                        ? $"Set level_idc {minimumLevel} (the lowest level whose MaxFS admits this frame size) or reduce resolution."
                        : "No level in Annex A Table A-1 admits this frame size; reduce resolution.";
                    throw new ArgumentException(
                        $"Frame size {mbW}×{mbH} = {totalMbs} macroblocks — counted on the padded coded picture, " +
                        "whose dimensions are the display dimensions rounded up to multiples of 16 — " +
                        $"exceeds MaxFS={maxFs} for level_idc {levelIdc} (H.264 Annex A Table A-1). {remedy}",
                        nameof(levelIdc));
                }

                return;
            }
        }

        // Unknown level: skip silently (forward-compatibility).
    }

    /// <summary>
    /// Lowest <c>level_idc</c> whose <c>MaxFS</c> (Annex A Table A-1) admits a
    /// <paramref name="mbW"/> × <paramref name="mbH"/> macroblock picture, or 0 when no level does.
    /// The table is ordered by ascending level, so the first fit is the minimum.
    /// </summary>
    /// <param name="mbW">Coded (padded) picture width in macroblocks.</param>
    /// <param name="mbH">Coded (padded) picture height in macroblocks.</param>
    public static byte MinimumLevelForFrameSize(int mbW, int mbH)
    {
        var totalMbs = mbW * mbH;
        foreach (var (lvl, maxFs) in Table)
        {
            if (totalMbs <= maxFs)
            {
                return lvl;
            }
        }

        return 0;
    }

    /// <summary>
    /// Floor for automatically selected levels: Level 3.1, the historical Kiln default. Auto
    /// selection never signals below this — a higher-than-necessary <c>level_idc</c> is always
    /// conformant (Annex A level limits are monotone), and only frame size (MaxFS) is validated
    /// here; MaxMBPS/MaxBR are not enforced (no VUI HRD), so under-signalling a tiny level for a
    /// small picture could still overstate what the stream honours. Keeping the 3.1 floor also
    /// leaves every existing ≤720p default-options stream byte-identical.
    /// </summary>
    public const byte AutoLevelFloorIdc = 31;

    /// <summary>
    /// Level to signal when the caller left <c>LevelIdc = 0</c> (auto): the lowest Annex A
    /// Table A-1 level whose <c>MaxFS</c> admits the coded picture, floored at
    /// <see cref="AutoLevelFloorIdc"/>.
    /// </summary>
    /// <param name="mbW">Coded (padded) picture width in macroblocks.</param>
    /// <param name="mbH">Coded (padded) picture height in macroblocks.</param>
    /// <exception cref="ArgumentException">No level in Table A-1 admits this frame size.</exception>
    public static byte AutoLevelForFrameSize(int mbW, int mbH)
    {
        var minimumLevel = MinimumLevelForFrameSize(mbW, mbH);
        if (minimumLevel == 0)
        {
            throw new ArgumentException(
                $"Frame size {mbW}×{mbH} = {mbW * mbH} macroblocks — counted on the padded coded picture, " +
                "whose dimensions are the display dimensions rounded up to multiples of 16 — exceeds " +
                "MaxFS for every level in H.264 Annex A Table A-1 (Level 5.2, MaxFS=36864). Reduce resolution.");
        }

        return Math.Max(AutoLevelFloorIdc, minimumLevel);
    }
}
