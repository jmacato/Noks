namespace Noks.WebRtc;

public enum WebRtcSignalKind { Offer, Answer, Candidate }
public sealed record WebRtcSignal(WebRtcSignalKind Kind, string Json);
public enum WebRtcState { New, Connecting, Connected, Failed, Closed }
public sealed record WebRtcStatistics(long SentPackets, long ReceivedPackets, long ReceivedSamples, long ReceivedEnergy, long ReceivedControlPackets);
