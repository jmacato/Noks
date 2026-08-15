using System.Diagnostics;
using System.Reflection;
using Noks.AvaloniaApp;
using Noks.Dct3.Core;
using Noks.Dct3.Peripherals;
using Noks.Dct3.Sim;
using Noks.Dct3.State;
using Noks.AvaloniaApp.Emulation;
using Noks.Application.Persistence;

namespace Noks.Application.Tests;

public sealed class PhoneEmulatorPersistenceTests
{
    [Fact]
    public void PersistenceCheckpointRunsOncePerSecond()
    {
        long interval = GetPrivateStaticField<long>("PersistenceSaveIntervalTicks");

        Assert.Equal(Stopwatch.Frequency, interval);
    }

    [Fact]
    public void ProfilePersistenceKeyDoesNotDependOnFirmwareBuild()
    {
        const string stableContactId = "abcdefghijklmnopqrstuvwx";

        string first = PhonePersistence.CreateProfileKey(stableContactId);
        string second = PhonePersistence.CreateProfileKey(stableContactId);

        Assert.Equal(first, second);
        Assert.StartsWith("profile-v1-", first, StringComparison.Ordinal);
        Assert.DoesNotContain("firmware", first, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0x6F3A, SimPhonebookCodec.RecordLength)]
    [InlineData(0x6F3C, 176)]
    public async Task CompletedContactAndMessageWritesBypassPeriodicThrottle(
        int fileId,
        int recordLength)
    {
        RecordingPersistenceStore store = new();
        PhonePersistenceSession persistence = new("test", store, Dct3PersistenceSnapshot.Empty);
        using PhoneEmulator emulator = new(new byte[0x20_0000], persistence: persistence);
        Dct3Machine machine = new(new byte[0x20_0000]);
        InvokePrivate(emulator, "MarkPersistenceLoaded", machine);
        SetPrivateField(emulator, "lastPersistenceSaveTimestamp", Stopwatch.GetTimestamp());

        SimCard sim = Assert.IsType<SimCard>(typeof(Dct3Machine)
            .GetProperty("Sim", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(machine));
        List<SimMutation> mutations = [];
        machine.SimMutationCommitted += mutations.Add;
        SendSimApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, 0x7F, 0x10);
        SendSimApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, (byte)(fileId >> 8), (byte)fileId);
        byte[] record = Enumerable.Repeat((byte)0xFF, recordLength).ToArray();
        record[0] = 0x01;
        Assert.Equal(
            [0xDC, 0x90, 0x00],
            SendSimApdu(sim, [0xA0, 0xDC, 0x01, 0x04, (byte)recordLength, .. record]));
        SimMutation mutation = Assert.Single(mutations, item => item.FileId == fileId);
        // Firmware reaches the SIM through Mad2Io, which mirrors this version after
        // each completed APDU byte. The direct test probe updates that mirror here.
        SetPrivateField(machine.Io, "simPersistenceVersion", sim.PersistenceVersion);

        InvokePrivate(emulator, "EnqueueSimMutation", mutation);
        Assert.Equal(1, GetPrivateField<int>(emulator, "immediatePersistenceSavePending"));
        InvokePrivate(emulator, "FlushRequestedPersistenceSave", machine);

        Dct3PersistenceSnapshot snapshot = await store.Saved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, GetPrivateField<int>(emulator, "immediatePersistenceSavePending"));
        Assert.Contains(snapshot.SimFiles, file => file.Parent == 0x7F10 && file.Id == fileId);
    }

    [Theory]
    [InlineData(0x7F20, 0x6F7E, SimMutationOrigin.Firmware)]
    [InlineData(0x7F10, 0x6F3A, SimMutationOrigin.PersistenceRestore)]
    [InlineData(0x7F10, 0x6F3C, SimMutationOrigin.PersistenceRestore)]
    public void UnrelatedOrRestoredSimMutationsDoNotRequestImmediateSave(
        int parentFileId,
        int fileId,
        SimMutationOrigin origin)
    {
        using PhoneEmulator emulator = new(new byte[0x20_0000]);
        SimMutation mutation = new(
            (ushort)parentFileId,
            (ushort)fileId,
            1,
            new byte[1],
            new byte[1],
            origin);

        InvokePrivate(emulator, "EnqueueSimMutation", mutation);

        Assert.Equal(0, GetPrivateField<int>(emulator, "immediatePersistenceSavePending"));
    }

    private static byte[] SendSimApdu(SimCard sim, params byte[] bytes)
    {
        List<byte> responseBytes = [];
        foreach (byte value in bytes)
        {
            if (sim.Transmit(value) is { } response)
            {
                responseBytes.AddRange(response.Data);
            }
        }

        return responseBytes.ToArray();
    }

    private static T GetPrivateStaticField<T>(string name) =>
        Assert.IsType<T>(typeof(PhoneEmulator)
            .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null));

    private static T GetPrivateField<T>(object target, string name) =>
        Assert.IsType<T>(target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target));

    private static void SetPrivateField(object target, string name, object value) =>
        target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static void InvokePrivate(object target, string name, params object[] arguments) =>
        target.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, arguments);

    private sealed class RecordingPersistenceStore : IPhonePersistenceStore
    {
        public TaskCompletionSource<Dct3PersistenceSnapshot> Saved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<Dct3PersistenceSnapshot?> LoadAsync(
            string key,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Dct3PersistenceSnapshot?>(null);

        public ValueTask SaveAsync(
            string key,
            Dct3PersistenceSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved.TrySetResult(snapshot);
            return ValueTask.CompletedTask;
        }
    }
}
