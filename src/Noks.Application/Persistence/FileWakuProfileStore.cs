using Noks.Application;

namespace Noks.Application.Persistence;

public sealed class FileWakuProfileStore : IWakuProfileStore
{
    public static FileWakuProfileStore Default { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Noks",
            "profile.json"));

    private readonly string path;

    public FileWakuProfileStore(string path)
    {
        this.path = path;
    }

    public async ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveAsync(string value, CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{path}.tmp";
        await File.WriteAllTextAsync(tempPath, value, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }
}
