using Noks.Dct3.State;

namespace Noks.Application.Persistence;

public sealed class FilePhonePersistenceStore : IPhonePersistenceStore
{
    public static FilePhonePersistenceStore Default { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Noks",
            "persistence"));

    private readonly string directory;

    public FilePhonePersistenceStore(string directory)
    {
        this.directory = directory;
    }

    public Dct3PersistenceSnapshot? Load(string key)
    {
        string path = PathForKey(key);
        if (!File.Exists(path))
        {
            return null;
        }

        return PhonePersistence.Deserialize(File.ReadAllText(path));
    }

    public async ValueTask<Dct3PersistenceSnapshot?> LoadAsync(string key, CancellationToken cancellationToken)
    {
        string path = PathForKey(key);
        if (!File.Exists(path))
        {
            return null;
        }

        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return PhonePersistence.Deserialize(text);
    }

    public async ValueTask SaveAsync(string key, Dct3PersistenceSnapshot snapshot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        string path = PathForKey(key);
        string tempPath = $"{path}.tmp";
        await File.WriteAllTextAsync(tempPath, PhonePersistence.Serialize(snapshot), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    private string PathForKey(string key) => Path.Combine(directory, $"{key}.json");
}
