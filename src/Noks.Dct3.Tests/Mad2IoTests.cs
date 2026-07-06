using Noks.Dct3.Core;
using Noks.Dct3.Display;
using Noks.Dct3.Input;
using Noks.Dct3.Peripherals;
using Noks.Dct3.Sim;
namespace Noks.Dct3.Tests;

public sealed class Mad2IoTests
{
    [Fact]
    public void KeyMatrix_ReadsSelectedColumnsAsActiveLowBits()
    {
        Dct3KeyMatrix keyMatrix = new();
        Dct3Machine machine = new(new byte[0x200000], keyMatrix: keyMatrix);
        machine.Io.SetStartupPowerKeyHeld(false);

        machine.Io.Write(0x28, 0xFE);
        Assert.Equal(0xFF, machine.Io.Read(0x2A));

        keyMatrix.SetKey(column: 0, row: 2, pressed: true);
        Assert.Equal(0xFB, machine.Io.Read(0x2A));

        keyMatrix.SetKey(column: 1, row: 3, pressed: true);
        Assert.Equal(0xFB, machine.Io.Read(0x2A));

        machine.Io.Write(0x28, 0xFC);
        Assert.Equal(0xF3, machine.Io.Read(0x2A));

        keyMatrix.SetKey(column: 0, row: 2, pressed: false);
        Assert.Equal(0xF7, machine.Io.Read(0x2A));
    }

    [Fact]
    public void KeyMatrix_PowerKeyUses3310ReadPortBitByDefault()
    {
        Dct3KeyMatrix keyMatrix = new();
        Dct3Machine machine = new(new byte[0x200000], keyMatrix: keyMatrix);
        machine.Io.SetStartupPowerKeyHeld(false);

        Assert.Equal(0xFF, machine.Io.Read(0x2A));

        keyMatrix.SetPowerKey(true);

        Assert.Equal(0xFD, machine.Io.Read(0x2A));
    }

    [Fact]
    public void UifExternalInputs_DefaultToPulledHigh()
    {
        Dct3Machine machine = new(new byte[0x200000]);

        Assert.Equal(0xFF, machine.Io.Read(0xF0));
        Assert.Equal(0xFF, machine.Io.Read(0xF1));
        Assert.Equal(0xFF, machine.Io.Read(0xF2));
        Assert.Equal(0xFF, machine.Io.Read(0xF3));
    }

    [Fact]
    public void Reset_PreservesReleasedStartupPowerKeyWhenRequested()
    {
        Dct3Machine machine = new(new byte[0x200000]);
        Assert.True(machine.Io.StartupPowerKeyHeld);

        machine.Io.SetStartupPowerKeyHeld(false);
        machine.Io.Reset(startupPowerKeyHeld: machine.Io.StartupPowerKeyHeld);

        Assert.False(machine.Io.StartupPowerKeyHeld);
        Assert.Equal(0xFF, machine.Io.Read(0x2A));

        machine.Io.Reset();

        Assert.True(machine.Io.StartupPowerKeyHeld);
        Assert.Equal(0xFD, machine.Io.Read(0x2A));
    }

    [Fact]
    public void KeyMatrix_ConcurrentSetPreservesPressedBits()
    {
        Dct3KeyMatrix keyMatrix = new();

        Parallel.For(0, Dct3KeyMatrix.ColumnCount * Dct3KeyMatrix.RowCount, index =>
        {
            keyMatrix.SetKey(index / Dct3KeyMatrix.RowCount, index % Dct3KeyMatrix.RowCount, pressed: true);
        });

        for (int column = 0; column < Dct3KeyMatrix.ColumnCount; column++)
        {
            byte select = (byte)(0xFF & ~(1 << column));
            Assert.Equal(0x00, keyMatrix.ReadSelectedColumns(select));
        }
    }

