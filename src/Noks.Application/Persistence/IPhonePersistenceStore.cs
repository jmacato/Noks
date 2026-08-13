using Noks.Dct3.State;

namespace Noks.Application.Persistence;

public interface IPhonePersistenceStore
{
    ValueTask<Dct3PersistenceSnapshot?> LoadAsync(string key, CancellationToken cancellationToken);

    ValueTask SaveAsync(string key, Dct3PersistenceSnapshot snapshot, CancellationToken cancellationToken);
}
