namespace Noks.Waku;

public interface IWakuTransport
{
    ValueTask<WakuPublishResult> PublishAsync(
        WakuPublishRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<WakuTransportMessage> SubscribeAsync(
        IReadOnlyList<string> contentTopics,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<WakuTransportMessage> QueryStoreAsync(
        WakuStoreQuery query,
        CancellationToken cancellationToken = default);
}
