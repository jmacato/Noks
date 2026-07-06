using System.Buffers.Binary;
using Noks.Dct3.Core;
using Noks.Dct3.Peripherals;
using Noks.Dct3.Radio;
using Noks.Dct3.Sim;
using Noks.Dct3.State;
using Noks.Dct3.Firmware;

namespace Noks.Dct3.Tests;

public sealed class Dct3MachineTests
{
    private const int V418RandomAccessMatcherOffset = 0x72D1A;
    private const int V418RandomAccessLiteralOffset = 0x72F5C;
    private const int V639RandomAccessMatcherOffset = 0x7FEBA;
    private const int V639RandomAccessLiteralOffset = 0x80144;
    private const int V607SimLockCheckRoutineOffset = 0x18EE2;
    private const int V607SimLockCheckLiteralOffset = 0x19248;
    private const int V607AutomaticKeyguardSettingOffset = 0x3E8102 - 0x200000;
    private const int V607AutomaticKeyguardEnabledByteOffset = V607AutomaticKeyguardSettingOffset + 0x27;
    private const int V639FirmwareVersionOffset = 0x1FC;
    private const int V639FirmwareModelOffset = 0x20D;

    [Fact]
    public void Constructor_GenericFirmware_UsesDefaultSimImsi()
    {
        Dct3Machine machine = new(new byte[0x200000]);

        Assert.Equal(SimCard.DefaultImsi, machine.Sim.Imsi);
    }

    [Fact]
    public void DspRunBoundary_ReplaysDesiredFacadeCellAfterGuestReset()
    {
        Dct3Machine machine = new(new byte[0x200000]);
        byte[] sharedRam = machine.Bus.DspSharedRam;

        machine.SetFacadeNetworkAvailable(false);
        machine.Io.Write(0x02, 0x01);
        Assert.False(machine.Dsp.FacadeNetworkAvailable);
        Assert.Equal(0x83, ReadDspMdiRcvByte(sharedRam, 1));
        Assert.Equal(Dsp.NoSignalRssiMeasurement, ReadDspMdiRcvByte(sharedRam, 4));
        AcknowledgeDspMdiRcv(sharedRam, machine.Dsp);
        machine.ServicePendingPeripherals();

        WriteDspMdiSend(sharedRam, type: 0x56, [0x03, 0xEC]);
        machine.Dsp.OnSharedWrite(0x0A4, ReadDsp16(sharedRam, 0x0A4), size: 2);
        Assert.Equal(ReadDsp16(sharedRam, 0x1CA), ReadDsp16(sharedRam, 0x1C8));

        machine.SetFacadeNetworkAvailable(true);
        machine.ServicePendingPeripherals();
        Assert.True(machine.Dsp.FacadeNetworkAvailable);
        Assert.Equal(Dsp.DefaultRssiMeasurement, ReadDspMdiRcvByte(sharedRam, 4));
        AcknowledgeDspMdiRcv(sharedRam, machine.Dsp);
        Assert.Equal(0x80, ReadDspMdiRcvByte(sharedRam, 1));
        Assert.Equal(0x40, ReadDspMdiRcvByte(sharedRam, 2));

        // Drain SCH plus SI2/SI3/SI4 before the guest restart test.
        for (int i = 0; i < 4; i++)
        {
            AcknowledgeDspMdiRcv(sharedRam, machine.Dsp);
        }
        Assert.Equal(ReadDsp16(sharedRam, 0x1CA), ReadDsp16(sharedRam, 0x1C8));

        machine.Io.Write(0x02, 0x00);
        machine.Reset();
        machine.Io.Write(0x02, 0x01);

        Assert.True(machine.Dsp.FacadeNetworkAvailable);
        Assert.Equal(Dsp.DefaultRssiMeasurement, ReadDspMdiRcvByte(sharedRam, 4));
        AcknowledgeDspMdiRcv(sharedRam, machine.Dsp);
        Assert.Equal(0x80, ReadDspMdiRcvByte(sharedRam, 1));
        Assert.Equal(0x40, ReadDspMdiRcvByte(sharedRam, 2));
    }

