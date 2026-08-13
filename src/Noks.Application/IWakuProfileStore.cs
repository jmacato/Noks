namespace Noks.Application;

public interface IWakuProfileStore
{
    ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(string value, CancellationToken cancellationToken = default);
}
