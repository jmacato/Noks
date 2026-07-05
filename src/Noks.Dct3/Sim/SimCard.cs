using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Noks.Dct3.Core;
using Noks.Dct3.Messaging;
using Noks.Dct3.State;

namespace Noks.Dct3.Sim;

public sealed class SimCard
{
    private static readonly SimFileKey RootKey = new(0, 0x3F00);
    private static readonly SimFileKey AdnKey = new(0x7F10, 0x6F3A);
    private static readonly SimFileKey MsisdnKey = new(0x7F10, 0x6F40);
    public const string DefaultImsi = "208010000000001";
    public const string DefaultTestNetworkImsi = "001010000000001";
    public const int AdnRecordCount = 250;
    public const int OrdinaryAdnRecordCount = AdnRecordCount - 1;
    public const int ManagedOwnNumberRecord = AdnRecordCount;
    private const string DefaultSmsParametersRecord =
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" +
        "FD" +
        "FFFFFFFFFFFFFFFFFFFFFFFF" +
        "06912143658709FFFFFFFFFF" +
        "FFFFFF";
    public const int SmsStorageRecordLength = 176;
    // READ/UPDATE RECORD uses a one-byte, one-based record number. Record 0 is invalid.
    public const int SmsStorageRecordCount = byte.MaxValue;
    private static readonly HashSet<ushort> VolatileGsmFileIds =
    [
        0x6F20, // EF_Kc
        0x6F30, // EF_PLMNsel
        0x6F31, // EF_HPLMN
        0x6F37, // EF_ACMmax
        0x6F39, // EF_ACM
        0x6F74, // EF_BCCH
        0x6F78, // EF_ACC
        0x6F7B, // EF_FPLMN
        0x6F7E, // EF_LOCI
        0x6FAD, // EF_AD
    ];
    private static readonly string BlankSmsParametersRecord = new('F', 88);
    private static readonly byte[] DefaultServiceTable = BuildSimServiceTable(
        14,
        1, 2, 4, 6, 7,
        9, 10, 11, 12,
        13, 14, 15, 16,
        17, 18, 19,
        26,
        30,
        35, 38, 56);

    private static readonly byte[] DefaultAtr =
    [
        0x3B, 0xFF, 0x96, 0x00, 0x00, 0xF0, 0x00, 0x00,
        0x00, 0xF0, 0x00, 0x00, 0x00, 0xE1, 0x00, 0x00,
        0x00, 0x80, 0x31, 0xE0, 0x67, 0x73, 0x77, 0x69,
        0x63, 0x63, 0x00, 0x00, 0x73, 0xFE, 0x21, 0x00,
        0x7F,
    ];
    private static readonly byte[] LegacyDirectAtr =
    [
        0x3B, 0xFF, 0x96, 0x00, 0x00, 0xF0, 0x00, 0x00,
        0x00, 0xF0, 0x00, 0x00, 0x00, 0xE1, 0x00, 0x00,
        0x00, 0x80, 0x31, 0xE0, 0x67, 0x73, 0x77, 0x69,
        0x63, 0x63, 0x00, 0x00, 0x73, 0xFE, 0x21, 0x00,
        0x7F,
    ];

    private readonly List<byte> tx = new(32);
    private readonly Dictionary<SimFileKey, SimFile> files = [];
    private readonly Dictionary<SimFileKey, byte[]> persistenceBaseline = [];
    private readonly IDct3Trace? trace;
    private readonly byte[] imsiEf;
    private readonly byte[] operatorNameEf;
    private readonly byte[] serviceProviderNameEf;
    private readonly byte[] answerToReset;
    private byte[] pendingResponse = [];
    private SimCardResponse? pendingOutput;
    private SimTransportState transportState;
    private int expectedTxLength;
    private byte pendingIns;
    private SimFileKey currentDirectory;
    private SimFileKey selectedFile;
    private byte[] managedOwnNumberRecord = [];

    public string Imsi { get; }

    public long PersistenceVersion { get; private set; }

    public event Action<SimMutation>? MutationCommitted;

    public SimCard(
        IDct3Trace? trace,
        string? imsi = null,
        byte[]? answerToReset = null,
        string? serviceProviderName = null,
        string? ownPhoneNumber = null)
    {
        this.trace = trace;
        this.answerToReset = (answerToReset ?? DefaultAtr).ToArray();
        Imsi = imsi ?? DefaultImsi;
        imsiEf = EncodeImsi(Imsi);
        operatorNameEf = EncodeAlphaIdentifier(serviceProviderName, 20);
        serviceProviderNameEf = EncodeServiceProviderName(serviceProviderName);
        managedOwnNumberRecord = SimPhonebookCodec.Encode(
            "My Number",
            ownPhoneNumber ?? Dct3PhoneSettings.DefaultOwnPhoneNumber);
        BuildDefaultFileSystem();
        RepairManagedOwnNumber(SimMutationOrigin.ManagedRepair, emitEvenWhenUnchanged: false);
        CapturePersistenceBaseline();
        Reset();
    }

