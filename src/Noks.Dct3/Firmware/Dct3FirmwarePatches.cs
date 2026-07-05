using Noks.Dct3.Core;
namespace Noks.Dct3.Firmware;

internal static class Dct3FirmwarePatches
{
    private const uint FlashBase = Dct3Machine.FlashBase;
    private const int Nhm5RussianLanguageSettingsCurrentLanguageByteOffset = 0x1A;
    private const int V607AutomaticKeyguardEnabledByteOffset = 0x27;
    private const int V418RingtoneParserAllocationSignatureOffset = 0x711EC;
    private const int V418RingtoneParserInitialCapacitySignatureOffset = 0x71250;
    private const int V418RingtoneParserFinalCapacitySignatureOffset = 0x712D0;
    private const int V418ReceivedRingtoneAllocationSignatureOffset = 0x97FA8;
    private const int V418ReceivedRingtoneCopySignatureOffset = 0x97FB8;
    private const int RingtoneBufferSizeInstructionsOffset = 2;

    private static readonly byte[] StaleMaintenanceStateRecordSignature =
    [
        0x05, 0x82, 0xFF, 0xFF, 0x04, 0x99, 0x04, 0x94,
    ];

    private static readonly byte[] Nhm5RussianLanguageSettingsSignature =
    [
        0x00, 0x01, 0x00, 0x01, 0x00, 0x02, 0x00, 0x01,
        0x0F, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0x00,
        0x01, 0x0F, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02,
        0x00, 0x00, 0x0F, 0x00, 0x00, 0x11, 0x00, 0x00,
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x00,
        0x00, 0x01, 0xFF, 0xFF,
    ];

    private static readonly byte[] V418RingtoneParserAllocationSignature =
    [
        0x61, 0x60, 0x20, 0xFF, 0x30, 0x3D, 0xF0, 0x16, 0xFF, 0x69,
    ];
    private static readonly byte[] V418RingtoneParserInitialCapacitySignature =
    [
        0x81, 0x21, 0x20, 0xFF, 0x30, 0x21, 0x81, 0x60, 0x48, 0xBD,
    ];
    private static readonly byte[] V418RingtoneParserFinalCapacitySignature =
    [
        0xFD, 0xB5, 0x20, 0xFF, 0x30, 0x2C, 0x81, 0x60, 0x48, 0xE3,
    ];
    private static readonly byte[] V418ReceivedRingtoneAllocationSignature =
    [
        0x1C, 0x06, 0x20, 0xFF, 0x30, 0x2D, 0xF7, 0xF0, 0xF8, 0x8B,
    ];
    private static readonly byte[] V418ReceivedRingtoneCopySignature =
    [
        0x19, 0x89, 0x22, 0xFF, 0x32, 0x2D, 0xF0, 0x48, 0xFA, 0x4F,
    ];
    private static readonly byte[] V418RingtoneBufferAllocationInstructions =
    [
        0x20, 0x10, // movs r0, #16
        0x02, 0x00, // lsls r0, r0, #8 = 4096
    ];
    private static readonly byte[] V418RingtoneBufferInitialCapacityInstructions =
    [
        0x20, 0x7E, // movs r0, #126
        0x01, 0x40, // lsls r0, r0, #5 = 4032
    ];
    private static readonly byte[] V418RingtoneBufferFinalCapacityInstructions =
    [
        0x20, 0x7F, // movs r0, #127
        0x01, 0x40, // lsls r0, r0, #5 = 4064
    ];
    private static readonly byte[] V418ReceivedRingtoneCopySizeInstructions =
    [
        0x22, 0x7F, // movs r2, #127
        0x01, 0x52, // lsls r2, r2, #5 = 4064
    ];

    private static readonly int[] V607AutomaticKeyguardSettingsOffsets =
    [
        0x3E069A - (int)FlashBase,
        0x3E49C8 - (int)FlashBase,
        0x3E4A70 - (int)FlashBase,
        0x3E4B04 - (int)FlashBase,
        0x3E4B90 - (int)FlashBase,
        0x3E4C24 - (int)FlashBase,
        0x3E4CB8 - (int)FlashBase,
        0x3E4D44 - (int)FlashBase,
        0x3E4EA8 - (int)FlashBase,
        0x3E4F34 - (int)FlashBase,
        0x3E7B7E - (int)FlashBase,
        0x3E7C18 - (int)FlashBase,
        0x3E7CA4 - (int)FlashBase,
        0x3E7D3E - (int)FlashBase,
        0x3E7EDA - (int)FlashBase,
        0x3E7F66 - (int)FlashBase,
        0x3E8102 - (int)FlashBase,
    ];
    private static readonly byte[] V607AutomaticKeyguardSettingsSignature =
    [
        0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x01, 0x02,
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];
    internal static bool ApplyNhm5RussianLanguagePmmRepair(byte[] flash, IDct3Trace? trace = null)
    {
        int changed = 0;
        int searchStart = 0;

        while (searchStart <= flash.Length - Nhm5RussianLanguageSettingsSignature.Length)
        {
            int relativeOffset = flash.AsSpan(searchStart).IndexOf(Nhm5RussianLanguageSettingsSignature);
            if (relativeOffset < 0)
            {
                break;
            }

            int settingsOffset = searchStart + relativeOffset;
            flash[settingsOffset + Nhm5RussianLanguageSettingsCurrentLanguageByteOffset] = 0x01;
            changed++;
            searchStart = settingsOffset + Nhm5RussianLanguageSettingsSignature.Length;
        }

        if (changed == 0)
        {
            return false;
        }

        trace?.Event($"firmware patch: NHM-5 Russian PMM language set to English ({changed} records)");
        return true;
    }

