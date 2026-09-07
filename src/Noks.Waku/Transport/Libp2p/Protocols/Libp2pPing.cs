using System.Security.Cryptography;
using Noks.Waku.Transport.Libp2p.Mplex;

namespace Noks.Waku.Transport.Libp2p.Protocols;

internal static class Libp2pPing
{
    public const string Protocol = "/ipfs/ping/1.0.0";
    private const int ChallengeLength = 32;

    public static async Task HandleAsync(MplexStream stream, CancellationToken cancellationToken)
    {
        while (await stream.ReadExactlyAsync(ChallengeLength, cancellationToken) is { } challenge)
            await stream.SendAsync(challenge, cancellationToken);
        await stream.CloseWriteAsync(cancellationToken);
    }

    public static async Task QueryAsync(ILibp2pConnection connection, CancellationToken cancellationToken)
    {
        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeLength);
        MplexStream stream = await connection.OpenStreamAsync(Protocol, cancellationToken);
        try
        {
            await stream.SendAsync(challenge, cancellationToken);
            byte[]? response = await stream.ReadExactlyAsync(ChallengeLength, cancellationToken);
            if (response is null || !CryptographicOperations.FixedTimeEquals(challenge, response))
                throw new IOException("Libp2p peer returned an invalid ping response.");
        }
        finally
        {
            using CancellationTokenSource closeTimeout = new(TimeSpan.FromSeconds(2));
            try { await stream.CloseWriteAsync(closeTimeout.Token); }
            catch (Exception) { }
        }
    }
}
