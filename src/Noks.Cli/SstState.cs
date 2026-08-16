namespace Noks.Cli;

public sealed class SstState
{
    public uint[] R { get; init; } = new uint[16];

    public uint[] RFiq { get; init; } = new uint[7];

    public uint[] RSvc { get; init; } = new uint[2];

    public uint[] RAbt { get; init; } = new uint[2];

    public uint[] RIrq { get; init; } = new uint[2];

    public uint[] RUnd { get; init; } = new uint[2];

    public uint Cpsr { get; init; }

    public uint[] Spsr { get; init; } = new uint[5];

    public uint[] Pipeline { get; init; } = new uint[2];

    public uint Access { get; init; }
}
