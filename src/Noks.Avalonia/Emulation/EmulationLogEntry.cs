namespace Noks.AvaloniaApp.Emulation;

public sealed record EmulationLogEntry(
    long Sequence,
    TimeSpan EmulatedTime,
    EmulationLogChannel Channel,
    string Text)
{
    public string DisplayText =>
        $"{EmulatedTime.TotalSeconds,10:F6}  {Channel.ToString().ToUpperInvariant(),-5}  {Text}";
}