    [Fact]
    public void FacadeNetworkQueue_CoalescesToLatestStateBeforeDspStart()
    {
        Dct3Machine machine = new(new byte[0x200000]);
        byte[] sharedRam = machine.Bus.DspSharedRam;

        machine.SetFacadeNetworkAvailable(false);
        machine.SetFacadeNetworkAvailable(true);
        machine.Io.Write(0x02, 0x01);

        Assert.True(machine.Dsp.FacadeNetworkAvailable);
        Assert.Equal(Dsp.DefaultRssiMeasurement, ReadDspMdiRcvByte(sharedRam, 4));
        AcknowledgeDspMdiRcv(sharedRam, machine.Dsp);

        machine.ServicePendingPeripherals();
        Assert.True(machine.Dsp.FacadeNetworkAvailable);
        Assert.Equal(ReadDsp16(sharedRam, 0x1CA), ReadDsp16(sharedRam, 0x1C8));

        // Rewriting RUN=1 is not a new service boundary. It must not enqueue a
        // duplicate availability refresh.
        machine.Io.Write(0x02, 0x01);
        Assert.Equal(ReadDsp16(sharedRam, 0x1CA), ReadDsp16(sharedRam, 0x1C8));

        WriteDspMdiSend(sharedRam, type: 0x56, [0x03, 0xEC]);
        machine.Dsp.OnSharedWrite(0x0A4, ReadDsp16(sharedRam, 0x0A4), size: 2);
        Assert.Equal(0x80, ReadDspMdiRcvByte(sharedRam, 1));
        Assert.Equal(0x40, ReadDspMdiRcvByte(sharedRam, 2));
    }

    [Fact]
    public void Constructor_GenericFirmware_PreservesAutomaticKeyguardPmmRecord()
    {
        byte[] flash = new byte[0x200000];
        WriteV607AutomaticKeyguardSetting(flash);

        Dct3Machine machine = new(flash);

        Assert.Equal(0x01, machine.Flash.Data[V607AutomaticKeyguardEnabledByteOffset]);
    }

    [Fact]
    public void Constructor_V607SimLockFirmware_UsesTestNetworkImsiByDefault()
    {
        Dct3Machine machine = new(BuildV607SimLockFirmware());

        Assert.Equal(SimCard.DefaultTestNetworkImsi, machine.Sim.Imsi);
    }

    [Fact]
    public void Constructor_V607SimLockFirmware_PreservesExplicitSimImsi()
    {
        const string explicitImsi = "208010000000001";

        Dct3Machine machine = new(BuildV607SimLockFirmware(), simImsi: explicitImsi);

        Assert.Equal(explicitImsi, machine.Sim.Imsi);
    }

    [Fact]
    public void Constructor_GenericFirmware_UsesSettingsSimImsi()
    {
        const string settingsImsi = "001010000000001";

        Dct3Machine machine = new(new byte[0x200000], settings: new Dct3PhoneSettings(SimImsi: settingsImsi));

        Assert.Equal(settingsImsi, machine.Sim.Imsi);
    }

    [Fact]
    public void LegacyRfDataRead_ReturnsIdleStatusInsteadOfLastWrittenByte()
    {
        Dct3Machine machine = new(new byte[0x200000]);

        machine.Bus.WriteByte(0x600000, 0x04, Noks.Cpu.ArmAccess.Nonsequential);
        machine.Bus.WriteByte(0x600100, 0x7F, Noks.Cpu.ArmAccess.Nonsequential);

        Assert.Equal(0x04u, machine.Bus.ReadByte(0x600000, Noks.Cpu.ArmAccess.Nonsequential));
        Assert.Equal(0x00u, machine.Bus.ReadByte(0x600100, Noks.Cpu.ArmAccess.Nonsequential));
    }

    [Fact]
    public void Constructor_V639Firmware_UsesTestNetworkImsiByDefault()
    {
        Dct3Machine machine = new(BuildV639Firmware());

        Assert.Equal(SimCard.DefaultTestNetworkImsi, machine.Sim.Imsi);
    }

    [Fact]
    public void Constructor_V607SimLockFirmware_DisablesAutomaticKeyguardPmmRecord()
    {
        byte[] flash = BuildV607SimLockFirmware();
        WriteV607AutomaticKeyguardSetting(flash);

        Dct3Machine machine = new(flash);

        Assert.Equal(0x00, machine.Flash.Data[V607AutomaticKeyguardEnabledByteOffset]);
    }

    [Fact]
    public void Constructor_V418StyleRachMatcher_ResolvesRandomAccessReferenceTable()
    {
        Dct3Machine machine = new(BuildFirmwareWithRandomAccessMatcher(
            V418RandomAccessMatcherOffset,
            ldrR7Literal: 0x4F87,
            V418RandomAccessLiteralOffset,
            tableAddress: 0x00119F80));

        Assert.Equal(0x19F80, machine.RandomAccessReferenceTableOffset);
    }

    [Fact]
    public void Constructor_V639StyleRachMatcher_ResolvesRandomAccessReferenceTable()
    {
        Dct3Machine machine = new(BuildFirmwareWithRandomAccessMatcher(
            V639RandomAccessMatcherOffset,
            ldrR7Literal: 0x4F99,
            V639RandomAccessLiteralOffset,
            tableAddress: 0x00111904));

        Assert.Equal(0x11904, machine.RandomAccessReferenceTableOffset);
    }

