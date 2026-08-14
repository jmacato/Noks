using Noks.Dct3.Audio;

namespace Noks.AvaloniaApp.Audio;

public interface IPhoneAudio : IDisposable
{
    bool SupportsAnnouncements => false;

    void Update(Dct3AudioState state);

    void PlayAnnouncement(CallAudioAnnouncement announcement)
    {
    }

    void StopAnnouncement(Guid callId)
    {
    }

    void SetAnnouncementEndedHandler(Action<Guid>? handler)
    {
    }
}
