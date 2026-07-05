namespace Noks.Dct3.Audio;

public sealed record Mad2AudioState(
    bool BuzzerEnabled,
    byte BuzzerDivider,
    byte BuzzerVolume)
{
    public static Mad2AudioState Off { get; } = new(false, 0, 0);

    public bool Audible => BuzzerEnabled && BuzzerDivider != 0 && BuzzerVolume != 0;
}
