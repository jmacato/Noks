namespace Noks.Waku.Tests;

public sealed class WakuEventPolicyTests
{
    [Fact]
    public void MessagesReceiptsAndContactSyncAreDurable()
    {
        Assert.Equal(WakuDeliveryClass.Realtime, WakuEventPolicy.GetDeliveryClass(WakuEventKind.PulseCover));
        Assert.Equal(WakuDeliveryClass.Durable, WakuEventPolicy.GetDeliveryClass(WakuEventKind.Sms));
        Assert.Equal(WakuDeliveryClass.Durable, WakuEventPolicy.GetDeliveryClass(WakuEventKind.DeliveryReceipt));
        Assert.Equal(WakuDeliveryClass.Durable, WakuEventPolicy.GetDeliveryClass(WakuEventKind.ContactUpdate));

        Assert.Equal(WakuDeliveryClass.Durable, WakuEventPolicy.GetDeliveryClass(WakuEventKind.RendezvousRequest));
        Assert.Equal(WakuDeliveryClass.Durable, WakuEventPolicy.GetDeliveryClass(WakuEventKind.RendezvousAccept));
        Assert.Equal(WakuDeliveryClass.Durable, WakuEventPolicy.GetDeliveryClass(WakuEventKind.RendezvousConfirm));
        Assert.Equal(WakuDeliveryClass.Durable, WakuEventPolicy.GetDeliveryClass(WakuEventKind.RendezvousReady));
        Assert.Equal(WakuDeliveryClass.Realtime, WakuEventPolicy.GetDeliveryClass(WakuEventKind.CallInvite));
        Assert.Equal(WakuDeliveryClass.Realtime, WakuEventPolicy.GetDeliveryClass(WakuEventKind.CallConnected));
        Assert.Equal(WakuDeliveryClass.Realtime, WakuEventPolicy.GetDeliveryClass(WakuEventKind.SdpOffer));
        Assert.Equal(WakuDeliveryClass.Realtime, WakuEventPolicy.GetDeliveryClass(WakuEventKind.IceCandidate));
        Assert.Equal(TimeSpan.FromDays(7), WakuEventPolicy.GetMaximumLifetime(WakuEventKind.Sms));
        Assert.Equal(TimeSpan.FromMinutes(2), WakuEventPolicy.GetMaximumLifetime(WakuEventKind.CallInvite));
    }

    [Fact]
    public void UnknownKindsAreRejected()
    {
        Assert.False(WakuEventPolicy.IsKnown((WakuEventKind)255));
        Assert.Throws<ArgumentOutOfRangeException>(() => WakuEventPolicy.GetDeliveryClass((WakuEventKind)255));
    }

    [Fact]
    public void RealtimeEventsCannotOutliveSignalingWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WakuApplicationMessage(
            Guid.NewGuid(),
            WakuEventKind.CallInvite,
            1_750_000_000_000,
            1_750_000_000_000 + (long)TimeSpan.FromMinutes(3).TotalMilliseconds,
            new byte[33],
            new byte[32],
            []));
    }
}
