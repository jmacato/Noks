using System.Threading;

namespace Noks.Dct3.Input;

public sealed class Dct3KeyMatrix
{
    public const int ColumnCount = 5;
    public const int RowCount = 8;

    private long pressedBits;
    private long generations;
    private int powerKeyPressed;

    public int ChangeGeneration => Generations.Change;

    public int PressGeneration => Generations.Press;

    public (int Change, int Press) Generations
    {
        get
        {
            long snapshot = Volatile.Read(ref generations);
            return (unchecked((int)snapshot), unchecked((int)(snapshot >> 32)));
        }
    }

    public bool PowerKeyPressed => Volatile.Read(ref powerKeyPressed) != 0;

    public bool SetKey(int column, int row, bool pressed)
    {
        Validate(column, row);
        long mask = 1L << BitIndex(column, row);

        while (true)
        {
            long current = Volatile.Read(ref pressedBits);
            long next = pressed ? current | mask : current & ~mask;
            if (next == current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref pressedBits, next, current) == current)
            {
                RecordChange(pressed);
                return true;
            }
        }
    }

    public bool SetPowerKey(bool pressed)
    {
        int next = pressed ? 1 : 0;
        int previous = Interlocked.Exchange(ref powerKeyPressed, next);
        if (previous == next)
        {
            return false;
        }

        RecordChange(pressed);
        return true;
    }

    public bool IsKeyPressed(int column, int row)
    {
        Validate(column, row);
        long mask = 1L << BitIndex(column, row);
        return (Volatile.Read(ref pressedBits) & mask) != 0;
    }

    public byte ReadSelectedColumns(byte columnSelect)
    {
        byte data = 0xFF;
        long snapshot = Volatile.Read(ref pressedBits);

        for (int column = 0; column < ColumnCount; column++)
        {
            if ((columnSelect & (1 << column)) == 0)
            {
                data &= ReadColumn(snapshot, column);
            }
        }

        return data;
    }

    private static byte ReadColumn(long snapshot, int column)
    {
        long columnBits = (snapshot >> (column * RowCount)) & 0xFF;
        return (byte)(0xFF & ~columnBits);
    }

    private void RecordChange(bool pressed)
    {
        while (true)
        {
            long current = Volatile.Read(ref generations);
            uint change = unchecked((uint)current) + 1;
            uint press = unchecked((uint)(current >> 32)) + (pressed ? 1u : 0u);
            long next = unchecked((long)(((ulong)press << 32) | change));
            if (Interlocked.CompareExchange(ref generations, next, current) == current)
            {
                return;
            }
        }
    }

    private static int BitIndex(int column, int row) => column * RowCount + row;

    private static void Validate(int column, int row)
    {
        if ((uint)column >= ColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        if ((uint)row >= RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }
    }
}
