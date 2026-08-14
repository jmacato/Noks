using Noks.Application;
using Noks.Dct3.Radio;
using Noks.AvaloniaApp.Emulation;

namespace Noks.AvaloniaApp.Messaging;

internal static class WakuBridgeIngress
{
    public static void DrainNetworkRequests(PhoneEmulator source, WakuPhoneBridge? bridge)
    {
        ArgumentNullException.ThrowIfNull(source);

        while (source.TryDequeueOutgoingNetworkRequest(out OutgoingNetworkRequest? request) && request is not null)
        {
            if (bridge is null || !bridge.TryEnqueue(request))
            {
                source.ResolveNetworkRequest(new ResolveNetworkRequest(
                    request.RequestId,
                    NetworkRequestDecision.Reject));
            }
        }
    }
}