    public static byte[] CreateLegacyDirectAnswerToReset() => LegacyDirectAtr.ToArray();

    public ReadOnlySpan<byte> AnswerToReset()
    {
        Reset();
        trace?.Event($"SIM ATR queued len={answerToReset.Length}");
        return answerToReset;
    }

    public void Reset()
    {
        tx.Clear();
        pendingResponse = [];
        pendingOutput = null;
        transportState = SimTransportState.AwaitingInitialByte;
        expectedTxLength = 0;
        pendingIns = 0;
        currentDirectory = RootKey;
        selectedFile = RootKey;
    }

    public SimCardResponse? Transmit(byte value)
    {
        pendingOutput = null;

        if (transportState == SimTransportState.AwaitingInitialByte)
        {
            tx.Clear();
            tx.Add(value);

            if (value == 0xFF)
            {
                transportState = SimTransportState.Pps;
                expectedTxLength = 0;
                return null;
            }

            transportState = SimTransportState.TpduHeader;
            expectedTxLength = 5;
            TryCompleteTpduHeader();
            return pendingOutput;
        }

        if (TryResyncFromAbandonedSelect(value))
        {
            return null;
        }

        tx.Add(value);

        switch (transportState)
        {
            case SimTransportState.Pps:
                TryCompletePps();
                break;
            case SimTransportState.TpduHeader:
                TryCompleteTpduHeader();
                break;
            case SimTransportState.CommandData:
                TryCompleteCommandData();
                break;
        }

        return pendingOutput;
    }

    private bool TryResyncFromAbandonedSelect(byte value)
    {
        if (transportState != SimTransportState.CommandData ||
            value != 0xA0 ||
            pendingIns != 0xA4 ||
            expectedTxLength != 7 ||
            tx.Count != 5)
        {
            return false;
        }

        trace?.Event("SIM TPDU resync: abandoned SELECT data phase");
        tx.Clear();
        tx.Add(value);
        transportState = SimTransportState.TpduHeader;
        expectedTxLength = 5;
        pendingIns = 0;
        return true;
    }

    private void TryCompletePps()
    {
        if (tx.Count == 2)
        {
            expectedTxLength = PpsLength(tx[1]);
        }

        if (expectedTxLength == 0 || tx.Count < expectedTxLength)
        {
            return;
        }

        byte[] pps = tx.ToArray();
        tx.Clear();
        transportState = SimTransportState.AwaitingInitialByte;
        expectedTxLength = 0;

        if (IsSupportedPps(pps))
        {
            SetResponse(pps, true);
            trace?.Event($"SIM PPS {Convert.ToHexString(pps)}");
            return;
        }

        trace?.Event($"SIM PPS rejected {Convert.ToHexString(pps)}");
    }

    private void TryCompleteTpduHeader()
    {
        if (tx.Count < expectedTxLength)
        {
            return;
        }

        pendingIns = tx[1];
        SimApduResult result = ProcessCommand(CollectionsMarshal.AsSpan(tx), 0, false);

        if (result.ProcedureLength >= 0)
        {
            SetResponse([pendingIns], false);
            transportState = SimTransportState.CommandData;
            expectedTxLength = 5 + result.ProcedureLength;
            return;
        }

        tx.Clear();
        transportState = SimTransportState.AwaitingInitialByte;
        expectedTxLength = 0;
        CompleteCommand(pendingIns, result);
    }

    private void TryCompleteCommandData()
    {
        if (tx.Count < expectedTxLength)
        {
            return;
        }

        byte ins = pendingIns;
        SimApduResult result = ProcessCommand(CollectionsMarshal.AsSpan(tx), 1, true);
        tx.Clear();
        transportState = SimTransportState.AwaitingInitialByte;
        expectedTxLength = 0;
        pendingIns = 0;
        CompleteCommand(ins, result);
    }

    private SimApduResult ProcessCommand(ReadOnlySpan<byte> apdu, int procedureCount, bool traceCommand)
    {
        byte cla = apdu[0];
        byte ins = apdu[1];
        byte p1 = apdu[2];
        byte p2 = apdu[3];
        byte p3 = apdu[4];
        ReadOnlySpan<byte> data = apdu[5..];

        if (traceCommand || procedureCount == 0 && !CommandNeedsData(cla, ins, p3))
        {
            trace?.Event($"SIM APDU {cla:X2} {ins:X2} {p1:X2} {p2:X2} {p3:X2} data={data.Length} {Convert.ToHexString(data)}");
        }

        if (procedureCount == 0 && ins != 0xC0)
        {
            pendingResponse = [];
        }

        if (cla != 0xA0)
        {
            return SimApduResult.Status(0x6E, 0x00);
        }

        return ins switch
        {
            0xA4 => SelectFile(p1, p2, p3, data, procedureCount),
            0xC0 => GetResponse(p1, p2, p3, data),
            0xF2 => Status(p1, p2, p3),
            0xB0 => ReadBinary(p1, p2, p3, data),
            0xB2 => ReadRecord(p1, p2, p3, data),
            0xD6 => UpdateBinary(p1, p2, p3, data, procedureCount),
            0xDC => UpdateRecord(p1, p2, p3, data, procedureCount),
            0x20 or 0x24 or 0x26 or 0x28 or 0x2C => VerifyLikeCommand(p3, data, procedureCount),
            0x88 => RunGsmAlgorithm(p1, p2, p3, data, procedureCount),
            0xFA => SimApduResult.Status(0x90, 0x00),
            0x10 or 0x14 or 0xC2 => DataInCommand(p3, data, procedureCount),
            0x12 => SimApduResult.Status(0x90, 0x00),
            _ => SimApduResult.Status(0x6D, 0x00),
        };
    }

