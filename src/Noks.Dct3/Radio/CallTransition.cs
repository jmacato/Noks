namespace Noks.Dct3.Radio;

public sealed record CallTransition(
    Guid RequestId,
    CallDirection Direction,
    CallTransitionKind Kind,
    string NormalizedRemoteNumber);
