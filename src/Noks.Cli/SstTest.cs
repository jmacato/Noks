namespace Noks.Cli;

public sealed class SstTest
{
    public required int Index { get; init; }

    public required SstState Initial { get; init; }

    public required SstState Final { get; init; }

    public required SstTransaction[] Transactions { get; init; }

    public required uint Opcode { get; init; }

    public required uint BaseAddr { get; init; }
}
