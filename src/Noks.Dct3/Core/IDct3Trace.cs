namespace Noks.Dct3.Core;

public interface IDct3Trace
{
    bool MadStateEnabled { get; }

    void MadRead(uint offset, byte value);

    void MadWrite(uint offset, byte value);

    void MadState(string message);

    void CcontRead(int reg, byte value);

    void CcontWrite(int reg, byte value);

    void LcdCommand(byte value);

    void LcdData(byte value, int x, int y, bool vertical);

    void FlashCommand(string description);

    void InterfaceAccess(string block, bool write, uint offset, uint value);

    void DspRam(bool write, uint offset, uint value);

    void Unmapped(bool write, uint address, uint value, int size);

    void Event(string message);

    void FbusFrame(bool transmitted, ReadOnlySpan<byte> frame)
    {
    }

    void MbusByte(bool transmitted, byte value)
    {
    }
}
