using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Noks.Dct3.Peripherals;

internal static class PeripheralChannel<TPeripheral>
    where TPeripheral : class
{
    private static readonly Channel<IWorkItem> Work = Channel.CreateUnbounded<IWorkItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private static readonly Thread? WorkerThread = TryStartWorker();
    private static readonly bool MetricsEnabled =
        Environment.GetEnvironmentVariable("NOKS_PERIPHERAL_METRICS") == "1";
    private static int workerThreadId;
    private static long queuedInvocations;
    private static long inlineInvocations;
    private static long workerWakeups;
    private static long completedInvocations;
    private static long synchronizationAllocations;
    private static long requestReplyTicks;

    public static bool IsWorkerThread =>
        WorkerThread is null || Environment.CurrentManagedThreadId == Volatile.Read(ref workerThreadId);

    public static PeripheralWorkerMetrics Metrics => new(
        typeof(TPeripheral).Name,
        MetricsEnabled,
        Volatile.Read(ref queuedInvocations),
        Volatile.Read(ref inlineInvocations),
        Volatile.Read(ref workerWakeups),
        Volatile.Read(ref completedInvocations),
        Volatile.Read(ref synchronizationAllocations),
        TimeSpan.FromSeconds(Volatile.Read(ref requestReplyTicks) / (double)Stopwatch.Frequency));

    public static void Invoke(TPeripheral peripheral, Action<TPeripheral> action) =>
        Invoke(
            peripheral,
            target =>
            {
                action(target);
                return true;
            });

    public static TResult Invoke<TResult>(TPeripheral peripheral, Func<TPeripheral, TResult> action)
    {
        if (IsWorkerThread)
        {
            if (MetricsEnabled)
            {
                Interlocked.Increment(ref inlineInvocations);
            }

            return action(peripheral);
        }

        long startedAt = MetricsEnabled ? Stopwatch.GetTimestamp() : 0;
        if (MetricsEnabled)
        {
            Interlocked.Increment(ref queuedInvocations);
        }

        WorkItem<TResult> item = WorkItem<TResult>.Rent(peripheral, action, out bool allocated);
        if (MetricsEnabled && allocated)
        {
            Interlocked.Increment(ref synchronizationAllocations);
        }

        if (!Work.Writer.TryWrite(item))
        {
            item.ReleaseRejected();
            throw new InvalidOperationException($"{typeof(TPeripheral).Name} worker rejected a message.");
        }

        try
        {
            return item.Wait();
        }
        finally
        {
            if (MetricsEnabled)
            {
                Interlocked.Add(ref requestReplyTicks, Stopwatch.GetTimestamp() - startedAt);
            }
        }
    }

    private static Thread StartWorker()
    {
        Thread thread = new(Run)
        {
            IsBackground = true,
            Name = $"Noks {typeof(TPeripheral).Name} peripheral",
        };
        thread.Start();
        return thread;
    }

    private static Thread? TryStartWorker()
    {
        try
        {
            return StartWorker();
        }
        catch (PlatformNotSupportedException) when (OperatingSystem.IsBrowser())
        {
            return null;
        }
    }

    private static void Run()
    {
        Volatile.Write(ref workerThreadId, Environment.CurrentManagedThreadId);
        while (true)
        {
            ValueTask<IWorkItem> pending = Work.Reader.ReadAsync();
            if (MetricsEnabled && !pending.IsCompletedSuccessfully)
            {
                Interlocked.Increment(ref workerWakeups);
            }

            IWorkItem item = pending.AsTask().GetAwaiter().GetResult();
            try
            {
                item.Execute();
            }
            finally
            {
                if (MetricsEnabled)
                {
                    Interlocked.Increment(ref completedInvocations);
                }
            }
        }
    }

    private interface IWorkItem
    {
        void Execute();
    }

    private sealed class WorkItem<TResult> : IWorkItem
    {
        [ThreadStatic]
        private static WorkItem<TResult>? callerItem;

        private readonly AutoResetEvent completed = new(false);
        private TPeripheral? peripheral;
        private Func<TPeripheral, TResult>? action;
        private TResult? result;
        private ExceptionDispatchInfo? exception;
        private bool inUse;

        public static WorkItem<TResult> Rent(
            TPeripheral peripheral,
            Func<TPeripheral, TResult> action,
            out bool allocated)
        {
            WorkItem<TResult>? item = callerItem;
            allocated = item is null;
            item ??= callerItem = new WorkItem<TResult>();
            item.Prepare(peripheral, action);
            return item;
        }

        private void Prepare(TPeripheral peripheral, Func<TPeripheral, TResult> action)
        {
            if (inUse)
            {
                throw new InvalidOperationException(
                    $"{typeof(TPeripheral).Name} caller attempted overlapping worker grants.");
            }

            inUse = true;
            this.peripheral = peripheral;
            this.action = action;
            result = default;
            exception = null;
        }

        public void Execute()
        {
            try
            {
                result = action!(peripheral!);
            }
            catch (Exception ex)
            {
                exception = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                completed.Set();
            }
        }

        public TResult Wait()
        {
            completed.WaitOne();
            TResult completedResult = result!;
            ExceptionDispatchInfo? completedException = exception;
            Release();
            completedException?.Throw();
            return completedResult;
        }

        public void ReleaseRejected() => Release();

        private void Release()
        {
            peripheral = null;
            action = null;
            result = default;
            exception = null;
            inUse = false;
        }
    }
}
