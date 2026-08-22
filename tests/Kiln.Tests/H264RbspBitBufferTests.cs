using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

public sealed class H264RbspBitBufferTests
{
    [Fact]
    public void ToArray_byte_length_matches_bit_length_rounded_up_to_bytes()
    {
        var bs = new H264RbspBitBuffer();
        for (var i = 0; i < 1000; i++)
        {
            bs.WriteBit((i & 1) != 0);
        }

        bs.WriteRbspTrailingBits();
        // Capture before ToArray(): ToArray's internal Flush() pads the partial 32-bit word, which inflates
        // BitLength. The RBSP must be byte-aligned (ceil(bits/8)), not word-aligned (the old bug appended
        // up to 3 trailing zero bytes, which VideoToolbox rejected).
        var expectedLen = (bs.BitLength + 7) / 8;
        var bytes = bs.ToArray();
        bytes.Length.Should().Be(expectedLen);
    }
}
