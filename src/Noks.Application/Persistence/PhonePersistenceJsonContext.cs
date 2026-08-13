using System.Text.Json.Serialization;
using Noks.Dct3.State;

namespace Noks.Application.Persistence;

[JsonSerializable(typeof(Dct3PersistenceSnapshot))]
public sealed partial class PhonePersistenceJsonContext : JsonSerializerContext;
