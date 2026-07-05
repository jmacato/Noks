namespace Noks.Dct3.Messaging;

public static class NokiaSmartMessagingRingtone
{
    public const ushort DestinationPort = 0x1581;
    public const string DemoRingtoneName = "OdeToJoy";
    public const string DemoRingtoneNotation =
        "4e2 4e2 4f2 4g2 4g2 4f2 4e2 4d2 4c2 4c2 4d2 4e2 4e2 8d2 2d2";

    private static readonly int[] SupportedTempos =
    [
        25, 28, 31, 35, 40, 45, 50, 56, 63, 70, 80, 90, 100, 112, 125, 140,
        160, 180, 200, 225, 250, 285, 320, 355, 400, 450, 500, 565, 635, 715, 800, 900,
    ];

    public readonly record struct RtttlMetadata(
        string Title,
        int DefaultDuration,
        int DefaultOctave,
        int BeatsPerMinute);

    public static byte[] Encode(string name, int tempo, string notation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notation);
        List<Note> notes;
        if (IsRtttl(notation))
        {
            (name, notes) = ParseRtttl(notation);
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            notes = ParseComposerNotation(notation, tempo);
        }

        if (!IsValidSmartMessagingName(name))
        {
            throw new ArgumentException("A Smart Messaging ringtone name must contain 1-15 ASCII characters.", nameof(name));
        }

        if (notes.Count == 0 || notes.All(note => note.Pitch == 0))
        {
            throw new ArgumentException("The ringtone must contain at least one note.", nameof(notation));
        }

        if (notes.Count > byte.MaxValue)
        {
            throw new ArgumentException(
                $"A Nokia Smart Messaging ringtone supports at most {byte.MaxValue} notes and rests.",
                nameof(notation));
        }

