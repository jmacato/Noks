using System.Buffers.Binary;

namespace Noks.Cli;

public static class SstTestFileReader
{
    private const uint Magic = 0xD33DBAE0;

    public static List<SstTest> Load(string path)
    {
        byte[] buffer = File.ReadAllBytes(path);

        uint magic = ReadU32(buffer, 0);
        if (magic != Magic)
        {
            throw new InvalidDataException($"{path}: The magic value 0x{magic:X8} is invalid.");
        }

        int count = (int)ReadU32(buffer, 4);
        List<SstTest> tests = new(count);

        int ptr = 8;
        for (int i = 0; i < count; i++)
        {
            int testSize = (int)ReadU32(buffer, ptr);
            int p = ptr + 4;

            (int initialSize, SstState initial) = ReadState(buffer, p);
            p += initialSize;

            (int finalSize, SstState final) = ReadState(buffer, p);
            p += finalSize;

            (int transactionsSize, SstTransaction[] transactions) = ReadTransactions(buffer, p);
            p += transactionsSize;

            uint opcode = ReadU32(buffer, p + 8);
            uint baseAddr = ReadU32(buffer, p + 12);

            tests.Add(new SstTest
            {
                Index = i,
                Initial = initial,
                Final = final,
                Transactions = transactions,
                Opcode = opcode,
                BaseAddr = baseAddr,
            });

            ptr += testSize;
        }

        return tests;
    }

    private static (int Size, SstState State) ReadState(byte[] buffer, int offset)
    {
        int size = (int)ReadU32(buffer, offset);
        int p = offset + 8;

        SstState state = new()
        {
            Cpsr = ReadU32(buffer, p + (31 * 4)),
            Access = ReadU32(buffer, p + (39 * 4)),
        };

        for (int i = 0; i < 16; i++)
        {
            state.R[i] = ReadU32(buffer, p + (i * 4));
        }

        for (int i = 0; i < 7; i++)
        {
            state.RFiq[i] = ReadU32(buffer, p + ((16 + i) * 4));
        }

        for (int i = 0; i < 2; i++)
        {
            state.RSvc[i] = ReadU32(buffer, p + ((23 + i) * 4));
            state.RAbt[i] = ReadU32(buffer, p + ((25 + i) * 4));
            state.RIrq[i] = ReadU32(buffer, p + ((27 + i) * 4));
            state.RUnd[i] = ReadU32(buffer, p + ((29 + i) * 4));
        }

        for (int i = 0; i < 5; i++)
        {
            state.Spsr[i] = ReadU32(buffer, p + ((32 + i) * 4));
        }

        state.Pipeline[0] = ReadU32(buffer, p + (37 * 4));
        state.Pipeline[1] = ReadU32(buffer, p + (38 * 4));

        return (size, state);
    }

    private static (int Size, SstTransaction[] Transactions) ReadTransactions(byte[] buffer, int offset)
    {
        int size = (int)ReadU32(buffer, offset);
        int count = (int)ReadU32(buffer, offset + 8);

        SstTransaction[] transactions = new SstTransaction[count];
        int p = offset + 12;

        for (int i = 0; i < count; i++)
        {
            transactions[i] = new SstTransaction(
                Kind: ReadU32(buffer, p),
                Size: ReadU32(buffer, p + 4),
                Addr: ReadU32(buffer, p + 8),
                Data: ReadU32(buffer, p + 12),
                Cycle: ReadU32(buffer, p + 16),
                Access: ReadU32(buffer, p + 20));
            p += 24;
        }

        return (size, transactions);
    }

    private static uint ReadU32(byte[] buffer, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
    }
}
