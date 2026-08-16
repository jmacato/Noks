namespace Noks.Cli;

public sealed class SstFileResult
{
    public required string FileName { get; init; }

    public required int Total { get; init; }

    public required int Passed { get; init; }

    public required IReadOnlyList<SstFailure> Failures { get; init; }

    public bool AllPassed => Passed == Total;
}
