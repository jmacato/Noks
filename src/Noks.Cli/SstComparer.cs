namespace Noks.Cli;

public static class SstComparer
{
    public static void Compare(SstState expected, SstState actual, List<string> errors)
    {
        for (int i = 0; i < 16; i++)
        {
            CompareValue($"R{i}", expected.R[i], actual.R[i], errors);
        }

        for (int i = 0; i < 7; i++)
        {
            CompareValue($"R{8 + i}_fiq", expected.RFiq[i], actual.RFiq[i], errors);
        }

        for (int i = 0; i < 2; i++)
        {
            CompareValue($"R{13 + i}_svc", expected.RSvc[i], actual.RSvc[i], errors);
            CompareValue($"R{13 + i}_abt", expected.RAbt[i], actual.RAbt[i], errors);
            CompareValue($"R{13 + i}_irq", expected.RIrq[i], actual.RIrq[i], errors);
            CompareValue($"R{13 + i}_und", expected.RUnd[i], actual.RUnd[i], errors);
        }

        CompareValue("CPSR", expected.Cpsr, actual.Cpsr, errors);

        string[] spsrNames = ["SPSR_fiq", "SPSR_svc", "SPSR_abt", "SPSR_irq", "SPSR_und"];
        for (int i = 0; i < 5; i++)
        {
            CompareValue(spsrNames[i], expected.Spsr[i], actual.Spsr[i], errors);
        }

        CompareValue("pipeline[0]", expected.Pipeline[0], actual.Pipeline[0], errors);
        CompareValue("pipeline[1]", expected.Pipeline[1], actual.Pipeline[1], errors);
        CompareValue("fetch-access", expected.Access, actual.Access, errors);
    }

    private static void CompareValue(string name, uint expected, uint actual, List<string> errors)
    {
        if (expected != actual)
        {
            errors.Add($"{name}: expected {expected:X8}, got {actual:X8}");
        }
    }
}
