using Noks.Dct3.Input;
using Noks.Dct3.State;

namespace Noks.Application.Persistence;

public static class PhoneSettingsParser
{
    public static bool HasExplicitNetworkIdentity(IReadOnlyList<string> args) =>
        TryGetSetting(args, "sim-imsi") is not null ||
        TryGetSetting(args, "network-name") is not null ||
        TryGetSetting(args, "gsm-network-name") is not null;

    public static bool IsAutomaticCountrySelectionDisabled(IReadOnlyList<string> args) =>
        HasFlag(args, "no-ip-operator");

    public static Dct3PhoneSettings Parse(IReadOnlyList<string> args)
    {
        string? simImsi = TryGetSetting(args, "sim-imsi");
        string? networkName =
            TryGetSetting(args, "network-name") ??
            TryGetSetting(args, "gsm-network-name");
        Dct3KeyMap? keyMap = null;
        string? keyMapValue = TryGetSetting(args, "keymap");
        if (keyMapValue is not null)
        {
            if (!Dct3KeyMaps.TryParseMap(keyMapValue, out Dct3KeyMap parsedKeyMap))
            {
                throw new ArgumentException("The --keymap value is invalid. Use 3310.");
            }

            keyMap = parsedKeyMap;
        }

        if (simImsi is not null && (simImsi.Length != 15 || simImsi.Any(ch => ch < '0' || ch > '9')))
        {
            simImsi = null;
        }

        return new Dct3PhoneSettings(
            simImsi,
            networkName ?? Dct3PhoneSettings.DefaultNetworkName,
            keyMap);
    }

    private static bool HasFlag(IReadOnlyList<string> args, string name)
    {
        string option = "--" + name;
        string truePrefix = option + "=true";
        string onePrefix = option + "=1";

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg.Equals(option, StringComparison.Ordinal) ||
                arg.Equals(truePrefix, StringComparison.OrdinalIgnoreCase) ||
                arg.Equals(onePrefix, StringComparison.Ordinal))
            {
                return true;
            }

            if (Uri.TryCreate(arg, UriKind.Absolute, out Uri? uri))
            {
                string? queryValue = TryGetQueryValue(uri.Query, name);
                if (queryValue is "" or "1" ||
                    string.Equals(queryValue, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? TryGetSetting(IReadOnlyList<string> args, string name)
    {
        string option = "--" + name;
        string prefix = option + "=";

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];

            if (arg.Equals(option, StringComparison.Ordinal) && i + 1 < args.Count)
            {
                return args[i + 1];
            }

            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                return arg[prefix.Length..];
            }

            if (Uri.TryCreate(arg, UriKind.Absolute, out Uri? uri))
            {
                string? queryValue = TryGetQueryValue(uri.Query, name);
                if (queryValue is not null)
                {
                    return queryValue;
                }
            }
        }

        return null;
    }

    private static string? TryGetQueryValue(string query, string name)
    {
        if (query.StartsWith("?", StringComparison.Ordinal))
        {
            query = query[1..];
        }

        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            if (!key.Equals(name, StringComparison.Ordinal))
            {
                continue;
            }

            return pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
        }

        return null;
    }
}
