#if !BROWSER
using System.Net;
using System.Text;
using System.Text.Json;
using Noks.Dct3.Audio;
using Noks.Dct3.Display;
using Noks.AvaloniaApp.Emulation;
using Noks.Application.Input;

namespace Noks.AvaloniaApp.Diagnostics;

internal sealed class DesktopAutomationServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Func<PhoneEmulator> getEmulator;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task serverTask;

    private DesktopAutomationServer(int port, Func<PhoneEmulator> getEmulator)
    {
        this.getEmulator = getEmulator;
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        serverTask = ServeAsync(cancellation.Token);
        Console.WriteLine($"Noks desktop automation: http://127.0.0.1:{port}/");
    }

    public static DesktopAutomationServer? TryStart(IReadOnlyList<string> args, Func<PhoneEmulator> getEmulator)
    {
        int option = -1;
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == "--automation-port")
            {
                option = i;
                break;
            }
        }

        if (option < 0)
        {
            return null;
        }

        if (!int.TryParse(args[option + 1], out int port) || port is < 1024 or > 65535)
        {
            throw new ArgumentException("The --automation-port value is invalid. Use a port from 1024 through 65535.");
        }

        return new DesktopAutomationServer(port, getEmulator);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        listener.Close();
        try
        {
            serverTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        cancellation.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = HandleAsync(context, cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (context.Request.HttpMethod == "GET" && path is "" or "/")
            {
                await WriteTextAsync(
                    context.Response,
                    "GET /state\nGET /frame.pgm\nGET /memory?address=0x100000&length=256\n" +
                    "POST /incoming/call?number=12345\nPOST /incoming/sms?originator=12345&text=hello\n" +
                    "POST /key/{name}/tap?holdMs=100\nPOST /key/{name}/press\nPOST /key/{name}/release\n\n" +
                    "Keys: 0-9, star, hash, menu, up, down, back, power\n" +
                    "Example: curl -d '' http://127.0.0.1:PORT/key/menu/tap\n",
                    "text/plain; charset=utf-8",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/state")
            {
                await WriteStateAsync(context.Response, getEmulator(), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/frame.pgm")
            {
                await WriteFrameAsync(context.Response, getEmulator().Frame, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/memory")
            {
                uint address = ParseMemoryAddress(context.Request.QueryString["address"]);
                int length = ParseMemoryLength(context.Request.QueryString["length"]);
                byte[] data = await getEmulator().ReadMemoryAsync(address, length, cancellationToken).ConfigureAwait(false);
                await WriteBytesAsync(context.Response, data, "application/octet-stream", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/incoming/call")
            {
                getEmulator().QueueIncomingCall(context.Request.QueryString["number"] ?? "12345");
                await WriteTextAsync(context.Response, "ok\n", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/incoming/sms")
            {
                getEmulator().QueueIncomingSms(
                    context.Request.QueryString["originator"] ?? "12345",
                    context.Request.QueryString["text"] ?? "");
                await WriteTextAsync(context.Response, "ok\n", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && TryParseKeyPath(path, out PhoneKey key, out string action))
            {
                PhoneEmulator emulator = getEmulator();
                switch (action)
                {
                    case "press":
                        emulator.SetKey(key, true);
                        break;
                    case "release":
                        emulator.SetKey(key, false);
                        break;
                    case "tap":
                        int holdMilliseconds = ParseHoldMilliseconds(context.Request.QueryString["holdMs"]);
                        emulator.SetKey(key, true);
                        try
                        {
                            await Task.Delay(holdMilliseconds, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            emulator.SetKey(key, false);
                        }

                        break;
                }

                await WriteTextAsync(context.Response, "ok\n", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await WriteTextAsync(context.Response, "not found\n", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await WriteTextAsync(context.Response, ex.Message + "\n", "text/plain; charset=utf-8", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static async Task WriteStateAsync(
        HttpListenerResponse response,
        PhoneEmulator emulator,
        CancellationToken cancellationToken)
    {
        PhoneTelemetryState telemetry = emulator.Telemetry;
        GsmControlState gsm = emulator.GsmState;
        Dct3AudioState audio = emulator.AudioState;
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartObject();
            json.WriteString("status", emulator.Status);
            json.WriteNumber("steps", telemetry.ExecutedSteps);
            json.WriteNumber("cycles", telemetry.Cycles);
            json.WriteNumber("emulatedSeconds", telemetry.EmulatedSeconds);
            json.WriteString("pc", $"{telemetry.Pc:X8}");
            json.WriteNumber("undefinedInstructions", telemetry.UndefinedInstructions);
            json.WriteString("lastUndefinedAddress", $"{telemetry.LastUndefinedAddress:X8}");
            json.WriteString("lastUndefinedInstruction", $"{telemetry.LastUndefinedInstruction:X8}");
            json.WriteString("simControl", $"{telemetry.SimControl:X2}");
            json.WriteString("simStatus", $"{telemetry.SimStatus:X2}");
            json.WriteString("simInterrupt", $"{telemetry.SimInterrupt:X2}");
            json.WriteNumber("simRxCount", telemetry.SimRxCount);
            json.WriteNumber("simTxCount", telemetry.SimTxCount);
            json.WriteBoolean("poweredOff", telemetry.PoweredOff);
            json.WriteBoolean("registered", gsm.Registered);
            json.WriteBoolean("dedicatedChannel", gsm.DedicatedChannelActive);
            json.WriteNumber("pendingGsm", gsm.PendingIncomingServices);
            json.WriteBoolean("toneAudible", audio.Audible);
            json.WriteNumber("toneDivider", audio.Buzzer.BuzzerDivider);
            json.WriteNumber("toneVolume", audio.Buzzer.BuzzerVolume);
            json.WriteBoolean("dspToneAudible", audio.DspTone.Audible);
            json.WriteNumber("dspToneOscillator1Hz", audio.DspTone.Oscillator1Hz);
            json.WriteNumber("dspToneOscillator2Hz", audio.DspTone.Oscillator2Hz);
            json.WriteNumber("dspToneAmplitude", audio.DspTone.Amplitude);
            json.WriteString("heldKeys", telemetry.HeldInputKeys);
            json.WriteNumber("pendingKeyTransitions", emulator.PendingKeyTransitions);
            json.WriteNumber("lcdWrites", emulator.Frame.DataWrites);
            json.WriteNumber("wallClockPauses", telemetry.WallClockPauseCount);
            json.WriteEndObject();
        }

        response.ContentType = "application/json";
        response.ContentLength64 = buffer.Length;
        buffer.Position = 0;
        await buffer.CopyToAsync(response.OutputStream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteFrameAsync(
        HttpListenerResponse response,
        PhoneEmulator.LcdFrame frame,
        CancellationToken cancellationToken)
    {
        byte[] header = Encoding.ASCII.GetBytes($"P5\n{Pcd8544.Width} {Pcd8544.Height}\n255\n");
        byte[] pixels = new byte[Pcd8544.Width * Pcd8544.Height];
        for (int y = 0; y < Pcd8544.Height; y++)
        {
            for (int x = 0; x < Pcd8544.Width; x++)
            {
                pixels[y * Pcd8544.Width + x] = frame.GetPixel(x, y) ? (byte)0 : (byte)255;
            }
        }

        response.ContentType = "image/x-portable-graymap";
        response.ContentLength64 = header.Length + pixels.Length;
        await response.OutputStream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await response.OutputStream.WriteAsync(pixels, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(
        HttpListenerResponse response,
        string text,
        string contentType,
        CancellationToken cancellationToken)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        response.ContentType = contentType;
        response.ContentLength64 = data.Length;
        await response.OutputStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteBytesAsync(
        HttpListenerResponse response,
        byte[] data,
        string contentType,
        CancellationToken cancellationToken)
    {
        response.ContentType = contentType;
        response.ContentLength64 = data.Length;
        await response.OutputStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private static uint ParseMemoryAddress(string? value)
    {
        string text = value?.Trim() ?? "";
        bool parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint address)
            : uint.TryParse(text, out address);
        return parsed ? address : throw new ArgumentException("The address must be decimal or a hexadecimal RAM address with the 0x prefix.");
    }

    private static int ParseMemoryLength(string? value) =>
        int.TryParse(value, out int length) && length is >= 1 and <= 0x80000
            ? length
            : throw new ArgumentException("The length must be from 1 through 524288 bytes.");

    private static int ParseHoldMilliseconds(string? value) =>
        int.TryParse(value, out int milliseconds) ? Math.Clamp(milliseconds, 10, 5000) : 100;

    private static bool TryParseKeyPath(string path, out PhoneKey key, out string action)
    {
        key = default;
        action = "";
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 &&
            parts[0] == "key" &&
            TryParseKey(parts[1], out key) &&
            (action = parts[2]) is "tap" or "press" or "release";
    }

    private static bool TryParseKey(string name, out PhoneKey key)
    {
        key = name.ToLowerInvariant() switch
        {
            "0" => PhoneKey.Digit0,
            "1" => PhoneKey.Digit1,
            "2" => PhoneKey.Digit2,
            "3" => PhoneKey.Digit3,
            "4" => PhoneKey.Digit4,
            "5" => PhoneKey.Digit5,
            "6" => PhoneKey.Digit6,
            "7" => PhoneKey.Digit7,
            "8" => PhoneKey.Digit8,
            "9" => PhoneKey.Digit9,
            "star" or "asterisk" => PhoneKey.Star,
            "hash" or "pound" => PhoneKey.Hash,
            "menu" or "ok" or "enter" => PhoneKey.Main,
            "up" or "left" => PhoneKey.Left,
            "down" or "right" => PhoneKey.Right,
            "clear" or "back" or "c" or "cancel" => PhoneKey.Cancel,
            "power" => PhoneKey.Power,
            _ => (PhoneKey)(-1),
        };
        return (int)key >= 0;
    }
}
#endif
