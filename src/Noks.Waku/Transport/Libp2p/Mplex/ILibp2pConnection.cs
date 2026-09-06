namespace Noks.Waku.Transport.Libp2p.Mplex;

internal interface ILibp2pConnection : IAsyncDisposable
{
    bool IsAlive { get; }

    Task Completion { get; }

    ValueTask<MplexStream> OpenStreamAsync(string protocol, CancellationToken cancellationToken);
}
