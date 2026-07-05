using Noks.Dct3.Audio;
using Noks.Dct3.Messaging;
using Noks.Dct3.Sim;
using Noks.Dct3.State;

namespace Noks.Dct3.Radio;

internal static class SmsTpduCodec
{
    internal static byte[] BuildCpAck(byte uplinkTransactionAndProtocolDiscriminator) =>
    [
        (byte)(uplinkTransactionAndProtocolDiscriminator ^ 0x80),
        GsmProtocol.CpAckMessageType,
    ];

    internal static byte[] BuildRpAckCpData(byte uplinkTransactionAndProtocolDiscriminator, byte messageReference) =>
    [
        (byte)(uplinkTransactionAndProtocolDiscriminator ^ 0x80),
        GsmProtocol.CpDataMessageType,
        0x02,
        0x03,
        messageReference,
    ];

    internal static byte[] BuildRpErrorCpData(
        byte uplinkTransactionAndProtocolDiscriminator,
        byte messageReference) =>
    [
        (byte)(uplinkTransactionAndProtocolDiscriminator ^ 0x80),
        GsmProtocol.CpDataMessageType,
        0x04,
        0x05,
        messageReference,
        0x01,
        0x15,
    ];


    internal static byte[] BuildSmsDeliverTpdu(string originator, string text, DateTimeOffset serviceCentreTime)
    {
        string sanitizedOriginator = GsmAlphabet.SanitizeDialableAddress(originator);
        byte[] originatorDigits = GsmAlphabet.EncodeSemiOctets(sanitizedOriginator);
        byte[] userData = GsmAlphabet.PackGsm7(GsmAlphabet.SanitizeSmsText(text), out byte userDataLength);

        List<byte> tpdu =
        [
            0x04,
            (byte)sanitizedOriginator.Length,
            0x81,
        ];
        tpdu.AddRange(originatorDigits);
        tpdu.Add(0x00);
        tpdu.Add(0x00);
        tpdu.AddRange(GsmAlphabet.BuildTimestampAndTimeZone(serviceCentreTime));
        tpdu.Add(userDataLength);
        tpdu.AddRange(userData);
        return tpdu.ToArray();
    }

    internal static byte[] BuildSmartMessageDeliverTpdu(
        string originator,
        ushort destinationPort,
        ReadOnlySpan<byte> payload,
        SmartMessageConcatenation concatenation,
        DateTimeOffset serviceCentreTime)
    {
        int userDataHeaderLength = concatenation.IsMultipart ? 12 : 7;
        int maximumPayloadLength = concatenation.IsMultipart
            ? SmartMessageSms.ConcatenatedPartPayloadCapacity
            : SmartMessageSms.SinglePartPayloadCapacity;
        if (payload.Length == 0 || payload.Length > maximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"This Smart Messaging SMS part must contain 1-{maximumPayloadLength} bytes.");
        }

        if (concatenation.IsMultipart &&
            (concatenation.PartCount > SmartMessageSms.MaximumPartCount ||
             concatenation.PartNumber == 0 ||
             concatenation.PartNumber > concatenation.PartCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(concatenation),
                $"A concatenated Smart Messaging SMS must identify a valid part within " +
                $"the {SmartMessageSms.MaximumPartCount}-part Nokia limit.");
        }

        string sanitizedOriginator = GsmAlphabet.SanitizeDialableAddress(originator);
        byte[] originatorDigits = GsmAlphabet.EncodeSemiOctets(sanitizedOriginator);
        byte firstOctet = concatenation.IsMultipart &&
            concatenation.PartNumber < concatenation.PartCount
                ? (byte)0x40
                : (byte)0x44;
        List<byte> tpdu =
        [
            firstOctet,
            (byte)sanitizedOriginator.Length,
            0x81,
        ];
        tpdu.AddRange(originatorDigits);
        tpdu.Add(0x00);
        tpdu.Add(0xF5);
        tpdu.AddRange(GsmAlphabet.BuildTimestampAndTimeZone(serviceCentreTime));
        tpdu.Add((byte)(userDataHeaderLength + payload.Length));
        tpdu.Add((byte)(userDataHeaderLength - 1));
        tpdu.Add(0x05);
        tpdu.Add(0x04);
        tpdu.Add((byte)(destinationPort >> 8));
        tpdu.Add((byte)destinationPort);
        tpdu.Add(0x00);
        tpdu.Add(0x00);
        if (concatenation.IsMultipart)
        {
            tpdu.Add(0x00);
            tpdu.Add(0x03);
            tpdu.Add(concatenation.Reference);
            tpdu.Add(concatenation.PartCount);
            tpdu.Add(concatenation.PartNumber);
        }

