namespace Noks.Dct3.Memory;

internal sealed class I2cEeprom24C128
{
    public const int Size = 0x4000;
    private const int TraceLimit = 8192;
    private const int WriteTraceLimit = 2048;

    private enum BusState
    {
        Idle,
        ReceiveControl,
        ReceiveAddressHigh,
        ReceiveAddressLow,
        ReceiveData,
        PrepareAck,
        SendAck,
        SendData,
        ReceiveReadAck,
        IgnoreUntilStop,
    }

    private readonly byte[] data = new byte[Size];
    private BusState state;
    private BusState stateAfterAck;
    private bool previousSclHigh = true;
    private bool previousMasterSdaHigh = true;
    private bool driveSdaLow;
    private byte shift;
    private int bitCount;
    private int addressHigh;
    private int currentAddress;
    private byte outputByte;
    private readonly Action<string>? trace;
    private int traceLines;
    private int writeTraceLines;
    private bool traceSuppressed;
    private bool writeTraceSuppressed;

    public I2cEeprom24C128(ReadOnlySpan<byte> image, Action<string>? trace = null)
    {
        this.trace = trace;
        data.AsSpan().Fill(0xFF);
        image[..Math.Min(image.Length, data.Length)].CopyTo(data);
    }

    public byte[] Data => data;

    public bool DrivesSdaLow => driveSdaLow;

    public long PersistenceVersion { get; private set; }

    public void ResetBus()
    {
        state = BusState.Idle;
        stateAfterAck = BusState.Idle;
        previousSclHigh = true;
        previousMasterSdaHigh = true;
        driveSdaLow = false;
        shift = 0;
        bitCount = 0;
        addressHigh = 0;
    }

    public void Observe(bool sclHigh, bool masterSdaHigh)
    {
        if (previousMasterSdaHigh && !masterSdaHigh && sclHigh)
        {
            Start();
        }
        else if (!previousMasterSdaHigh && masterSdaHigh && sclHigh)
        {
            Stop();
        }
        else if (!previousSclHigh && sclHigh)
        {
            OnSclRising(masterSdaHigh);
        }
        else if (previousSclHigh && !sclHigh)
        {
            OnSclFalling();
        }

        previousSclHigh = sclHigh;
        previousMasterSdaHigh = masterSdaHigh;
    }

    private void Start()
    {
        state = BusState.ReceiveControl;
        stateAfterAck = BusState.Idle;
        driveSdaLow = false;
        shift = 0;
        bitCount = 0;
    }

    private void Stop()
    {
        state = BusState.Idle;
        stateAfterAck = BusState.Idle;
        driveSdaLow = false;
        shift = 0;
        bitCount = 0;
    }

    private void OnSclRising(bool masterSdaHigh)
    {
        switch (state)
        {
            case BusState.ReceiveControl:
            case BusState.ReceiveAddressHigh:
            case BusState.ReceiveAddressLow:
            case BusState.ReceiveData:
                ReceiveBit(masterSdaHigh);
                break;

            case BusState.SendData:
                bitCount++;
                break;

            case BusState.ReceiveReadAck:
                if (masterSdaHigh)
                {
                    state = BusState.IgnoreUntilStop;
                    driveSdaLow = false;
                    break;
                }

                currentAddress = (currentAddress + 1) & (Size - 1);
                BeginReadByte();
                state = BusState.SendData;
                break;
        }
    }

    private void OnSclFalling()
    {
        switch (state)
        {
            case BusState.PrepareAck:
                driveSdaLow = true;
                state = BusState.SendAck;
                break;

            case BusState.SendAck:
                driveSdaLow = false;
                state = stateAfterAck;
                if (state == BusState.SendData)
                {
                    BeginReadByte();
                }

                break;

            case BusState.SendData:
                if (bitCount < 8)
                {
                    DriveCurrentReadBit();
                }
                else
                {
                    driveSdaLow = false;
                    state = BusState.ReceiveReadAck;
                }

                break;
        }
    }

    private void ReceiveBit(bool masterSdaHigh)
    {
        shift = (byte)((shift << 1) | (masterSdaHigh ? 1 : 0));
        bitCount++;

        if (bitCount < 8)
        {
            return;
        }

        byte received = shift;
        shift = 0;
        bitCount = 0;

        switch (state)
        {
            case BusState.ReceiveControl:
                ReceiveControl(received);
                break;
            case BusState.ReceiveAddressHigh:
                addressHigh = received;
                AckThen(BusState.ReceiveAddressLow);
                break;
            case BusState.ReceiveAddressLow:
                currentAddress = ((addressHigh << 8) | received) & (Size - 1);
                Log($"addr {currentAddress:X4}");
                AckThen(BusState.ReceiveData);
                break;
            case BusState.ReceiveData:
                LogWrite(currentAddress, received);
                if (data[currentAddress] != received)
                {
                    data[currentAddress] = received;
                    PersistenceVersion++;
                }

                currentAddress = (currentAddress + 1) & (Size - 1);
                AckThen(BusState.ReceiveData);
                break;
        }
    }

    private void ReceiveControl(byte control)
    {
        bool selected = (control & 0xF0) == 0xA0;
        if (!selected)
        {
            state = BusState.IgnoreUntilStop;
            driveSdaLow = false;
            return;
        }

        bool read = (control & 0x01) != 0;
        Log($"control {control:X2} {(read ? "read" : "write")}");
        AckThen(read ? BusState.SendData : BusState.ReceiveAddressHigh);
    }

    private void AckThen(BusState next)
    {
        driveSdaLow = false;
        state = BusState.PrepareAck;
        stateAfterAck = next;
    }

    private void DriveCurrentReadBit()
    {
        int mask = 1 << (7 - bitCount);
        driveSdaLow = (outputByte & mask) == 0;
    }

    private void BeginReadByte()
    {
        outputByte = data[currentAddress];
        bitCount = 0;
        Log($"r {currentAddress:X4}={outputByte:X2}");
        DriveCurrentReadBit();
    }

    private void LogWrite(int address, byte value)
    {
        byte old = data[address];
        LogWriteRecord(old == value
            ? $"w {address:X4}={value:X2}"
            : $"w {address:X4}:{old:X2}->{value:X2}");
    }

    private void Log(string message)
    {
        if (trace is null)
        {
            return;
        }

        if (traceLines < TraceLimit)
        {
            trace($"EEPROM {message}");
        }
        else if (!traceSuppressed)
        {
            trace("EEPROM trace limit reached");
            traceSuppressed = true;
        }

        traceLines++;
    }

    private void LogWriteRecord(string message)
    {
        if (trace is null)
        {
            return;
        }

        if (writeTraceLines < WriteTraceLimit)
        {
            trace($"EEPROM {message}");
        }
        else if (!writeTraceSuppressed)
        {
            trace("EEPROM write trace limit reached");
            writeTraceSuppressed = true;
        }

        writeTraceLines++;
    }
}
