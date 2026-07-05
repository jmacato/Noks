namespace Noks.Dct3.Audio;

public sealed record CallAudioAnnouncement(
    Guid CallId,
    CallAudioAnnouncementKind Kind,
    string Text);
