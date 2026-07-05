using Noks.Dct3.Messaging;
namespace Noks.Dct3.Radio;

internal readonly record struct NitzClockDateTime(
    int Year,
    int Month,
    int Day,
    int Hour,
    int Minute,
    int Second);
