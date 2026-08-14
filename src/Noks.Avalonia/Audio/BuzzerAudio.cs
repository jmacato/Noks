using System.Runtime.InteropServices;
using Noks.Dct3.Audio;
using Noks.Dct3.Messaging;

namespace Noks.AvaloniaApp.Audio;

public sealed class BuzzerAudio : IPhoneAudio
{
    private const string AudioToolboxLibrary = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const int SampleRate = Dct3AudioPcmGenerator.DefaultSampleRate;
    // Three buffers total about 17.4 ms. Larger buffers smear the firmware's
    // short articulation gaps and make adjacent notes sound uneven.
    private const int FramesPerBuffer = 256;
    private const int BytesPerFrame = 2;
    private const int BufferCount = 3;
    private const uint AudioFormatLinearPcm = 0x6C70636D; // 'lpcm'
    private const uint AudioFormatFlagIsSignedInteger = 1u << 2;
    private const uint AudioFormatFlagIsPacked = 1u << 3;
    private readonly object lifecycleLock = new();
    private readonly object stateLock = new();
    private readonly object renderLock = new();
    private readonly Dct3AudioPcmGenerator generator = new(SampleRate);
    private readonly ushort[] pcmBuffer = new ushort[FramesPerBuffer];
    private readonly short[] renderBuffer = new short[FramesPerBuffer];
    private readonly IntPtr[] buffers = new IntPtr[BufferCount];
    private readonly AudioQueueOutputCallback callback;
    private GCHandle selfHandle;
    private IntPtr queue;
    private bool audible;
    private bool disposed;

    public BuzzerAudio()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("CoreAudio output is only available on macOS.");
        }

        callback = OnAudioQueueOutput;
        selfHandle = GCHandle.Alloc(this);

        try
        {
            EnsureQueueStarted();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Update(Dct3AudioState state)
    {
        ThrowIfDisposed();

        lock (stateLock)
        {
            generator.Update(state);
            audible = generator.Audible;
        }

        // Keep CoreAudio active through silent articulation gaps. A queue restart adds latency
        // to each note after a rest. Dispose stops the queue.
        if (audible)
        {
            EnsureQueueStarted();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StopQueue();

        if (selfHandle.IsAllocated)
        {
            selfHandle.Free();
        }
    }

    private void EnsureQueueStarted()
    {
        lock (lifecycleLock)
        {
            ThrowIfDisposed();

            if (queue != IntPtr.Zero)
            {
                return;
            }

            IntPtr nextQueue = IntPtr.Zero;

            try
            {
                AudioStreamBasicDescription format = new()
                {
                    SampleRate = SampleRate,
                    FormatId = AudioFormatLinearPcm,
                    FormatFlags = AudioFormatFlagIsSignedInteger | AudioFormatFlagIsPacked,
                    BytesPerPacket = BytesPerFrame,
                    FramesPerPacket = 1,
                    BytesPerFrame = BytesPerFrame,
                    ChannelsPerFrame = 1,
                    BitsPerChannel = 16,
                };

                Check(
                    AudioQueueNewOutput(
                        ref format,
                        callback,
                        GCHandle.ToIntPtr(selfHandle),
                        IntPtr.Zero,
                        IntPtr.Zero,
                        0,
                        out nextQueue),
                    nameof(AudioQueueNewOutput));

                queue = nextQueue;
                uint bufferByteSize = FramesPerBuffer * BytesPerFrame;

                for (int i = 0; i < buffers.Length; i++)
                {
                    Check(AudioQueueAllocateBuffer(nextQueue, bufferByteSize, out buffers[i]), nameof(AudioQueueAllocateBuffer));
                    Check(FillAndEnqueue(nextQueue, buffers[i]), nameof(AudioQueueEnqueueBuffer));
                }

                Check(AudioQueueStart(nextQueue, IntPtr.Zero), nameof(AudioQueueStart));
            }
            catch
            {
                queue = IntPtr.Zero;
                Array.Clear(buffers);

                if (nextQueue != IntPtr.Zero)
                {
                    _ = AudioQueueDispose(nextQueue, 1);
                }

                throw;
            }
        }
    }

    private void StopQueue()
    {
        IntPtr stoppedQueue;

        lock (lifecycleLock)
        {
            stoppedQueue = queue;

            if (stoppedQueue == IntPtr.Zero)
            {
                return;
            }

            queue = IntPtr.Zero;
            Array.Clear(buffers);
        }

        lock (renderLock)
        {
            lock (stateLock)
            {
                generator.Reset();
                audible = false;
            }
        }

        _ = AudioQueueStop(stoppedQueue, 1);
        _ = AudioQueueDispose(stoppedQueue, 1);
    }

    private static void OnAudioQueueOutput(IntPtr userData, IntPtr audioQueue, IntPtr buffer)
    {
        try
        {
            if (userData == IntPtr.Zero)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr(userData);

            if (handle.Target is BuzzerAudio audio)
            {
                audio.FillAndEnqueue(audioQueue, buffer);
            }
        }
        catch
        {
            // Never let a managed exception cross the native Audio Queue callback boundary.
        }
    }

    private int FillAndEnqueue(IntPtr audioQueue, IntPtr buffer)
    {
        if (disposed || audioQueue == IntPtr.Zero || audioQueue != queue)
        {
            return 0;
        }

        lock (renderLock)
        {
            if (disposed || audioQueue != queue)
            {
                return 0;
            }

            Render();

            if (disposed || audioQueue != queue)
            {
                return 0;
            }

            AudioQueueBuffer audioBuffer = Marshal.PtrToStructure<AudioQueueBuffer>(buffer);
            Marshal.Copy(renderBuffer, 0, audioBuffer.AudioData, renderBuffer.Length);
            audioBuffer.AudioDataByteSize = FramesPerBuffer * BytesPerFrame;
            Marshal.StructureToPtr(audioBuffer, buffer, false);
        }

        if (disposed || audioQueue != queue)
        {
            return 0;
        }

        return AudioQueueEnqueueBuffer(audioQueue, buffer, 0, IntPtr.Zero);
    }

    private void Render()
    {
        lock (stateLock)
        {
            generator.Render(pcmBuffer);
        }

        for (int i = 0; i < renderBuffer.Length; i++)
        {
            renderBuffer[i] = unchecked((short)pcmBuffer[i]);
        }
    }

    private static void Check(int status, string operation)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"{operation} failed with OSStatus {status}.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(BuzzerAudio));
        }
    }

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription format,
        AudioQueueOutputCallback callback,
        IntPtr userData,
        IntPtr callbackRunLoop,
        IntPtr callbackRunLoopMode,
        uint flags,
        out IntPtr queue);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bufferByteSize, out IntPtr buffer);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueEnqueueBuffer(
        IntPtr queue,
        IntPtr buffer,
        uint packetDescriptionCount,
        IntPtr packetDescriptions);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueStop(IntPtr queue, byte immediate);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioQueueDispose(IntPtr queue, byte immediate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AudioQueueOutputCallback(IntPtr userData, IntPtr queue, IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }
}
