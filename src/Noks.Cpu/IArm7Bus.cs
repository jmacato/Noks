// SPDX-License-Identifier: MIT

namespace Noks.Cpu;

public interface IArm7Bus
{
    uint ReadWord(uint address, ArmAccess access);

    uint ReadHalf(uint address, ArmAccess access);

    uint ReadByte(uint address, ArmAccess access);

    void WriteWord(uint address, uint value, ArmAccess access);

    void WriteHalf(uint address, ushort value, ArmAccess access);

    void WriteByte(uint address, byte value, ArmAccess access);

    void Idle();
}