    private static bool CommandNeedsData(byte cla, byte ins, byte p3)
    {
        return cla == 0xA0 && ins switch
        {
            0xA4 or 0x20 or 0x24 or 0x26 or 0x28 or 0x2C or 0x88 or 0xD6 or 0xDC => true,
            0x10 or 0x14 or 0xC2 => p3 > 0,
            _ => false,
        };
    }

    private void CompleteCommand(byte ins, SimApduResult result)
    {
        if (result.Data.Length == 0)
        {
            QueueStatus(result.Sw1, result.Sw2);
            return;
        }

        QueueDataResponse(ins, result.Data, result.Sw1, result.Sw2);
    }

    private SimApduResult SelectFile(byte p1, byte p2, byte p3, ReadOnlySpan<byte> data, int procedureCount)
    {
        if (p1 != 0 || p2 != 0 || p3 != 2)
        {
            return SimApduResult.Status(0x6B, 0x00);
        }

        if (procedureCount == 0)
        {
            return data.Length == 0
                ? SimApduResult.Procedure(2)
                : SimApduResult.Status(0x6F, 0x00);
        }

        if (data.Length != 2)
        {
            return SimApduResult.Status(0x67, 0x02);
        }

        ushort fid = BinaryPrimitives.ReadUInt16BigEndian(data);

        if (!TryResolveFile(fid, out SimFileKey key, out SimFile file))
        {
            return SimApduResult.Status(0x94, 0x04);
        }

        selectedFile = key;
        currentDirectory = file.Kind == SimFileKind.Directory ? key : DirectoryKey(file.Parent);

        pendingResponse = BuildSelectResponse(file);
        return SimApduResult.Status(0x9F, (byte)pendingResponse.Length);
    }

    private bool TryResolveFile(ushort fid, out SimFileKey key, out SimFile file)
    {
        if (fid == RootKey.Id)
        {
            key = RootKey;
            return TryGetFile(key, out file);
        }

        key = new SimFileKey(currentDirectory.Id, fid);

        if (TryGetFile(key, out file))
        {
            return true;
        }

        key = new SimFileKey(RootKey.Id, fid);

        if (TryGetFile(key, out file) && file.Kind == SimFileKind.Directory)
        {
            return true;
        }

        key = default;
        file = null!;
        return false;
    }

    private bool TryGetFile(SimFileKey key, out SimFile file)
    {
        if (files.TryGetValue(key, out SimFile? found) && found is not null)
        {
            file = found;
            return true;
        }

        file = null!;
        return false;
    }

    private static SimFileKey DirectoryKey(ushort id)
    {
        return id == RootKey.Id ? RootKey : new SimFileKey(RootKey.Id, id);
    }

    private SimApduResult GetResponse(byte p1, byte p2, byte length, ReadOnlySpan<byte> data)
    {
        if (p1 != 0 || p2 != 0 || data.Length != 0)
        {
            return SimApduResult.Status(0x6B, 0x00);
        }

        int count = length == 0 ? 256 : length;

        if (count > pendingResponse.Length)
        {
            return SimApduResult.Status(0x6F, 0x00);
        }

        return SimApduResult.Response(pendingResponse.AsSpan(0, count).ToArray(), 0x90, 0x00);
    }

    private SimApduResult Status(byte p1, byte p2, byte length)
    {
        if (p1 != 0 || p2 != 0)
        {
            return SimApduResult.Status(0x6B, 0x00);
        }

        SimFile file = files[currentDirectory];
        byte[] response = BuildSelectResponse(file);
        int count = length == 0 ? 256 : length;
        byte[] data = new byte[count];
        response.AsSpan(0, Math.Min(response.Length, count)).CopyTo(data);
        return SimApduResult.Response(data, 0x90, 0x00);
    }

    private SimApduResult ReadBinary(byte p1, byte p2, byte length, ReadOnlySpan<byte> data)
    {
        if (data.Length != 0)
        {
            return SimApduResult.Status(0x6F, 0x00);
        }

        SimFile file = files[selectedFile];

        if (file.Kind != SimFileKind.Transparent)
        {
            return SimApduResult.Status(0x94, 0x08);
        }

        int count = length == 0 ? 256 : length;
        int offset = (p1 << 8) | p2;

        if (count > file.Data.Length)
        {
            return SimApduResult.Status(0x67, 0x00);
        }

        if (offset + count > file.Data.Length)
        {
            return SimApduResult.Status(0x6B, 0x00);
        }

        return SimApduResult.Response(file.Data.AsSpan(offset, count).ToArray(), 0x90, 0x00);
    }

