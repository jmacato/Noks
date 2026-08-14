namespace Noks.AvaloniaApp.Emulation;

public sealed record EmulationPacing(double RateScale, double DriftMilliseconds)
{
    public static EmulationPacing Initial { get; } = new(1.0, 0.0);
}