        byte[] payload = EncodeNotes(name, notes);
        _ = SmartMessageSms.GetPartCount(payload.Length);
        return payload;
    }

    public static bool IsRtttl(string notation)
    {
        if (string.IsNullOrWhiteSpace(notation))
        {
            return false;
        }

        int firstSeparator = notation.IndexOf(':');
        return firstSeparator >= 0 && notation.IndexOf(':', firstSeparator + 1) > firstSeparator;
    }

    public static byte[] EncodeRtttl(string rtttl) => Encode("Ringtone", tempo: 63, rtttl);

    public static int GetSmsPartCount(int encodedPayloadLength) =>
        SmartMessageSms.GetPartCount(encodedPayloadLength);

    public static bool TryParseRtttlMetadata(string notation, out RtttlMetadata metadata)
    {
        metadata = default;
        if (!IsRtttl(notation))
        {
            return false;
        }

        try
        {
            string[] sections = SplitRtttl(notation);
            ParsedRtttlDefaults defaults = ParseRtttlDefaults(sections[0], sections[1], nameof(notation));
            if (!IsValidSmartMessagingName(defaults.Title))
            {
                return false;
            }

            metadata = new RtttlMetadata(
                defaults.Title,
                defaults.DefaultDuration,
                defaults.DefaultOctave,
                defaults.Tempo);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static byte[] EncodeNotes(string name, List<Note> notes)
    {
        BitWriter writer = new(capacity: 512);
        writer.Write(0x02, 8);
        writer.Write(0x4A, 7);
        writer.Align();
        writer.Write(0x3A, 7);
        writer.Write(0x20, 3);
        writer.Write((byte)(name.Length << 4), 4);
        foreach (char character in name)
        {
            writer.Write((byte)character, 8);
        }

        writer.Write(0x01, 8);
        writer.Write(0x00, 3);
        writer.Write(0x00, 2);
        writer.Write(unchecked((byte)(0x15 << 4)), 4);
        int commandCountBit = writer.Position;
        writer.Skip(8);

        int commandCount = 0;
        int currentScale = -1;
        int currentStyle = -1;
        bool tempoWritten = false;
        foreach (Note note in notes.SkipWhile(note => note.Pitch == 0))
        {
            if (note.Pitch != 0)
            {
                if (currentScale != note.Scale)
                {
                    currentScale = note.Scale;
                    writer.Write(0x40, 3);
                    writer.Write((byte)((currentScale - 4) << 6), 2);
                    commandCount++;
                }

                if (currentStyle != note.StyleCode)
                {
                    currentStyle = note.StyleCode;
                    writer.Write(0x60, 3);
                    writer.Write((byte)(currentStyle << 6), 2);
                    commandCount++;
                }
            }

            if (!tempoWritten)
            {
                int tempoIndex = ResolveTempoIndex(note.Tempo);
                writer.Write(0x80, 3);
                writer.Write((byte)(tempoIndex << 3), 5);
                tempoWritten = true;
                commandCount++;
            }

            writer.Write(0x20, 3);
            writer.Write((byte)(note.Pitch << 4), 4);
            writer.Write((byte)(note.DurationCode << 5), 3);
            writer.Write((byte)(note.DurationSpecCode << 6), 2);
            commandCount++;
        }

        if (commandCount > byte.MaxValue)
        {
            throw new ArgumentException("The ringtone contains too many encoded commands.", "notation");
        }

        writer.Align();
        writer.Write(0x00, 8);
        int endBit = writer.Position;
        writer.Position = commandCountBit;
        writer.Write((byte)commandCount, 8);
        writer.Position = endBit;
        return writer.ToArray();
    }

    public static byte[] EncodeDemoRingtone() =>
        Encode(DemoRingtoneName, tempo: 125, DemoRingtoneNotation);

    private static List<Note> ParseComposerNotation(string notation, int tempo)
    {
        List<Note> notes = [];
        foreach (string token in notation.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            int cursor = 0;
            while (cursor < token.Length && char.IsDigit(token[cursor]))
            {
                cursor++;
            }

            if (cursor == 0 || !int.TryParse(token.AsSpan(0, cursor), out int duration))
            {
                throw new ArgumentException($"Invalid ringtone token '{token}'.", nameof(notation));
            }

            int durationCode = duration switch
            {
                1 => 0,
                2 => 1,
                4 => 2,
                8 => 3,
                16 => 4,
                32 => 5,
                _ => throw new ArgumentException($"Unsupported duration in ringtone token '{token}'.", nameof(notation)),
            };

            string value = token[cursor..];
            if (value is "-" or "‐" or "–" or "—")
            {
                notes.Add(new Note(Pitch: 0, Scale: 4, durationCode, DurationSpecCode: 0, StyleCode: 0, tempo));
                continue;
            }

            if (value.Length < 2 || !int.TryParse(value.AsSpan(1), out int octave) || octave is < 1 or > 4)
            {
                throw new ArgumentException($"Invalid note in ringtone token '{token}'.", nameof(notation));
            }

            int pitch = char.ToLowerInvariant(value[0]) switch
            {
                'c' => 1,
                'd' => 3,
                'e' => 5,
                'f' => 6,
                'g' => 8,
                'a' => 10,
                'b' or 'h' => 12,
                _ => throw new ArgumentException($"Unsupported pitch in ringtone token '{token}'.", nameof(notation)),
            };
            notes.Add(new Note(pitch, Scale: octave + 3, durationCode, DurationSpecCode: 0, StyleCode: 0, tempo));
        }

        return notes;
    }

    private static (string Name, List<Note> Notes) ParseRtttl(string rtttl)
    {
        string[] sections = SplitRtttl(rtttl);
        ParsedRtttlDefaults defaults = ParseRtttlDefaults(sections[0], sections[1], nameof(rtttl));

        List<Note> notes = [];
        foreach (string rawToken in sections[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string token = rawToken.ToLowerInvariant();
            int cursor = 0;
            while (cursor < token.Length && char.IsDigit(token[cursor]))
            {
                cursor++;
            }

            int durationCode = cursor == 0
                ? DurationCode(defaults.DefaultDuration, rawToken, nameof(rtttl))
                : DurationCode(ParseDuration(token[..cursor], rawToken, nameof(rtttl)), rawToken, nameof(rtttl));
            int dots = 0;
            while (cursor < token.Length && token[cursor] == '.')
            {
                dots++;
                cursor++;
            }

            if (cursor >= token.Length)
            {
                throw new ArgumentException($"Invalid RTTTL note '{rawToken}'.", nameof(rtttl));
            }

            char pitchCharacter = token[cursor++];
            int pitch = pitchCharacter switch
            {
                'p' => 0,
                'c' => 1,
                'd' => 3,
                'e' => 5,
                'f' => 6,
                'g' => 8,
                'a' => 10,
                'b' or 'h' => 12,
                _ => throw new ArgumentException($"Unsupported RTTTL pitch in '{rawToken}'.", nameof(rtttl)),
            };

            if (cursor < token.Length && token[cursor] == '#')
            {
                pitch = pitch switch
                {
                    1 or 3 or 6 or 8 or 10 => pitch + 1,
                    _ => throw new ArgumentException($"Unsupported RTTTL sharp in '{rawToken}'.", nameof(rtttl)),
                };
                cursor++;
            }

            int octaveValue = defaults.DefaultOctave;
            while (cursor < token.Length)
            {
                if (token[cursor] == '.')
                {
                    dots++;
                    cursor++;
                    continue;
                }

                if (char.IsDigit(token[cursor]) &&
                    int.TryParse(token.AsSpan(cursor, 1), out int noteOctave) &&
                    noteOctave is >= 4 and <= 7)
                {
                    octaveValue = noteOctave;
                    cursor++;
                    continue;
                }

                throw new ArgumentException($"Invalid RTTTL note '{rawToken}'.", nameof(rtttl));
            }

            if (dots > 2)
            {
                throw new ArgumentException($"RTTTL note '{rawToken}' has too many duration dots.", nameof(rtttl));
            }

            notes.Add(new Note(
                pitch,
                Scale: pitch == 0 ? 4 : octaveValue,
                durationCode,
                DurationSpecCode: dots,
                StyleCode: defaults.StyleCode,
                defaults.Tempo));
        }

        return (defaults.Title, notes);
    }

    private static string[] SplitRtttl(string rtttl)
    {
        string[] sections = rtttl.Trim().Split(':', 3);
        if (sections.Length != 3)
        {
            throw new ArgumentException("RTTTL must use the name:defaults:notes structure.", nameof(rtttl));
        }

        return sections;
    }

    private static ParsedRtttlDefaults ParseRtttlDefaults(
        string title,
        string settings,
        string parameterName)
    {
        int defaultDuration = 4;
        int defaultOctave = 5;
        int tempo = 63;
        int styleCode = 0;
        foreach (string setting in settings.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = setting.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                throw new ArgumentException($"Invalid RTTTL default '{setting}'.", parameterName);
            }

            switch (pair[0].ToLowerInvariant())
            {
                case "d":
                    defaultDuration = ParseDuration(pair[1], setting, parameterName);
                    break;
                case "o" when int.TryParse(pair[1], out int octave) && octave is >= 4 and <= 7:
                    defaultOctave = octave;
                    break;
                case "b" when int.TryParse(pair[1], out int beatsPerMinute) && beatsPerMinute > 0:
                    tempo = beatsPerMinute;
                    break;
                case "s":
                    styleCode = pair[1].ToLowerInvariant() switch
                    {
                        "n" or "natural" => 0,
                        "c" or "continuous" => 1,
                        "s" or "staccato" => 2,
                        _ => throw new ArgumentException($"Invalid RTTTL style '{pair[1]}'.", parameterName),
                    };
                    break;
                default:
                    throw new ArgumentException($"Unsupported RTTTL default '{setting}'.", parameterName);
            }
        }

        return new ParsedRtttlDefaults(
            title.Trim(),
            defaultDuration,
            defaultOctave,
            tempo,
            styleCode);
    }

    private static int ParseDuration(string value, string token, string parameterName)
    {
        if (!int.TryParse(value, out int duration))
        {
            throw new ArgumentException($"Invalid duration in '{token}'.", parameterName);
        }

        _ = DurationCode(duration, token, parameterName);
        return duration;
    }

    private static int DurationCode(int duration, string token, string parameterName) => duration switch
    {
        1 => 0,
        2 => 1,
        4 => 2,
        8 => 3,
        16 => 4,
        32 => 5,
        _ => throw new ArgumentException($"Unsupported duration in '{token}'.", parameterName),
    };

    private static bool IsValidSmartMessagingName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= 15 &&
        name.All(character => character is >= ' ' and <= '~');

    private static int ResolveTempoIndex(int tempo)
    {
        for (int i = 0; i < SupportedTempos.Length; i++)
        {
            if (tempo <= SupportedTempos[i])
            {
                return i;
            }
        }

        return SupportedTempos.Length - 1;
    }

    private readonly record struct Note(
        int Pitch,
        int Scale,
        int DurationCode,
        int DurationSpecCode,
        int StyleCode,
        int Tempo);

    private readonly record struct ParsedRtttlDefaults(
        string Title,
        int DefaultDuration,
        int DefaultOctave,
        int Tempo,
        int StyleCode);

    private sealed class BitWriter
    {
        private byte[] buffer;

        public BitWriter(int capacity)
        {
            buffer = new byte[capacity];
        }

        public int Position { get; set; }

        public void Write(byte value, int bits)
        {
            EnsureCapacity(bits);
            for (int bit = 0; bit < bits; bit++)
            {
                int mask = 1 << (7 - bit);
                int byteIndex = Position / 8;
                int destinationMask = 1 << (7 - Position % 8);
                if ((value & mask) != 0)
                {
                    buffer[byteIndex] |= (byte)destinationMask;
                }
                else
                {
                    buffer[byteIndex] &= unchecked((byte)~destinationMask);
                }

                Position++;
            }
        }

        public void Skip(int bits)
        {
            EnsureCapacity(bits);
            Position += bits;
        }

        public void Align()
        {
            int alignedPosition = (Position + 7) & ~7;
            EnsureCapacity(alignedPosition - Position);
            Position = alignedPosition;
        }

        public byte[] ToArray() => buffer[..(Position / 8)];

        private void EnsureCapacity(int additionalBits)
        {
            int requiredBytes = (Position + additionalBits + 7) / 8;
            if (requiredBytes <= buffer.Length)
            {
                return;
            }

            Array.Resize(ref buffer, Math.Max(requiredBytes, buffer.Length * 2));
        }
    }
}
