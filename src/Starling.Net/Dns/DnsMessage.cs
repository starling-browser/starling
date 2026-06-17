using System.Buffers.Binary;
using System.Text;

namespace Starling.Net.Dns;

/// <summary>
/// DNS message wire format per RFC 1035 §4.1. Encoder + decoder.
/// </summary>
/// <remarks>
/// Implemented as a static utility so the resolver, the cache, and the test
/// suite can all manipulate raw packets without dragging in the resolver's
/// async / socket dependencies. The decoder is forgiving: malformed records
/// trigger <see cref="FormatException"/> rather than producing partial state.
/// </remarks>
public static class DnsMessage
{
    public enum QType : ushort { A = 1, NS = 2, CNAME = 5, AAAA = 28 }
    public enum QClass : ushort { IN = 1 }

    public enum RCode : byte
    {
        NoError = 0,
        FormatError = 1,
        ServerFailure = 2,
        NameError = 3,    // NXDOMAIN
        NotImplemented = 4,
        Refused = 5,
    }

    public readonly record struct Header(
        ushort Id, bool Qr, byte Opcode, bool Aa, bool Tc, bool Rd, bool Ra,
        RCode Rcode, ushort QdCount, ushort AnCount, ushort NsCount, ushort ArCount);

    public readonly record struct Question(string Name, QType Type, QClass Class);

    public abstract record Answer(string Name, QType Type, QClass Class, uint Ttl);
    public sealed record AAnswer(string Name, QType Type, QClass Class, uint Ttl,
        byte[] IPv4) : Answer(Name, Type, Class, Ttl);
    public sealed record AaaaAnswer(string Name, QType Type, QClass Class, uint Ttl,
        byte[] IPv6) : Answer(Name, Type, Class, Ttl);
    public sealed record CNameAnswer(string Name, QType Type, QClass Class, uint Ttl,
        string Target) : Answer(Name, Type, Class, Ttl);
    public sealed record OtherAnswer(string Name, QType Type, QClass Class, uint Ttl,
        byte[] RData) : Answer(Name, Type, Class, Ttl);

    // -----------------------------------------------------------------------
    // Encoder
    // -----------------------------------------------------------------------

