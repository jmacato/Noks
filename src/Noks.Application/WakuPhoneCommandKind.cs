using Noks.Dct3.Radio;
namespace Noks.Application;

public enum WakuPhoneCommandKind
{
    ResolveNetworkRequest,
    QueueIncomingSmartMessage,
    QueueIncomingCall,
    QueueIncomingSms,
    SetManagedOwnNumber,
    BeginCallMedia,
    ActivateCallMedia,
    ApplyCallMediaSignal,
    EndCallMedia,
    ConnectNetworkCall,
    TerminateNetworkCall,
}
