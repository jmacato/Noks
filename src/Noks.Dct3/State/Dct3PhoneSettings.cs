using Noks.Dct3.Input;
namespace Noks.Dct3.State;

public sealed record Dct3PhoneSettings(
    string? SimImsi = null,
    string NetworkName = Dct3PhoneSettings.DefaultNetworkName,
    Dct3KeyMap? KeyMap = null,
    string OwnPhoneNumber = Dct3PhoneSettings.DefaultOwnPhoneNumber)
{
    public const string DefaultNetworkName = "";
    public const string DefaultOwnPhoneNumber = "000000000000000";

    public static Dct3PhoneSettings Default { get; } = new();

    public string EffectiveNetworkName =>
        string.IsNullOrWhiteSpace(NetworkName) ? DefaultNetworkName : NetworkName.Trim();

    public string EffectiveOwnPhoneNumber =>
        string.IsNullOrWhiteSpace(OwnPhoneNumber) ? DefaultOwnPhoneNumber : OwnPhoneNumber.Trim();
}
