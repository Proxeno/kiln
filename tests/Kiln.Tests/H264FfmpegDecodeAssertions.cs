using FluentAssertions;

namespace Kiln.Tests;

/// <summary>ffmpeg must not report H.264 decode failures on stderr (libav ~6.x strings).</summary>
internal static class H264FfmpegDecodeAssertions
{
    private static readonly string[] DecodeFailureSubstrings =
    [
        "error while decoding",
        "corrupt",
        "corrupt decoded frame",
        "corrupted macroblock",
        "negative number of zero coeffs",
        "total_coeff=-1",
        "cabac decode of qscale diff failed",
        "top block unavailable for requested intra",
        "left block unavailable for requested intra4x4",
    ];

    public static void AssertStderrHasNoDecodeErrors(string stderr, string because)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return;
        }

        foreach (var sub in DecodeFailureSubstrings)
        {
            stderr.Should().NotContain(sub, because);
        }
    }
}