    private SimApduResult ReadRecord(byte recordNumber, byte mode, byte length, ReadOnlySpan<byte> data)
    {
        if (data.Length != 0)
        {
            return SimApduResult.Status(0x6F, 0x00);
        }

        SimFile file = files[selectedFile];

        if (file.Kind is not SimFileKind.LinearFixed and not SimFileKind.Cyclic || file.RecordLength == 0 || recordNumber == 0)
        {
            return SimApduResult.Status(0x94, 0x08);
        }

        if ((mode & 0x07) != 0x04)
        {
            return SimApduResult.Status(0x6B, 0x00);
        }

        int count = length == 0 ? file.RecordLength : length;

        if (count > file.RecordLength)
        {
            return SimApduResult.Status(0x67, 0x00);
        }

        int offset = (recordNumber - 1) * file.RecordLength;

        if (offset >= file.Data.Length)
        {
            return SimApduResult.Status(0x94, 0x02);
        }

        return SimApduResult.Response(file.Data.AsSpan(offset, count).ToArray(), 0x90, 0x00);
    }

    private SimApduResult UpdateBinary(byte p1, byte p2, byte length, ReadOnlySpan<byte> data, int procedureCount)
    {
        if ((p1 & 0x80) != 0)
        {
            return SimApduResult.Status(0x6B, 0x00);
        }

        int count = length == 0 ? 256 : length;

        if (procedureCount == 0)
        {
            return data.Length == 0
                ? SimApduResult.Procedure(count)
                : SimApduResult.Status(0x6F, 0x00);
        }

        if (data.Length != count)
        {
            return SimApduResult.Status(0x67, 0x00);
        }

        SimFile file = files[selectedFile];

        if (file.Kind != SimFileKind.Transparent)
        {
            return SimApduResult.Status(0x94, 0x08);
        }

        int offset = (p1 << 8) | p2;

        if (offset + count > file.Data.Length)
        {
            return SimApduResult.Status(0x6B, 0x00);
        }

        byte[] oldValue = file.Data.ToArray();
        data.CopyTo(file.Data.AsSpan(offset, count));
        CommitMutation(selectedFile, 0, oldValue, file.Data, SimMutationOrigin.Firmware);
        return SimApduResult.Status(0x90, 0x00);
    }

    private SimApduResult UpdateRecord(byte recordNumber, byte mode, byte length, ReadOnlySpan<byte> data, int procedureCount)
    {
        int count = length == 0 ? 256 : length;

        if (procedureCount == 0)
        {
            return data.Length == 0
                ? SimApduResult.Procedure(count)
                : SimApduResult.Status(0x6F, 0x00);
        }

        if (data.Length != count)
        {
            return SimApduResult.Status(0x67, 0x00);
        }

        SimFile file = files[selectedFile];

        if (file.Kind is not SimFileKind.LinearFixed and not SimFileKind.Cyclic || file.RecordLength == 0 || recordNumber == 0)
        {
            return SimApduResult.Status(0x94, 0x08);
        }

        if ((mode & 0x07) != 0x04)
        {
            return SimApduResult.Status(0x6B, 0x00);
        }

        if (count != file.RecordLength)
        {
            return SimApduResult.Status(0x67, 0x00);
        }

        int offset = (recordNumber - 1) * file.RecordLength;

        if (offset >= file.Data.Length)
        {
            return SimApduResult.Status(0x94, 0x02);
        }

        byte[] oldValue = file.Data.AsSpan(offset, count).ToArray();
        data.CopyTo(file.Data.AsSpan(offset, count));
        CommitMutation(
            selectedFile,
            recordNumber,
            oldValue,
            file.Data.AsSpan(offset, count),
            SimMutationOrigin.Firmware);
        if (IsManagedOwnNumberRecord(selectedFile, recordNumber))
        {
            RepairManagedOwnNumber(SimMutationOrigin.ManagedRepair, emitEvenWhenUnchanged: false);
        }

        return SimApduResult.Status(0x90, 0x00);
    }

    public void SetManagedOwnNumber(string phoneNumber)
    {
        managedOwnNumberRecord = SimPhonebookCodec.Encode("My Number", phoneNumber);
        RepairManagedOwnNumber(SimMutationOrigin.Host, emitEvenWhenUnchanged: true);
    }

