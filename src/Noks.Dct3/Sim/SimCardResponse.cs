using Noks.Dct3.Messaging;
namespace Noks.Dct3.Sim;

public readonly record struct SimCardResponse(byte[] Data, bool Complete);
