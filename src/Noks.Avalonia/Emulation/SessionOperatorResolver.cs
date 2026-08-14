using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Noks.Dct3.Firmware;
using Noks.Dct3.State;
using Noks.Application.Persistence;

namespace Noks.AvaloniaApp.Emulation;

internal static class SessionOperatorResolver
{
    // ISO 3166-1 alpha-2 to MCC. Only MCCs present in the Nokia firmware table are useful here.
    private const string CountryMccPairs =
        "GR=202;NL=204;BE=206;FR=208;MC=212;AD=213;ES=214;HU=216;BA=218;HR=219;RS=220;IT=222;" +
        "RO=226;CH=228;CZ=230;SK=231;AT=232;GB=234,235;DK=238;SE=240;NO=242;FI=244;LT=246;LV=247;" +
        "EE=248;RU=250;UA=255;BY=257;MD=259;PL=260;DE=262;GI=266;PT=268;LU=270;IE=272;IS=274;" +
        "AL=276;MT=278;CY=280;GE=282;AM=283;BG=284;TR=286;FO=288;GL=290;SM=292;SI=293;MK=294;LI=295;" +
        "GP=340;MQ=340;BL=340;MF=340;AZ=400;KZ=401;IN=404;LK=413;LB=415;JO=416;SY=417;KW=419;" +
        "SA=420;OM=422;AE=424;IL=425;PS=425;BH=426;QA=427;UZ=434;KG=437;VN=452;HK=454;MO=455;" +
        "KH=456;LA=457;CN=460;TW=466;BD=470;MV=472;MY=502;AU=505;ID=510;PH=515;TH=520;SG=525;" +
        "BN=528;NZ=530;FJ=542;NC=546;PF=547;EG=602;DZ=603;MA=604;TN=605;SN=608;GN=611;CI=612;" +
        "TG=615;MU=617;LR=618;GH=620;NG=621;CM=624;CV=625;GA=628;SC=633;SD=634;RW=635;ET=636;" +
        "SO=637;KE=639;TZ=640;UG=641;BI=642;MZ=643;RE=647;ZW=648;MW=650;LS=651;BW=652;SZ=653;" +
        "ZA=655;VE=734;SR=746";

    public static async Task<Dct3PhoneSettings> ResolveAsync(
        byte[] firmware,
        IReadOnlyList<string> args,
        Dct3PhoneSettings settings,
        Func<string?> localeCountryProvider,
        CancellationToken cancellationToken = default)
    {
        if (PhoneSettingsParser.HasExplicitNetworkIdentity(args) ||
            PhoneSettingsParser.IsAutomaticCountrySelectionDisabled(args))
        {
            return settings;
        }

        Uri? countryLookupUri = ResolveCountryLookupUri(args);
        string? country = countryLookupUri is null
            ? null
            : await TryLookupCountryAsync(countryLookupUri, cancellationToken);
        string source = "public IP";
        if (country is null)
        {
            country = await TryLookupCountryFromFreeGeoipAsync(cancellationToken);
            source = "free geoip";
        }

        if (country is null)
        {
            try
            {
                country = NormalizeCountry(localeCountryProvider());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Noks operator locale fallback unavailable: {ex.GetType().Name}");
            }

            source = "locale fallback";
        }

        if (country is null)
        {
            Console.WriteLine("Noks operator auto-selection: The country is unavailable. The firmware default is active.");
            return settings;
        }

        FirmwareMobileOperator? selected = SelectOperator(firmware, country);
        if (selected is null)
        {
            Console.WriteLine($"Noks operator auto-selection: country={country} source={source}. No firmware PLMN matches.");
            return settings;
        }

        string imsi = CreateImsi(selected.Plmn);
        Console.WriteLine(
            $"Noks operator auto-selection: country={country} source={source} plmn={selected.Mcc}-{selected.Mnc} name=\"{selected.Name}\"");
        return settings with { SimImsi = imsi, NetworkName = Dct3PhoneSettings.DefaultNetworkName };
    }

    internal static FirmwareMobileOperator? SelectOperator(ReadOnlySpan<byte> firmware, string country)
    {
        string[] mccs = GetMccs(country);
        if (mccs.Length == 0)
        {
            return null;
        }

        FirmwareMobileOperator[] candidates = FirmwareOperatorDatabase
            .Parse(firmware)
            .Where(candidate => mccs.Contains(candidate.Mcc, StringComparer.Ordinal))
            .ToArray();
        return candidates.Length == 0 ? null : candidates[RandomNumberGenerator.GetInt32(candidates.Length)];
    }

    internal static string? GetLocaleCountry()
    {
        try
        {
            string cultureName = CultureInfo.CurrentCulture.Name;
            return cultureName.Length == 0 ? null : NormalizeCountry(new RegionInfo(cultureName).TwoLetterISORegionName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Uri? ResolveCountryLookupUri(IReadOnlyList<string> args)
    {
        foreach (string argument in args)
        {
            if (Uri.TryCreate(argument, UriKind.Absolute, out Uri? pageUri) &&
                pageUri.Scheme is "http" or "https")
            {
                return new Uri(pageUri, "/api/session-region");
            }
        }
        return null;
    }

    private static async Task<string?> TryLookupCountryAsync(
        Uri countryLookupUri,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            using HttpClient client = new();
            using HttpResponseMessage response = await client.GetAsync(countryLookupUri, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using Stream content = await response.Content.ReadAsStreamAsync(timeout.Token);
            using JsonDocument json = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);
            return json.RootElement.TryGetProperty("country", out JsonElement countryElement)
                ? NormalizeCountry(countryElement.GetString())
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.WriteLine($"Noks operator IP lookup unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    private static readonly Uri FreeGeoipLookupUri = new("https://ipwho.is/?fields=success,country_code");

    private static async Task<string?> TryLookupCountryFromFreeGeoipAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            using HttpClient client = new();
            using HttpResponseMessage response = await client.GetAsync(FreeGeoipLookupUri, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using Stream content = await response.Content.ReadAsStreamAsync(timeout.Token);
            using JsonDocument json = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);
            JsonElement root = json.RootElement;
            bool success = root.TryGetProperty("success", out JsonElement successElement) &&
                successElement.ValueKind == JsonValueKind.True;
            return success && root.TryGetProperty("country_code", out JsonElement countryElement)
                ? NormalizeCountry(countryElement.GetString())
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.WriteLine($"Noks operator free geoip lookup unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    private static string CreateImsi(string plmn) => plmn + "0000000001";

    private static string[] GetMccs(string country)
    {
        string prefix = NormalizeCountry(country) + "=";
        foreach (string pair in CountryMccPairs.Split(';'))
        {
            if (pair.StartsWith(prefix, StringComparison.Ordinal))
            {
                return pair[prefix.Length..].Split(',');
            }
        }

        return [];
    }

    private static string? NormalizeCountry(string? country)
    {
        country = country?.Trim().ToUpperInvariant();
        return country is { Length: 2 } && country.All(ch => ch is >= 'A' and <= 'Z') ? country : null;
    }
}
