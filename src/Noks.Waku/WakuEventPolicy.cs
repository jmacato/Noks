namespace Noks.Waku;

public static class WakuEventPolicy
{
    public static readonly TimeSpan MaximumDurableLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaximumRealtimeLifetime = TimeSpan.FromMinutes(2);

    public static WakuDeliveryClass GetDeliveryClass(WakuEventKind kind) => kind switch
    {
        WakuEventKind.PulseCover => WakuDeliveryClass.Realtime,

        WakuEventKind.Sms or
        WakuEventKind.DeliveryReceipt or
        WakuEventKind.ContactUpdate or
        WakuEventKind.RendezvousRequest or
        WakuEventKind.RendezvousAccept or
        WakuEventKind.RendezvousDecline or
        WakuEventKind.RendezvousConfirm or
        WakuEventKind.RendezvousReady => WakuDeliveryClass.Durable,

        WakuEventKind.ReconnectWake or
        WakuEventKind.CallInvite or
        WakuEventKind.CallRinging or
        WakuEventKind.CallAccept or
        WakuEventKind.CallReject or
        WakuEventKind.CallHangup or
        WakuEventKind.CallFailed or
        WakuEventKind.CallConnected or
        WakuEventKind.SdpOffer or
        WakuEventKind.SdpAnswer or
        WakuEventKind.IceCandidate => WakuDeliveryClass.Realtime,

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Waku event kind."),
    };

    public static bool IsKnown(WakuEventKind kind)
    {
        try
        {
            _ = GetDeliveryClass(kind);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public static bool ShouldPublishEphemeral(WakuEventKind kind) =>
        GetDeliveryClass(kind) == WakuDeliveryClass.Realtime;

    public static TimeSpan GetMaximumLifetime(WakuEventKind kind) => GetDeliveryClass(kind) switch
    {
        WakuDeliveryClass.Durable => MaximumDurableLifetime,
        WakuDeliveryClass.Realtime => MaximumRealtimeLifetime,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Waku event kind."),
    };
}
