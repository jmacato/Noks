using System.Diagnostics;
using System.Threading;
using Noks.Dct3.Core;

namespace Noks.AvaloniaApp.Emulation;

public sealed class RealTimePacer
{
    private const double MaxCatchUpDebtSeconds = 0.25;
    private long anchorCycles;
    private long anchorTicks;
    private double driftMilliseconds;

    public RealTimePacer()
    {
        Reset(0, Stopwatch.GetTimestamp());
    }

    public EmulationPacing State => new(1.0, driftMilliseconds);

    public void Reanchor(long cycles)
    {
        Reset(cycles, Stopwatch.GetTimestamp());
    }

    public void Pace(long cycles, CancellationToken cancellationToken)
    {
        long targetTicks = TargetTicks(cycles);
        bool waited = WaitUntil(targetTicks, cancellationToken);
        UpdateDrift(cycles, targetTicks, Stopwatch.GetTimestamp());
        if (!waited)
        {
            Thread.Yield();
        }
    }

    public async ValueTask PaceAsync(long cycles, CancellationToken cancellationToken)
    {
        long targetTicks = TargetTicks(cycles);
        bool delayed = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            long remainingTicks = targetTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                break;
            }

            int waitMilliseconds = Math.Max(
                1,
                (int)Math.Ceiling(remainingTicks * 1000.0 / Stopwatch.Frequency));
            await Task.Delay(waitMilliseconds, cancellationToken).ConfigureAwait(false);
            delayed = true;
        }

        long completedAt = Stopwatch.GetTimestamp();
        UpdateDrift(cycles, targetTicks, completedAt);

        if (delayed)
        {
            return;
        }

        await Task.Yield();
    }

    private void UpdateDrift(long cycles, long targetTicks, long nowTicks)
    {
        driftMilliseconds = (nowTicks - targetTicks) * 1000.0 / Stopwatch.Frequency;

        if (driftMilliseconds > MaxCatchUpDebtSeconds * 1000.0)
        {
            Reset(cycles, nowTicks);
        }
    }

    private long TargetTicks(long cycles)
    {
        double targetSeconds = (cycles - anchorCycles) / (double)Dct3Machine.CyclesPerSecond;
        return anchorTicks + (long)Math.Round(targetSeconds * Stopwatch.Frequency);
    }

    private static bool WaitUntil(long targetTicks, CancellationToken cancellationToken)
    {
        bool waited = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            long remainingTicks = targetTicks - Stopwatch.GetTimestamp();

            if (remainingTicks <= 0)
            {
                return waited;
            }

            double remainingSeconds = remainingTicks / (double)Stopwatch.Frequency;
            int waitMilliseconds = Math.Max(1, (int)Math.Ceiling(remainingSeconds * 1000.0));
            cancellationToken.WaitHandle.WaitOne(waitMilliseconds);
            waited = true;
        }

        return waited;
    }

    private void Reset(long cycles, long nowTicks)
    {
        anchorCycles = cycles;
        anchorTicks = nowTicks;
        driftMilliseconds = 0.0;
    }
}
