namespace Noks.Waku;

public enum ContactCardValidationFailure
{
    None,
    Malformed,
    UnsupportedVersion,
    InvalidGeneration,
    InvalidTime,
    InvalidStableId,
    InvalidSignature,
    WrongEnvelopeBinding,
    WrongMailboxBinding,
}
