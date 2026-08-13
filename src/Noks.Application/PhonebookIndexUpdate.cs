namespace Noks.Application;

public sealed record PhonebookIndexUpdate(
    IReadOnlyList<string> AddedNumbers,
    IReadOnlyList<string> RemovedNumbers,
    string? WrittenNumber);
