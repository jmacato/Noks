#if BROWSER
using Noks.AvaloniaApp.Browser;
#endif
namespace Noks.AvaloniaApp.Audio;

public static class PhoneAudio
{
    public static IPhoneAudio? Create()
    {
#if BROWSER
        return new BrowserBuzzerAudio();
#else
        if (OperatingSystem.IsMacOS())
        {
            return new BuzzerAudio();
        }

        return null;
#endif
    }
}
