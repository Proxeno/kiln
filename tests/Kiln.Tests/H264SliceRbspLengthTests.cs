using FluentAssertions;
using Kiln.Internal.H264;
using Xunit;

namespace Kiln.Tests;

public sealed class H264SliceRbspLengthTests
{
    /// <summary>
    /// Guard: a full 32×32 IDR slice must carry four macroblocks. A ~20-byte RBSP indicates a truncated
    /// bitstream (ffmpeg may still report “success” while output is garbage).
    /// </summary>
    [Fact]
    public void Slice_rbsp_32x32_flat_luma_flat_chroma_is_not_truncated()
    {
        const int w = 32;
        const int h = 32;
        var enc = new H264BaselineSliceEncoder(w, h, qp: 28, chromaDcRdLambda: 0);
        enc.MacroblockCount.Should().Be(4);

        var y = new byte[w * h];
        var u = new byte[w * h / 4];
        var v = new byte[w * h / 4];
        Array.Fill(y, (byte)128);
        Array.Fill(u, (byte)60);
        Array.Fill(v, (byte)128);

        var rbsp = enc.EncodeSliceRbsp(y, w, u, v, w / 2, isIdr: true, isPslice: false, frameNum: 0, idrPicId: 0);

        // Guard: RBSP must be longer than a bare header-only stub (which would indicate truncated MB data).
        // Phase 2 may select Intra_16×16 for flat content, producing a compact but correct bitstream;
        // the old threshold of 18 bytes assumed I4×4 overhead and is now too high.
        rbsp.Length.Should().BeGreaterThan(
            4,
            $"expected non-trivial slice RBSP for 4 MBs; got {rbsp.Length} bytes (likely truncated or missing MB data).");
    }
}
