using System.Buffers.Binary;
using System.Text;

namespace Noks.Waku;

public static class WakuSmsPayloadCodec
{
    private const byte Version = 1;
    private const int HeaderSize = 8;
    private static ReadOnlySpan<byte> Magic => "NSM1"u8;

    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] message = Encoding.UTF8.GetBytes(text);
        if (message.Length > WakuEnvelopeCodec.MaximumPayloadSize - HeaderSize)
            throw new ArgumentException("SMS text is too large for a Waku envelope.", nameof(text));
        byte[] encoded = new byte[HeaderSize + message.Length];
        Magic.CopyTo(encoded);
        encoded[4] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(6, 2), checked((ushort)message.Length));
        message.CopyTo(encoded, HeaderSize);
        return encoded;
    }

    public static bool TryDecode(ReadOnlySpan<byte> encoded, out string text)
    {
        text = string.Empty;
        if (encoded.Length < HeaderSize || !encoded[..4].SequenceEqual(Magic) ||
            encoded[4] != Version || encoded[5] != 0)
        {
            return false;
        }
        int length = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(6, 2));
        if (encoded.Length != HeaderSize + length)
            return false;
        try
        {
            text = new UTF8Encoding(false, true).GetString(encoded[HeaderSize..]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }
}
