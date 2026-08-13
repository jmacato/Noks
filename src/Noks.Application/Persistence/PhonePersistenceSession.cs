using Noks.Dct3.State;

namespace Noks.Application.Persistence;

public sealed record PhonePersistenceSession(
    string Key,
    IPhonePersistenceStore Store,
    Dct3PersistenceSnapshot InitialSnapshot);
