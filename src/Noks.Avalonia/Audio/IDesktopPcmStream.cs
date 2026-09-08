#if !BROWSER
namespace Noks.AvaloniaApp.Audio;

internal interface IDesktopPcmStream : IDisposable
{
    Exception? Failure { get; }
}
#endif
