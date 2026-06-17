namespace Starling.Net.Http.H2.Hpack;

/// <summary>
/// HPACK variable-length integer representation (RFC 7541 §5.1). Integers are
/// stored in an N-bit prefix of the first octet; values that don't fit spill
/// into following octets, 7 bits at a time, little-endian, with a continuation
/// bit in the MSB.
/// </summary>
internal static class HpackInteger
{
    /// <summary>
    /// Decode an integer with an <paramref name="prefixBits"/>-bit prefix
    /// starting at <paramref name="offset"/>. On success advances
    /// <paramref name="offset"/> past the integer and returns true. Returns
    /// false on truncation or an over-long encoding (&gt; 32 bits of payload).
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> buf, ref int offset, int prefixBits, out int value)
    {
        value = 0;
        if (offset >= buf.Length)
        {
            return false;
        }

        var max = (1 << prefixBits) - 1;
        var prefix = buf[offset] & max;
        offset++;
        if (prefix < max)
        {
            value = prefix;
            return true;
        }

        // Continuation octets: 7 bits each, low-order first.
        long result = max;
        var shift = 0;
        while (true)
        {
            if (offset >= buf.Length)
            {
                return false;
            }

            var b = buf[offset++];
            result += (long)(b & 0x7f) << shift;
            if (result > int.MaxValue)
            {
                return false; // guard against overflow / DoS
            }

            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
            if (shift >= 32)
            {
                return false;
            }
        }

        value = (int)result;
        return true;
    }

    /// <summary>
    /// Encode <paramref name="value"/> into <paramref name="dst"/> using an
    /// <paramref name="prefixBits"/>-bit prefix. <paramref name="firstByteHigh"/>
    /// supplies the bits above the prefix in the first octet (e.g. the 0x80
    /// "indexed" flag). Returns the number of bytes written.
    /// </summary>
    public static int Encode(Span<byte> dst, int value, int prefixBits, byte firstByteHigh)
    {
        var max = (1 << prefixBits) - 1;
        if (value < max)
        {
            dst[0] = (byte)(firstByteHigh | value);
            return 1;
        }

        dst[0] = (byte)(firstByteHigh | max);
        var pos = 1;
        var remaining = value - max;
        while (remaining >= 0x80)
        {
            dst[pos++] = (byte)((remaining & 0x7f) | 0x80);
            remaining >>= 7;
        }
        dst[pos++] = (byte)remaining;
        return pos;
    }

    /// <summary>Worst-case byte count for encoding a 32-bit value (1 prefix + 5 continuation).</summary>
    public const int MaxEncodedLength = 6;
}