    [Fact]
    public void KeyMatrix_ConcurrentChangesPublishCoherentGenerations()
    {
        Dct3KeyMatrix keyMatrix = new();
        int keyCount = Dct3KeyMatrix.ColumnCount * Dct3KeyMatrix.RowCount;

        Parallel.For(0, keyCount, index =>
        {
            keyMatrix.SetKey(index / Dct3KeyMatrix.RowCount, index % Dct3KeyMatrix.RowCount, pressed: true);
        });

        Assert.Equal((keyCount, keyCount), keyMatrix.Generations);

        Parallel.For(0, keyCount, index =>
        {
            keyMatrix.SetKey(index / Dct3KeyMatrix.RowCount, index % Dct3KeyMatrix.RowCount, pressed: false);
        });

        Assert.Equal((keyCount * 2, keyCount), keyMatrix.Generations);
    }

    [Fact]
    public void Timer0TicksUntilCompare_TracksNextCompareMatch()
    {
        Dct3Machine machine = new(new byte[0x200000]);
        machine.Io.Write(0x12, 0x00);
        machine.Io.Write(0x13, 0x03);

        Assert.Equal(3, machine.Io.Timer0TicksUntilCompare);

        machine.Io.TickTimer0();

        Assert.Equal(2, machine.Io.Timer0TicksUntilCompare);
    }

    [Fact]
    public void Timer0Compare_LatchesFiqWithoutOverridingInterruptDisable()
    {
        Dct3Machine machine = new(new byte[0x200000]);
        machine.Io.Write(0x0A, 0x00);
        machine.Io.Write(0x0B, 0x00);
        machine.Io.Write(0x0C, 0x0A);
        machine.Io.Write(0x12, 0x00);
        machine.Io.Write(0x13, 0x01);

        machine.Io.TickTimer0();

        Assert.Equal(0x0A, machine.Io.Read(0x0C) & 0x0F);
        Assert.Equal(0x10, machine.Io.EffectiveFiqStatusValue & 0x10);
        Assert.False(machine.Cpu.FiqLine);
        Assert.False(machine.Cpu.IrqLine);

        machine.Io.Write(0x0C, 0x05);

        Assert.True(machine.Cpu.FiqLine);
        Assert.False(machine.Cpu.IrqLine);
    }

    [Fact]
    public void Timer0Compare_DuringSchedulerCriticalSection_DoesNotNestFiq()
    {
        const uint msrCpsrFieldsR1 = 0xE129F001;
        const uint nop = 0xE1A00000;
        const uint interruptDisableBits = 0xC0;
        Dct3Machine machine = new(new byte[0x200000]);
        Noks.Cpu.Arm7Tdmi cpu = machine.Cpu;

        // Put the CPU's interrupt-sampling latch in the enabled System-mode state.
        cpu.ForceStatus(Noks.Cpu.Arm7Tdmi.ModeSys);
        cpu.SetGpr(15, Dct3Machine.FlashBase + 8);
        cpu.PrimePipeline(nop, nop, Noks.Cpu.ArmAccess.Code | Noks.Cpu.ArmAccess.Sequential);
        cpu.Step();

        // The v4.18 FIQ scheduler uses this critical sequence. MAD2 interrupts stop
        // before MSR changes System mode to masked FIQ mode. A timer compare can latch here.
        // It must not rewrite CTSI or preempt the pending MSR.
        cpu.SetGpr(1, Noks.Cpu.Arm7Tdmi.ModeFiq | interruptDisableBits);
        cpu.SetGpr(15, Dct3Machine.FlashBase + 8);
        cpu.PrimePipeline(
            msrCpsrFieldsR1,
            nop,
            Noks.Cpu.ArmAccess.Code | Noks.Cpu.ArmAccess.Sequential);
        machine.Io.Write(0x0A, 0x00);
        machine.Io.Write(0x0C, 0x0A);
        machine.Io.Write(0x12, 0x00);
        machine.Io.Write(0x13, 0x01);

        machine.Io.TickTimer0();
        cpu.Step();

        Assert.Equal(0x0A, machine.Io.InterruptControlRegister & 0x0F);
        Assert.False(cpu.FiqLine);
        Assert.Equal(Noks.Cpu.Arm7Tdmi.ModeFiq | interruptDisableBits, cpu.CpsrValue);
        Assert.Equal(Dct3Machine.FlashBase + 0x0C, cpu.GetGpr(15));
        Assert.Equal(0u, cpu.GetGpr(14));
    }

