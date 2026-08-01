using Noks.Waku.Transport.Libp2p.Wire;

namespace Noks.Waku.Transport.Protocols;

internal static class WakuProtocolCodec
{
    public const string LightPush = "/vac/waku/lightpush/3.0.0";
    public const string LightPushV2 = "/vac/waku/lightpush/2.0.0-beta1";
    public const string FilterSubscribe = "/vac/waku/filter-subscribe/2.0.0-beta1";
    public const string FilterPush = "/vac/waku/filter-push/2.0.0-beta1";
    public const string Store = "/vac/waku/store-query/3.0.0";
    public const int SuccessStatusCode = 200;

    public static byte[] EncodeLightPushRequest(WakuPublishRequest request, out string requestId)
    {
        requestId = Guid.NewGuid().ToString();
        string pubsubTopic = WakuSharding.GetPubsubTopic(request.ContentTopic);
        ProtobufWriter writer = new();
        writer.WriteString(1, requestId);
        writer.WriteString(20, pubsubTopic);
        writer.WriteMessage(21, message => WriteWakuMessage(message, request));
        return writer.ToArray();
    }

    public static WakuStatusResponse DecodeLightPushResponse(ReadOnlySpan<byte> encoded) =>
        DecodeStatusResponse(encoded, relayPeerCountField: 12);

    public static byte[] EncodeFilterSubscribeRequest(
        string pubsubTopic,
        IReadOnlyList<string> contentTopics,
        out string requestId)
    {
        requestId = Guid.NewGuid().ToString();
        ProtobufWriter writer = new();
        writer.WriteString(1, requestId);
        writer.WriteUInt32(2, 1);
        writer.WriteString(10, pubsubTopic);
        foreach (string topic in contentTopics)
            writer.WriteString(11, topic);
        return writer.ToArray();
    }

    public static WakuStatusResponse DecodeFilterSubscribeResponse(ReadOnlySpan<byte> encoded) =>
        DecodeStatusResponse(encoded, relayPeerCountField: null);

    public static WakuWireMessage DecodeFilterPush(ReadOnlySpan<byte> encoded)
    {
        byte[]? wakuMessage = null;
        ProtobufReader reader = new(encoded);
        while (reader.TryReadTag(out int field, out int wireType))
        {
            if (field == 1 && wireType == 2)
                wakuMessage = reader.ReadBytes();
            else
                reader.Skip(wireType);
        }

        return DecodeWakuMessage(wakuMessage ??
            throw new FormatException("Waku Filter push contains no message."));
    }

    public static byte[] EncodeStoreQueryRequest(
        string pubsubTopic,
        IReadOnlyList<string> contentTopics,
        long startUnixMilliseconds,
        long endUnixMilliseconds,
        ReadOnlySpan<byte> paginationCursor,
        out string requestId)
    {
        requestId = Guid.NewGuid().ToString();
        ProtobufWriter writer = new();
        writer.WriteString(1, requestId);
        writer.WriteBool(2, true);
        writer.WriteString(10, pubsubTopic);
        foreach (string topic in contentTopics)
            writer.WriteString(11, topic);
        writer.WriteSInt64(12, MillisecondsToNanoseconds(startUnixMilliseconds));
        writer.WriteSInt64(13, MillisecondsToNanoseconds(endUnixMilliseconds));
        if (!paginationCursor.IsEmpty)
            writer.WriteBytes(51, paginationCursor);
        writer.WriteBool(52, true);
        writer.WriteUInt64(53, 100);
        return writer.ToArray();
    }

