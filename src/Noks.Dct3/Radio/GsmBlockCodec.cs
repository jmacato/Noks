using System.Buffers.Binary;
using Noks.Dct3.Audio;
using Noks.Dct3.Core;
using Noks.Dct3.Messaging;
using Noks.Dct3.Sim;
using Noks.Dct3.State;

namespace Noks.Dct3.Radio;

internal static class GsmBlockCodec
{
    internal const int BroadcastBsPaMfrms = 2;

    internal static readonly int[] CcchBlockOffsets = [6, 12, 16, 22, 26, 32, 36, 42, 46];

    internal static (int MultiframePhase, int FrameOffset) CalculatePagingGroup(string imsi)
    {
        if (imsi.Length != 15 || imsi.Any(ch => ch < '0' || ch > '9'))
        {
            throw new ArgumentException("Paging IMSI must be 15 decimal digits.", nameof(imsi));
        }

        int imsiMod1000 = int.Parse(imsi[^3..], System.Globalization.CultureInfo.InvariantCulture);
        int availablePagingBlocks = CcchBlockOffsets.Length;
        int pagingGroup = imsiMod1000 % (availablePagingBlocks * BroadcastBsPaMfrms);
        return (pagingGroup / availablePagingBlocks, CcchBlockOffsets[pagingGroup % availablePagingBlocks]);
    }

    internal static byte[] BuildChannelChangedConfirm(ReadOnlySpan<byte> payload)
    {
        byte confirm = payload.Length > 6 ? (byte)(payload[6] & 0x01) : (byte)0;
        return [0x01, 0x89, confirm];
    }

    internal static byte[] BuildSchInformation(byte bsic, int fn)
    {
        // GSM 04.08 9.1.30: BSIC(6) T1(11) T2(5) T3'(3) packed over 25 bits.
        int t1 = fn / 1326 % 2048;
        int t2 = fn % 26;
        int t3Prime = (fn % 51 - 1) / 10;

        return
        [
            (byte)((bsic << 2) | (t1 >> 9)),
            (byte)(t1 >> 1),
            (byte)(((t1 & 1) << 7) | (t2 << 2) | (t3Prime >> 1)),
            (byte)((t3Prime & 1) << 7),
        ];
    }

    internal static byte[] BuildSystemInformation2()
    {
        byte[] layer2 =
        [
            0x59, 0x06, 0x1A,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0xFF,
            0x40, 0x00, 0x00,
        ];

        return layer2;
    }

    internal static byte[] BuildNeighbourCellDescription(ushort arfcn)
    {
        arfcn &= 0x03FF;
        if (arfcn is >= 1 and <= 124)
        {
            byte[] description = new byte[16];
            description[15 - ((arfcn - 1) >> 3)] = (byte)(1 << ((arfcn - 1) & 7));
            return description;
        }

        return
        [
            (byte)(0x8E | (arfcn >> 9)),
            (byte)(arfcn >> 1),
            (byte)((arfcn & 0x01) << 7),
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00,
        ];
    }

    internal static byte[] BuildPagingRequestType1(string imsi, byte channelNeeded = 0x00)
    {
        byte[] mobileIdentity = EncodeImsiMobileIdentityContents(imsi);
        int l2PseudoLength = 3 + 1 + mobileIdentity.Length;
        byte[] layer2 = new byte[23];
        layer2.AsSpan().Fill(0x2B);

        layer2[0] = (byte)((l2PseudoLength << 2) | 0x01);
        layer2[1] = 0x06;
        layer2[2] = 0x21;
        // Paging Request Type 1 packs Page Mode first and Channel Needed second.
        // Keep Page Mode as normal paging (0) and place Mobile Identity 1's
        // channel-needed field in the following half-octet.
        layer2[3] = (byte)((channelNeeded & 0x03) << 4);
        layer2[4] = (byte)mobileIdentity.Length;
        mobileIdentity.CopyTo(layer2, 5);

        return layer2;
    }