    /// <summary>
    /// Build a standard recursion-desired query for a single (name, qtype, qclass).
    /// </summary>
    public static byte[] BuildQuery(ushort id, string name, QType qtype, QClass qclass = QClass.IN)
    {
        var nameBytes = EncodeName(name);
        var len = 12 + nameBytes.Length + 4;
        var buf = new byte[len];

        // Header — id, flags, counts.
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), id);
        // Flags: standard query, RD=1.
        buf[2] = 0b0000_0001;
        buf[3] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4, 2), 1);  // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), 0);  // ANCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8, 2), 0);  // NSCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10, 2), 0); // ARCOUNT

        nameBytes.CopyTo(buf, 12);
        var off = 12 + nameBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(off, 2), (ushort)qtype);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(off + 2, 2), (ushort)qclass);
        return buf;
    }

    /// <summary>
    /// Encode a domain name into the labels-with-length-bytes form. The trailing
    /// zero-length root label is included.
    /// </summary>
    public static byte[] EncodeName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return [0];
        }
        // Strip trailing dot for consistency.
        if (name[^1] == '.')
        {
            name = name[..^1];
        }

        var labels = name.Split('.');
        var totalLen = 1; // trailing root null
        foreach (var label in labels)
        {
            if (label.Length == 0)
            {
                throw new FormatException("Empty label in DNS name.");
            }

            if (label.Length > 63)
            {
                throw new FormatException($"Label '{label}' exceeds 63 chars.");
            }

            totalLen += 1 + label.Length;
        }
        if (totalLen > 255)
        {
            throw new FormatException($"Encoded name '{name}' exceeds 255 bytes.");
        }

        var buf = new byte[totalLen];
        var o = 0;
        foreach (var label in labels)
        {
            buf[o++] = (byte)label.Length;
            foreach (var ch in label)
            {
                if (ch >= 0x80)
                {
                    throw new FormatException(
                        "Non-ASCII labels require IDNA Punycode conversion, which is not implemented yet.");
                }

                buf[o++] = (byte)ch;
            }
        }
        buf[o] = 0; // root
        return buf;
    }

    // -----------------------------------------------------------------------
    // Decoder
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parse a full DNS response into header + question + answer sections.
    /// </summary>
    public static (Header Header, List<Question> Questions, List<Answer> Answers)
        Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12)
        {
            throw new FormatException("Packet shorter than DNS header.");
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(packet[..2]);
        var f1 = packet[2];
        var f2 = packet[3];
        var header = new Header(
            Id: id,
            Qr: (f1 & 0x80) != 0,
            Opcode: (byte)((f1 >> 3) & 0xF),
            Aa: (f1 & 0x04) != 0,
            Tc: (f1 & 0x02) != 0,
            Rd: (f1 & 0x01) != 0,
            Ra: (f2 & 0x80) != 0,
            Rcode: (RCode)(f2 & 0xF),
            QdCount: BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2)),
            AnCount: BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2)),
            NsCount: BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(8, 2)),
            ArCount: BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(10, 2)));

        var off = 12;
        var questions = new List<Question>(header.QdCount);
        for (var i = 0; i < header.QdCount; i++)
        {
            var (qname, qoff) = DecodeName(packet, off);
            off = qoff;
            if (off + 4 > packet.Length)
            {
                throw new FormatException("Truncated question.");
            }

            var qtype = (QType)BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(off, 2));
            var qclass = (QClass)BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(off + 2, 2));
            off += 4;
            questions.Add(new Question(qname, qtype, qclass));
        }

        var answers = new List<Answer>(header.AnCount);
        for (var i = 0; i < header.AnCount; i++)
        {
            var (aname, anoff) = DecodeName(packet, off);
            off = anoff;
            if (off + 10 > packet.Length)
            {
                throw new FormatException("Truncated answer.");
            }

            var atype = (QType)BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(off, 2));
            var aclass = (QClass)BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(off + 2, 2));
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(off + 4, 4));
            var rdlen = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(off + 8, 2));
            off += 10;
            if (off + rdlen > packet.Length)
            {
                throw new FormatException("Truncated rdata.");
            }

            Answer ans = atype switch
            {
                QType.A when rdlen == 4 =>
                    new AAnswer(aname, atype, aclass, ttl, packet.Slice(off, 4).ToArray()),
                QType.AAAA when rdlen == 16 =>
                    new AaaaAnswer(aname, atype, aclass, ttl, packet.Slice(off, 16).ToArray()),
                QType.CNAME =>
                    new CNameAnswer(aname, atype, aclass, ttl, DecodeName(packet, off).Name),
                _ =>
                    new OtherAnswer(aname, atype, aclass, ttl, packet.Slice(off, rdlen).ToArray()),
            };
            answers.Add(ans);
            off += rdlen;
        }

        return (header, questions, answers);
    }

    /// <summary>
    /// Decode a (possibly compressed) name starting at <paramref name="start"/>.
    /// Returns the dotted name plus the offset immediately after the name.
    /// Follows compression pointers per RFC 1035 §4.1.4.
    /// </summary>
    public static (string Name, int NextOffset) DecodeName(
        ReadOnlySpan<byte> packet, int start)
    {
        var sb = new StringBuilder();
        var off = start;
        int? endOffset = null;
        var hops = 0;
        while (off < packet.Length)
        {
            var lenByte = packet[off];
            if (lenByte == 0)
            {
                off++;
                endOffset ??= off;
                return (sb.ToString().TrimEnd('.'), endOffset.Value);
            }
            if ((lenByte & 0xC0) == 0xC0)
            {
                // Pointer: high 2 bits set; next 14 bits = offset.
                if (off + 1 >= packet.Length)
                {
                    throw new FormatException("Truncated pointer.");
                }

                var ptr = ((lenByte & 0x3F) << 8) | packet[off + 1];
                if (++hops > 32)
                {
                    throw new FormatException("DNS name compression loop.");
                }

                endOffset ??= off + 2;
                off = ptr;
                continue;
            }
            if ((lenByte & 0xC0) != 0)
            {
                throw new FormatException($"Reserved label type 0x{lenByte:X2}.");
            }

            off++;
            if (off + lenByte > packet.Length)
            {
                throw new FormatException("Label past end.");
            }

            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            for (var i = 0; i < lenByte; i++)
            {
                sb.Append((char)packet[off + i]);
            }

            off += lenByte;
        }
        throw new FormatException("Unterminated name.");
    }
}
