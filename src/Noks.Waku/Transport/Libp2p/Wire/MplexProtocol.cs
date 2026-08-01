namespace Noks.Waku.Transport.Libp2p.Wire;

internal static class MplexProtocol
{
    public const string Protocol = "/mplex/6.7.0";

    public const int NewStream = 0;
    public const int MessageReceiver = 1;
    public const int MessageInitiator = 2;
    public const int CloseReceiver = 3;
    public const int CloseInitiator = 4;
    public const int ResetReceiver = 5;
    public const int ResetInitiator = 6;
}