    [Fact]
    public void Timer1TicksUntilInterrupt_TracksHalfRangeInterrupt()
    {
        Dct3Machine machine = new(new byte[0x200000]);

        Assert.Equal(0x8000, machine.Io.Timer1TicksUntilInterrupt);

        machine.Io.TickTimer1();

        Assert.Equal(0x7FFF, machine.Io.Timer1TicksUntilInterrupt);
    }

    [Fact]
    public void Timer1Destination_ReadsHalfRangeInterruptCounter()
    {
        Dct3Machine machine = new(new byte[0x200000]);

        Assert.Equal(0x80, machine.Io.Read(0x06));
        Assert.Equal(0x00, machine.Io.Read(0x07));

        machine.Io.TickTimer1();

        Assert.Equal(0x80, machine.Io.Read(0x06));
        Assert.Equal(0x00, machine.Io.Read(0x07));
        Assert.Equal(0x7FFF, machine.Io.Timer1TicksUntilInterrupt);
    }

    [Fact]
    public void Fiq8TimerEnabled_ReflectsControlBit()
    {
        Dct3Machine machine = new(new byte[0x200000]);

        Assert.False(machine.Io.Fiq8TimerEnabled);

        machine.Io.Write(0x16, 0x01);

        Assert.True(machine.Io.Fiq8TimerEnabled);
    }

    [Fact]
    public void SimControl_LegacyConfig_QueuesAtr()
    {
        Dct3Machine machine = new(new byte[0x200000]);

        machine.Io.Write(0x39, 0x32);

        Assert.NotEqual(0, machine.Io.Read(0x39) & SerialBytePort.CardReadyStatus);

        machine.Io.TickSim(machine.Io.NextSimWakeCycle);

        Assert.Equal(1, machine.Io.Read(0x3C));
        Assert.Equal(0x3B, machine.Io.Read(0x37));
    }

    [Fact]
    public void SimControl_LegacyCardReadyStaysVisibleAfterAtr()
    {
        Mad2Io io = CreateMad2Io();

        io.Write(0x39, 0x32);

        while (io.NextSimWakeCycle != long.MaxValue)
        {
            io.TickSim(io.NextSimWakeCycle);
            while (io.Read(0x3C) != 0)
            {
                _ = io.Read(0x37);
            }
        }

        byte visibleControl = io.Read(0x39);
        Assert.NotEqual(0, visibleControl & SerialBytePort.CardReadyStatus);
        Assert.NotEqual(0, visibleControl & SerialBytePort.ReceiveCompleteStatus);
    }

    [Fact]
    public void SimControl_ResetQueuesAtr()
    {
        Dct3Machine machine = new(new byte[0x200000]);

        machine.Io.Write(0x39, 0x80);

        Assert.NotEqual(0, machine.Io.Read(0x39) & SerialBytePort.CardReadyStatus);

        machine.Io.TickSim(machine.Io.NextSimWakeCycle);

        Assert.Equal(1, machine.Io.Read(0x3C));
        Assert.Equal(0x3B, machine.Io.Read(0x37));
    }

    private static Mad2Io CreateMad2Io() =>
        new(
            new Ccont(CcontAdcInputs.NormalBattery(), null),
            new Dct3KeyMatrix(),
            new Pcd8544(),
            new SimCard(null),
            null,
            Dct3KeyMap.Nokia3310,
            null);

    [Fact]
    public void MbusTimerAck_RearmsAfterDelay_NotImmediately()
    {
        Dct3Machine machine = new(new byte[0x200000]);
        machine.Io.Write(0x0A, 0x00);
        machine.Io.Write(0x18, 0x40);

        machine.Io.Write(0x08, 0x08);

        Assert.Equal(0, machine.Io.EffectiveFiqStatusValue & 0x08);
        Assert.True(machine.Io.NextMbusWakeCycle < long.MaxValue);

        machine.Io.TickMbusTimer(machine.Io.NextMbusWakeCycle - 1);
        Assert.Equal(0, machine.Io.EffectiveFiqStatusValue & 0x08);

        machine.Io.TickMbusTimer(machine.Io.NextMbusWakeCycle);
        Assert.Equal(0x08, machine.Io.EffectiveFiqStatusValue & 0x08);
    }
}
