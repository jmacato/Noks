using System;
using Noks.Dct3.State;

namespace Noks.Dct3.Input;

public static class Dct3KeyMaps
{
    public static Dct3KeyMap Resolve(ReadOnlySpan<byte> flash, Dct3PhoneSettings settings)
    {
        if (settings.KeyMap is { } keyMap)
        {
            return keyMap;
        }

        return Dct3KeyMap.Nokia3310;
    }

    public static bool TryParseMap(string value, out Dct3KeyMap keyMap)
    {
        keyMap = value.Trim().ToLowerInvariant() switch
        {
            "3310" or "nokia3310" or "default" => Dct3KeyMap.Nokia3310,
            _ => (Dct3KeyMap)(-1),
        };

        return keyMap is Dct3KeyMap.Nokia3310;
    }

    public static string Format(Dct3KeyMap keyMap) => keyMap switch
    {
        Dct3KeyMap.Nokia3310 => "3310",
        _ => "unknown",
    };

    public static bool TryParseKey(string name, out Dct3Key key)
    {
        key = name.Trim().ToLowerInvariant() switch
        {
            "0" or "digit0" => Dct3Key.Digit0,
            "1" or "digit1" => Dct3Key.Digit1,
            "2" or "digit2" => Dct3Key.Digit2,
            "3" or "digit3" => Dct3Key.Digit3,
            "4" or "digit4" => Dct3Key.Digit4,
            "5" or "digit5" => Dct3Key.Digit5,
            "6" or "digit6" => Dct3Key.Digit6,
            "7" or "digit7" => Dct3Key.Digit7,
            "8" or "digit8" => Dct3Key.Digit8,
            "9" or "digit9" => Dct3Key.Digit9,
            "*" or "star" or "asterisk" => Dct3Key.Star,
            "#" or "hash" or "pound" => Dct3Key.Hash,
            "up" or "left" or "prev" or "previous" or "softleft" or "b" => Dct3Key.Up,
            "down" or "right" or "next" or "softright" or "y" => Dct3Key.Down,
            "menu" or "navi" or "action1" or "enter" or "ok" => Dct3Key.Main,
            "c" or "clear" or "cancel" or "back" or "del" => Dct3Key.Clear,
            "power" => Dct3Key.Power,
            _ => (Dct3Key)(-1),
        };

        return key is >= Dct3Key.Power and <= Dct3Key.Clear;
    }

    public static Dct3KeyBinding GetBinding(Dct3Key key, Dct3KeyMap keyMap) =>
        TryGetBinding(key, keyMap, out Dct3KeyBinding binding)
            ? binding
            : throw new ArgumentOutOfRangeException(nameof(key));

    public static bool TryGetBinding(Dct3Key key, Dct3KeyMap keyMap, out Dct3KeyBinding binding)
    {
        binding = Nokia3310Binding(key);

        return key is >= Dct3Key.Power and <= Dct3Key.Clear;
    }

    private static Dct3KeyBinding Nokia3310Binding(Dct3Key key) => key switch
    {
        Dct3Key.Power => new(0, 0, true),
        Dct3Key.Digit0 => new(0, 2, false),
        Dct3Key.Digit1 => new(1, 4, false),
        Dct3Key.Digit2 => new(1, 3, false),
        Dct3Key.Digit3 => new(4, 1, false),
        Dct3Key.Digit4 => new(2, 4, false),
        Dct3Key.Digit5 => new(2, 3, false),
        Dct3Key.Digit6 => new(2, 2, false),
        Dct3Key.Digit7 => new(3, 4, false),
        Dct3Key.Digit8 => new(3, 3, false),
        Dct3Key.Digit9 => new(3, 2, false),
        Dct3Key.Star => new(4, 4, false),
        Dct3Key.Hash => new(4, 2, false),
        Dct3Key.Up => new(0, 1, false),
        Dct3Key.Down => new(1, 1, false),
        Dct3Key.Main => new(4, 3, false),
        Dct3Key.Clear => new(0, 4, false),
        _ => default,
    };

}