    public void ApplyOverlay(IEnumerable<SimFileOverlay> overlays)
    {
        foreach (SimFileOverlay overlay in overlays)
        {
            SimFileKey key = new(overlay.Parent, overlay.Id);
            if (!files.TryGetValue(key, out SimFile? file) ||
                file.Kind == SimFileKind.Directory ||
                overlay.Data.Length != file.Data.Length ||
                !ShouldPersistFile(key))
            {
                continue;
            }

            byte[] oldValue = file.Data.ToArray();
            overlay.Data.CopyTo(file.Data);
            CommitMutation(key, 0, oldValue, file.Data, SimMutationOrigin.PersistenceRestore);
        }

        RepairManagedOwnNumber(SimMutationOrigin.ManagedRepair, emitEvenWhenUnchanged: false);
    }

    public SimFileOverlay[] CreateOverlay()
    {
        List<SimFileOverlay> overlays = [];

        foreach ((SimFileKey key, SimFile file) in files)
        {
            if (file.Kind == SimFileKind.Directory ||
                !persistenceBaseline.TryGetValue(key, out byte[]? baseline) ||
                !ShouldPersistFile(key) ||
                file.Data.AsSpan().SequenceEqual(baseline))
            {
                continue;
            }

            overlays.Add(new SimFileOverlay(key.Parent, key.Id, file.Data.ToArray()));
        }

        return overlays.ToArray();
    }

    private void CapturePersistenceBaseline()
    {
        persistenceBaseline.Clear();

        foreach ((SimFileKey key, SimFile file) in files)
        {
            if (file.Kind != SimFileKind.Directory)
            {
                persistenceBaseline[key] = file.Data.ToArray();
            }
        }
    }

    private static bool ShouldPersistFile(SimFileKey key) =>
        !IsGsmApplicationDirectory(key.Parent) || !VolatileGsmFileIds.Contains(key.Id);

    private static bool IsGsmApplicationDirectory(ushort parent) =>
        parent is 0x7F20 or 0x7F21 or 0x7F40;

    private static SimApduResult VerifyLikeCommand(byte p3, ReadOnlySpan<byte> data, int procedureCount)
    {
        if (p3 != 8)
        {
            return SimApduResult.Status(0x67, 0x00);
        }

        if (procedureCount == 0)
        {
            return data.Length == 0
                ? SimApduResult.Procedure(8)
                : SimApduResult.Status(0x6F, 0x00);
        }

        return data.Length == 8
            ? SimApduResult.Status(0x90, 0x00)
            : SimApduResult.Status(0x67, 0x00);
    }

    private SimApduResult RunGsmAlgorithm(byte p1, byte p2, byte p3, ReadOnlySpan<byte> data, int procedureCount)
    {
        if (p1 != 0 || p2 != 0 || p3 != 16)
        {
            return SimApduResult.Status(0x67, 0x00);
        }

        if (procedureCount == 0)
        {
            return data.Length == 0
                ? SimApduResult.Procedure(16)
                : SimApduResult.Status(0x6F, 0x00);
        }

        if (data.Length != 16)
        {
            return SimApduResult.Status(0x67, 0x00);
        }

        Span<byte> result = stackalloc byte[12];

        for (int i = 0; i < result.Length; i++)
        {
            result[i] = i < data.Length ? (byte)(data[i] ^ 0xA5) : (byte)0x00;
        }

        pendingResponse = result.ToArray();
        return SimApduResult.Status(0x9F, (byte)pendingResponse.Length);
    }

    private static SimApduResult DataInCommand(byte p3, ReadOnlySpan<byte> data, int procedureCount)
    {
        if (procedureCount == 0)
        {
            return p3 == 0
                ? SimApduResult.Status(0x90, 0x00)
                : data.Length == 0
                    ? SimApduResult.Procedure(p3)
                    : SimApduResult.Status(0x6F, 0x00);
        }

        return data.Length == p3
            ? SimApduResult.Status(0x90, 0x00)
            : SimApduResult.Status(0x67, 0x00);
    }

    private void QueueDataResponse(byte procedure, ReadOnlySpan<byte> data, byte sw1, byte sw2)
    {
        byte[] response = new byte[1 + data.Length + 2];
        response[0] = procedure;
        data.CopyTo(response.AsSpan(1));
        response[^2] = sw1;
        response[^1] = sw2;
        SetResponse(response, true);
    }

    private void QueueStatus(byte sw1, byte sw2)
    {
        SetResponse([sw1, sw2], true);
        trace?.Event($"SIM SW {sw1:X2}{sw2:X2}");
    }

    private void SetResponse(ReadOnlySpan<byte> data, bool complete)
    {
        pendingOutput = new SimCardResponse(data.ToArray(), complete);
    }

    private byte[] BuildSelectResponse(SimFile file)
    {
        return file.Kind == SimFileKind.Directory
            ? BuildDirectorySelectResponse(file)
            : BuildElementaryFileSelectResponse(file);
    }

