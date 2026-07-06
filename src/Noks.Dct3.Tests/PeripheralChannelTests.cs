using Noks.Dct3.Peripherals;
using Noks.Dct3.Radio;
namespace Noks.Dct3.Tests;

public sealed class PeripheralChannelTests
{
    [Fact]
    public void Invocations_RunOnStableWorkerThread()
    {
        int callerThread = Environment.CurrentManagedThreadId;
        Dsp dsp = new(new byte[0x200], null);

        int dspThread = PeripheralChannel<Dsp>.Invoke(dsp, static _ => Environment.CurrentManagedThreadId);

        Assert.NotEqual(callerThread, dspThread);
        Assert.Equal(dspThread, PeripheralChannel<Dsp>.Invoke(dsp, static _ => Environment.CurrentManagedThreadId));
    }

    [Fact]
    public void ReusedWaiter_ConsumesCompletionBeforeNextInvocation()
    {
        Dsp dsp = new(new byte[0x200], null);

        for (int expected = 0; expected < 100; expected++)
        {
            int actual = PeripheralChannel<Dsp>.Invoke(dsp, _ => expected);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ReusedWaiter_AfterWorkerException_DoesNotLeakFailureOrSignal()
    {
        Dsp dsp = new(new byte[0x200], null);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            PeripheralChannel<Dsp>.Invoke<int>(dsp, static _ => throw new InvalidOperationException("Expected test exception.")));
        int result = PeripheralChannel<Dsp>.Invoke(dsp, static _ => 42);

        Assert.Equal("Expected test exception.", exception.Message);
        Assert.Equal(42, result);
    }
}
