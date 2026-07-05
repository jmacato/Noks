using Noks.Dct3.Core;
namespace Noks.Dct3.Memory;

public sealed class IntelFlash16
{
    private const ushort ManufacturerId = 0x0089;
    private const ushort DeviceId = 0xD0;
    private const int BlockSize = 0x10000;

    private enum FlashMode
    {
        ReadArray,
        ReadId,
        ReadStatus,
        ProgramSetup,
        EraseSetup,
    }

    private readonly byte[] data;
    private readonly IDct3Trace? trace;
    private byte[]? persistenceBaseline;
    private bool[]? persistenceDirtyBlocks;
    private FlashMode mode = FlashMode.ReadArray;
    private byte status = 0x80;

    public IntelFlash16(byte[] image, int size, IDct3Trace? trace)
    {
        this.trace = trace;
        data = new byte[size];
        Array.Fill(data, (byte)0xFF);
        image.AsSpan(0, Math.Min(image.Length, size)).CopyTo(data);
    }

    public byte[] Data => data;

    public bool InArrayMode => mode == FlashMode.ReadArray;

    public int ProgramCount { get; private set; }

    public int EraseCount { get; private set; }

    public long PersistenceVersion { get; private set; }

    public ushort ReadDevice(uint offset)
    {
        return mode switch
        {
            FlashMode.ReadId => ((offset >> 1) & 1) == 0 ? ManufacturerId : DeviceId,
            FlashMode.ReadStatus => status,
            _ => (ushort)((data[offset & ~1u] << 8) | data[(offset & ~1u) + 1]),
        };
    }

    public void WriteDevice(uint offset, ushort value)
    {
        switch (mode)
        {
            case FlashMode.ProgramSetup:
                byte high = (byte)(data[offset & ~1u] & (byte)(value >> 8));
                byte low = (byte)(data[(offset & ~1u) + 1] & (byte)value);
                bool programChanged = data[offset & ~1u] != high || data[(offset & ~1u) + 1] != low;
                data[offset & ~1u] = high;
                data[(offset & ~1u) + 1] = low;
                ProgramCount++;
                if (programChanged)
                {
                    MarkPersistenceDirty(offset & ~1u, 2);
                    PersistenceVersion++;
                }

                mode = FlashMode.ReadStatus;
                return;

            case FlashMode.EraseSetup:
                if ((value & 0xFF) == 0xD0)
                {
                    uint block = offset & ~(uint)(BlockSize - 1);
                    bool eraseChanged = data.AsSpan((int)block, BlockSize).IndexOfAnyExcept((byte)0xFF) >= 0;
                    Array.Fill(data, (byte)0xFF, (int)block, BlockSize);
                    EraseCount++;
                    if (eraseChanged)
                    {
                        MarkPersistenceDirty(block, BlockSize);
                        PersistenceVersion++;
                    }

                    trace?.FlashCommand($"erase block {block:X6}");
                }
                else
                {
                    status |= 0x30;
                    trace?.FlashCommand($"bad erase confirm {value:X4}");
                }

                mode = FlashMode.ReadStatus;
                return;
        }

        switch (value & 0xFF)
        {
            case 0xFF:
                mode = FlashMode.ReadArray;
                break;
            case 0x90:
                mode = FlashMode.ReadId;
                trace?.FlashCommand("read id");
                break;
            case 0x70:
                mode = FlashMode.ReadStatus;
                break;
            case 0x50:
                status = 0x80;
                mode = FlashMode.ReadArray;
                break;
            case 0x40 or 0x10:
                mode = FlashMode.ProgramSetup;
                break;
            case 0x20:
                mode = FlashMode.EraseSetup;
                break;
            case 0xB0 or 0xD0:
                mode = FlashMode.ReadStatus;
                break;
            default:
                trace?.FlashCommand($"unknown command {value:X4} at {offset:X6}");
                mode = FlashMode.ReadArray;
                break;
        }
    }

    public void CapturePersistenceBaseline()
    {
        persistenceBaseline = data.ToArray();
        persistenceDirtyBlocks = new bool[(data.Length + BlockSize - 1) / BlockSize];
        PersistenceVersion++;
    }

    public void ApplyOverlay(IEnumerable<FlashOverlayBlock> blocks)
    {
        foreach (FlashOverlayBlock block in blocks)
        {
            if (block.Offset < 0 ||
                block.Offset >= data.Length ||
                block.Data.Length == 0 ||
                block.Data.Length > data.Length - block.Offset)
            {
                continue;
            }

            block.Data.CopyTo(data.AsSpan(block.Offset));
            MarkPersistenceDirty((uint)block.Offset, block.Data.Length);
        }

        PersistenceVersion++;
    }

    public FlashOverlayBlock[] CreateOverlay()
    {
        if (persistenceBaseline is null || persistenceBaseline.Length != data.Length)
        {
            return [];
        }

        List<FlashOverlayBlock> blocks = [];
        bool[]? dirtyBlocks = persistenceDirtyBlocks;

        if (dirtyBlocks is null)
        {
            for (int offset = 0; offset < data.Length; offset += BlockSize)
            {
                AddOverlayBlockIfChanged(blocks, offset);
            }

            return blocks.ToArray();
        }

        for (int blockIndex = 0; blockIndex < dirtyBlocks.Length; blockIndex++)
        {
            if (!dirtyBlocks[blockIndex])
            {
                continue;
            }

            int offset = blockIndex * BlockSize;
            if (!AddOverlayBlockIfChanged(blocks, offset))
            {
                dirtyBlocks[blockIndex] = false;
            }
        }

        return blocks.ToArray();
    }

    private bool AddOverlayBlockIfChanged(List<FlashOverlayBlock> blocks, int offset)
    {
        if (persistenceBaseline is null || offset >= data.Length)
        {
            return false;
        }

        int length = Math.Min(BlockSize, data.Length - offset);
        ReadOnlySpan<byte> current = data.AsSpan(offset, length);
        if (current.SequenceEqual(persistenceBaseline.AsSpan(offset, length)))
        {
            return false;
        }

        blocks.Add(new FlashOverlayBlock(offset, current.ToArray()));
        return true;
    }

    private void MarkPersistenceDirty(uint offset, int length)
    {
        if (persistenceDirtyBlocks is not { } dirtyBlocks || length <= 0 || offset >= data.Length)
        {
            return;
        }

        uint endExclusive = (uint)Math.Min(data.Length, (long)offset + length);
        int firstBlock = (int)(offset / BlockSize);
        int lastBlock = (int)((endExclusive - 1) / BlockSize);

        for (int block = firstBlock; block <= lastBlock; block++)
        {
            dirtyBlocks[block] = true;
        }
    }
}
