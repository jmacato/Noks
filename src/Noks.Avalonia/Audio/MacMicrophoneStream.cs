#if !BROWSER
using System.Runtime.InteropServices;

namespace Noks.AvaloniaApp.Audio;

internal sealed class MacMicrophoneStream : IDesktopPcmStream
{
    private const string Library = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const int FramesPerBuffer = 256;
    private readonly object disposeLock = new();
    private readonly Action<short[]> receive;
    private readonly InputCallback callback;
    private readonly short[] samples = new short[FramesPerBuffer];
    private GCHandle selfHandle;
    private IntPtr queue;
    private Exception? failure;
    private int disposed;
    [ThreadStatic] private static MacMicrophoneStream? callbackStream;

    internal MacMicrophoneStream(int sampleRate, Action<short[]> receive)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Audio Queue input requires macOS.");
        ArgumentNullException.ThrowIfNull(receive);
        this.receive = receive;
        callback = OnInput;
        selfHandle = GCHandle.Alloc(this);
        try
        {
            StreamFormat format = new()
            {
                SampleRate = sampleRate,
                FormatId = 0x6C70636D,
                FormatFlags = (1u << 2) | (1u << 3),
                BytesPerPacket = 2,
                FramesPerPacket = 1,
                BytesPerFrame = 2,
                ChannelsPerFrame = 1,
                BitsPerChannel = 16,
            };
            Check(AudioQueueNewInput(ref format, callback, GCHandle.ToIntPtr(selfHandle),
                IntPtr.Zero, IntPtr.Zero, 0, out queue), nameof(AudioQueueNewInput));
            for (int i = 0; i < 3; i++)
            {
                Check(AudioQueueAllocateBuffer(queue, FramesPerBuffer * 2, out IntPtr buffer), nameof(AudioQueueAllocateBuffer));
                Check(AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero), nameof(AudioQueueEnqueueBuffer));
            }
            Check(AudioQueueStart(queue, IntPtr.Zero), nameof(AudioQueueStart));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Exception? Failure => Volatile.Read(ref failure);

    public void Dispose()
    {
        if (ReferenceEquals(callbackStream, this))
            throw new InvalidOperationException("A microphone callback cannot dispose its own stream.");
        lock (disposeLock)
        {
            if (Volatile.Read(ref disposed) != 0 && !selfHandle.IsAllocated)
                return;
            Volatile.Write(ref disposed, 1);
            if (queue != IntPtr.Zero)
            {
                // Synchronous disposal ends native callbacks before their GCHandle is released.
                _ = AudioQueueStop(queue, 1);
                // Keep the native callback root alive if disposal fails. A later call can retry.
                Check(AudioQueueDispose(queue, 1), nameof(AudioQueueDispose));
                queue = IntPtr.Zero;
            }
            if (selfHandle.IsAllocated)
                selfHandle.Free();
            GC.KeepAlive(callback);
        }
    }

    private static void OnInput(IntPtr userData, IntPtr queue, IntPtr buffer,
        IntPtr timestamp, uint packetCount, IntPtr packetDescriptions)
    {
        MacMicrophoneStream? stream = null;
        MacMicrophoneStream? previous = callbackStream;
        try
        {
            stream = GCHandle.FromIntPtr(userData).Target as MacMicrophoneStream;
            if (stream is null || Volatile.Read(ref stream.disposed) != 0 || stream.Failure is not null)
                return;
            callbackStream = stream;
            AudioBuffer native = Marshal.PtrToStructure<AudioBuffer>(buffer);
            int count = checked((int)native.AudioDataByteSize / 2);
            if (count > FramesPerBuffer || native.AudioDataByteSize % 2 != 0)
                throw new IOException("The microphone returned an invalid PCM buffer.");
            if (count > 0)
            {
                short[] data = count == FramesPerBuffer ? stream.samples : new short[count];
                Marshal.Copy(native.AudioData, data, 0, count);
                stream.receive(data);
            }
            if (Volatile.Read(ref stream.disposed) == 0)
                Check(AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero), nameof(AudioQueueEnqueueBuffer));
        }
        catch (Exception exception)
        {
            if (stream is not null)
                Interlocked.CompareExchange(ref stream.failure, exception, null);
            // Managed exceptions must not cross the native callback boundary.
        }
        finally
        {
            callbackStream = previous;
        }
    }

    private static void Check(int status, string operation)
    {
        if (status != 0)
            throw new InvalidOperationException($"{operation} failed with OSStatus {status}. Check microphone access in System Settings.");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InputCallback(IntPtr userData, IntPtr queue, IntPtr buffer,
        IntPtr timestamp, uint packetCount, IntPtr packetDescriptions);

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamFormat
    {
        public double SampleRate;
        public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame;
        public uint ChannelsPerFrame, BitsPerChannel, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [DllImport(Library)]
    private static extern int AudioQueueNewInput(ref StreamFormat format, InputCallback callback,
        IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);
    [DllImport(Library)]
    private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bytes, out IntPtr buffer);
    [DllImport(Library)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetCount, IntPtr packets);
    [DllImport(Library)]
    private static extern int AudioQueueStart(IntPtr queue, IntPtr time);
    [DllImport(Library)]
    private static extern int AudioQueueStop(IntPtr queue, byte immediate);
    [DllImport(Library)]
    private static extern int AudioQueueDispose(IntPtr queue, byte immediate);
}
#endif