    private byte[] BuildDirectorySelectResponse(SimFile file)
    {
        byte[] response = new byte[23];
        response[2] = 0xFF;
        response[3] = 0xFF;
        response[4] = (byte)(file.Id >> 8);
        response[5] = (byte)file.Id;
        response[6] = file.Id == 0x3F00 ? (byte)0x01 : (byte)0x02;
        response[12] = 0x0A;
        response[13] = 0xB2;
        response[14] = CountChildren(file.Id, SimFileKind.Directory);
        response[15] = CountChildren(file.Id, SimFileKind.Transparent, SimFileKind.LinearFixed, SimFileKind.Cyclic);
        response[16] = 0x04;
        response[18] = 0x83;
        response[19] = 0x8A;
        response[20] = 0x83;
        response[21] = 0x8A;
        return response;
    }

    private byte[] BuildElementaryFileSelectResponse(SimFile file)
    {
        byte[] response = new byte[15];
        int size = file.Data.Length;
        response[2] = (byte)(size >> 8);
        response[3] = (byte)size;
        response[4] = (byte)(file.Id >> 8);
        response[5] = (byte)file.Id;
        response[6] = 0x04;
        response[11] = 0x01;
        response[12] = 0x02;
        response[13] = file.Kind switch
        {
            SimFileKind.LinearFixed => 0x01,
            SimFileKind.Cyclic => 0x03,
            _ => 0x00,
        };
        response[14] = file.Kind == SimFileKind.Transparent ? (byte)0x00 : (byte)Math.Min(file.RecordLength, 0xFF);
        return response;
    }

    private byte CountChildren(ushort parent, params SimFileKind[] kinds)
    {
        int count = files.Values.Count(file => file.Parent == parent && kinds.Contains(file.Kind));
        return (byte)Math.Min(count, 0xFF);
    }

    private void BuildDefaultFileSystem()
    {
        AddDirectory(0x3F00, 0);
        AddDirectory(0x7F10, 0x3F00);
        AddDirectory(0x7F20, 0x3F00);
        AddDirectory(0x7F21, 0x3F00);
        AddDirectory(0x7F40, 0x3F00);
        AddTransparent(0x2FE2, 0x3F00, "989999900000000000F1");
        AddGsmApplicationFiles(0x7F20);
        AddGsmApplicationFiles(0x7F21);
        AddGsmApplicationFiles(0x7F40);
        AddPcsCompatibilityFiles(0x7F40);
        AddTelecomFiles(0x7F10);
    }

    private void AddGsmApplicationFiles(ushort parent)
    {
        AddTransparent(0x6F05, parent, "0E01FFFF");
        AddTransparent(0x6F07, parent, imsiEf);
        AddTransparent(0x6F14, parent, operatorNameEf);
        AddTransparent(0x6F20, parent, "000000000000000000");
        AddTransparent(0x6F30, parent, "999999");
        AddTransparent(0x6F31, parent, "05");
        AddTransparent(0x6F37, parent, "000000");
        // FDN (service 3) is not advertised. The current SIM model does not track
        // EF invalidation and rehabilitation state, which the firmware uses to decide
        // whether FDN restricts the ME.
        AddTransparent(0x6F38, parent, DefaultServiceTable);
        AddCyclic(0x6F39, parent, 3, "000000", "000000", "000000");
        AddTransparent(0x6F3E, parent, "FFFFFFFF");
        AddTransparent(0x6F3F, parent, "FFFFFFFF");
        AddTransparent(0x6F41, parent, "FFFFFF0000");
        AddTransparent(0x6F45, parent, "FFFFFFFFFFFFFFFFFFFF");
        AddTransparent(0x6F46, parent, serviceProviderNameEf);
        AddTransparent(0x6F48, parent, "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF");
        AddTransparent(0x6F74, parent, "00000000000000000000000000000000");
        AddTransparent(0x6F78, parent, "0080");
        AddTransparent(0x6F7B, parent, "FFFFFFFFFFFFFFFFFFFFFFFF");
        AddTransparent(0x6F7E, parent, "FFFFFFFF99999999F9FF01");
        AddTransparent(0x6FAD, parent, "00FFFF");
        AddTransparent(0x6FAE, parent, "02");
        AddTransparent(0x6FB5, parent, "0000");
        AddTransparent(0x6FB6, parent, "00");
        AddLinearFixed(0x6FB7, parent, 16, "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00");
    }

    private void AddPcsCompatibilityFiles(ushort parent)
    {
        AddTransparentFilled(0x6F13, parent, 1);
        AddTransparentFilled(0x6F91, parent, 1);
        AddTransparentFilled(0x6F92, parent, 1);
        AddTransparentFilled(0x6F93, parent, 1);
        AddTransparentFilled(0x6F95, parent, 0x1D);
        AddTransparentFilled(0x6F96, parent, 0x1D);
        AddTransparentFilled(0x6F98, parent, 0x16);
        AddTransparentFilled(0x6F9B, parent, 0x25);
        AddTransparentFilled(0x6F9F, parent, 1);
    }

