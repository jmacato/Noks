using Noks.Dct3.Messaging;
namespace Noks.Application;

internal readonly record struct WakuRememberedEvent(
    Guid EventId,
    long ExpiresAtUnixMilliseconds);
