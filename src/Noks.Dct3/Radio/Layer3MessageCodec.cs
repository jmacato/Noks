using Noks.Dct3.Audio;
using Noks.Dct3.Messaging;
using Noks.Dct3.Sim;
using Noks.Dct3.State;

namespace Noks.Dct3.Radio;

internal static class Layer3MessageCodec
{
    internal static byte[] BuildChannelRelease() =>
    [
        GsmProtocol.RadioResourceProtocolDiscriminator, GsmProtocol.ChannelReleaseMessageType, 0x00,
    ];

    internal static byte[] BuildMmInformation(DateTimeOffset networkLocalTime, string networkName)
    {
        byte[] fullNetworkName = networkName.Length == 0 ? [] : BuildFullNetworkName(networkName);
        return
        [
            GsmProtocol.MobilityManagementProtocolDiscriminator,
            GsmProtocol.MmInformationMessageType,
            .. fullNetworkName,
            GsmProtocol.TimeZoneAndTimeInformationElement,
            .. GsmAlphabet.BuildTimestampAndTimeZone(networkLocalTime),
        ];
    }

    internal static byte[] BuildFullNetworkName(string networkName)
    {
        byte[] packed = GsmAlphabet.PackGsm7(networkName, out byte septetCount);
        int spareBits = packed.Length * 8 - septetCount * 7;

        return
        [
            GsmProtocol.FullNetworkNameInformationElement,
            (byte)(packed.Length + 1),
            (byte)(0x80 | spareBits),
            .. packed,
        ];
    }

    internal static byte[] BuildCipheringModeCommand() =>
    [
        GsmProtocol.RadioResourceProtocolDiscriminator, GsmProtocol.CipheringModeCommandMessageType, GsmProtocol.CipherModeSettingA5_1,
    ];

    internal static byte[] BuildCallControlMessage(byte uplinkTransactionAndProtocolDiscriminator, byte messageType) =>
    [
        (byte)(uplinkTransactionAndProtocolDiscriminator ^ 0x80),
        messageType,
    ];

    internal static byte[] BuildMobileTerminatedCallSetup(string callingNumber)
    {
        List<byte> information =
        [
            GsmProtocol.MobileTerminatedCallTransactionAndProtocolDiscriminator,
            GsmProtocol.SetupMessageType,
            0x04, 0x04, 0x60, 0x02, 0x00, 0x81,
            // TCH assignment is not emulated, so the code asks the MS to alert from SETUP.
            GsmProtocol.SignalInformationElement, GsmProtocol.RingBackToneOnSignal,
        ];

        byte[] callingParty = GsmAlphabet.BuildBcdNumberContents(callingNumber);
        information.Add(0x5C);
        information.Add((byte)callingParty.Length);
        information.AddRange(callingParty);
        return information.ToArray();
    }

    internal static bool TryDecodeCalledPartyNumber(
        ReadOnlySpan<byte> setup,
        out string normalizedDestination,
        out bool international)
    {
        normalizedDestination = "";
        international = false;
        for (int index = 2; index + 2 < setup.Length; index++)
        {
            if (setup[index] != 0x5E)
            {
                continue;
            }

            int length = setup[index + 1];
            if (length < 2 || index + 2 + length > setup.Length)
            {
                continue;
            }

            if (GsmAlphabet.TryDecodeSemiOctets(setup.Slice(index + 3, length - 1), null, out normalizedDestination))
            {
                international = (setup[index + 2] & 0x70) == 0x10;
                return true;
            }
        }

        return false;
    }

    internal static byte StripSendSequenceNumber(byte messageType) =>
        (byte)(messageType & GsmProtocol.MessageTypeWithoutSendSequenceNumberMask);
}
