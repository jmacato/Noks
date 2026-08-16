namespace Noks.Cli;

public sealed class SstFailure
{
    public required int TestIndex { get; init; }

    public required uint Opcode { get; init; }

    public required uint InitialCpsr { get; init; }

    public required IReadOnlyList<string> Errors { get; init; }
}