    internal static bool TryResolveV418LongRingtoneBufferPatch(
        ReadOnlySpan<byte> flash,
        out uint triggerPc)
    {
        triggerPc = 0;
        if (!HasBytes(flash, V418RingtoneParserAllocationSignatureOffset, V418RingtoneParserAllocationSignature) ||
            !HasBytes(flash, V418RingtoneParserInitialCapacitySignatureOffset, V418RingtoneParserInitialCapacitySignature) ||
            !HasBytes(flash, V418RingtoneParserFinalCapacitySignatureOffset, V418RingtoneParserFinalCapacitySignature) ||
            !HasBytes(flash, V418ReceivedRingtoneAllocationSignatureOffset, V418ReceivedRingtoneAllocationSignature) ||
            !HasBytes(flash, V418ReceivedRingtoneCopySignatureOffset, V418ReceivedRingtoneCopySignature))
        {
            return false;
        }

        // This arms just before the first allocation-size instruction runs. The
        // firmware image must stay byte-exact at startup, so the patch applies later.
        triggerPc = FlashBase + V418RingtoneParserAllocationSignatureOffset +
            RingtoneBufferSizeInstructionsOffset + 4u;
        return true;
    }

    internal static bool ApplyV418LongRingtoneBufferPatch(byte[] flash, IDct3Trace? trace = null)
    {
        if (!TryResolveV418LongRingtoneBufferPatch(flash, out _))
        {
            return false;
        }

        // The ringtone parser adds a 16-byte state block before its allocation. A 4 KiB
        // allocation leaves 4080 bytes for the converted tone. The initial capacity is
        // 4032 bytes, to leave room for a 15-character title. The final capacity is 4064 bytes.
        V418RingtoneBufferAllocationInstructions.CopyTo(
            flash.AsSpan(V418RingtoneParserAllocationSignatureOffset + RingtoneBufferSizeInstructionsOffset));
        V418RingtoneBufferInitialCapacityInstructions.CopyTo(
            flash.AsSpan(V418RingtoneParserInitialCapacitySignatureOffset + RingtoneBufferSizeInstructionsOffset));
        V418RingtoneBufferFinalCapacityInstructions.CopyTo(
            flash.AsSpan(V418RingtoneParserFinalCapacitySignatureOffset + RingtoneBufferSizeInstructionsOffset));

        // The receiving UI makes a second, fixed 300-byte allocation and copy after parsing.
        // This patch allocates 4 KiB instead, and copies only the parser's 4064-byte bound.
        V418RingtoneBufferAllocationInstructions.CopyTo(
            flash.AsSpan(V418ReceivedRingtoneAllocationSignatureOffset + RingtoneBufferSizeInstructionsOffset));
        V418ReceivedRingtoneCopySizeInstructions.CopyTo(
            flash.AsSpan(V418ReceivedRingtoneCopySignatureOffset + RingtoneBufferSizeInstructionsOffset));

        trace?.Event("firmware patch: v4.18 received ringtone buffers expanded to 4 KiB");
        return true;
    }

    internal static bool ApplyV607AutomaticKeyguardPmmRepair(byte[] flash, IDct3Trace? trace = null)
    {
        int changed = 0;

        foreach (int settingsOffset in V607AutomaticKeyguardSettingsOffsets)
        {
            int flagOffset = settingsOffset + V607AutomaticKeyguardEnabledByteOffset;
            if (!HasBytes(flash, settingsOffset, V607AutomaticKeyguardSettingsSignature) ||
                (uint)flagOffset >= (uint)flash.Length ||
                flash[flagOffset] != 0x01)
            {
                continue;
            }

            flash[flagOffset] = 0x00;
            changed++;
        }

        if (changed == 0)
        {
            return false;
        }

        trace?.Event($"firmware patch: v6.07 automatic keyguard PMM disabled ({changed} records)");
        return true;
    }

    internal static bool ApplyStaleMaintenanceStatePmmRepair(byte[] flash, IDct3Trace? trace = null)
    {
        int changed = 0;
        int searchStart = 0;

        while (searchStart <= flash.Length - StaleMaintenanceStateRecordSignature.Length)
        {
            int relativeOffset = flash.AsSpan(searchStart).IndexOf(StaleMaintenanceStateRecordSignature);
            if (relativeOffset < 0)
            {
                break;
            }

            int recordOffset = searchStart + relativeOffset;
            flash[recordOffset + 2] = 0x00;
            changed++;
            searchStart = recordOffset + StaleMaintenanceStateRecordSignature.Length;
        }

        if (changed == 0)
        {
            return false;
        }

        trace?.Event($"firmware patch: stale maintenance PMM state disabled ({changed} records)");
        return true;
    }

    private static bool HasBytes(ReadOnlySpan<byte> flash, int offset, ReadOnlySpan<byte> expected)
    {
        return offset >= 0 &&
            offset <= flash.Length - expected.Length &&
            flash.Slice(offset, expected.Length).SequenceEqual(expected);
    }
}
