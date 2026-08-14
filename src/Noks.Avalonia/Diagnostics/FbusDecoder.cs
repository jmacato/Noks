namespace Noks.AvaloniaApp.Diagnostics;

public static class FbusDecoder
{
    public static string Describe(bool transmitted, ReadOnlySpan<byte> frame)
    {
        string direction = transmitted ? "TX" : "RX";
        string raw = string.Join(' ', frame.ToArray().Select(value => $"{value:X2}"));
        if (frame.Length < 6)
        {
            return $"{direction} truncated ({frame.Length} B) | {raw}";
        }

        int declaredLength = (frame[4] << 8) | frame[5];
        string medium = frame[0] switch
        {
            0x1C => "IR",
            0x1E => "CABLE",
            0x1F => "MBUS",
            _ => $"MEDIA-{frame[0]:X2}",
        };
        string destination = DeviceName(frame[1]);
        string source = DeviceName(frame[2]);
        string packet = PacketName(frame[3]);
        string detail = DescribePayload(frame[3], frame.Length > 6 ? frame[6..] : []);
        string lengthWarning = declaredLength > frame.Length - 6
            ? $" [declared {declaredLength} B, captured {frame.Length - 6} B]"
            : $" ({declaredLength} B)";

        return $"{direction} {medium} {source} -> {destination}  {packet}{detail}{lengthWarning} | {raw}";
    }

    private static string DescribePayload(byte packetType, ReadOnlySpan<byte> payload)
    {
        if (packetType == 0x40 && payload.Length >= 3)
        {
            return $" / {ServiceName(payload[2])}";
        }

        if (packetType == 0xF4 && payload.Length > 0)
        {
            return payload[0] switch
            {
                0x00 => " / set DSP debug flags",
                0x03 => " / DSP boot",
                0x04 when payload.Length >= 4 =>
                    $" / MDI sniff {(payload[1] == 0 ? "MCU->DSP" : "DSP->MCU")} type={payload[3]:X2}",
                0x05 when payload.Length >= 2 => $" / MDI send type={payload[1]:X2}",
                _ => $" / subtype {payload[0]:X2}",
            };
        }

        return "";
    }

    private static string DeviceName(byte value) => value switch
    {
        0x00 => "PHONE",
        0x0C => "PC",
        0xFF => "BROADCAST",
        _ => $"DEV-{value:X2}",
    };

    private static string PacketName(byte value) => value switch
    {
        0x00 => "DEBUG",
        0x01 => "CALLING",
        0x02 => "AUTHENTICATION",
        0x03 => "PHONEBOOK STATUS",
        0x04 => "PHONE ID",
        0x05 => "WELCOME MESSAGE",
        0x08 => "SECURITY CODE",
        0x0A => "NETWORK INFO",
        0x0C => "KEY EVENT",
        0x0D => "DISPLAY",
        0x11 => "ALARM",
        0x13 => "CALENDAR",
        0x14 => "SMS STATUS",
        0x40 => "SERVICE",
        0x64 => "PHONE INFO",
        0x74 => "DSP COUNTERS",
        0x7F => "ACK",
        0xD0 => "DEVICE ANNOUNCEMENT",
        0xD1 => "RPC QUERY",
        0xD2 => "RPC RESULT",
        0xD4 => "DEVICE GOING AWAY",
        0xD5 => "ROUTING ERROR",
        0xF0 => "RLP SEND",
        0xF1 => "RLP RECEIVE",
        0xF4 => "DSP DEBUG",
        _ => $"TYPE-{value:X2}",
    };

    private static string ServiceName(byte value) => value switch
    {
        0x66 => "read IMEI",
        0x68 => "read ADC",
        0x6E => "read security code",
        0x70 => "enable event trace",
        0x71 => "disable event trace",
        0x72 => "read SIM identity",
        0x7C => "call control",
        0x7D => "display contrast",
        0x8A => "read SIM-lock state",
        0x8F => "buzzer test",
        0x97 => "read ADC value",
        0xA1 => "vibra test",
        0xAC => "screen dump",
        0xC8 or 0xE8 => "read version",
        0xCA => "read product info",
        0xCE => "run self-test",
        0xCF => "read self-test result",
        0xD4 => "read memory",
        0xD5 => "write memory",
        _ => $"command {value:X2}",
    };
}
