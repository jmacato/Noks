using System.Text;
using Noks.Waku;
using Noks.Dct3.Sim;

namespace Noks.Application;

public static class NokiaBusinessCardVCard
{
    public const ushort DestinationPort = 0x23F4;
    public const int MaximumSinglePartPayloadBytes = 133;

    public static byte[] Encode(string userName, string phoneNumber)
    {
        string alias = SimPhonebookCodec.CreateAlphaIdentifierAlias(userName);
        if (!NoksTemporaryNumber.IsCanonical(phoneNumber))
            throw new ArgumentException("Business-card number is invalid.", nameof(phoneNumber));
        string vCard = $"BEGIN:VCARD\r\nVERSION:2.1\r\nN:{alias}\r\nTEL;PREF:{phoneNumber}\r\nEND:VCARD\r\n";
        byte[] payload = Encoding.ASCII.GetBytes(vCard);
        if (payload.Length > MaximumSinglePartPayloadBytes)
            throw new InvalidOperationException("The business card does not fit in one Smart Message.");
        return payload;
    }
}
