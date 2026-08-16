using Noks.Cpu;

namespace Noks.Cli;

public static class SstRunner
{
    public static SstFileResult RunFile(string path, bool checkAccess, int maxFailures)
    {
        List<SstTest> tests = SstTestFileReader.Load(path);

        int passed = 0;
        List<SstFailure> failures = new();

        foreach (SstTest test in tests)
        {
            SstBus testBus = new(test.Transactions) { CheckAccess = checkAccess };
            Arm7Tdmi cpu = new(testBus);

            List<string> errors;

            try
            {
                SstCpuMapper.Apply(cpu, test.Initial);
                cpu.Step();

                errors = new List<string>(testBus.Errors);

                if (testBus.Remaining > 0)
                {
                    errors.Add($"{testBus.Remaining} expected transaction(s) never issued");
                }

                SstState actual = SstCpuMapper.Capture(cpu);
                SstComparer.Compare(test.Final, actual, errors);
            }
            catch (Exception ex)
            {
                errors = [$"exception: {ex.Message}"];
            }

            if (errors.Count == 0)
            {
                passed++;
            }
            else if (failures.Count < maxFailures)
            {
                failures.Add(new SstFailure
                {
                    TestIndex = test.Index,
                    Opcode = test.Opcode,
                    InitialCpsr = test.Initial.Cpsr,
                    Errors = errors,
                });
            }
        }

        return new SstFileResult
        {
            FileName = Path.GetFileName(path),
            Total = tests.Count,
            Passed = passed,
            Failures = failures,
        };
    }
}
