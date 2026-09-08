using System.Buffers.Binary;
using System.Text;

namespace Noks.WebRtc;

/// <summary>RFC 3550 compound RTCP sender-report and CNAME support.</summary>
public static class RtcpReports
{
    public static byte[] CreateSenderReport(uint ssrc, uint rtpTimestamp, uint packetCount, uint octetCount, DateTimeOffset now, string cname)
    {
        ArgumentException.ThrowIfNullOrEmpty(cname);
        var name = Encoding.UTF8.GetBytes(cname);
        if (name.Length > 255) throw new ArgumentOutOfRangeException(nameof(cname));
        var sdesLength = 4 + 4 + 2 + name.Length + 1;
        sdesLength = (sdesLength + 3) & ~3;
        var result = new byte[28 + sdesLength];
        result[0] = 0x80; result[1] = 200; BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), 6);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), ssrc);
        var ntp = ToNtp(now); BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(8), ntp);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), rtpTimestamp); BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), packetCount); BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(24), octetCount);
        var at = 28; result[at] = 0x81; result[at + 1] = 202; BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(at + 2), (ushort)(sdesLength / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at + 4), ssrc); result[at + 8] = 1; result[at + 9] = (byte)name.Length; name.CopyTo(result, at + 10);
        return result;
    }

    internal static bool TryParse(ReadOnlySpan<byte> compound, out uint? byeSsrc)
    {
        byeSsrc = null;
        if (compound.Length < 12) return false;
        var pos = 0; var packets = 0; var cname = false;
        while (pos < compound.Length)
        {
            if (compound.Length - pos < 4) return false;
            var b0 = compound[pos]; var count = b0 & 31; var padding = (b0 & 0x20) != 0;
            if (b0 >> 6 != 2) return false;
            var pt = compound[pos + 1]; var length = (BinaryPrimitives.ReadUInt16BigEndian(compound.Slice(pos + 2)) + 1) * 4;
            if (length < 4 || length > compound.Length - pos || padding && pos + length != compound.Length) return false;
            var body = compound.Slice(pos + 4, length - 4); var useful = body;
            if (padding) { var n = compound[pos + length - 1]; if (n == 0 || n > length - 4 || n % 4 != 0) return false; useful = body[..^n]; }
            if (packets++ == 0 && pt is not (200 or 201)) return false;
            switch (pt)
            {
                case 200: if (useful.Length != 24 + count * 24) return false; break;
                case 201: if (useful.Length != 4 + count * 24) return false; break;
                case 202: if (!ValidateSdes(useful, count, ref cname)) return false; break;
                case 203: if (!ValidateBye(useful, count, ref byeSsrc)) return false; break;
            }
            pos += length;
        }
        return pos == compound.Length && packets >= 2 && cname;
    }

    private static bool ValidateSdes(ReadOnlySpan<byte> body, int chunks, ref bool cname)
    {
        var pos = 0;
        for (var c = 0; c < chunks; c++)
        {
            if (body.Length - pos < 4) return false; pos += 4; var ended = false;
            while (pos < body.Length)
            {
                var type = body[pos++]; if (type == 0) { ended = true; while (pos % 4 != 0) { if (pos >= body.Length || body[pos++] != 0) return false; } break; }
                if (pos >= body.Length) return false; var n = body[pos++]; if (n > body.Length - pos) return false; if (type == 1 && n != 0) cname = true; pos += n;
            }
            if (!ended || pos > body.Length) return false;
        }
        return pos == body.Length;
    }

    private static bool ValidateBye(ReadOnlySpan<byte> body, int sources, ref uint? bye)
    {
        if (body.Length < sources * 4) return false;
        if (sources > 0) bye ??= BinaryPrimitives.ReadUInt32BigEndian(body);
        var pos = sources * 4; if (pos == body.Length) return true;
        var n = body[pos++]; if (n > body.Length - pos) return false; pos += n;
        while (pos < body.Length) if (body[pos++] != 0) return false;
        return true;
    }

    private static ulong ToNtp(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime(); var seconds = (ulong)(utc - DateTimeOffset.UnixEpoch).TotalSeconds + 2_208_988_800UL;
        var fraction = ((ulong)(utc.Ticks % TimeSpan.TicksPerSecond) * (1UL << 32) / (ulong)TimeSpan.TicksPerSecond);
        return seconds << 32 | fraction;
    }
}
