namespace Noks.AvaloniaApp.Startup;

public static class FlashPathResolver
{
    private const string DefaultFlashPath = "3310/My 3310 NR2 v.4.18.en.fls";

    public static string Resolve(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--flash" && i + 1 < args.Count)
            {
                return args[i + 1];
            }

            if (!args[i].StartsWith('-'))
            {
                return args[i];
            }
        }

        string? sidecar = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "*.fls", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (sidecar is not null)
        {
            return sidecar;
        }

        string current = Path.GetFullPath(DefaultFlashPath);

        if (File.Exists(current))
        {
            return current;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);

        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, DefaultFlashPath);

            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return current;
    }

    public static string? ResolveExternalEeprom(IReadOnlyList<string> args, string flashPath)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--eeprom" && i + 1 < args.Count)
            {
                return args[i + 1];
            }
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(flashPath));
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(directory, "*.bin", SearchOption.TopDirectoryOnly)
            .Where(file => Path.GetFileName(file).Contains("eeprom", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
