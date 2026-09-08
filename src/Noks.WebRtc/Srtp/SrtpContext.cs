using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Noks.WebRtc;

/// <summary>RFC 3711 SRTP_AES128_CM_HMAC_SHA1_80 protection context.</summary>
/// <remarks>All public operations are serialized and this instance owns independent send and receive state.</remarks>
public sealed class SrtpContext : IDisposable
{
    private const int TagLength = 10;
    private const int ReplayWindow = 64;
    private const int MaxSsrcStates = 64;
    private readonly object _sync = new();
    private readonly byte[] _srtpEncryptionKey;
    private readonly byte[] _srtpSalt;
    private readonly byte[] _srtpAuthenticationKey;
    private readonly byte[] _srtcpEncryptionKey;
    private readonly byte[] _srtcpSalt;
    private readonly byte[] _srtcpAuthenticationKey;
    private readonly Dictionary<uint, RtpState> _sendRtp = [];
    private readonly Dictionary<uint, RtpState> _receiveRtp = [];
    private readonly Dictionary<uint, RtcpState> _sendRtcp = [];
    private readonly Dictionary<uint, RtcpState> _receiveRtcp = [];
    private bool _disposed;

    public SrtpContext(byte[] masterKey, byte[] masterSalt)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        ArgumentNullException.ThrowIfNull(masterSalt);
        if (masterKey.Length != 16 || masterSalt.Length != 14)
            throw new ArgumentException("SRTP_AES128_CM_HMAC_SHA1_80 requires a 16-byte master key and a 14-byte master salt.");

