namespace Noks.Waku;

public enum WakuEventKind : byte
{
    PulseCover = 0,

    Sms = 1,
    DeliveryReceipt = 2,
    ContactUpdate = 3,

    RendezvousRequest = 16,
    RendezvousAccept = 17,
    RendezvousDecline = 18,
    ReconnectWake = 19,
    RendezvousConfirm = 20,
    RendezvousReady = 21,

    CallInvite = 32,
    CallRinging = 33,
    CallAccept = 34,
    CallReject = 35,
    CallHangup = 36,
    CallFailed = 37,
    CallConnected = 38,
    SdpOffer = 40,
    SdpAnswer = 41,
    IceCandidate = 42,
}
