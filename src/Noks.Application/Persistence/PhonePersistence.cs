using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noks.Dct3.Input;
using Noks.Dct3.State;

namespace Noks.Application.Persistence;

public static class PhonePersistence
{
    public static string CreateProfileKey(string stableContactId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableContactId);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(stableContactId));
        return $"profile-v1-{Convert.ToHexString(hash, 0, 16)}";
    }

    public static string CreateKey(ReadOnlySpan<byte> firmware) =>
        CreateKey(firmware, Dct3PhoneSettings.Default);

    public static string CreateKey(ReadOnlySpan<byte> firmware, Dct3PhoneSettings settings)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(firmware, hash);
        string sim = settings.SimImsi ?? "default";
        string network = settings.EffectiveNetworkName;
        Dct3KeyMap keyMap = Dct3KeyMaps.Resolve(firmware, settings);
        string keyMapProfile = settings.KeyMap is not null || keyMap != Dct3KeyMap.Nokia3310
            ? $"\nkeymap={Dct3KeyMaps.Format(keyMap)}"
            : "";
        byte[] profileHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sim}\n{network}{keyMapProfile}"));
        return $"{firmware.Length:X8}-{Convert.ToHexString(hash)}-{Convert.ToHexString(profileHash, 0, 8)}";
    }

    public static string Serialize(Dct3PersistenceSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, PhonePersistenceJsonContext.Default.Dct3PersistenceSnapshot);

    public static Dct3PersistenceSnapshot? Deserialize(string text)
    {
        try
        {
            Dct3PersistenceSnapshot? snapshot = JsonSerializer.Deserialize(text, PhonePersistenceJsonContext.Default.Dct3PersistenceSnapshot);
            return snapshot?.Version == Dct3PersistenceSnapshot.CurrentVersion ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