        tpdu.AddRange(payload);
        return tpdu.ToArray();
    }

    internal static bool TryDecodeSmsSubmitTpdu(
        ReadOnlySpan<byte> tpdu,
        out string normalizedDestination,
        out string text,
        out bool international)
    {
        normalizedDestination = "";
        text = "";
        international = false;
        if (tpdu.Length < 7 || (tpdu[0] & 0x03) != 0x01)
        {
            return false;
        }

        international = (tpdu[3] & 0x70) == 0x10;

        byte firstOctet = tpdu[0];
        int destinationDigits = tpdu[2];
        int destinationBytes = (destinationDigits + 1) / 2;
        int offset = 4;
        if (destinationDigits is < 1 or > 20 || offset + destinationBytes + 3 > tpdu.Length ||
            !GsmAlphabet.TryDecodeSemiOctets(
                tpdu.Slice(offset, destinationBytes),
                destinationDigits,
                out normalizedDestination))
        {
            return false;
        }

        offset += destinationBytes;
        _ = tpdu[offset++]; // TP-PID
        byte dataCodingScheme = tpdu[offset++];
        int validityPeriodLength = ((firstOctet >> 3) & 0x03) switch
        {
            0 => 0,
            2 => 1,
            _ => 7,
        };
        if (offset + validityPeriodLength >= tpdu.Length)
        {
            return false;
        }

        offset += validityPeriodLength;
        int userDataLength = tpdu[offset++];
        ReadOnlySpan<byte> userData = tpdu[offset..];
        int alphabet = dataCodingScheme & 0x0C;
        if (alphabet == 0x00)
        {
            int requiredBytes = (userDataLength * 7 + 7) / 8;
            if (userDataLength > 160 || requiredBytes > userData.Length)
            {
                return false;
            }

            text = GsmAlphabet.DecodeGsm7(userData[..requiredBytes], userDataLength);
            return true;
        }

        if (userDataLength > userData.Length)
        {
            return false;
        }

        if (alphabet == 0x08)
        {
            if ((userDataLength & 1) != 0)
            {
                return false;
            }

            text = System.Text.Encoding.BigEndianUnicode.GetString(userData[..userDataLength]);
            return true;
        }

        text = new string(userData[..userDataLength].ToArray().Select(value =>
            value is >= 0x20 and <= 0x7E ? (char)value : '\uFFFD').ToArray());
        return true;
    }

    internal static bool TryGetMobileOriginatedRpDataReference(ReadOnlySpan<byte> cpData, out byte messageReference)
    {
        messageReference = 0;

        if (!TryGetCpUserData(cpData, out ReadOnlySpan<byte> rpdu))
        {
            return false;
        }

        if (rpdu.Length < 2 || (rpdu[0] & 0x07) != GsmProtocol.RpDataMobileToNetworkMessageType)
        {
            return false;
        }

        messageReference = rpdu[1];
        return true;
    }

    internal static bool TryGetMobileTerminatedRpAckReference(ReadOnlySpan<byte> cpData, out byte messageReference)
    {
        messageReference = 0;

        if (!TryGetCpUserData(cpData, out ReadOnlySpan<byte> rpdu))
        {
            return false;
        }

        if (rpdu.Length < 2 || (rpdu[0] & 0x07) != GsmProtocol.RpAckMobileToNetworkMessageType)
        {
            return false;
        }

        messageReference = rpdu[1];
        return true;
    }

    internal static bool TryGetCpUserData(ReadOnlySpan<byte> cpData, out ReadOnlySpan<byte> rpdu)
    {
        rpdu = [];

        if (cpData.Length < 4)
        {
            return false;
        }

        int userDataOffset = cpData.Length >= 5 && cpData[2] == 0x01 ? 4 : 3;
        int userDataLength = cpData[userDataOffset - 1];

        if (userDataLength == 0 || cpData.Length < userDataOffset + userDataLength)
        {
            return false;
        }

        rpdu = cpData.Slice(userDataOffset, userDataLength);
        return true;
    }

    internal static bool TrySkipLengthPrefixed(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset >= data.Length)
        {
            return false;
        }

        int length = data[offset++];
        if (offset + length > data.Length)
        {
            return false;
        }

        offset += length;
        return true;
    }
}