    private void AddTelecomFiles(ushort parent)
    {
        AddLinearFixed(0x6F3A, parent, SimPhonebookCodec.RecordLength, AdnRecordCount);
        AddLinearFixed(0x6F3B, parent, 30, 2);
        AddSmsStorageFile(0x6F3C, parent, SmsStorageRecordCount);
        AddLinearFixed(0x6F3D, parent, 14, 3);
        AddLinearFixed(0x6F40, parent, SimPhonebookCodec.RecordLength, 1);
        AddLinearFixed(0x6F42, parent, 44, DefaultSmsParametersRecord, BlankSmsParametersRecord);
        AddTransparent(0x6F43, parent, "04FF");
        AddCyclic(0x6F44, parent, 30, 3);
        AddLinearFixed(0x6F47, parent, 30, 2);
        AddLinearFixed(0x6F49, parent, 30, 2);
        AddLinearFixed(0x6F4A, parent, 13, 2);
        AddLinearFixed(0x6F4B, parent, 13, 2);
        AddLinearFixed(0x6F4C, parent, 13, 2);
    }

    private void AddDirectory(ushort id, ushort parent)
    {
        files.Add(new SimFileKey(parent, id), new SimFile(id, parent, SimFileKind.Directory, 0, 0, []));
    }

    private void AddTransparent(ushort id, ushort parent, string hex)
    {
        files.Add(new SimFileKey(parent, id), new SimFile(id, parent, SimFileKind.Transparent, 0, 0, Convert.FromHexString(hex)));
    }

    private void AddTransparent(ushort id, ushort parent, ReadOnlySpan<byte> data)
    {
        files.Add(new SimFileKey(parent, id), new SimFile(id, parent, SimFileKind.Transparent, 0, 0, data.ToArray()));
    }

    private void AddTransparentFilled(ushort id, ushort parent, int length)
    {
        byte[] data = new byte[length];
        Array.Fill(data, (byte)0xFF);
        files.Add(new SimFileKey(parent, id), new SimFile(id, parent, SimFileKind.Transparent, 0, 0, data));
    }

    private void AddLinearFixed(ushort id, ushort parent, int recordLength, int records)
    {
        byte[] data = new byte[recordLength * records];
        Array.Fill(data, (byte)0xFF);
        files.Add(new SimFileKey(parent, id), new SimFile(id, parent, SimFileKind.LinearFixed, 1, recordLength, data));
    }

    private void AddSmsStorageFile(ushort id, ushort parent, int records)
    {
        const int recordLength = SmsStorageRecordLength;

        if (records is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(records), "SIM record numbers are one byte and record 0 is invalid.");
        }

        byte[] data = new byte[recordLength * records];
        Array.Fill(data, (byte)0xFF);

        for (int record = 0; record < records; record++)
        {
            data[record * recordLength] = 0x00;
        }

