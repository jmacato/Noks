using System.Collections.Immutable;
using Noks.Waku;
using Noks.Dct3.Radio;

namespace Noks.Application;

public sealed record WakuPhoneCommand(
    WakuPhoneCommandKind Kind,
    Guid RequestId,
    NetworkRequestDecision Decision,
    string Address,
    string Text,
    ushort DestinationPort,
    ImmutableArray<byte> Payload)
{
    public WakuEventKind EventKind { get; init; }

    public bool IsCaller { get; init; }

    public long IssuedAtUnixMilliseconds { get; init; }

    public static WakuPhoneCommand Resolve(Guid requestId, NetworkRequestDecision decision) =>
        new(WakuPhoneCommandKind.ResolveNetworkRequest, requestId, decision, "", "", 0, []);

    public static WakuPhoneCommand SmartMessage(string address, ushort destinationPort, ReadOnlySpan<byte> payload) =>
        new(
            WakuPhoneCommandKind.QueueIncomingSmartMessage,
            Guid.Empty,
            default,
            address,
            "",
            destinationPort,
            ImmutableArray.Create(payload.ToArray()));

    public static WakuPhoneCommand IncomingCall(Guid requestId, string address) =>
        new(WakuPhoneCommandKind.QueueIncomingCall, requestId, default, address, "", 0, []);

    public static WakuPhoneCommand IncomingSms(string address, string text, long issuedAtUnixMilliseconds) =>
        new(WakuPhoneCommandKind.QueueIncomingSms, Guid.Empty, default, address, text, 0, [])
        {
            IssuedAtUnixMilliseconds = issuedAtUnixMilliseconds,
        };

    public static WakuPhoneCommand ManagedOwnNumber(string number) =>
        new(WakuPhoneCommandKind.SetManagedOwnNumber, Guid.Empty, default, number, "", 0, []);

    public static WakuPhoneCommand BeginMedia(Guid attemptId, bool isCaller) =>
        new(WakuPhoneCommandKind.BeginCallMedia, attemptId, default, "", "", 0, [])
        {
            IsCaller = isCaller,
        };

    public static WakuPhoneCommand ActivateMedia(Guid attemptId) =>
        new(WakuPhoneCommandKind.ActivateCallMedia, attemptId, default, "", "", 0, []);

    public static WakuPhoneCommand ApplyMediaSignal(
        Guid attemptId,
        WakuEventKind kind,
        ReadOnlySpan<byte> payload) =>
        new(
            WakuPhoneCommandKind.ApplyCallMediaSignal,
            attemptId,
            default,
            "",
            "",
            0,
            ImmutableArray.Create(payload.ToArray()))
        {
            EventKind = kind,
        };

    public static WakuPhoneCommand EndMedia(Guid attemptId) =>
        new(WakuPhoneCommandKind.EndCallMedia, attemptId, default, "", "", 0, []);

    public static WakuPhoneCommand ConnectCall(Guid attemptId) =>
        new(WakuPhoneCommandKind.ConnectNetworkCall, attemptId, default, "", "", 0, []);

    public static WakuPhoneCommand TerminateCall(Guid attemptId) =>
        new(WakuPhoneCommandKind.TerminateNetworkCall, attemptId, default, "", "", 0, []);
}
