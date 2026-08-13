using Noks.Dct3.Messaging;
namespace Noks.Application.Input;

public sealed class PhoneInputState
{
    private readonly Dictionary<long, PhoneKey> pointerKeys = [];
    private readonly Dictionary<int, PhoneKey> keyboardKeys = [];
    private readonly Dictionary<PhoneKey, int> activeSourceCounts = [];

    public IEnumerable<PhoneKey> ActiveKeys => activeSourceCounts.Keys;

    public PressChange PressPointer(long pointerId, PhoneKey key)
        => PressSource(pointerKeys, pointerId, key);

    public ReleaseChange ReleasePointer(long pointerId)
        => ReleaseSource(pointerKeys, pointerId);

    public bool TryGetPointerKey(long pointerId, out PhoneKey key)
        => pointerKeys.TryGetValue(pointerId, out key);

    public PressChange PressKeyboard(int sourceKey, PhoneKey key)
        => PressSource(keyboardKeys, sourceKey, key);

    public ReleaseChange ReleaseKeyboard(int sourceKey)
        => ReleaseSource(keyboardKeys, sourceKey);

    public bool IsActive(PhoneKey key)
        => activeSourceCounts.ContainsKey(key);

    public void Clear()
    {
        pointerKeys.Clear();
        keyboardKeys.Clear();
        activeSourceCounts.Clear();
    }

    private PressChange PressSource<TSource>(
        Dictionary<TSource, PhoneKey> sources,
        TSource source,
        PhoneKey key)
        where TSource : notnull
    {
        PhoneKey? previousKey = null;
        bool previousKeyBecameInactive = false;
        if (sources.TryGetValue(source, out PhoneKey existingKey))
        {
            if (existingKey == key)
            {
                return new PressChange(
                    SourceChanged: false,
                    PreviousKey: null,
                    PreviousKeyBecameInactive: false,
                    KeyBecameActive: false);
            }

            previousKey = existingKey;
            previousKeyBecameInactive = RemoveSource(existingKey);
        }

        sources[source] = key;
        bool keyBecameActive = AddSource(key);
        return new PressChange(
            SourceChanged: true,
            previousKey,
            previousKeyBecameInactive,
            keyBecameActive);
    }

    private ReleaseChange ReleaseSource<TSource>(
        Dictionary<TSource, PhoneKey> sources,
        TSource source)
        where TSource : notnull
    {
        if (!sources.Remove(source, out PhoneKey key))
        {
            return new ReleaseChange(Found: false, default, KeyBecameInactive: false);
        }

        return new ReleaseChange(
            Found: true,
            key,
            KeyBecameInactive: RemoveSource(key));
    }

    private bool AddSource(PhoneKey key)
    {
        activeSourceCounts.TryGetValue(key, out int count);
        activeSourceCounts[key] = count + 1;
        return count == 0;
    }

    private bool RemoveSource(PhoneKey key)
    {
        int count = activeSourceCounts[key] - 1;
        if (count > 0)
        {
            activeSourceCounts[key] = count;
            return false;
        }

        activeSourceCounts.Remove(key);
        return true;
    }

    public readonly record struct PressChange(
        bool SourceChanged,
        PhoneKey? PreviousKey,
        bool PreviousKeyBecameInactive,
        bool KeyBecameActive);

    public readonly record struct ReleaseChange(
        bool Found,
        PhoneKey Key,
        bool KeyBecameInactive);
}
