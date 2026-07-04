// SPDX-License-Identifier: MIT

namespace Noks.Cpu;

[Flags]
public enum ArmAccess : uint
{
    Nonsequential = 0,
    Sequential = 1,
    Code = 2,
    Dma = 4,
    Lock = 8,
}