    internal static byte[] BuildPagingFillRequestType1()
    {
        byte[] layer2 = new byte[23];
        layer2.AsSpan().Fill(0x2B);
        layer2[0] = 0x15;
        layer2[1] = 0x06;
        layer2[2] = 0x21;
        layer2[3] = 0x00;
        layer2[4] = 0x01;
        layer2[5] = 0xF0;
        return layer2;
    }

    internal static byte[] EncodeImsiMobileIdentityContents(string imsi)
    {
        if (imsi.Length != 15 || imsi.Any(ch => ch < '0' || ch > '9'))
        {
            throw new ArgumentException("Paging IMSI must be 15 decimal digits.", nameof(imsi));
        }

        byte[] contents = new byte[8];
        contents[0] = (byte)((Digit(imsi[0]) << 4) | 0x09);
        int digit = 1;

        for (int i = 1; i < contents.Length; i++)
        {
            int lo = digit < imsi.Length ? Digit(imsi[digit++]) : 0xF;
            int hi = digit < imsi.Length ? Digit(imsi[digit++]) : 0xF;
            contents[i] = (byte)(lo | (hi << 4));
        }

        return contents;
    }

    internal static int Digit(char value) => value - '0';

    internal static byte[] BuildImmediateAssignment(byte requestReference, ushort frameNumber, byte bsic, ushort arfcn)
    {
        // GSM 04.08 9.1.18 / 10.5.2.30: Immediate Assignment on CCCH,
        // echoing the CHANNEL REQUEST reference and assigning a non-hopping SDCCH/8.
        DecodeRequestReferenceFrame(frameNumber, out byte t1, out byte t3, out byte t2);
        byte tsc = (byte)(bsic & 0x07);
        arfcn &= 0x03FF;

        return
        [
            0x2D, 0x06, 0x3F, 0x00,
            0x41, (byte)((tsc << 5) | (arfcn >> 8)), (byte)arfcn,
            requestReference,
            (byte)((t1 << 3) | (t3 >> 3)),
            (byte)(((t3 & 0x07) << 5) | t2),
            0x00,
            0x00,
            0x2B, 0x2B, 0x2B, 0x2B, 0x2B, 0x2B,
            0x2B, 0x2B, 0x2B, 0x2B, 0x2B,
        ];
    }

    internal static void DecodeRequestReferenceFrame(ushort frameNumber, out byte t1, out byte t3, out byte t2)
    {
        int fn = frameNumber % 42432;
        t1 = (byte)((fn / 1326) % 32);
        t2 = (byte)(fn % 26);
        t3 = (byte)(fn % 51);
    }

    internal static bool IsImmediateAssignment(ReadOnlySpan<byte> packet) =>
        packet.Length >= 15 &&
        packet[1] == 0x80 &&
        packet[2] == 0x60 &&
        packet[12] == 0x2D &&
        packet[13] == 0x06 &&
        packet[14] == 0x3F;

    internal static byte[] BuildSimlReadbackReply(ReadOnlySpan<byte> localPayload, int blockIndex)
    {
        byte[] packet = new byte[0x38];
        packet[0] = 0x36;
        packet[1] = 0x74;
        packet[2] = 0x35;
        packet[3] = 0x32;
        packet[4] = (byte)blockIndex;
        packet[0x0F] = 0x78;
        if (localPayload.Length >= 0x1A)
        {
            localPayload[2..0x1A].CopyTo(packet.AsSpan(0x1E, 0x18));
        }

        return packet;
    }

    internal static bool IsFacadeRadioPacket(ReadOnlySpan<byte> packet) =>
        packet.Length >= 2 && IsFacadeRadioPacket(packet[1]);

    internal static bool IsFacadeRadioPacket(byte type) =>
        type is 0x80 or 0x83 or 0x84 or 0x86 or 0x88 or 0x8B;
}
