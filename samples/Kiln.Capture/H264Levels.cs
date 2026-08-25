namespace Kiln.Capture;

/// <summary>
/// Picks the lowest H.264 level whose frame-size limit covers a given picture.
/// </summary>
/// <remarks>
/// <c>H264BaselineEncoderOptions.LevelIdc</c> defaults to 3.1, whose MaxFS is 3600 macroblocks —
/// enough for 720p (3600) but not for 1080p (8160 once padded to the macroblock grid). The encoder
/// validates this and throws, so a caller recording at whatever size a camera offers has to choose
/// the level itself. Values are MaxFS from ITU-T H.264 Table A-1.
/// </remarks>
internal static class H264Levels
{
    private static readonly (byte LevelIdc, int MaxFrameSizeInMbs)[] Levels =
    [
        (10, 99),
        (11, 396),
        (12, 396),
        (13, 396),
        (20, 396),
        (21, 792),
        (22, 1620),
        (30, 1620),
        (31, 3600),
        (32, 5120),
        (40, 8192),
        (41, 8192),
        (42, 8704),
        (50, 22080),
        (51, 36864),
        (52, 36864),
    ];

    /// <summary>Returns the lowest level_idc that admits a picture of the given size.</summary>
    internal static byte ForFrameSize(int width, int height)
    {
        // Levels constrain the coded picture, which is padded up to whole macroblocks.
        var mbs = ((width + 15) / 16) * ((height + 15) / 16);

        foreach (var (levelIdc, maxFrameSizeInMbs) in Levels)
        {
            if (mbs <= maxFrameSizeInMbs)
            {
                return levelIdc;
            }
        }

        throw new InvalidOperationException(
            $"{width}x{height} is {mbs} macroblocks, beyond the largest level this sample knows about.");
    }
}