    public static WakuStoreResponse DecodeStoreQueryResponse(ReadOnlySpan<byte> encoded)
    {
        string requestId = "";
        uint statusCode = 0;
        string? statusDescription = null;
        List<WakuWireMessage> messages = [];
        byte[]? paginationCursor = null;
        byte[]? lastMessageHash = null;

        ProtobufReader reader = new(encoded);
        while (reader.TryReadTag(out int field, out int wireType))
        {
            switch (field)
            {
                case 1 when wireType == 2:
                    requestId = reader.ReadString();
                    break;
                case 10 when wireType == 0:
                    statusCode = reader.ReadUInt32();
                    break;
                case 11 when wireType == 2:
                    statusDescription = reader.ReadString();
                    break;
                case 20 when wireType == 2:
                    (WakuWireMessage? Message, byte[]? Hash) stored = DecodeStoreMessage(reader.ReadBytes());
                    if (stored.Message is not null)
                        messages.Add(stored.Message.Value);
                    if (stored.Hash is { Length: > 0 })
                        lastMessageHash = stored.Hash;
                    break;
                case 51 when wireType == 2:
                    paginationCursor = reader.ReadBytes();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new WakuStoreResponse(
            requestId,
            statusCode,
            statusDescription,
            messages,
            paginationCursor ?? lastMessageHash);
    }

    public static WakuWireMessage DecodeWakuMessage(ReadOnlySpan<byte> encoded)
    {
        byte[] payload = [];
        string contentTopic = "";
        long timestampNanoseconds = 0;
        bool ephemeral = false;

        ProtobufReader reader = new(encoded);
        while (reader.TryReadTag(out int field, out int wireType))
        {
            switch (field)
            {
                case 1 when wireType == 2:
                    payload = reader.ReadBytes();
                    break;
                case 2 when wireType == 2:
                    contentTopic = reader.ReadString();
                    break;
                case 10 when wireType == 0:
                    timestampNanoseconds = reader.ReadSInt64();
                    break;
                case 31 when wireType == 0:
                    ephemeral = reader.ReadBool();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(contentTopic))
            throw new FormatException("Waku message has no content topic.");
        return new WakuWireMessage(
            contentTopic,
            payload,
            timestampNanoseconds / 1_000_000,
            ephemeral);
    }

    private static void WriteWakuMessage(ProtobufWriter writer, WakuPublishRequest request)
    {
        writer.WriteBytes(1, request.Payload.Span);
        writer.WriteString(2, request.ContentTopic);
        writer.WriteUInt32(3, 0);
        writer.WriteSInt64(10, MillisecondsToNanoseconds(request.TimestampUnixMilliseconds));
        writer.WriteBool(31, request.Ephemeral);
    }

    private static WakuStatusResponse DecodeStatusResponse(
        ReadOnlySpan<byte> encoded,
        int? relayPeerCountField)
    {
        string requestId = "";
        uint statusCode = 0;
        string? statusDescription = null;
        uint? relayPeerCount = null;

        ProtobufReader reader = new(encoded);
        while (reader.TryReadTag(out int field, out int wireType))
        {
            if (field == 1 && wireType == 2)
                requestId = reader.ReadString();
            else if (field == 10 && wireType == 0)
                statusCode = reader.ReadUInt32();
            else if (field == 11 && wireType == 2)
                statusDescription = reader.ReadString();
            else if (field == relayPeerCountField && wireType == 0)
                relayPeerCount = reader.ReadUInt32();
            else
                reader.Skip(wireType);
        }

        return new WakuStatusResponse(requestId, statusCode, statusDescription, relayPeerCount);
    }

    private static (WakuWireMessage? Message, byte[]? Hash) DecodeStoreMessage(ReadOnlySpan<byte> encoded)
    {
        byte[]? hash = null;
        byte[]? message = null;
        ProtobufReader reader = new(encoded);
        while (reader.TryReadTag(out int field, out int wireType))
        {
            if (field == 1 && wireType == 2)
                hash = reader.ReadBytes();
            else if (field == 2 && wireType == 2)
                message = reader.ReadBytes();
            else
                reader.Skip(wireType);
        }

        return (message is null ? null : DecodeWakuMessage(message), hash);
    }

    private static long MillisecondsToNanoseconds(long milliseconds) =>
        checked(milliseconds * 1_000_000);
}
