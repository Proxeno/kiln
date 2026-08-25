namespace Kiln.Capture.Mp4;

/// <summary>
/// Minimal big-endian writer for ISO base media file format boxes (ISO/IEC 14496-12).
/// Every box is a 32-bit size followed by a four-character type; <see cref="Box"/> reserves
/// the size field and patches it when the returned scope is disposed.
/// </summary>
internal sealed class BoxWriter
{
    private readonly MemoryStream _stream = new();

    internal long Position => _stream.Position;

    internal void UInt8(byte value) => _stream.WriteByte(value);

    internal void UInt16(ushort value)
    {
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
    }

    internal void UInt24(uint value)
    {
        _stream.WriteByte((byte)(value >> 16));
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
    }

    internal void UInt32(uint value)
    {
        _stream.WriteByte((byte)(value >> 24));
        _stream.WriteByte((byte)(value >> 16));
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
    }

    internal void Int32(int value) => UInt32(unchecked((uint)value));

    /// <summary>Writes a four-character box type or brand. ASCII only, exactly four bytes.</summary>
    internal void FourCc(string value)
    {
        if (value.Length != 4)
        {
            throw new ArgumentException($"Four-character code must be 4 characters; got \"{value}\".", nameof(value));
        }

        foreach (var c in value)
        {
            _stream.WriteByte((byte)c);
        }
    }

    internal void Bytes(ReadOnlySpan<byte> value) => _stream.Write(value);

    /// <summary>Writes <paramref name="count"/> zero bytes.</summary>
    internal void Zeros(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _stream.WriteByte(0);
        }
    }

    /// <summary>Writes a full-box version byte followed by 24 bits of flags.</summary>
    internal void FullBoxHeader(byte version, uint flags)
    {
        UInt8(version);
        UInt24(flags);
    }

    /// <summary>
    /// Opens a box of the given type. The 32-bit size is reserved now and patched on dispose,
    /// so boxes nest naturally with <c>using</c>.
    /// </summary>
    internal BoxScope Box(string type)
    {
        var start = _stream.Position;
        UInt32(0);
        FourCc(type);
        return new BoxScope(this, start);
    }

    internal byte[] ToArray() => _stream.ToArray();

    private void PatchSize(long start)
    {
        var end = _stream.Position;
        _stream.Position = start;
        UInt32(checked((uint)(end - start)));
        _stream.Position = end;
    }

    internal readonly struct BoxScope : IDisposable
    {
        private readonly BoxWriter _writer;
        private readonly long _start;

        internal BoxScope(BoxWriter writer, long start)
        {
            _writer = writer;
            _start = start;
        }

        public void Dispose() => _writer.PatchSize(_start);
    }
}
