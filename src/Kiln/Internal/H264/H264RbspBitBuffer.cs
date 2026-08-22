using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Kiln.Internal.H264;

/// <summary>
/// MSB-first bit writer for RBSP data. Bits accumulate into a 32-bit register that is flushed
/// big-endian into the backing array whenever it fills, which keeps the common case (a handful of bits
/// per syntax element) to a shift and an OR. Reusable via <see cref="Reset"/>; the backing array grows
/// on demand.
/// </summary>
internal sealed class H264RbspBitBuffer
{
    private byte[] _buffer;
    private int _length;
    private uint _curBits;
    private int _leftBits = 32;

    public H264RbspBitBuffer(int initialCapacity = 4096)
    {
        if (initialCapacity < 4)
        {
            initialCapacity = 4;
        }

        _buffer = new byte[initialCapacity];
    }

    public int BitLength => checked((_length * 8) + (32 - _leftBits));

    /// <summary>Reset state so the buffer can be reused for the next slice/RBSP without reallocating.</summary>
    public void Reset()
    {
        _length = 0;
        _curBits = 0;
        _leftBits = 32;
    }

    /// <summary>Write <paramref name="n"/> low bits of <paramref name="v"/> (MSB of those n bits first).</summary>
    public void WriteBits(int n, uint v)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, 31);
        if (n == 0)
        {
            return;
        }

        if (n < _leftBits)
        {
            _curBits = (_curBits << n) | (v & ((1u << n) - 1u));
            _leftBits -= n;
        }
        else
        {
            var nRem = n - _leftBits;
            // When nRem == 0, the full n-bit value fills the current word (mask was wrongly 0, dropping bits).
            var mask = nRem == 0 ? (v & ((1u << _leftBits) - 1u)) : (v >> nRem);
            _curBits = (_curBits << _leftBits) | mask;
            FlushWord();
            _curBits = nRem == 0 ? 0u : (v & ((1u << nRem) - 1u));
            _leftBits = nRem == 0 ? 32 : 32 - nRem;
        }
    }

    public void WriteBit(bool bit) => WriteBits(1, bit ? 1u : 0u);

    public void WriteUe(uint codeNum)
    {
        var coded = codeNum + 1;
        var bits = 32 - BitOperations.LeadingZeroCount(coded);
        var leadingZeroBits = bits - 1;
        WriteBits(leadingZeroBits, 0u);
        WriteBits(bits, coded);
    }

    public void WriteSe(int value) => WriteUe(value <= 0 ? (uint)(-value * 2) : (uint)(value * 2 - 1));

    /// <summary>RBSP trailing bits: single 1 bit then zeros until byte-aligned.</summary>
    public void WriteRbspTrailingBits()
    {
        WriteBit(true); // rbsp_stop_one_bit
        // Pad with zeros to the next BYTE boundary (ITU-T H.264 clause 7.3.2.11, rbsp_trailing_bits)
        // — NOT to the 32-bit accumulator boundary. Padding out to the word boundary would append up
        // to 3 extra zero bytes per NAL unit: some decoders ignore them, but stricter hardware parsers
        // (notably VideoToolbox) reject the slice, which showed up as tvOS "stop motion" playback with
        // only IDR frames decoded.
        var bitsUsedInWord = 32 - _leftBits;
        var padToByte = (8 - (bitsUsedInWord & 7)) & 7;
        if (padToByte > 0)
        {
            WriteBits(padToByte, 0u);
        }
    }

    /// <summary>Byte-aligned RBSP length (valid after <see cref="WriteRbspTrailingBits"/>); excludes 32-bit word padding.</summary>
    public int ByteLength => (BitLength + 7) / 8;

    /// <summary>Flush partial word padding with zeros (post RBSP trailing).</summary>
    public void Flush()
    {
        if (_leftBits is < 32 and > 0)
        {
            WriteBits(_leftBits, 0u);
        }
    }

    /// <summary>View of the written RBSP bytes. Valid until the next mutation. Calls <see cref="Flush"/>.</summary>
    public ReadOnlySpan<byte> WrittenSpan()
    {
        var byteLen = ByteLength; // capture before Flush pads the partial word to 4 bytes
        Flush();
        return _buffer.AsSpan(0, byteLen);
    }

    /// <summary>Backwards-compat allocation (used only by SPS/PPS; not on the per-frame slice hot path).</summary>
    public byte[] ToArray()
    {
        var byteLen = ByteLength; // capture before Flush pads the partial word to 4 bytes
        Flush();
        var arr = new byte[byteLen];
        _buffer.AsSpan(0, byteLen).CopyTo(arr);
        return arr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FlushWord()
    {
        EnsureCapacity(_length + 4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length, 4), _curBits);
        _length += 4;
        _curBits = 0;
        _leftBits = 32;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        var newCap = _buffer.Length * 2;
        if (newCap < required)
        {
            newCap = required;
        }

        var resized = new byte[newCap];
        _buffer.AsSpan(0, _length).CopyTo(resized);
        _buffer = resized;
    }
}
