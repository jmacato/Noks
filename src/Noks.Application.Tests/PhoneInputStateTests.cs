using Noks.AvaloniaApp;
using Noks.Application.Input;

namespace Noks.Application.Tests;

public sealed class PhoneInputStateTests
{
    [Fact]
    public void Separate_pointers_hold_separate_keys()
    {
        PhoneInputState state = new();

        PhoneInputState.PressChange first = state.PressPointer(10, PhoneKey.Digit1);
        PhoneInputState.PressChange second = state.PressPointer(11, PhoneKey.Digit2);

        Assert.True(first.KeyBecameActive);
        Assert.True(second.KeyBecameActive);
        Assert.True(state.IsActive(PhoneKey.Digit1));
        Assert.True(state.IsActive(PhoneKey.Digit2));

        Assert.True(state.ReleasePointer(10).KeyBecameInactive);
        Assert.False(state.IsActive(PhoneKey.Digit1));
        Assert.True(state.IsActive(PhoneKey.Digit2));
    }

    [Fact]
    public void Key_stays_down_until_its_last_pointer_is_released()
    {
        PhoneInputState state = new();

        Assert.True(state.PressPointer(20, PhoneKey.Main).KeyBecameActive);
        Assert.False(state.PressPointer(21, PhoneKey.Main).KeyBecameActive);
        Assert.False(state.ReleasePointer(20).KeyBecameInactive);
        Assert.True(state.IsActive(PhoneKey.Main));
        Assert.True(state.ReleasePointer(21).KeyBecameInactive);
        Assert.False(state.IsActive(PhoneKey.Main));
    }

    [Fact]
    public void Pointer_and_keyboard_sources_do_not_release_each_other()
    {
        PhoneInputState state = new();

        state.PressPointer(30, PhoneKey.Digit0);
        state.PressKeyboard(100, PhoneKey.Digit0);
        state.PressKeyboard(101, PhoneKey.Digit0);

        Assert.False(state.ReleasePointer(30).KeyBecameInactive);
        Assert.False(state.ReleaseKeyboard(100).KeyBecameInactive);
        Assert.True(state.IsActive(PhoneKey.Digit0));
        Assert.True(state.ReleaseKeyboard(101).KeyBecameInactive);
        Assert.False(state.IsActive(PhoneKey.Digit0));
    }

    [Fact]
    public void Duplicate_press_from_one_source_is_idempotent()
    {
        PhoneInputState state = new();

        Assert.True(state.PressPointer(40, PhoneKey.Cancel).SourceChanged);
        PhoneInputState.PressChange duplicate = state.PressPointer(40, PhoneKey.Cancel);

        Assert.False(duplicate.SourceChanged);
        Assert.False(duplicate.KeyBecameActive);
        Assert.True(state.ReleasePointer(40).KeyBecameInactive);
    }

    [Fact]
    public void Reassigned_source_releases_only_its_previous_key()
    {
        PhoneInputState state = new();
        state.PressPointer(50, PhoneKey.Left);
        state.PressPointer(51, PhoneKey.Left);

        PhoneInputState.PressChange change = state.PressPointer(50, PhoneKey.Right);

        Assert.Equal(PhoneKey.Left, change.PreviousKey);
        Assert.False(change.PreviousKeyBecameInactive);
        Assert.True(change.KeyBecameActive);
        Assert.True(state.IsActive(PhoneKey.Left));
        Assert.True(state.IsActive(PhoneKey.Right));
    }

    [Fact]
    public void Keyboard_release_uses_the_original_physical_source_mapping()
    {
        PhoneInputState state = new();
        state.PressKeyboard(80, PhoneKey.Star);

        PhoneInputState.ReleaseChange release = state.ReleaseKeyboard(80);

        Assert.True(release.Found);
        Assert.Equal(PhoneKey.Star, release.Key);
        Assert.True(release.KeyBecameInactive);
    }

    [Fact]
    public void Keyboard_repeat_can_follow_a_modifier_mapping_change()
    {
        PhoneInputState state = new();
        state.PressKeyboard(80, PhoneKey.Star);

        PhoneInputState.PressChange change = state.PressKeyboard(80, PhoneKey.Digit8);

        Assert.Equal(PhoneKey.Star, change.PreviousKey);
        Assert.True(change.PreviousKeyBecameInactive);
        Assert.True(change.KeyBecameActive);
        Assert.False(state.IsActive(PhoneKey.Star));
        Assert.True(state.IsActive(PhoneKey.Digit8));
    }

    [Fact]
    public void Clear_removes_all_pointer_and_keyboard_owners()
    {
        PhoneInputState state = new();
        state.PressPointer(60, PhoneKey.Digit4);
        state.PressKeyboard(61, PhoneKey.Digit5);

        state.Clear();

        Assert.Empty(state.ActiveKeys);
        Assert.False(state.ReleasePointer(60).Found);
        Assert.False(state.ReleaseKeyboard(61).Found);
    }
}
