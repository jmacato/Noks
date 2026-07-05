namespace Noks.Dct3.Display;

public sealed class Pcd8544
{
    public const int Width = 84;
    public const int Height = 48;
    private const int Banks = Height / 8;

    private readonly byte[] vram = new byte[Width * Banks];
    private readonly bool[] refreshedAddresses = new bool[Width * Banks];
    private int x;
    private int y;
    private int refreshedAddressCount;
    private bool extended;
    private bool vertical;

    public bool PowerDown { get; private set; } = true;

    public int DisplayMode { get; private set; }

    public long DataWrites { get; private set; }

    public long CommandWrites { get; private set; }

    public long CompletedRefreshes { get; private set; }

    public byte Vop { get; private set; }

    public ReadOnlySpan<byte> Vram => vram;

    public int X => x;

    public int Y => y;

    public bool Vertical => vertical;

    public event Action? FrameCompleted;

    public event Action? DisplayStateChanged;

    public void Reset()
    {
        Array.Clear(vram);
        Array.Clear(refreshedAddresses);
        x = 0;
        y = 0;
        refreshedAddressCount = 0;
        extended = false;
        vertical = false;
        PowerDown = true;
        DisplayMode = 0;
        Vop = 0;
    }

    public void WriteCommand(byte value)
    {
        CommandWrites++;

        if ((value & 0xF8) == 0x20)
        {
            bool nextPowerDown = (value & 0x04) != 0;
            bool displayStateChanged = PowerDown != nextPowerDown;
            PowerDown = nextPowerDown;
            vertical = (value & 0x02) != 0;
            extended = (value & 0x01) != 0;
            if (displayStateChanged)
            {
                DisplayStateChanged?.Invoke();
            }

            return;
        }

        if (!extended)
        {
            if ((value & 0xFA) == 0x08)
            {
                int nextDisplayMode = ((value >> 1) & 0x02) | (value & 0x01);
                bool displayStateChanged = DisplayMode != nextDisplayMode;
                DisplayMode = nextDisplayMode;
                if (displayStateChanged)
                {
                    DisplayStateChanged?.Invoke();
                }
            }
            else if ((value & 0xF8) == 0x40)
            {
                y = Math.Min(value & 0x07, Banks - 1);
            }
            else if ((value & 0x80) != 0)
            {
                x = Math.Min(value & 0x7F, Width - 1);
            }
        }
        else if ((value & 0x80) != 0)
        {
            Vop = (byte)(value & 0x7F);
        }
    }

    public void WriteData(byte value)
    {
        DataWrites++;
        int address = y * Width + x;
        if (address == 0 && refreshedAddressCount != 0)
        {
            Array.Clear(refreshedAddresses);
            refreshedAddressCount = 0;
        }

        vram[address] = value;
        if (!refreshedAddresses[address])
        {
            refreshedAddresses[address] = true;
            refreshedAddressCount++;
        }

        if (vertical)
        {
            y++;
            if (y >= Banks)
            {
                y = 0;
                x = x + 1 >= Width ? 0 : x + 1;
            }
        }
        else
        {
            x++;
            if (x >= Width)
            {
                x = 0;
                y = y + 1 >= Banks ? 0 : y + 1;
            }
        }

        if (refreshedAddressCount == vram.Length)
        {
            Array.Clear(refreshedAddresses);
            refreshedAddressCount = 0;
            CompletedRefreshes++;
            FrameCompleted?.Invoke();
        }
    }

    public bool GetPixel(int px, int py)
    {
        bool bit = ((vram[py / 8 * Width + px] >> (py % 8)) & 1) != 0;
        return DisplayMode == 3 ? !bit : bit;
    }
}