        _srtpEncryptionKey = Derive(masterKey, masterSalt, 0x00, 16);
        _srtpAuthenticationKey = Derive(masterKey, masterSalt, 0x01, 20);
        _srtpSalt = Derive(masterKey, masterSalt, 0x02, 14);
        _srtcpEncryptionKey = Derive(masterKey, masterSalt, 0x03, 16);
        _srtcpAuthenticationKey = Derive(masterKey, masterSalt, 0x04, 20);
        _srtcpSalt = Derive(masterKey, masterSalt, 0x05, 14);
    }

    public byte[] ProtectRtp(ReadOnlySpan<byte> packet)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!TryParseRtp(packet, out var headerLength, out var sequence, out var ssrc))
                throw new ArgumentException("Invalid RTP packet.", nameof(packet));

            var state = GetState(_sendRtp, ssrc, static () => new RtpState());
            var index = NextSendRtpIndex(state, sequence);
            var result = packet.ToArray();
            XorKeystream(result.AsSpan(headerLength), _srtpEncryptionKey, _srtpSalt, ssrc, index);
            var tag = Authenticate(_srtpAuthenticationKey, result, (uint)(index >> 16));
            Array.Resize(ref result, result.Length + TagLength);
            tag.CopyTo(result, result.Length - TagLength);
            CommitSendRtp(state, index);
            return result;
        }
    }

    public bool TryUnprotectRtp(ReadOnlySpan<byte> packet, out byte[] result)
    {
        lock (_sync)
        {
            result = [];
            if (_disposed || packet.Length < TagLength || !TryParseRtp(packet[..^TagLength], out var headerLength, out var sequence, out var ssrc))
                return false;
            if (packet.Length - TagLength - headerLength > 1_048_576)
                return false;

            var exists = _receiveRtp.TryGetValue(ssrc, out var state);
            if (!exists && _receiveRtp.Count >= MaxSsrcStates)
                return false;
            state ??= new RtpState();
            var index = EstimateRtpIndex(state, sequence);
            if (!CanAccept(state.Initialized, state.HighestIndex, state.ReplayBitmap, index))
                return false;

            var authenticated = packet[..^TagLength];
            var expected = Authenticate(_srtpAuthenticationKey, authenticated, (uint)(index >> 16));
            if (!CryptographicOperations.FixedTimeEquals(expected, packet[^TagLength..]))
                return false;

            result = authenticated.ToArray();
            XorKeystream(result.AsSpan(headerLength), _srtpEncryptionKey, _srtpSalt, ssrc, index);
            if (!ValidRtpPadding(result, headerLength))
            {
                CryptographicOperations.ZeroMemory(result);
                result = [];
                return false;
            }

            CommitReceive(state, index);
            if (!exists)
                InsertState(_receiveRtp, ssrc, state);
            return true;
        }
    }

    public byte[] ProtectRtcp(ReadOnlySpan<byte> packet)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!TryParseRtcp(packet, out var ssrc))
                throw new ArgumentException("Invalid RTCP compound packet.", nameof(packet));
            var state = GetState(_sendRtcp, ssrc, static () => new RtcpState());
            if (state.NextIndex > 0x7fff_ffff)
                throw new InvalidOperationException("The SRTCP key lifetime has been exhausted.");
            var index = state.NextIndex++;
            var result = packet.ToArray();
            XorKeystream(result.AsSpan(8), _srtcpEncryptionKey, _srtcpSalt, ssrc, index);
            Array.Resize(ref result, result.Length + 4 + TagLength);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(packet.Length, 4), 0x8000_0000u | (uint)index);
            var tag = Authenticate(_srtcpAuthenticationKey, result.AsSpan(0, packet.Length + 4));
            tag.CopyTo(result, packet.Length + 4);
            return result;
        }
    }

    public bool TryUnprotectRtcp(ReadOnlySpan<byte> packet, out byte[] result)
    {
        lock (_sync)
        {
            result = [];
            if (_disposed || packet.Length < 8 + 4 + TagLength)
                return false;
            var protectedLength = packet.Length - TagLength;
            var indexWord = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(protectedLength - 4, 4));
            var index = indexWord & 0x7fff_ffffu;
            var encrypted = (indexWord & 0x8000_0000u) != 0;
            var rtcp = packet[..(protectedLength - 4)];
            if (!TryParseRtcpHeader(rtcp, out var ssrc))
                return false;
            if (rtcp.Length - 8 > 1_048_576)
                return false;
            var exists = _receiveRtcp.TryGetValue(ssrc, out var state);
            if (!exists && _receiveRtcp.Count >= MaxSsrcStates)
                return false;
            state ??= new RtcpState();
            if (!CanAccept(state.Initialized, state.HighestIndex, state.ReplayBitmap, index))
                return false;
            var expected = Authenticate(_srtcpAuthenticationKey, packet[..protectedLength]);
            if (!CryptographicOperations.FixedTimeEquals(expected, packet[^TagLength..]))
                return false;

            result = rtcp.ToArray();
            if (encrypted)
                XorKeystream(result.AsSpan(8), _srtcpEncryptionKey, _srtcpSalt, ssrc, index);
            if (!TryParseRtcp(result, out _))
            {
                CryptographicOperations.ZeroMemory(result);
                result = [];
                return false;
            }
            CommitReceive(state, index);
            if (!exists)
                InsertState(_receiveRtcp, ssrc, state);
            return true;
        }
    }

    internal static byte[] DeriveForTest(byte[] masterKey, byte[] masterSalt, byte label, int length) => Derive(masterKey, masterSalt, label, length);
    internal static byte[] KeystreamForTest(byte[] key, byte[] salt, uint ssrc, ulong index, int length)
    {
        var output = new byte[length];
        XorKeystream(output, key, salt, ssrc, index);
        return output;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_srtpEncryptionKey);
            CryptographicOperations.ZeroMemory(_srtpSalt);
            CryptographicOperations.ZeroMemory(_srtpAuthenticationKey);
            CryptographicOperations.ZeroMemory(_srtcpEncryptionKey);
            CryptographicOperations.ZeroMemory(_srtcpSalt);
            CryptographicOperations.ZeroMemory(_srtcpAuthenticationKey);
            _sendRtp.Clear(); _receiveRtp.Clear(); _sendRtcp.Clear(); _receiveRtcp.Clear();
        }
    }

    private static byte[] Derive(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> masterSalt, byte label, int length)
    {
        var counter = new byte[16];
        masterSalt.CopyTo(counter);
        counter[7] ^= label;
        var output = new byte[length];
        using var aes = Aes.Create();
        aes.Key = masterKey.ToArray(); aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        var block = new byte[16];
        for (var offset = 0; offset < length; offset += 16)
        {
            encryptor.TransformBlock(counter, 0, 16, block, 0);
            block.AsSpan(0, Math.Min(16, length - offset)).CopyTo(output.AsSpan(offset));
            IncrementCounter(counter);
        }
        CryptographicOperations.ZeroMemory(counter); CryptographicOperations.ZeroMemory(block);
        return output;
    }

    private static byte[] Authenticate(byte[] key, ReadOnlySpan<byte> data, uint? roc = null)
    {
        var input = new byte[data.Length + (roc.HasValue ? 4 : 0)];
        data.CopyTo(input);
        if (roc.HasValue) BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(data.Length), roc.Value);
        var hash = HMACSHA1.HashData(key, input);
        CryptographicOperations.ZeroMemory(input);
        var tag = hash[..TagLength];
        CryptographicOperations.ZeroMemory(hash);
        return tag;
    }

    private static void XorKeystream(Span<byte> data, byte[] key, byte[] salt, uint ssrc, ulong index)
    {
        if (data.Length > 1_048_576) throw new ArgumentException("SRTP payload exceeds the AES-CM packet limit.");
        var counter = new byte[16]; salt.CopyTo(counter);
        counter[4] ^= (byte)(ssrc >> 24); counter[5] ^= (byte)(ssrc >> 16); counter[6] ^= (byte)(ssrc >> 8); counter[7] ^= (byte)ssrc;
        counter[8] ^= (byte)(index >> 40); counter[9] ^= (byte)(index >> 32); counter[10] ^= (byte)(index >> 24); counter[11] ^= (byte)(index >> 16);
        counter[12] ^= (byte)(index >> 8); counter[13] ^= (byte)index;
        using var aes = Aes.Create(); aes.Key = key; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(); var block = new byte[16];
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            encryptor.TransformBlock(counter, 0, 16, block, 0);
            var take = Math.Min(16, data.Length - offset);
            for (var j = 0; j < take; j++) data[offset + j] ^= block[j];
            IncrementCounter(counter);
        }
        CryptographicOperations.ZeroMemory(counter); CryptographicOperations.ZeroMemory(block);
    }

    private static void IncrementCounter(Span<byte> counter) { for (var i = 15; i >= 0 && ++counter[i] == 0; i--) { } }
    private static bool TryParseRtp(ReadOnlySpan<byte> p, out int header, out ushort sequence, out uint ssrc)
    {
        header = 0; sequence = 0; ssrc = 0;
        if (p.Length < 12 || p[0] >> 6 != 2) return false;
        header = 12 + (p[0] & 15) * 4;
        if (header > p.Length) return false;
        if ((p[0] & 0x10) != 0)
        {
            if (header + 4 > p.Length) return false;
            header += 4 + BinaryPrimitives.ReadUInt16BigEndian(p.Slice(header + 2, 2)) * 4;
            if (header > p.Length) return false;
        }
        sequence = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(2, 2)); ssrc = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(8, 4)); return true;
    }
    private static bool ValidRtpPadding(ReadOnlySpan<byte> p, int header) => (p[0] & 0x20) == 0 || p.Length > header && p[^1] != 0 && p[^1] <= p.Length - header;
    private static bool TryParseRtcp(ReadOnlySpan<byte> p, out uint ssrc)
    {
        if (!TryParseRtcpHeader(p, out ssrc)) return false;
        for (var offset = 0; offset < p.Length;)
        {
            if (p.Length - offset < 4 || p[offset] >> 6 != 2) return false;
            var bytes = (BinaryPrimitives.ReadUInt16BigEndian(p.Slice(offset + 2, 2)) + 1) * 4;
            if (bytes < 4 || bytes > p.Length - offset) return false;
            offset += bytes;
        }
        return true;
    }
    private static bool TryParseRtcpHeader(ReadOnlySpan<byte> p, out uint ssrc)
    {
        ssrc = 0; if (p.Length < 8 || p[0] >> 6 != 2 || p[1] is < 192 or > 223) return false;
        var firstLength = (BinaryPrimitives.ReadUInt16BigEndian(p.Slice(2, 2)) + 1) * 4;
        if (firstLength < 8 || firstLength > p.Length) return false;
        ssrc = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(4, 4)); return true;
    }
    private static ulong NextSendRtpIndex(RtpState state, ushort sequence)
    {
        if (!state.Initialized) return sequence;
        var roc = state.Roc;
        if (state.LastSequence > 32767 && sequence < 32768) { if (roc == uint.MaxValue) throw new InvalidOperationException("The SRTP key lifetime has been exhausted."); roc++; }
        var index = ((ulong)roc << 16) | sequence;
        if (index <= state.HighestIndex) throw new InvalidOperationException("SRTP sender sequence numbers must not repeat or move backwards.");
        return index;
    }
    private static ulong EstimateRtpIndex(RtpState state, ushort sequence)
    {
        if (!state.Initialized) return sequence;
        var roc = state.Roc;
        if (state.LastSequence < 32768) { if (sequence > state.LastSequence && sequence - state.LastSequence > 32768 && roc > 0) roc--; }
        else if (state.LastSequence - sequence > 32768) { if (roc == uint.MaxValue) return ulong.MaxValue; roc++; }
        return ((ulong)roc << 16) | sequence;
    }
    private static bool CanAccept(bool initialized, ulong highest, ulong bitmap, ulong index) => !initialized || index > highest || highest - index < ReplayWindow && (bitmap & (1UL << (int)(highest - index))) == 0;
    private static void CommitSendRtp(RtpState state, ulong index) { state.Initialized = true; state.HighestIndex = index; state.Roc = (uint)(index >> 16); state.LastSequence = (ushort)index; }
    private static void CommitReceive(RtpState state, ulong index)
    {
        if (!state.Initialized) { state.Initialized = true; state.HighestIndex = index; state.ReplayBitmap = 1; }
        else if (index > state.HighestIndex) { var delta = index - state.HighestIndex; state.ReplayBitmap = delta >= 64 ? 1 : (state.ReplayBitmap << (int)delta) | 1; state.HighestIndex = index; }
        else state.ReplayBitmap |= 1UL << (int)(state.HighestIndex - index);
        state.Roc = (uint)(state.HighestIndex >> 16); state.LastSequence = (ushort)state.HighestIndex;
    }
    private static void CommitReceive(RtcpState state, uint index)
    {
        if (!state.Initialized) { state.Initialized = true; state.HighestIndex = index; state.ReplayBitmap = 1; }
        else if (index > state.HighestIndex) { var delta = index - state.HighestIndex; state.ReplayBitmap = delta >= 64 ? 1 : (state.ReplayBitmap << (int)delta) | 1; state.HighestIndex = index; }
        else state.ReplayBitmap |= 1UL << (int)(state.HighestIndex - index);
    }
    private static T GetState<T>(Dictionary<uint, T> states, uint ssrc, Func<T> create) where T : State { if (!states.TryGetValue(ssrc, out var state)) { if (states.Count >= MaxSsrcStates) throw new InvalidOperationException("SRTP SSRC state capacity is exhausted for this key."); state = create(); InsertState(states, ssrc, state); } return state; }
    private static void InsertState<T>(Dictionary<uint, T> states, uint ssrc, T state) where T : State { states.Add(ssrc, state); }
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed, this); }
    private abstract class State { }
    private sealed class RtpState : State { public bool Initialized; public uint Roc; public ushort LastSequence; public ulong HighestIndex = ulong.MaxValue; public ulong ReplayBitmap; }
    private sealed class RtcpState : State { public bool Initialized; public uint NextIndex; public uint HighestIndex = uint.MaxValue; public ulong ReplayBitmap; }
}
