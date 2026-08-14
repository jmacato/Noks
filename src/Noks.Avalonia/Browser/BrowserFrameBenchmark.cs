#if BROWSER
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Noks.AvaloniaApp.Browser;

internal sealed class BrowserFrameBenchmark
{
    private const int DefaultDurationSeconds = 60;
    private const int RequiredCleanSeconds = 30;
    private static readonly HttpClient Client = new();

    private readonly Control target;
    private readonly Uri reportUri;
    private readonly string runId;
    private readonly TimeSpan duration;
    private readonly List<double> frameIntervals = new(DefaultDurationSeconds * 120);
    private readonly Queue<long> pendingInputTimestamps = new();
    private readonly List<double> inputToFrameLatencies = [];
    private TopLevel? topLevel;
    private long attachedAt;
    private long previousFrameAt;
    private bool started;

    private BrowserFrameBenchmark(
        Control target,
        Uri reportUri,
        string runId,
        TimeSpan duration)
    {
        this.target = target;
        this.reportUri = reportUri;
        this.runId = runId;
        this.duration = duration;
    }

    internal static void TryAttach(Control target, IReadOnlyList<string> args)
    {
        Uri? pageUri = args
            .Select(argument => Uri.TryCreate(argument, UriKind.Absolute, out Uri? uri) ? uri : null)
            .FirstOrDefault(uri => uri?.Scheme is "http" or "https");
        if (pageUri is null || QueryValue(pageUri, "ios-benchmark") != "1")
        {
            return;
        }

        string runId = QueryValue(pageUri, "benchmark-run") ?? Guid.NewGuid().ToString("N");
        int seconds = ParseDuration(QueryValue(pageUri, "benchmark-seconds"));
        BrowserFrameBenchmark benchmark = new(
            target,
            new Uri(pageUri, "/__benchmark/results"),
            runId,
            TimeSpan.FromSeconds(seconds));
        benchmark.Attach();
    }

