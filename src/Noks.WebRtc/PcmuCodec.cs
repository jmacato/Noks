namespace Noks.WebRtc;

/// <summary>G.711 mu-law, RTP payload type 0, 8000 samples per second.</summary>
public static class PcmuCodec
{
    public static byte Encode(short sample)
    {
        int value = sample;
        int sign = value < 0 ? 0x80 : 0;
        if (value < 0) value = -value;
        value = Math.Min(value, 32635) + 132;
        int exponent = 7;
        for (int mask = 0x4000; exponent > 0 && (value & mask) == 0; mask >>= 1) exponent--;
        int mantissa = (value >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    public static short Decode(byte encoded)
    {
        int value = (~encoded) & 255;
        int magnitude = (((value & 15) << 3) + 132) << ((value >> 4) & 7);
        magnitude -= 132;
        return (short)((value & 128) == 0 ? magnitude : -magnitude);
    }
}
