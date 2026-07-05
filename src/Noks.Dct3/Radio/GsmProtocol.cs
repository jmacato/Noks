namespace Noks.Dct3.Radio;

internal static class GsmProtocol
{
    internal const byte ChannelReleaseMessageType = 0x0D;
    internal const byte CipheringModeCommandMessageType = 0x35;
    internal const byte CipherModeSettingA5_1 = 0x01;
    internal const byte CpAckMessageType = 0x04;
    internal const byte CpDataMessageType = 0x01;
    internal const byte FullNetworkNameInformationElement = 0x43;
    internal const int MaximumIncomingSmsTextSeptets = 120;
    internal const byte MessageTypeWithoutSendSequenceNumberMask = 0xBF;
    internal const byte MmInformationMessageType = 0x32;
    internal const byte MobileTerminatedCallTransactionAndProtocolDiscriminator = CallControlProtocolDiscriminator;
    internal const byte MobilityManagementProtocolDiscriminator = 0x05;
    internal const byte RadioResourceProtocolDiscriminator = 0x06;
    internal const byte RingBackToneOnSignal = 0x01;
    internal const byte RpAckMobileToNetworkMessageType = 0x02;
    internal const byte RpDataMobileToNetworkMessageType = 0x00;
    internal const byte SetupMessageType = 0x05;
    internal const byte SignalInformationElement = 0x34;
    internal const byte TimeZoneAndTimeInformationElement = 0x47;
    internal const byte CallControlProtocolDiscriminator = 0x03;
    internal const string DefaultIncomingAddress = "12345";
    internal const string DefaultIncomingSmsText = "Hello from Noks";
}
