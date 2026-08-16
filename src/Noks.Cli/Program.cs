namespace Noks.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] == "run")
        {
            return RunCommand.Run(args[1..]);
        }

        if (args[0] == "convert-dct3")
        {
            return ConvertDct3Command.Run(args[1..]);
        }

        if (args[0] != "sst")
        {
            Console.Error.WriteLine($"Unknown command: '{args[0]}'.");
            PrintUsage();
            return 1;
        }

        string dir = Path.Combine("external", "ARM7TDMI", "v1");
        string? filter = null;
        int maxFailures = 3;
        bool checkAccess = true;
        bool sequential = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir" when i + 1 < args.Length:
                    dir = args[++i];
                    break;
                case "--filter" when i + 1 < args.Length:
                    filter = args[++i];
                    break;
                case "--details" when i + 1 < args.Length:
                    maxFailures = int.Parse(args[++i]);
                    break;
                case "--no-access-check":
                    checkAccess = false;
                    break;
                case "--sequential":
                    sequential = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete option: '{args[i]}'.");
                    PrintUsage();
                    return 1;
            }
        }

        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"The test directory was not found: '{dir}'.");
            Console.Error.WriteLine("Clone https://github.com/SingleStepTests/ARM7TDMI to external/ARM7TDMI. Alternatively, pass --dir.");
            return 1;
        }

        string[] files = Directory.GetFiles(dir, "*.json.bin");
        Array.Sort(files, StringComparer.Ordinal);

        if (filter is not null)
        {
            files = Array.FindAll(files, f => Path.GetFileName(f).Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        if (files.Length == 0)
        {
            Console.Error.WriteLine("No matching *.json.bin files were found.");
            return 1;
        }

        SstFileResult[] results = new SstFileResult[files.Length];

        if (sequential)
        {
            for (int i = 0; i < files.Length; i++)
            {
                results[i] = SstRunner.RunFile(files[i], checkAccess, maxFailures);
            }
        }
        else
        {
            Parallel.For(0, files.Length, i => results[i] = SstRunner.RunFile(files[i], checkAccess, maxFailures));
        }

        long totalTests = 0;
        long totalPassed = 0;
        int filesFailed = 0;

        foreach (SstFileResult result in results)
        {
            totalTests += result.Total;
            totalPassed += result.Passed;

            string verdict = result.AllPassed ? "PASS" : "FAIL";
            Console.WriteLine($"{result.FileName,-44} {result.Passed,6}/{result.Total,-6} {verdict}");

            if (result.AllPassed)
            {
                continue;
            }

            filesFailed++;

            foreach (SstFailure failure in result.Failures)
            {
                Console.WriteLine($"    test #{failure.TestIndex} opcode={failure.Opcode:X8} cpsr={failure.InitialCpsr:X8}");
                foreach (string error in failure.Errors)
                {
                    Console.WriteLine($"        {error}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"total: {totalPassed}/{totalTests} passed, {files.Length - filesFailed}/{files.Length} files clean");

        return totalPassed == totalTests ? 0 : 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("usage: noks sst [--dir <path>] [--filter <substring>] [--details <n>] [--no-access-check] [--sequential]");
        Console.WriteLine("       noks run <flash.fls> [--steps <n>] [--accelerate-idle] [--deterministic-time] [--iolog] [--ccontlog] [--dsplog] [--log-limit <n>] [--lcd-log] [--lcd-log-limit <n>] [--key <name@step[:hold]>] [--adc <name@step:value>] [--dsp-rssi <step:value>] [--watch <addr[:len]>] [--probe <addr>] [--probe-after <step>] [--patch <addr:hexbytes>] [--lcd-pgm <path>] [--flash-out <path>]");
        Console.WriteLine("       noks convert-dct3 <output.fls> --dir <path> [--pmm <file>]");
        Console.WriteLine("       noks convert-dct3 <output.fls> --mcu <file> --ppm <file> [--pmm <file>]");
        Console.WriteLine();
        Console.WriteLine("sst runs the SingleStepTests/ARM7TDMI suite against the Noks ARM7TDMI core.");
        Console.WriteLine("run boots a DCT3 flash image on the emulated MAD2 baseband.");
        Console.WriteLine("convert-dct3 converts split DCT3 update records into a flat flash image.");
    }
}
