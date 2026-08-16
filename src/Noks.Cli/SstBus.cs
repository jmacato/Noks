using Noks.Cpu;

namespace Noks.Cli;

public sealed class SstBus : IArm7Bus
{
    private readonly SstTransaction[] expected;
    private int cursor;

    public SstBus(SstTransaction[] expected)
    {
        this.expected = expected;
    }

    public bool CheckAccess { get; init; } = true;

    public List<string> Errors { get; } = new();

    public int Remaining => expected.Length - cursor;

    public uint ReadWord(uint address, ArmAccess access) => Handle(address, 4, access, false, 0);

    public uint ReadHalf(uint address, ArmAccess access) => Handle(address, 2, access, false, 0);

    public uint ReadByte(uint address, ArmAccess access) => Handle(address, 1, access, false, 0);

    public void WriteWord(uint address, uint value, ArmAccess access) => Handle(address, 4, access, true, value);

    public void WriteHalf(uint address, ushort value, ArmAccess access) => Handle(address, 2, access, true, value);

    public void WriteByte(uint address, byte value, ArmAccess access) => Handle(address, 1, access, true, value);

    public void Idle()
    {
    }

    private uint Handle(uint address, uint size, ArmAccess access, bool isWrite, uint value)
    {
        uint kind = isWrite ? 2u : (access & ArmAccess.Code) != 0 ? 0u : 1u;

        if (cursor >= expected.Length)
        {
            Errors.Add($"unexpected extra access: kind={kind} size={size} addr={address:X8}" + (isWrite ? $" data={value:X8}" : string.Empty));
            return address;
        }

        SstTransaction t = expected[cursor];
        cursor++;

        bool mismatch = t.Kind != kind || t.Size != size || t.Addr != address;

        if (isWrite && t.Data != value)
        {
            mismatch = true;
        }

        if (CheckAccess && t.Access != (uint)access)
        {
            mismatch = true;
        }

        if (mismatch)
        {
            string got = $"kind={kind} size={size} addr={address:X8} access={(uint)access}" + (isWrite ? $" data={value:X8}" : string.Empty);
            Errors.Add($"transaction[{cursor - 1}] expected {{{t}}} got {{{got}}}");
        }

        return t.Data;
    }
}
