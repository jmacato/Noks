using Noks.Dct3.Display;
using Noks.Dct3.Messaging;

namespace Noks.Cli;

public static class ConvertDct3Command
{
    public static int Run(string[] args)
    {
        string? output = null;
        string? dir = null;
        string? mcu = null;
        string? ppm = null;
        string? pmm = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir" when i + 1 < args.Length:
                    dir = args[++i];
                    break;
                case "--mcu" when i + 1 < args.Length:
                    mcu = args[++i];
                    break;
                case "--ppm" when i + 1 < args.Length:
                    ppm = args[++i];
                    break;
                case "--pmm" when i + 1 < args.Length:
                    pmm = args[++i];
                    break;
                default:
                    if (output is null && !args[i].StartsWith('-'))
                    {
                        output = args[i];
                    }
                    else
                    {
                        Console.Error.WriteLine($"Unknown or incomplete option: '{args[i]}'.");
                        PrintUsage();
                        return 1;
                    }

                    break;
            }
        }

        if (output is null)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            string[] partPaths = ResolvePartPaths(dir, mcu, ppm, pmm);
            Dct3UpdateImagePart[] parts = partPaths
                .Select(path => new Dct3UpdateImagePart(Path.GetFileName(path), File.ReadAllBytes(path)))
                .ToArray();
            byte[] flash = Dct3UpdateImageConverter.Convert(parts, out Dct3UpdateImagePartSummary[] summaries);
            File.WriteAllBytes(output, flash);

            Console.WriteLine($"Wrote {output}. size=0x{flash.Length:X}");
            foreach (Dct3UpdateImagePartSummary summary in summaries)
            {
                Console.WriteLine($"{summary.Name}: records={summary.Records} range=0x{summary.StartAddress:X6}-0x{summary.EndAddress:X6}");
            }

            return 0;
        }
        catch (Exception ex) when (
            ex is ArgumentException ||
            ex is IOException ||
            ex is InvalidDataException ||
            ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string[] ResolvePartPaths(string? dir, string? mcu, string? ppm, string? pmm)
    {
        if (dir is not null)
        {
            return ResolveDirectoryPartPaths(dir, pmm);
        }

        if (mcu is null || ppm is null)
        {
            throw new ArgumentException("The input options are incomplete. Provide --dir, or provide both --mcu and --ppm.");
        }

        List<string> paths = [mcu, ppm];
        if (pmm is not null)
        {
            paths.Add(pmm);
        }

        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"The part file was not found: {path}.", path);
            }
        }

        return paths.ToArray();
    }

    private static string[] ResolveDirectoryPartPaths(string dir, string? pmm)
    {
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"The directory was not found: {dir}.");
        }

        List<RecordCandidate> candidates = [];
        foreach (string path in Directory.EnumerateFiles(dir).Order(StringComparer.OrdinalIgnoreCase))
        {
            byte[] header = new byte[9];
            using FileStream stream = File.OpenRead(path);
            if (stream.Read(header) != header.Length)
            {
                continue;
            }

            if (Dct3UpdateImageConverter.TryGetFirstRecordAddress(header, out uint address))
            {
                candidates.Add(new RecordCandidate(path, address));
            }
        }

        string? mcu = SinglePath(candidates.Where(c => c.Address < 0x340000), "MCU");
        string? ppm = SinglePath(candidates.Where(c => c.Address >= 0x340000 && c.Address < 0x3D0000), "PPM");
        string? resolvedPmm = pmm is null
            ? SingleOptionalPath(candidates.Where(c => c.Address >= 0x3D0000), "PMM")
            : ResolveDirectoryPath(dir, pmm);

        if (mcu is null || ppm is null)
        {
            throw new ArgumentException("The directory must contain one MCU file and one PPM file.");
        }

        List<string> paths = [mcu, ppm];
        if (resolvedPmm is not null)
        {
            if (!File.Exists(resolvedPmm))
            {
                throw new FileNotFoundException($"The PMM file was not found: {resolvedPmm}.", resolvedPmm);
            }

            paths.Add(resolvedPmm);
        }

        return paths.ToArray();
    }

    private static string? SinglePath(IEnumerable<RecordCandidate> candidates, string label)
    {
        RecordCandidate[] matches = candidates.ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        if (matches.Length > 1)
        {
            throw new ArgumentException($"Multiple {label} files were found: {string.Join(", ", matches.Select(m => Path.GetFileName(m.Path)))}.");
        }

        return matches[0].Path;
    }

    private static string? SingleOptionalPath(IEnumerable<RecordCandidate> candidates, string label)
    {
        RecordCandidate[] matches = candidates.ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        if (matches.Length > 1)
        {
            throw new ArgumentException($"Multiple {label} files were found. Pass --pmm <file>.");
        }

        return matches[0].Path;
    }

    private static string ResolveDirectoryPath(string dir, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(dir, path);

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage: noks convert-dct3 <output.fls> --dir <path> [--pmm <file>]");
        Console.Error.WriteLine("       noks convert-dct3 <output.fls> --mcu <file> --ppm <file> [--pmm <file>]");
    }

    private readonly record struct RecordCandidate(string Path, uint Address);
}
