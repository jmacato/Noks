#if !BROWSER
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Noks.AvaloniaApp.Audio;

/// <summary>Streams signed 16-bit mono PCM to or from the default Windows waveform device.</summary>
internal sealed class WindowsPcmStream : IDesktopPcmStream
{
    private const uint WaveMapper = uint.MaxValue;
    private const uint CallbackEvent = 0x0005_0000;
    private const uint WhdrDone = 0x0000_0001;
    private const int BufferCount = 3;
    private const int SamplesPerBuffer = 256;
    private static readonly ConcurrentBag<WindowsPcmStream> FailedInitializations = [];

    private readonly object _processGate = new();
    private readonly object _disposeGate = new();
    private readonly Action<short[]> _process;
    private readonly bool _capture;
    private readonly Buffer[] _buffers = new Buffer[BufferCount];

    private EventWaitHandle? _bufferAvailable;
    private EventWaitHandle? _disposeRequested;
    private IntPtr _device;
    private Thread? _worker;
    private Exception? _failure;
    private int _disposed;
    private bool _cleanupCompleted;

    internal WindowsPcmStream(int sampleRate, Action<short[]> process, bool capture)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentNullException.ThrowIfNull(process);

        if (capture && waveInGetNumDevs() == 0)
        {
            throw new InvalidOperationException("Windows has no active microphone input device. Connect or enable a microphone before capture.");
        }

        _process = process;
        _capture = capture;

