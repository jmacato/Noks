using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace Noks.WebRtc;

/// <summary>Strict bounded RFC 8489 STUN codec used only by ICE.</summary>
internal static class Stun
{
    private const uint Cookie = 0x2112A442;
    private const ushort BindingRequest = 0x0001, BindingSuccess = 0x0101, BindingError = 0x0111;
    private const ushort Username = 0x0006, MessageIntegrity = 0x0008, ErrorCode = 0x0009, XorMappedAddress = 0x0020, Priority = 0x0024, UseCandidate = 0x0025, IceControlled = 0x8029, IceControlling = 0x802A, Fingerprint = 0x8028;

    internal sealed class Message
    {
        public required byte[] Raw { get; init; } public required byte[] TransactionId { get; init; } public ushort Type { get; init; }
        public string? Username { get; set; } public ulong TieBreaker { get; set; } public bool? RoleControlling { get; set; } public bool UseCandidate { get; set; }
        public int IntegrityOffset { get; set; } = -1; public IPEndPoint? XorMapped { get; set; }
        public int ErrorCode { get; set; }
        public bool IsRequest => Type == BindingRequest; public bool IsSuccess => Type == BindingSuccess;
    }
    public static byte[] BuildBareRequest(byte[] tx) => Build(BindingRequest, tx, [], null);
    public static byte[] BuildRequest(byte[] tx, string user, string password, bool controlling, ulong tie, bool use)
    {
        var attrs = new List<(ushort, byte[])> { (Username, System.Text.Encoding.UTF8.GetBytes(user)), (Priority, U32(1845501695)), (controlling ? IceControlling : IceControlled, U64(tie)) };
        if (use) attrs.Add((UseCandidate, [])); return Build(BindingRequest, tx, attrs, password);
    }
    public static byte[] BuildSuccess(byte[] tx, IPEndPoint mapped, string password) => Build(BindingSuccess, tx, [(XorMappedAddress, XorAddress(mapped, tx))], password);
    public static byte[] BuildError(byte[] tx, int code, string password) => Build(BindingError, tx, [(ErrorCode, new byte[] { 0, 0, (byte)(code / 100), (byte)(code % 100) })], password);
    private static byte[] Build(ushort type, byte[] tx, List<(ushort, byte[])> attrs, string? password)
    {
        var list = new List<(ushort, byte[])>(attrs); if (password is not null) list.Add((MessageIntegrity, new byte[20])); list.Add((Fingerprint, new byte[4]));
        var len = list.Sum(x => 4 + Pad(x.Item2.Length)); var packet = new byte[20 + len]; BinaryPrimitives.WriteUInt16BigEndian(packet, type); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)len); BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), Cookie); tx.CopyTo(packet, 8);
        var pos = 20; var miOffset = -1; var fpOffset = -1;
        foreach (var (at, value) in list) { BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(pos), at); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(pos + 2), (ushort)value.Length); value.CopyTo(packet, pos + 4); if (at == MessageIntegrity) miOffset = pos; if (at == Fingerprint) fpOffset = pos; pos += 4 + Pad(value.Length); }
        if (miOffset >= 0) { BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(miOffset + 24 - 20)); using var h = new HMACSHA1(System.Text.Encoding.UTF8.GetBytes(password!)); h.ComputeHash(packet.AsSpan(0, miOffset).ToArray()).CopyTo(packet, miOffset + 4); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)len); }
        var crc = Crc(packet.AsSpan(0, fpOffset)); BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(fpOffset + 4), crc ^ 0x5354554e); return packet;
    }
    public static bool TryParse(byte[] raw, out Message? m)
    {
        m = null; if (raw.Length < 20 || (raw[0] & 0xC0) != 0 || BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4)) != Cookie) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2)); if (length % 4 != 0 || length + 20 != raw.Length) return false;
        var type = BinaryPrimitives.ReadUInt16BigEndian(raw); if (type is not (BindingRequest or BindingSuccess or BindingError)) return false;
        var x = new Message { Raw = raw, Type = type, TransactionId = raw[8..20] }; var p = 20; var seen = new HashSet<ushort>(); var afterIntegrity = false; var roleSeen = false;
        while (p < raw.Length)
        {
            if (p + 4 > raw.Length) return false; var at = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(p)); var n = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(p + 2)); if (p + 4 + Pad(n) > raw.Length) return false; var v = raw.AsSpan(p + 4, n);
            if (!seen.Add(at) || afterIntegrity && at != Fingerprint) return false;
            switch (at) { case Username: x.Username = System.Text.Encoding.UTF8.GetString(v); break; case Priority when n == 4: break; case MessageIntegrity when n == 20: x.IntegrityOffset = p; afterIntegrity = true; break; case UseCandidate when n == 0: x.UseCandidate = true; break; case IceControlled when n == 8: if (roleSeen) return false; roleSeen = true; x.RoleControlling = false; x.TieBreaker = BinaryPrimitives.ReadUInt64BigEndian(v); break; case IceControlling when n == 8: if (roleSeen) return false; roleSeen = true; x.RoleControlling = true; x.TieBreaker = BinaryPrimitives.ReadUInt64BigEndian(v); break; case XorMappedAddress: x.XorMapped = DecodeXor(v, x.TransactionId); if (x.XorMapped is null) return false; break; case ErrorCode when n >= 4: x.ErrorCode = v[2] * 100 + v[3]; break; case Fingerprint when n == 4 && p + 8 == raw.Length: if (BinaryPrimitives.ReadUInt32BigEndian(v) != (Crc(raw.AsSpan(0, p)) ^ 0x5354554e)) return false; break; default: if (at < 0x8000) return false; break; }
            p += 4 + Pad(n);
        }
        m = x; return true;
    }
    public static bool VerifyIntegrity(Message m, string password)
    {
        if (m.IntegrityOffset < 0 || m.IntegrityOffset + 24 > m.Raw.Length) return false; var data = m.Raw.AsSpan(0, m.IntegrityOffset).ToArray(); BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)(m.IntegrityOffset + 24 - 20)); using var h = new HMACSHA1(System.Text.Encoding.UTF8.GetBytes(password)); return CryptographicOperations.FixedTimeEquals(h.ComputeHash(data), m.Raw.AsSpan(m.IntegrityOffset + 4, 20));
    }
    private static byte[] XorAddress(IPEndPoint ep, byte[] tx) { var a = ep.Address.MapToIPv6(); var is4 = ep.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork; var b = new byte[is4 ? 8 : 20]; b[1] = (byte)(is4 ? 1 : 2); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)(ep.Port ^ (Cookie >> 16))); var ip = is4 ? ep.Address.GetAddressBytes() : a.GetAddressBytes(); var mask = BitConverter.GetBytes(Cookie).Reverse().Concat(tx).ToArray(); for (var i = 0; i < ip.Length; i++) b[4 + i] = (byte)(ip[i] ^ mask[i]); return b; }
    private static IPEndPoint? DecodeXor(ReadOnlySpan<byte> v, byte[] tx) { if (v.Length is not (8 or 20) || v[0] != 0 || (v[1] != 1 && v[1] != 2)) return null; var n = v[1] == 1 ? 4 : 16; if (v.Length != 4 + n) return null; var mask = BitConverter.GetBytes(Cookie).Reverse().Concat(tx).ToArray(); var ip = new byte[n]; for (var i = 0; i < n; i++) ip[i] = (byte)(v[4 + i] ^ mask[i]); return new IPEndPoint(new IPAddress(ip), BinaryPrimitives.ReadUInt16BigEndian(v.Slice(2)) ^ (ushort)(Cookie >> 16)); }
    private static uint Crc(ReadOnlySpan<byte> bytes) { uint crc = 0xffffffff; foreach (var b in bytes) { crc ^= b; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); } return ~crc; }
    private static int Pad(int n) => (n + 3) & ~3; private static byte[] U32(uint x) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, x); return b; } private static byte[] U64(ulong x) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, x); return b; }
}