        files.Add(new SimFileKey(parent, id), new SimFile(id, parent, SimFileKind.LinearFixed, 1, recordLength, data));
    }

    private void AddLinearFixed(ushort id, ushort parent, int recordLength, params string[] records)
    {
        byte[] data = new byte[recordLength * records.Length];

        for (int i = 0; i < records.Length; i++)
        {
            byte[] record = Convert.FromHexString(records[i]);

            if (record.Length != recordLength)
            {
                throw new InvalidOperationException("SIM record length mismatch.");
            }

            record.CopyTo(data.AsSpan(i * recordLength));
        }

        files.Add(new SimFileKey(parent, id), new SimFile(id, parent, SimFileKind.LinearFixed, 1, recordLength, data));
    }

    private void AddCyclic(ushort id, ushort parent, int recordLength, int records)
    {
        byte[] data = new byte[recordLength * records];
        Array.Fill(data, (byte)0xFF);
        files.Add(new SimFileKey(parent, id), new SimFile(id, parent, SimFileKind.Cyclic, 3, recordLength, data));
    }

    private void AddCyclic(ushort id, ushort parent, int recordLength, params string[] records)
    {
        byte[] data = new byte[recordLength * records.Length];

        for (int i = 0; i < records.Length; i++)
        {
            byte[] record = Convert.FromHexString(records[i]);

            if (record.Length != recordLength)
            {
                throw new InvalidOperationException("SIM record length mismatch.");
            }

            record.CopyTo(data.AsSpan(i * recordLength));
        }

        files.Add(new SimFileKey(parent, id), new SimFile(id, parent, SimFileKind.Cyclic, 3, recordLength, data));
    }

    private void RepairManagedOwnNumber(SimMutationOrigin origin, bool emitEvenWhenUnchanged)
    {
        WriteManagedRecord(AdnKey, ManagedOwnNumberRecord, origin, emitEvenWhenUnchanged);
        WriteManagedRecord(MsisdnKey, 1, origin, emitEvenWhenUnchanged);
    }

    private void WriteManagedRecord(
        SimFileKey key,
        int recordNumber,
        SimMutationOrigin origin,
        bool emitEvenWhenUnchanged)
    {
        SimFile file = files[key];
        int offset = (recordNumber - 1) * file.RecordLength;
        Span<byte> destination = file.Data.AsSpan(offset, file.RecordLength);
        if (!emitEvenWhenUnchanged && destination.SequenceEqual(managedOwnNumberRecord))
        {
            return;
        }

        byte[] oldValue = destination.ToArray();
        managedOwnNumberRecord.CopyTo(destination);
        CommitMutation(key, recordNumber, oldValue, destination, origin);
    }

    private static bool IsManagedOwnNumberRecord(SimFileKey key, int recordNumber) =>
        key == AdnKey && recordNumber == ManagedOwnNumberRecord ||
        key == MsisdnKey && recordNumber == 1;

    private void CommitMutation(
        SimFileKey key,
        int recordNumber,
        ReadOnlySpan<byte> oldValue,
        ReadOnlySpan<byte> newValue,
        SimMutationOrigin origin)
    {
        PersistenceVersion++;
        MutationCommitted?.Invoke(new SimMutation(
            key.Parent,
            key.Id,
            recordNumber,
            oldValue,
            newValue,
            origin));
    }

    private static int PpsLength(byte pps0)
    {
        return 3
            + ((pps0 & 0x10) != 0 ? 1 : 0)
            + ((pps0 & 0x20) != 0 ? 1 : 0)
            + ((pps0 & 0x40) != 0 ? 1 : 0);
    }

    private static bool IsSupportedPps(ReadOnlySpan<byte> pps)
    {
        if (pps.Length < 3 || pps[0] != 0xFF || pps.Length != PpsLength(pps[1]))
        {
            return false;
        }

        if ((pps[1] & 0x8F) != 0)
        {
            return false;
        }

        byte checksum = 0;

        foreach (byte value in pps)
        {
            checksum ^= value;
        }

        return checksum == 0;
    }

    private static byte[] EncodeImsi(string imsi)
    {
        if (imsi.Length != 15 || imsi.Any(ch => ch < '0' || ch > '9'))
        {
            throw new ArgumentException("SIM IMSI must be 15 decimal digits.", nameof(imsi));
        }

        byte[] data = new byte[9];
        data[0] = 0x08;
        data[1] = (byte)((Digit(imsi[0]) << 4) | 0x09);
        int digit = 1;

        for (int i = 2; i < data.Length; i++)
        {
            int lo = digit < imsi.Length ? Digit(imsi[digit++]) : 0xF;
            int hi = digit < imsi.Length ? Digit(imsi[digit++]) : 0xF;
            data[i] = (byte)(lo | (hi << 4));
        }

        return data;
    }

    private static int Digit(char value) => value - '0';

    private static byte[] EncodeServiceProviderName(string? serviceProviderName)
    {
        byte[] data = EncodeAlphaIdentifier(serviceProviderName, 17, destinationOffset: 1);
        // EFSPN byte 1 bit 1 = 0: the ME does not also display the registered
        // PLMN name. The configured SPN is therefore the idle-screen name.
        data[0] = 0x00;

        return data;
    }

    private static byte[] EncodeAlphaIdentifier(string? value, int length, int destinationOffset = 0)
    {
        string name = string.IsNullOrWhiteSpace(value)
            ? Dct3PhoneSettings.DefaultNetworkName
            : value.Trim();
        byte[] data = new byte[length];
        Array.Fill(data, (byte)0xFF);

        int textLength = Math.Min(name.Length, data.Length - destinationOffset);
        for (int index = 0; index < textLength; index++)
        {
            char character = name[index];
            data[index + destinationOffset] = character is >= ' ' and <= '~'
                ? (byte)(character & 0x7F)
                : (byte)' ';
        }

        return data;
    }

    private static byte[] BuildSimServiceTable(int length, params int[] activeServices)
    {
        if (length < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "EFSST must contain at least two bytes.");
        }

        byte[] table = new byte[length];

        foreach (int service in activeServices)
        {
            if (service < 1 || service > length * 4)
            {
                throw new ArgumentOutOfRangeException(nameof(activeServices), "SIM service is outside the EFSST length.");
            }

            int index = (service - 1) / 4;
            int shift = ((service - 1) % 4) * 2;
            table[index] |= (byte)(0b11 << shift);
        }

        return table;
    }

    private readonly record struct SimFileKey(ushort Parent, ushort Id);

    private sealed record SimFile(ushort Id, ushort Parent, SimFileKind Kind, byte Structure, int RecordLength, byte[] Data);

    private readonly record struct SimApduResult(byte[] Data, byte Sw1, byte Sw2, int ProcedureLength)
    {
        public static SimApduResult Status(byte sw1, byte sw2) => new([], sw1, sw2, -1);

        public static SimApduResult Response(byte[] data, byte sw1, byte sw2) => new(data, sw1, sw2, -1);

        public static SimApduResult Procedure(int length) => new([], 0, 0, length);
    }

    private enum SimTransportState
    {
        AwaitingInitialByte,
        Pps,
        TpduHeader,
        CommandData,
    }

    private enum SimFileKind
    {
        Directory,
        Transparent,
        LinearFixed,
        Cyclic,
    }
}
