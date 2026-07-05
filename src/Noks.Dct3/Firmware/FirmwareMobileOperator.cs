namespace Noks.Dct3.Firmware;

public sealed record FirmwareMobileOperator(string CountryTag, string Mcc, string Mnc, string Name)
{
    public string Plmn => Mcc + Mnc;
}
