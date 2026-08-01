namespace Noks.Waku;

public interface IWakuTransportAvailability
{
    event Action<bool>? AvailabilityChanged;
}
