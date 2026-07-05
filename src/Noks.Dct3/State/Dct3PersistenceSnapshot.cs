using Noks.Dct3.Memory;
using Noks.Dct3.Sim;
namespace Noks.Dct3.State;

public sealed record Dct3PersistenceSnapshot(
    int Version,
    FlashOverlayBlock[] FlashBlocks,
    SimFileOverlay[] SimFiles)
{
    // The restore process omits flash PMM overlays. Firmware runtime PMM records are restart-safe
    // only after a modeled clean shutdown. An arbitrary live snapshot can stop the next boot
    // before the idle screen appears.
    public const int CurrentVersion = 4;

    public static Dct3PersistenceSnapshot Empty { get; } = new(CurrentVersion, [], []);
}