        try
        {
            _bufferAvailable = new EventWaitHandle(false, EventResetMode.AutoReset);
            _disposeRequested = new EventWaitHandle(false, EventResetMode.ManualReset);

            var format = new WaveFormatEx
            {
                FormatTag = 1, // WAVE_FORMAT_PCM
                Channels = 1,
                SamplesPerSec = checked((uint)sampleRate),
                AvgBytesPerSec = checked((uint)sampleRate * sizeof(short)),
                BlockAlign = sizeof(short),
                BitsPerSample = sizeof(short) * 8,
                Size = 0,
            };

            if (_capture)
            {
                ThrowIfFailed(
                    waveInOpen(
                        out _device,
                        WaveMapper,
                        ref format,
                        _bufferAvailable.SafeWaitHandle.DangerousGetHandle(),
                        IntPtr.Zero,
                        CallbackEvent),
                    "waveInOpen");
            }
            else
            {
                ThrowIfFailed(
                    waveOutOpen(
                        out _device,
                        WaveMapper,
                        ref format,
                        _bufferAvailable.SafeWaitHandle.DangerousGetHandle(),
                        IntPtr.Zero,
                        CallbackEvent),
                    "waveOutOpen");
            }

            for (var index = 0; index < _buffers.Length; index++)
            {
                _buffers[index] = new Buffer(SamplesPerBuffer);
                ThrowIfFailed(
                    _capture
                        ? waveInPrepareHeader(_device, _buffers[index].Header, (uint)Marshal.SizeOf<WaveHeader>())
                        : waveOutPrepareHeader(_device, _buffers[index].Header, (uint)Marshal.SizeOf<WaveHeader>()),
                    _capture ? "waveInPrepareHeader" : "waveOutPrepareHeader");
                _buffers[index].Prepared = true;
            }

            _worker = new Thread(Run)
            {
                IsBackground = true,
                Name = _capture ? "Noks Windows PCM capture" : "Noks Windows PCM output",
            };
            _worker.Start();
        }
        catch (Exception initializationFailure)
        {
            try
            {
                DisposeNativeResources();
            }
            catch (Exception cleanupFailure)
            {
                // Keep the event, headers, and pins alive: the native device may still reference them.
                Interlocked.CompareExchange(ref _failure, cleanupFailure, null);
                FailedInitializations.Add(this);
                throw new AggregateException(
                    "Windows PCM initialization failed and native cleanup did not complete. Native buffer memory remains retained.",
                    initializationFailure,
                    cleanupFailure);
            }

            DisposeEvents();
            throw;
        }
    }

    public Exception? Failure => Volatile.Read(ref _failure);

    public void Dispose()
    {
        // A rendering callback runs on this worker. It cannot synchronously wait for itself.
        if (Thread.CurrentThread == _worker)
        {
            throw new InvalidOperationException("WindowsPcmStream cannot be disposed from its rendering callback.");
        }

        // Hold the gate through cleanup so every concurrent caller returns only after it completes.
        lock (_disposeGate)
        {
            if (_cleanupCompleted)
            {
                return;
            }

            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // Do not let a new consumer call start after disposal has been requested.
                lock (_processGate)
                {
                }
            }

            _disposeRequested!.Set();
            _worker?.Join();

            // The worker owns waveOutWrite. Reset only after it has stopped using the device.
            DisposeNativeResources();
            DisposeEvents();
            _cleanupCompleted = true;
            GC.SuppressFinalize(this);
        }
    }

    private void Run()
    {
        try
        {
            if (_capture)
            {
                RunCapture();
            }
            else
            {
                RunOutput();
            }
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref _failure, exception, null);
            ResetDevice();
        }
    }

    private void RunOutput()
    {
        foreach (var buffer in _buffers)
        {
            QueueOutputBuffer(buffer);
        }

        var waitHandles = new WaitHandle[] { _disposeRequested!, _bufferAvailable! };
        while (WaitHandle.WaitAny(waitHandles) != 0)
        {
            foreach (var buffer in _buffers)
            {
                if ((ReadHeader(buffer.Header).Flags & WhdrDone) != 0)
                {
                    QueueOutputBuffer(buffer);
                }
            }
        }
    }

    private void RunCapture()
    {
        foreach (var buffer in _buffers)
        {
            if (!QueueCaptureBuffer(buffer))
            {
                return;
            }
        }

        ThrowIfFailed(waveInStart(_device), "waveInStart");

        var waitHandles = new WaitHandle[] { _disposeRequested!, _bufferAvailable! };
        while (WaitHandle.WaitAny(waitHandles) != 0)
        {
            foreach (var buffer in _buffers)
            {
                var header = ReadHeader(buffer.Header);
                if ((header.Flags & WhdrDone) == 0)
                {
                    continue;
                }

                DeliverCapturedBuffer(buffer, header.BytesRecorded);
                if (!QueueCaptureBuffer(buffer))
                {
                    return;
                }
            }
        }
    }

    private void QueueOutputBuffer(Buffer buffer)
    {
        lock (_processGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _process(buffer.Samples);
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ThrowIfFailed(waveOutWrite(_device, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()), "waveOutWrite");
    }

    private bool QueueCaptureBuffer(Buffer buffer)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        ThrowIfFailed(waveInAddBuffer(_device, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()), "waveInAddBuffer");
        return true;
    }

    private void DeliverCapturedBuffer(Buffer buffer, uint bytesRecorded)
    {
        var samplesRecorded = checked((int)Math.Min(bytesRecorded, (uint)(buffer.Samples.Length * sizeof(short))) / sizeof(short));
        if (samplesRecorded == 0)
        {
            return;
        }

        lock (_processGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _process(samplesRecorded == buffer.Samples.Length
                ? buffer.Samples
                : buffer.Samples.AsSpan(0, samplesRecorded).ToArray());
        }
    }

    private void ResetDevice()
    {
        var device = _device;
        if (device != IntPtr.Zero)
        {
            _ = ResetDevice(device);
        }
    }

    private void DisposeNativeResources()
    {
        var device = _device;
        if (device != IntPtr.Zero)
        {
            ThrowIfFailed(ResetDevice(device), _capture ? "waveInReset" : "waveOutReset");
            foreach (var buffer in _buffers)
            {
                if (buffer?.Prepared == true)
                {
                    var result = _capture
                        ? waveInUnprepareHeader(device, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>())
                        : waveOutUnprepareHeader(device, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>());
                    ThrowIfFailed(result, _capture ? "waveInUnprepareHeader" : "waveOutUnprepareHeader");
                    buffer.Prepared = false;
                }
            }

            ThrowIfFailed(_capture ? waveInClose(device) : waveOutClose(device), _capture ? "waveInClose" : "waveOutClose");
            _device = IntPtr.Zero;
        }

        foreach (var buffer in _buffers)
        {
            buffer?.Dispose();
        }
    }

    private void DisposeEvents()
    {
        _bufferAvailable?.Dispose();
        _bufferAvailable = null;
        _disposeRequested?.Dispose();
        _disposeRequested = null;
    }

    private static WaveHeader ReadHeader(IntPtr header) => Marshal.PtrToStructure<WaveHeader>(header);

    private uint ResetDevice(IntPtr device)
    {
        return _capture ? waveInReset(device) : waveOutReset(device);
    }

    private static void ThrowIfFailed(uint result, string operation)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"{operation} failed with WinMM error {result}.");
        }
    }

    private sealed class Buffer : IDisposable
    {
        public Buffer(int samples)
        {
            Samples = new short[samples];
            try
            {
                _pin = GCHandle.Alloc(Samples, GCHandleType.Pinned);
                Header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                Marshal.StructureToPtr(new WaveHeader
                {
                    Data = _pin.AddrOfPinnedObject(),
                    BufferLength = checked((uint)(samples * sizeof(short))),
                }, Header, false);
            }
            catch
            {
                if (Header != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(Header);
                }

                if (_pin.IsAllocated)
                {
                    _pin.Free();
                }

                throw;
            }
        }

        public short[] Samples { get; }

        public IntPtr Header { get; private set; }

        public bool Prepared { get; set; }

        private GCHandle _pin;

        public void Dispose()
        {
            if (Header != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Header);
                Header = IntPtr.Zero;
            }

            if (_pin.IsAllocated)
            {
                _pin.Free();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    // WAVEHDR uses DWORD_PTR for User and Reserved. This layout is 48 bytes on x64 and ARM64.
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint headerSize);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint headerSize);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutWrite(IntPtr device, IntPtr header, uint headerSize);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutReset(IntPtr device);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutClose(IntPtr device);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInOpen(out IntPtr device, uint deviceId, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInPrepareHeader(IntPtr device, IntPtr header, uint headerSize);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInUnprepareHeader(IntPtr device, IntPtr header, uint headerSize);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInAddBuffer(IntPtr device, IntPtr header, uint headerSize);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInStart(IntPtr device);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInReset(IntPtr device);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInClose(IntPtr device);
}
#endif
