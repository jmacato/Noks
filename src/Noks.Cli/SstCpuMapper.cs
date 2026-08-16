using Noks.Cpu;

namespace Noks.Cli;

public static class SstCpuMapper
{
    private static readonly ArmBank[] TwoRegBanks = [ArmBank.Svc, ArmBank.Abt, ArmBank.Irq, ArmBank.Und];

    public static void Apply(Arm7Tdmi cpu, SstState state)
    {
        cpu.ForceStatus(state.Cpsr);

        ArmBank current = Arm7Tdmi.GetBankByMode(state.Cpsr & 0x1F);

        for (int i = 0; i < 8; i++)
        {
            cpu.SetGpr(i, state.R[i]);
        }

        cpu.SetGpr(15, state.R[15]);

        if (current == ArmBank.Fiq)
        {
            for (int i = 0; i < 5; i++)
            {
                cpu.SetGpr(8 + i, state.RFiq[i]);
                cpu.SetBanked(ArmBank.None, i, state.R[8 + i]);
            }
        }
        else
        {
            for (int i = 0; i < 5; i++)
            {
                cpu.SetGpr(8 + i, state.R[8 + i]);
                cpu.SetBanked(ArmBank.Fiq, i, state.RFiq[i]);
            }
        }

        if (current == ArmBank.None)
        {
            cpu.SetGpr(13, state.R[13]);
            cpu.SetGpr(14, state.R[14]);
        }
        else
        {
            cpu.SetBanked(ArmBank.None, 5, state.R[13]);
            cpu.SetBanked(ArmBank.None, 6, state.R[14]);
        }

        if (current == ArmBank.Fiq)
        {
            cpu.SetGpr(13, state.RFiq[5]);
            cpu.SetGpr(14, state.RFiq[6]);
        }
        else
        {
            cpu.SetBanked(ArmBank.Fiq, 5, state.RFiq[5]);
            cpu.SetBanked(ArmBank.Fiq, 6, state.RFiq[6]);
        }

        for (int b = 0; b < TwoRegBanks.Length; b++)
        {
            ArmBank whichBank = TwoRegBanks[b];
            uint[] values = BankValues(state, whichBank);

            if (current == whichBank)
            {
                cpu.SetGpr(13, values[0]);
                cpu.SetGpr(14, values[1]);
            }
            else
            {
                cpu.SetBanked(whichBank, 5, values[0]);
                cpu.SetBanked(whichBank, 6, values[1]);
            }
        }

        for (int i = 0; i < 5; i++)
        {
            cpu.SetSpsrRaw((ArmBank)(i + 1), state.Spsr[i]);
        }

        cpu.PrimePipeline(state.Pipeline[0], state.Pipeline[1], (ArmAccess)state.Access);
    }

    public static SstState Capture(Arm7Tdmi cpu)
    {
        ArmBank current = Arm7Tdmi.GetBankByMode(cpu.CpsrValue & 0x1F);

        SstState state = new()
        {
            Cpsr = cpu.CpsrValue,
            Access = (uint)cpu.PipelineAccess,
        };

        for (int i = 0; i < 8; i++)
        {
            state.R[i] = cpu.GetGpr(i);
        }

        state.R[15] = cpu.GetGpr(15);

        if (current == ArmBank.Fiq)
        {
            for (int i = 0; i < 5; i++)
            {
                state.R[8 + i] = cpu.GetBanked(ArmBank.None, i);
                state.RFiq[i] = cpu.GetGpr(8 + i);
            }

            state.RFiq[5] = cpu.GetGpr(13);
            state.RFiq[6] = cpu.GetGpr(14);
        }
        else
        {
            for (int i = 0; i < 5; i++)
            {
                state.R[8 + i] = cpu.GetGpr(8 + i);
                state.RFiq[i] = cpu.GetBanked(ArmBank.Fiq, i);
            }

            state.RFiq[5] = cpu.GetBanked(ArmBank.Fiq, 5);
            state.RFiq[6] = cpu.GetBanked(ArmBank.Fiq, 6);
        }

        if (current == ArmBank.None)
        {
            state.R[13] = cpu.GetGpr(13);
            state.R[14] = cpu.GetGpr(14);
        }
        else
        {
            state.R[13] = cpu.GetBanked(ArmBank.None, 5);
            state.R[14] = cpu.GetBanked(ArmBank.None, 6);
        }

        for (int b = 0; b < TwoRegBanks.Length; b++)
        {
            ArmBank whichBank = TwoRegBanks[b];
            uint[] values = BankValues(state, whichBank);

            if (current == whichBank)
            {
                values[0] = cpu.GetGpr(13);
                values[1] = cpu.GetGpr(14);
            }
            else
            {
                values[0] = cpu.GetBanked(whichBank, 5);
                values[1] = cpu.GetBanked(whichBank, 6);
            }
        }

        for (int i = 0; i < 5; i++)
        {
            state.Spsr[i] = cpu.GetSpsrRaw((ArmBank)(i + 1));
        }

        state.Pipeline[0] = cpu.GetPipelineOpcode(0);
        state.Pipeline[1] = cpu.GetPipelineOpcode(1);

        return state;
    }

    private static uint[] BankValues(SstState state, ArmBank whichBank)
    {
        return whichBank switch
        {
            ArmBank.Svc => state.RSvc,
            ArmBank.Abt => state.RAbt,
            ArmBank.Irq => state.RIrq,
            _ => state.RUnd,
        };
    }
}
