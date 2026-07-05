namespace Noks.Dct3.Radio;

public sealed record OutgoingNetworkRequest(
    Guid RequestId,
    NetworkRequestKind Kind,
    string NormalizedDestination,
    string SmsText);