    private void Attach()
    {
        target.AttachedToVisualTree += OnAttachedToVisualTree;
        target.AddHandler(
            InputElement.PointerPressedEvent,
            OnPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (started)
        {
            return;
        }

        topLevel = TopLevel.GetTopLevel(target);
        if (topLevel is null)
        {
            return;
        }

        started = true;
        attachedAt = Stopwatch.GetTimestamp();
        topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (started)
        {
            pendingInputTimestamps.Enqueue(Stopwatch.GetTimestamp());
        }
    }

    private void OnAnimationFrame(TimeSpan timestamp)
    {
        long now = Stopwatch.GetTimestamp();
        if (previousFrameAt != 0)
        {
            frameIntervals.Add(ElapsedMilliseconds(previousFrameAt, now));
        }

        previousFrameAt = now;
        while (pendingInputTimestamps.TryDequeue(out long inputAt))
        {
            inputToFrameLatencies.Add(ElapsedMilliseconds(inputAt, now));
        }

        if (ElapsedMilliseconds(attachedAt, now) < duration.TotalMilliseconds)
        {
            topLevel?.RequestAnimationFrame(OnAnimationFrame);
            return;
        }

        target.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _ = PostReportAsync(CreateReport());
    }

    private string CreateReport()
    {
        double nominalMilliseconds = DetectNominalFrameInterval(frameIntervals);
        double nominalFps = 1000 / nominalMilliseconds;
        double averageMilliseconds = frameIntervals.Count == 0 ? 0 : frameIntervals.Average();
        double measuredFps = averageMilliseconds == 0 ? 0 : 1000 / averageMilliseconds;
        double jitterLimit = nominalMilliseconds * 1.5;
        int jitterFrames = frameIntervals.Count(interval => interval > jitterLimit);
        int droppedFrames = frameIntervals.Sum(interval =>
            Math.Max(0, (int)Math.Round(interval / nominalMilliseconds) - 1));
        IReadOnlyList<SecondWindow> windows = CreateSecondWindows(frameIntervals, nominalMilliseconds);
        int longestCleanRun = 0;
        int currentCleanRun = 0;
        int? consistentAtSecond = null;

        foreach (SecondWindow window in windows)
        {
            if (window.IsClean)
            {
                currentCleanRun++;
                longestCleanRun = Math.Max(longestCleanRun, currentCleanRun);
                if (consistentAtSecond is null && currentCleanRun >= RequiredCleanSeconds)
                {
                    consistentAtSecond = window.Index - RequiredCleanSeconds + 1;
                }
            }
            else
            {
                currentCleanRun = 0;
            }
        }

        StringBuilder json = new();
        json.Append('{');
        AppendString(json, "source", "avalonia-ui-thread");
        AppendString(json, "runId", runId);
        AppendNumber(json, "durationMs", frameIntervals.Sum());
        AppendNumber(json, "frameCount", frameIntervals.Count);
        AppendNumber(json, "nominalFps", nominalFps);
        AppendNumber(json, "measuredFps", measuredFps);
        AppendNumber(json, "medianMs", Percentile(frameIntervals, 0.50));
        AppendNumber(json, "p95Ms", Percentile(frameIntervals, 0.95));
        AppendNumber(json, "p99Ms", Percentile(frameIntervals, 0.99));
        AppendNumber(json, "maximumMs", frameIntervals.Count == 0 ? 0 : frameIntervals.Max());
        AppendNumber(json, "jitterFrames", jitterFrames);
        AppendNumber(json, "droppedFrames", droppedFrames);
        AppendNumber(json, "completeSeconds", windows.Count);
        AppendNumber(json, "longestCleanRunSeconds", longestCleanRun);
        AppendNullableNumber(json, "consistentAtSecond", consistentAtSecond);
        AppendBoolean(json, "passed", consistentAtSecond is not null);
        json.Append(",\"inputToFrameMs\":[");
        for (int index = 0; index < inputToFrameLatencies.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            json.Append(inputToFrameLatencies[index].ToString("0.###", CultureInfo.InvariantCulture));
        }

        json.Append("]}");
        return json.ToString();
    }

    private async Task PostReportAsync(string report)
    {
        try
        {
            using StringContent content = new(report, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Client.PostAsync(reportUri, content);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"iOS frame benchmark report failed: {ex.Message}");
        }
    }

    private static IReadOnlyList<SecondWindow> CreateSecondWindows(
        IReadOnlyList<double> intervals,
        double nominalMilliseconds)
    {
        List<SecondWindow> windows = [];
        List<double> current = [];
        double elapsed = 0;
        int windowIndex = 0;

        foreach (double interval in intervals)
        {
            elapsed += interval;
            while (elapsed >= (windowIndex + 1) * 1000)
            {
                if (current.Count > 0)
                {
                    double average = current.Average();
                    bool isClean = average <= nominalMilliseconds * 1.02 &&
                        current.All(value => value <= nominalMilliseconds * 1.5);
                    windows.Add(new SecondWindow(windowIndex, isClean));
                }

                current.Clear();
                windowIndex++;
            }

            current.Add(interval);
        }

        return windows;
    }

    private static double DetectNominalFrameInterval(IReadOnlyList<double> intervals)
    {
        double median = Percentile(intervals.Take(120).ToArray(), 0.5);
        if (median <= 12.5)
        {
            return 1000.0 / 120;
        }

        return median <= 25 ? 1000.0 / 60 : median;
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        int index = (int)Math.Ceiling((sorted.Length - 1) * percentile);
        return sorted[index];
    }

    private static double ElapsedMilliseconds(long start, long end)
        => (end - start) * 1000.0 / Stopwatch.Frequency;

    private static int ParseDuration(string? value)
        => int.TryParse(value, CultureInfo.InvariantCulture, out int seconds)
            ? Math.Clamp(seconds, RequiredCleanSeconds + 5, 180)
            : DefaultDurationSeconds;

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

    private static void AppendString(StringBuilder json, string name, string value)
    {
        AppendName(json, name);
        json.Append('"');
        json.Append(JsonEncodedText.Encode(value));
        json.Append('"');
    }

    private static void AppendNumber(StringBuilder json, string name, double value)
    {
        AppendName(json, name);
        json.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static void AppendNullableNumber(StringBuilder json, string name, int? value)
    {
        AppendName(json, name);
        json.Append(value?.ToString(CultureInfo.InvariantCulture) ?? "null");
    }

    private static void AppendBoolean(StringBuilder json, string name, bool value)
    {
        AppendName(json, name);
        json.Append(value ? "true" : "false");
    }

    private static void AppendName(StringBuilder json, string name)
    {
        if (json.Length > 1)
        {
            json.Append(',');
        }

        json.Append('"');
        json.Append(name);
        json.Append("\":");
    }

    private sealed record SecondWindow(int Index, bool IsClean);
}
#endif
