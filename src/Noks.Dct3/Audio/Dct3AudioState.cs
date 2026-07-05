using Noks.Dct3.Radio;
namespace Noks.Dct3.Audio;

/// <summary>
/// This is the complete audio state that firmware controls.
/// The shared PCM renderer receives this state.
/// </summary>
public sealed record Dct3AudioState(
    Mad2AudioState Buzzer,
    DspToneState DspTone)
{
    public static Dct3AudioState Off { get; } = new(Mad2AudioState.Off, DspToneState.Off);

    public bool Audible => Buzzer.Audible || DspTone.Audible;
}
