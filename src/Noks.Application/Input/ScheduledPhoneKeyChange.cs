using Noks.Dct3.Messaging;
namespace Noks.Application.Input;

public readonly record struct ScheduledPhoneKeyChange(long Step, PhoneKey Key, bool Pressed);
