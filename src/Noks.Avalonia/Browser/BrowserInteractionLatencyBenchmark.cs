#if BROWSER
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Noks.Application.Input;

namespace Noks.AvaloniaApp.Browser;

internal static class BrowserInteractionLatencyBenchmark
{
    private static readonly ConcurrentDictionary<int, TraceState> Traces = [];
    private static int enabled;
    private static int activeTraceId;

    internal static void Configure(IReadOnlyList<string> args)
    {
        Uri? pageUri = args
            .Select(argument => Uri.TryCreate(argument, UriKind.Absolute, out Uri? uri) ? uri : null)
            .FirstOrDefault(uri => uri?.Scheme is "http" or "https");
        Volatile.Write(
            ref enabled,
            pageUri is not null && QueryValue(pageUri, "audio-latency") == "1" ? 1 : 0);
    }

    internal static int BeginPointer(PhoneKey key)
    {
        if (!Enabled)
        {
            return 0;
        }

        int traceId = BrowserAudioInterop.TakeLatencyInputId();
        if (traceId <= 0)
        {
            return 0;
        }

        TraceState trace = new(traceId, key);
        trace.Mark(Stage.CSharpPointer);
        Traces[traceId] = trace;
        Volatile.Write(ref activeTraceId, traceId);
        return traceId;
    }

    internal static void MarkInputQueued(int traceId) => Mark(traceId, Stage.InputQueued);

    internal static void MarkWorkerDequeued(PhoneKey key) => MarkActive(key, Stage.WorkerDequeued);

    internal static void MarkMatrixApplied(PhoneKey key) => MarkActive(key, Stage.MatrixApplied);

    internal static void MarkKeypadInterrupt() => MarkActive(Stage.KeypadInterrupt);

    internal static void MarkAudioStatePublished() => MarkActive(Stage.AudioStatePublished);

    internal static void MarkAudioEventRaised() => MarkActive(Stage.AudioEventRaised);

    internal static void MarkAudioUiDispatch() => MarkActive(Stage.AudioUiDispatch);

    internal static void MarkBackendUpdate() => MarkActive(Stage.BackendUpdate);

    internal static int MarkPcmDemand()
    {
        int traceId = Volatile.Read(ref activeTraceId);
        Mark(traceId, Stage.PcmDemand);
        return traceId;
    }

    internal static void MarkPcmRenderStarted(int traceId) => Mark(traceId, Stage.PcmRenderStarted);

    internal static void MarkPcmRenderCompleted(int traceId) => Mark(traceId, Stage.PcmRenderCompleted);

    internal static string CreateSnapshot(int traceId)
    {
        if (!Traces.TryGetValue(traceId, out TraceState? trace))
        {
            return "";
        }

        return trace.CreateJson();
    }

    private static bool Enabled => Volatile.Read(ref enabled) != 0;

    private static void MarkActive(Stage stage)
        => Mark(Volatile.Read(ref activeTraceId), stage);

    private static void MarkActive(PhoneKey key, Stage stage)
    {
        int traceId = Volatile.Read(ref activeTraceId);
        if (Traces.TryGetValue(traceId, out TraceState? trace) && trace.Key == key)
        {
            trace.Mark(stage);
        }
    }

    private static void Mark(int traceId, Stage stage)
    {
        if (traceId > 0 && Traces.TryGetValue(traceId, out TraceState? trace))
        {
            trace.Mark(stage);
        }
    }

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (string pairText in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = pairText.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0].Replace('+', ' ')) == name)
            {
                return pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            }
        }

        return null;
    }

    private enum Stage
    {
        CSharpPointer,
        InputQueued,
        WorkerDequeued,
        MatrixApplied,
        KeypadInterrupt,
        AudioStatePublished,
        AudioEventRaised,
        AudioUiDispatch,
        BackendUpdate,
        PcmDemand,
        PcmRenderStarted,
        PcmRenderCompleted,
    }

    private sealed class TraceState(int id, PhoneKey key)
    {
        private readonly long[] timestamps = new long[Enum.GetValues<Stage>().Length];

        internal PhoneKey Key { get; } = key;

        internal void Mark(Stage stage)
        {
            int index = (int)stage;
            Interlocked.CompareExchange(ref timestamps[index], Stopwatch.GetTimestamp(), 0);
        }

        internal string CreateJson()
        {
            long origin = Volatile.Read(ref timestamps[(int)Stage.CSharpPointer]);
            if (origin == 0)
            {
                return "";
            }

            StringBuilder json = new();
            json.Append("{\"traceId\":");
            json.Append(id.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"key\":\"");
            json.Append(Key);
            json.Append("\",\"stagesMs\":{");
            bool wroteStage = false;
            foreach (Stage stage in Enum.GetValues<Stage>())
            {
                long timestamp = Volatile.Read(ref timestamps[(int)stage]);
                if (timestamp == 0)
                {
                    continue;
                }

                if (wroteStage)
                {
                    json.Append(',');
                }

                json.Append('\"');
                json.Append(StageName(stage));
                json.Append("\":");
                double milliseconds = (timestamp - origin) * 1000.0 / Stopwatch.Frequency;
                json.Append(milliseconds.ToString("0.###", CultureInfo.InvariantCulture));
                wroteStage = true;
            }

            json.Append("}}");
            return json.ToString();
        }

        private static string StageName(Stage stage) => stage switch
        {
            Stage.CSharpPointer => "csharpPointer",
            Stage.InputQueued => "inputQueued",
            Stage.WorkerDequeued => "workerDequeued",
            Stage.MatrixApplied => "matrixApplied",
            Stage.KeypadInterrupt => "keypadInterrupt",
            Stage.AudioStatePublished => "audioStatePublished",
            Stage.AudioEventRaised => "audioEventRaised",
            Stage.AudioUiDispatch => "audioUiDispatch",
            Stage.BackendUpdate => "backendUpdate",
            Stage.PcmDemand => "pcmDemand",
            Stage.PcmRenderStarted => "pcmRenderStarted",
            Stage.PcmRenderCompleted => "pcmRenderCompleted",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
    }
}
#endif