    [Fact]
    public void PublishRandomAccessReference_WritesResolvedFirmwareTable()
    {
        Dct3Machine machine = new(BuildFirmwareWithRandomAccessMatcher(
            V639RandomAccessMatcherOffset,
            ldrR7Literal: 0x4F99,
            V639RandomAccessLiteralOffset,
            tableAddress: 0x00111904));

        machine.Dsp.PublishRandomAccessReference?.Invoke(0x02, 0x01, 0x32, 0x14);

        byte[] table = machine.Bus.Ram.AsSpan(0x11904, 6).ToArray();
        Assert.Equal([0x01, 0x02, 0x01, 0x32, 0x14, 0x00], table);
    }

    [Fact]
    public void Constructor_DecodedSimLockInitRecord_ResolvesTableOffset()
    {
        Dct3Machine machine = new(BuildFirmwareWithDecodedSimLockRecord(0x00110810));

        Assert.Equal(0x10810, machine.DecodedSimLockOffset);
    }

    [Fact]
    public void PublishDecodedSimLock_WritesResolvedFirmwareTable()
    {
        Dct3Machine machine = new(BuildFirmwareWithDecodedSimLockRecord(0x00110810));

        machine.Dsp.PublishDecodedSimLock?.Invoke();

        byte[] record = machine.Bus.Ram.AsSpan(0x10810, 0x18).ToArray();
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], record[..8]);
        Assert.Equal(0x00, record[0x08]);
        Assert.Equal(0x00, record[0x09]);
        Assert.Equal(0x00, record[0x0A]);
        Assert.Equal(0x00, record[0x0B]);
        Assert.Equal(0xFF, record[0x0C]);
        Assert.Equal(0xFF, record[0x0D]);
        Assert.Equal(0xFF, record[0x0E]);
        Assert.Equal(0xFF, record[0x0F]);
        Assert.Equal(0x00, record[0x10]);
        Assert.Equal(0x00, record[0x11]);
        Assert.Equal(0x00, record[0x12]);
        Assert.Equal(0x00, record[0x13]);
        Assert.Equal(0xFF, record[0x14]);
        Assert.Equal(0xFF, record[0x15]);
        Assert.Equal(0x00, record[0x16]);
        Assert.Equal(0x00, record[0x17]);
    }

    [Fact]
    public void CreateRamSnapshot_ReturnsIndependentRangeCopy()
    {
        Dct3Machine machine = new(new byte[0x200000]);
        machine.Bus.Ram[0x20] = 0x12;
        machine.Bus.Ram[0x21] = 0x34;
        machine.Bus.Ram[0x22] = 0x56;
        machine.Bus.Ram[0x23] = 0x78;

        byte[] snapshot = machine.CreateRamSnapshot(0x100020, 4);
        machine.Bus.Ram[0x20] = 0xFF;

        Assert.Equal([0x12, 0x34, 0x56, 0x78], snapshot);
    }

    [Theory]
    [InlineData(0x0FFFFF, 1)]
    [InlineData(0x17FFFF, 2)]
    public void CreateRamSnapshot_OutsideMainRam_Throws(uint address, int length)
    {
        Dct3Machine machine = new(new byte[0x200000]);

        Assert.Throws<ArgumentOutOfRangeException>(() => machine.CreateRamSnapshot(address, length));
    }

    [Fact]
    public void Step_WallClockTimer_AdvancesCcontRtcWhenEnabled()
    {
        Dct3Machine machine = new(new byte[0x200000], timerClock: Dct3TimerClock.WallClock);
        WriteCcont(machine.Ccont, 0x7, 10);
        WriteCcont(machine.Ccont, 0x8, 20);
        WriteCcont(machine.Ccont, 0x9, 3);
        WriteCcont(machine.Ccont, 0xA, 4);
        WriteCcont(machine.Ccont, 0x6, 0x54);

        Thread.Sleep(1_150);
        machine.RefreshWallClockTimers();
        machine.Step();

        CcontRtcState state = machine.Ccont.RtcState;
        Assert.Equal(11, state.Second);
        Assert.Equal(20, state.Minute);
        Assert.Equal(3, state.Hour);
        Assert.Equal(4, state.Day);
    }

    [Fact]
    public void ServiceWallClockTimers_AdvancesCcontRtcWithoutCpuStep()
    {
        Dct3Machine machine = new(new byte[0x200000], timerClock: Dct3TimerClock.WallClock);
        WriteCcont(machine.Ccont, 0x7, 10);
        WriteCcont(machine.Ccont, 0x8, 20);
        WriteCcont(machine.Ccont, 0x9, 3);
        WriteCcont(machine.Ccont, 0xA, 4);
        WriteCcont(machine.Ccont, 0x6, 0x54);
        long cyclesBefore = machine.Bus.Cycles;

        Thread.Sleep(1_150);
        machine.ServiceWallClockTimers();

        CcontRtcState state = machine.Ccont.RtcState;
        Assert.Equal(cyclesBefore, machine.Bus.Cycles);
        Assert.Equal(11, state.Second);
        Assert.Equal(20, state.Minute);
        Assert.Equal(3, state.Hour);
        Assert.Equal(4, state.Day);
    }

    [Fact]
    public void ServiceWallClockTimers_WithCatchUpLimit_TreatsHostSleepAsPause()
    {
        Dct3Machine machine = new(
            new byte[0x200000],
            timerClock: Dct3TimerClock.WallClock,
            wallClockCatchUpLimit: TimeSpan.FromMilliseconds(200));
        WriteCcont(machine.Ccont, 0x7, 10);
        WriteCcont(machine.Ccont, 0x8, 20);
        WriteCcont(machine.Ccont, 0x9, 3);
        WriteCcont(machine.Ccont, 0xA, 4);
        WriteCcont(machine.Ccont, 0x6, 0x54);
        long cyclesBefore = machine.Bus.Cycles;

        Thread.Sleep(1_150);
        machine.ServiceWallClockTimers();

        CcontRtcState state = machine.Ccont.RtcState;
        Assert.Equal(cyclesBefore, machine.Bus.Cycles);
        Assert.Equal(10, state.Second);
        Assert.Equal(20, state.Minute);
        Assert.Equal(3, state.Hour);
        Assert.Equal(4, state.Day);
        Assert.Equal(1, machine.WallClockPauseCount);
        Assert.True(machine.LastWallClockPauseMilliseconds >= 200);
    }

    [Fact]
    public void ServicePendingPeripherals_WithWallClock_AdvancesSimUart()
    {
        Dct3Machine machine = new(new byte[0x200000], timerClock: Dct3TimerClock.WallClock);
        machine.Io.Write(0x39, 0x32);
        Thread.Sleep(2);
        machine.ServicePendingPeripherals();
        while (machine.Io.Read(0x3C) != 0)
        {
            _ = machine.Io.Read(0x37);
        }

        foreach (byte value in new byte[] { 0xA0, 0xA4, 0x00, 0x00, 0x02 })
        {
            machine.Io.Write(0x36, value);
        }

        Thread.Sleep(2);
        machine.ServicePendingPeripherals();

        Assert.NotEqual(0, machine.Io.Read(0x3C));
        Assert.Equal(0xA4, machine.Io.Read(0x37));
    }

    [Fact]
    public void ArmMsr_WhenDisablingFiq_PreventsImmediateNestedFiq()
    {
        const uint msrCpsrFieldsR1 = 0xE129F001;
        const uint nop = 0xE1A00000;
        const uint fiqAndIrqDisabled = 0xC0;
        Dct3Machine machine = new(new byte[0x200000]);
        Noks.Cpu.Arm7Tdmi cpu = machine.Cpu;

        cpu.ForceStatus(Noks.Cpu.Arm7Tdmi.ModeSys);
        cpu.SetGpr(1, Noks.Cpu.Arm7Tdmi.ModeFiq | fiqAndIrqDisabled);
        cpu.SetGpr(15, Dct3Machine.FlashBase + 8);
        cpu.PrimePipeline(
            msrCpsrFieldsR1,
            nop,
            Noks.Cpu.ArmAccess.Code | Noks.Cpu.ArmAccess.Sequential);

        cpu.Step();
        cpu.FiqLine = true;
        cpu.Step();

        Assert.Equal(Noks.Cpu.Arm7Tdmi.ModeFiq | fiqAndIrqDisabled, cpu.CpsrValue);
        Assert.Equal(Dct3Machine.FlashBase + 0x10, cpu.GetGpr(15));
        Assert.Equal(0u, cpu.GetGpr(14));
    }

    [Fact]
    public void ArmLdmUserRegisters_DoesNotCorruptFiqStackOnFollowingInstruction()
    {
        const uint ldmUserR0FromSp = 0xE89D0001;
        const uint addSpFour = 0xE28DD004;
        const uint fiqStack = 0x00100100;
        const uint systemStack = 0x00100200;
        Dct3Machine machine = new(new byte[0x200000]);
        Noks.Cpu.Arm7Tdmi cpu = machine.Cpu;

        cpu.ForceStatus(Noks.Cpu.Arm7Tdmi.ModeFiq | 0xC0);
        cpu.SetGpr(13, fiqStack);
        cpu.SetBanked(Noks.Cpu.ArmBank.None, 5, systemStack);
        cpu.SetGpr(15, Dct3Machine.FlashBase + 8);
        cpu.PrimePipeline(
            ldmUserR0FromSp,
            addSpFour,
            Noks.Cpu.ArmAccess.Code | Noks.Cpu.ArmAccess.Sequential);

        cpu.Step();
        cpu.Step();

        Assert.Equal(fiqStack + 4, cpu.GetGpr(13));
        Assert.Equal(systemStack, cpu.GetBanked(Noks.Cpu.ArmBank.None, 5));
    }

    [Fact]
    public void AccelerateIdleSpin_AtCanonicalBusyLoop_SkipsWholeEquivalentLoops()
    {
        Dct3Machine machine = new(BuildIdleYieldFirmware());
        IdleYieldRuntimeHook hook = Assert.IsType<IdleYieldRuntimeHook>(machine.IdleYieldHook);
        PrepareIdleLoopFixedPoint(machine, hook);
        long cyclesBefore = machine.Bus.Cycles;

        int skippedInstructions = machine.AccelerateIdleSpin(maximumInstructions: 4000);

        Assert.True(
            skippedInstructions > 0,
            $"pc={machine.Cpu.GetGpr(15):X8} cpsr={machine.Cpu.CpsrValue:X8} " +
            $"flag={machine.Bus.Ram[(int)(hook.AliveFlagAddress - 0x100000)]:X2} " +
            $"interrupt={machine.InterruptPending} cycles={machine.Bus.Cycles}");
        Assert.Equal(0, skippedInstructions % 4);
        Assert.Equal(skippedInstructions / 4 * 8, machine.Bus.Cycles - cyclesBefore);
        Assert.Equal(hook.LoopFetchStartAddress, machine.Cpu.GetGpr(15));
    }

    [Fact]
    public void AccelerateIdleSpin_WhenAliveFlagWasCleared_DoesNotSkip()
    {
        Dct3Machine machine = new(BuildIdleYieldFirmware());
        IdleYieldRuntimeHook hook = Assert.IsType<IdleYieldRuntimeHook>(machine.IdleYieldHook);
        PrepareIdleLoopFixedPoint(machine, hook);
        machine.Bus.Ram[(int)(hook.AliveFlagAddress - 0x100000)] = 0;

        Assert.Equal(0, machine.AccelerateIdleSpin());
    }

    [Fact]
    public void AccelerateIdleSpin_WithUiStyleTrace_StillSkipsIdleLoop()
    {
        Dct3Machine machine = new(BuildIdleYieldFirmware(), new SilentTrace());
        IdleYieldRuntimeHook hook = Assert.IsType<IdleYieldRuntimeHook>(machine.IdleYieldHook);
        PrepareIdleLoopFixedPoint(machine, hook);

        Assert.True(machine.AccelerateIdleSpin(maximumInstructions: 4000) > 0);
    }

    [Fact]
    public void AccelerateIdleSpin_WithWallClock_DoesNotMixClockDomains()
    {
        Dct3Machine machine = new(BuildIdleYieldFirmware(), timerClock: Dct3TimerClock.WallClock);
        IdleYieldRuntimeHook hook = Assert.IsType<IdleYieldRuntimeHook>(machine.IdleYieldHook);
        PrepareIdleLoopFixedPoint(machine, hook);

        Assert.Equal(0, machine.AccelerateIdleSpin());
    }

    [Fact]
    public void AccelerateIdleSpin_DoesNotSkipPeripheralDeadline()
    {
        Dct3Machine machine = new(BuildIdleYieldFirmware());
        IdleYieldRuntimeHook hook = Assert.IsType<IdleYieldRuntimeHook>(machine.IdleYieldHook);
        PrepareIdleLoopFixedPoint(machine, hook);
        machine.Dsp.SyncCycle(machine.Bus.Cycles);
        machine.Dsp.SetRunning(true);
        machine.ServicePendingPeripherals();
        long dspDeadline = machine.Dsp.NextWakeCycle(machine.Bus.Cycles);

        Assert.True(machine.AccelerateIdleSpin(maximumInstructions: 1_000_000) > 0);
        Assert.True(machine.Bus.Cycles < dspDeadline);
    }

    [Fact]
    public void AccelerateIdleSpin_MatchesRealExecutionAtSameCycleBoundary()
    {
        Dct3Machine accelerated = new(BuildIdleYieldFirmware());
        Dct3Machine reference = new(BuildIdleYieldFirmware());
        PrepareIdleLoopFixedPoint(accelerated, Assert.IsType<IdleYieldRuntimeHook>(accelerated.IdleYieldHook));
        PrepareIdleLoopFixedPoint(reference, Assert.IsType<IdleYieldRuntimeHook>(reference.IdleYieldHook));

        int skippedInstructions = accelerated.AccelerateIdleSpin(maximumInstructions: 4000);
        for (int i = 0; i < skippedInstructions; i++)
        {
            reference.Step();
        }

        Assert.True(skippedInstructions > 0);
        AssertMachinesEqual(reference, accelerated);
    }

    [Fact]
    public void AccelerateIdleSpin_BeforeFixedPoint_DoesNotSkip()
    {
        Dct3Machine machine = new(BuildIdleYieldFirmware());
        IdleYieldRuntimeHook hook = Assert.IsType<IdleYieldRuntimeHook>(machine.IdleYieldHook);
        ConfigureIdleLoop(machine, hook);

        Assert.Equal(0, machine.AccelerateIdleSpin());
    }

    [Fact]
    public void AccelerateIdleSpin_WithAliveReadWatch_DoesNotSkip()
    {
        Dct3Machine machine = new(BuildIdleYieldFirmware());
        IdleYieldRuntimeHook hook = Assert.IsType<IdleYieldRuntimeHook>(machine.IdleYieldHook);
        PrepareIdleLoopFixedPoint(machine, hook);
        machine.Bus.WatchReads = true;
        machine.Bus.WatchLow = hook.AliveFlagAddress;
        machine.Bus.WatchHigh = hook.AliveFlagAddress + 1;

        Assert.Equal(0, machine.AccelerateIdleSpin());
    }

    [Fact]
    public void AccelerateIdleSpin_WithDeferredSoftwareReset_DoesNotSkip()
    {
        Dct3Machine machine = new(BuildIdleYieldFirmware());
        IdleYieldRuntimeHook hook = Assert.IsType<IdleYieldRuntimeHook>(machine.IdleYieldHook);
        PrepareIdleLoopFixedPoint(machine, hook);
        machine.Io.Write(0x01, 0x05);

        Assert.Equal(0, machine.AccelerateIdleSpin());
    }

    private static void PrepareIdleLoopFixedPoint(Dct3Machine machine, IdleYieldRuntimeHook hook)
    {
        ConfigureIdleLoop(machine, hook);
        for (int i = 0; i < 4; i++)
        {
            machine.Step();
        }
    }

    private static void ConfigureIdleLoop(Dct3Machine machine, IdleYieldRuntimeHook hook)
    {
        machine.Bus.Ram[(int)(hook.AliveFlagAddress - 0x100000)] = 1;
        machine.Cpu.ForceStatus(Noks.Cpu.Arm7Tdmi.ModeSys | 0x20 | 0x20000000);
        machine.Cpu.SetGpr(0, 1);
        machine.Cpu.SetGpr(6, hook.AliveFlagAddress);
        machine.Cpu.SetGpr(15, hook.LoopFetchStartAddress);
        machine.Cpu.PrimePipeline(
            0x7830,
            0x2800,
            Noks.Cpu.ArmAccess.Code | Noks.Cpu.ArmAccess.Sequential);
    }

    private static void AssertMachinesEqual(Dct3Machine expected, Dct3Machine actual)
    {
        Assert.Equal(expected.Bus.Cycles, actual.Bus.Cycles);
        Assert.Equal(expected.Cpu.CpsrValue, actual.Cpu.CpsrValue);
        Assert.Equal(expected.Cpu.PipelineAccess, actual.Cpu.PipelineAccess);
        Assert.Equal(expected.Cpu.IrqLine, actual.Cpu.IrqLine);
        Assert.Equal(expected.Cpu.FiqLine, actual.Cpu.FiqLine);
        Assert.Equal(expected.Cpu.IrqAcceptanceEnabled, actual.Cpu.IrqAcceptanceEnabled);
        Assert.Equal(expected.Cpu.FiqAcceptanceEnabled, actual.Cpu.FiqAcceptanceEnabled);
        Assert.Equal(expected.Cpu.GetPipelineOpcode(0), actual.Cpu.GetPipelineOpcode(0));
        Assert.Equal(expected.Cpu.GetPipelineOpcode(1), actual.Cpu.GetPipelineOpcode(1));
        for (int register = 0; register < 16; register++)
        {
            Assert.Equal(expected.Cpu.GetGpr(register), actual.Cpu.GetGpr(register));
        }

        foreach (Noks.Cpu.ArmBank bank in Enum.GetValues<Noks.Cpu.ArmBank>())
        {
            Assert.Equal(expected.Cpu.GetSpsrRaw(bank), actual.Cpu.GetSpsrRaw(bank));
            for (int register = 0; register < 7; register++)
            {
                Assert.Equal(expected.Cpu.GetBanked(bank, register), actual.Cpu.GetBanked(bank, register));
            }
        }

        Assert.Equal(expected.Io.Timer0Counter, actual.Io.Timer0Counter);
        Assert.Equal(expected.Io.Timer1Counter, actual.Io.Timer1Counter);
        Assert.Equal(expected.Io.EffectiveFiqStatusValue, actual.Io.EffectiveFiqStatusValue);
        Assert.Equal(expected.Io.IrqStatusValue, actual.Io.IrqStatusValue);
        Assert.Equal(expected.Io.PeripheralState, actual.Io.PeripheralState);
        Assert.Equal(expected.Io.AudioState, actual.Io.AudioState);
        Assert.Equal(expected.DspState, actual.DspState);
        Assert.Equal(expected.Ccont.RtcState, actual.Ccont.RtcState);
        Assert.Equal(expected.Flash.ProgramCount, actual.Flash.ProgramCount);
        Assert.Equal(expected.Flash.EraseCount, actual.Flash.EraseCount);
        Assert.Equal(expected.Lcd.DisplayMode, actual.Lcd.DisplayMode);
        Assert.Equal(expected.Lcd.PowerDown, actual.Lcd.PowerDown);
        Assert.Equal(expected.Lcd.DataWrites, actual.Lcd.DataWrites);
        Assert.Equal(expected.Lcd.Vram, actual.Lcd.Vram);
        Assert.Equal(expected.Bus.Ram, actual.Bus.Ram);
        Assert.Equal(expected.Bus.DspSharedRam, actual.Bus.DspSharedRam);
    }

    private static byte[] BuildV607SimLockFirmware()
    {
        byte[] flash = new byte[0x200000];
        ReadOnlySpan<byte> routineSignature =
        [
            0x48, 0xD9, 0x78, 0x02, 0x2A, 0x00, 0xD1, 0x08,
            0x46, 0x59, 0x78, 0x09, 0x29, 0x00, 0xD0, 0x49,
        ];
        ReadOnlySpan<byte> literalSignature =
        [
            0x00, 0x11, 0x09, 0x24,
            0x00, 0x10, 0xA6, 0xE4,
        ];

        routineSignature.CopyTo(flash.AsSpan(V607SimLockCheckRoutineOffset));
        literalSignature.CopyTo(flash.AsSpan(V607SimLockCheckLiteralOffset));
        return flash;
    }

    private static byte[] BuildIdleYieldFirmware()
    {
        const uint loopFunctionAddress = 0x2D1620;
        const uint loopPrefixAddress = 0x2D16BE;
        const uint r6LoadAddress = 0x2D1644;
        const uint r6LiteralAddress = 0x2D1784;
        const uint aliveFlagAddress = 0x11A294;
        const uint fiqClearAddress = 0x2D73BA;
        const uint fiqClearLiteralAddress = 0x2D7468;
        byte[] flash = new byte[0x200000];
        Array.Fill(flash, (byte)0xFF);

        WriteFlashHalf(flash, loopFunctionAddress, 0xB5F0);
        uint literalBase = (r6LoadAddress + 4) & ~3u;
        uint literalWordOffset = (r6LiteralAddress - literalBase) / 4;
        WriteFlashHalf(flash, r6LoadAddress, (ushort)(0x4E00 | literalWordOffset));
        WriteFlashWord(flash, r6LiteralAddress, aliveFlagAddress);
        WriteFlashBytes(flash, loopPrefixAddress,
        [
            0x46, 0x48, 0x70, 0x30, 0x48, 0x31, 0x70, 0x07,
            0x46, 0x40, 0x73, 0x28, 0x78, 0x30, 0x28, 0x00,
            0xD0, 0xBE, 0xE7, 0xFB,
        ]);
        WriteFlashBytes(flash, fiqClearAddress, [0xB5, 0x10, 0x49, 0x2A, 0x20, 0x00, 0x70, 0x08]);
        WriteFlashWord(flash, fiqClearLiteralAddress, aliveFlagAddress);
        return flash;
    }

    private sealed class SilentTrace : IDct3Trace
    {
        public bool MadStateEnabled => false;

        public void MadRead(uint offset, byte value) { }
        public void MadWrite(uint offset, byte value) { }
        public void MadState(string message) { }
        public void CcontRead(int reg, byte value) { }
        public void CcontWrite(int reg, byte value) { }
        public void LcdCommand(byte value) { }
        public void LcdData(byte value, int x, int y, bool vertical) { }
        public void FlashCommand(string description) { }
        public void InterfaceAccess(string block, bool write, uint offset, uint value) { }
        public void DspRam(bool write, uint offset, uint value) { }
        public void Unmapped(bool write, uint address, uint value, int size) { }
        public void Event(string message) { }
    }

    private static void WriteFlashBytes(byte[] flash, uint address, ReadOnlySpan<byte> bytes) =>
        bytes.CopyTo(flash.AsSpan((int)(address - Dct3Machine.FlashBase)));

    private static void WriteFlashHalf(byte[] flash, uint address, ushort value)
    {
        int offset = (int)(address - Dct3Machine.FlashBase);
        flash[offset] = (byte)(value >> 8);
        flash[offset + 1] = (byte)value;
    }

    private static void WriteFlashWord(byte[] flash, uint address, uint value)
    {
        int offset = (int)(address - Dct3Machine.FlashBase);
        flash[offset] = (byte)(value >> 24);
        flash[offset + 1] = (byte)(value >> 16);
        flash[offset + 2] = (byte)(value >> 8);
        flash[offset + 3] = (byte)value;
    }

    private static void WriteDspMdiSend(byte[] sharedRam, byte type, ReadOnlySpan<byte> payload)
    {
        ushort tail = ReadDsp16(sharedRam, 0x0A4);
        int byteOffset = tail * 2;
        WriteDspMdiSendByte(sharedRam, byteOffset, (byte)payload.Length);
        WriteDspMdiSendByte(sharedRam, byteOffset + 1, type);
        for (int i = 0; i < payload.Length; i++)
        {
            WriteDspMdiSendByte(sharedRam, byteOffset + 2 + i, payload[i]);
        }

        if ((payload.Length & 1) != 0)
        {
            WriteDspMdiSendByte(sharedRam, byteOffset + 2 + payload.Length, 0);
        }

        int totalBytes = 2 + payload.Length + (payload.Length & 1);
        WriteDsp16(sharedRam, 0x0A4, (ushort)((tail + totalBytes / 2) % 82));
    }

    private static void WriteDspMdiSendByte(byte[] sharedRam, int offset, byte value) =>
        sharedRam[offset % 0xA4] = value;

    private static byte ReadDspMdiRcvByte(byte[] sharedRam, int index)
    {
        ushort head = ReadDsp16(sharedRam, 0x1CA);
        if (head is < 0x80 or > 0xE3)
        {
            head = 0x80;
        }
        return sharedRam[0x100 + ((head - 0x80) * 2 + index) % 200];
    }

    private static void AcknowledgeDspMdiRcv(byte[] sharedRam, Dsp dsp)
    {
        ushort tail = ReadDsp16(sharedRam, 0x1C8);
        WriteDsp16(sharedRam, 0x1CA, tail);
        dsp.OnSharedWrite(0x1CA, tail, size: 2);
    }

    private static ushort ReadDsp16(byte[] sharedRam, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(sharedRam.AsSpan(offset, 2));

    private static void WriteDsp16(byte[] sharedRam, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(sharedRam.AsSpan(offset, 2), value);

    private static void WriteCcont(Ccont ccont, int register, byte value)
    {
        ccont.Write((byte)(register << 3));
        ccont.Write(value);
    }

    private static byte[] BuildV639Firmware()
    {
        byte[] flash = new byte[0x200000];
        ReadOnlySpan<byte> version =
        [
            0x56, 0x20, 0x30, 0x36, 0x2E, 0x33, 0x39,
        ];
        ReadOnlySpan<byte> model =
        [
            0x4E, 0x48, 0x4D, 0x2D, 0x35,
        ];

        version.CopyTo(flash.AsSpan(V639FirmwareVersionOffset));
        model.CopyTo(flash.AsSpan(V639FirmwareModelOffset));
        return flash;
    }

    private static void WriteV607AutomaticKeyguardSetting(byte[] flash)
    {
        ReadOnlySpan<byte> setting =
        [
            0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x01, 0x02,
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x02, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x02, 0x00, 0x00, 0x11, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x00, 0x00, 0x1E, 0x00, 0x01,
        ];

        setting.CopyTo(flash.AsSpan(V607AutomaticKeyguardSettingOffset));
    }

    private static byte[] BuildFirmwareWithRandomAccessMatcher(
        int matcherOffset,
        ushort ldrR7Literal,
        int literalOffset,
        uint tableAddress)
    {
        byte[] flash = new byte[0x200000];
        ReadOnlySpan<byte> matcherPrefix =
        [
            0xB5, 0xF0, 0x78, 0x43, 0x08, 0xD9, 0x06, 0x09,
            0x0E, 0x0D, 0x78, 0x82, 0x09, 0x51, 0x07, 0x5B,
            0x0E, 0x9B, 0x43, 0x19, 0x06, 0x09, 0x0E, 0x0E,
            0x06, 0xD1, 0x0E, 0xC9, 0x06, 0x09, 0x0E, 0x09,
            0x46, 0x8C,
        ];

        matcherPrefix.CopyTo(flash.AsSpan(matcherOffset));
        flash[matcherOffset + matcherPrefix.Length] = (byte)(ldrR7Literal >> 8);
        flash[matcherOffset + matcherPrefix.Length + 1] = (byte)ldrR7Literal;
        flash[literalOffset] = (byte)(tableAddress >> 24);
        flash[literalOffset + 1] = (byte)(tableAddress >> 16);
        flash[literalOffset + 2] = (byte)(tableAddress >> 8);
        flash[literalOffset + 3] = (byte)tableAddress;
        return flash;
    }

    private static byte[] BuildFirmwareWithDecodedSimLockRecord(uint tableAddress)
    {
        byte[] flash = new byte[0x200000];
        int offset = 0x1000;

        flash[offset] = 0x00;
        flash[offset + 1] = 0x00;
        flash[offset + 2] = 0x00;
        flash[offset + 3] = 0x12;
        flash[offset + 4] = (byte)(tableAddress >> 24);
        flash[offset + 5] = (byte)(tableAddress >> 16);
        flash[offset + 6] = (byte)(tableAddress >> 8);
        flash[offset + 7] = (byte)tableAddress;

        ReadOnlySpan<byte> initialRecord =
        [
            0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF,
        ];
        initialRecord.CopyTo(flash.AsSpan(offset + 8));

        return flash;
    }
}
