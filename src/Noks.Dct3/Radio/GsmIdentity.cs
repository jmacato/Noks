namespace Noks.Dct3.Radio;

internal static class GsmIdentity
{
    public static byte[] EncodePlmnFromImsi(string imsi)
    {
        if (imsi.Length < 5 || imsi[..5].Any(ch => ch < '0' || ch > '9'))
        {
            throw new ArgumentException("IMSI must start with five decimal MCC/MNC digits.", nameof(imsi));
        }

        int mcc1 = imsi[0] - '0';
        int mcc2 = imsi[1] - '0';
        int mcc3 = imsi[2] - '0';
        int mnc1 = imsi[3] - '0';
        int mnc2 = imsi[4] - '0';

        return
        [
            (byte)((mcc2 << 4) | mcc1),
            (byte)((0x0F << 4) | mcc3),
            (byte)((mnc2 << 4) | mnc1),
        ];
    }

    public static byte[] EncodeLaiFromImsi(string imsi, ushort lac = 1)
    {
        byte[] plmn = EncodePlmnFromImsi(imsi);
        return
        [
            plmn[0],
            plmn[1],
            plmn[2],
            (byte)(lac >> 8),
            (byte)lac,
        ];
    }
}
