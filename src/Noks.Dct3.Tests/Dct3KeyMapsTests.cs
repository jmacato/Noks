using Noks.Dct3.Input;
using Noks.Dct3.State;
namespace Noks.Dct3.Tests;

public sealed class Dct3KeyMapsTests
{
    [Theory]
    [InlineData(Dct3KeyMap.Nokia3310, Dct3Key.Digit1, 1, 4, false)]
    [InlineData(Dct3KeyMap.Nokia3310, Dct3Key.Digit2, 1, 3, false)]
    [InlineData(Dct3KeyMap.Nokia3310, Dct3Key.Digit3, 4, 1, false)]
    [InlineData(Dct3KeyMap.Nokia3310, Dct3Key.Star, 4, 4, false)]
    [InlineData(Dct3KeyMap.Nokia3310, Dct3Key.Hash, 4, 2, false)]
    [InlineData(Dct3KeyMap.Nokia3310, Dct3Key.Main, 4, 3, false)]
    [InlineData(Dct3KeyMap.Nokia3310, Dct3Key.Clear, 0, 4, false)]
    [InlineData(Dct3KeyMap.Nokia3310, Dct3Key.Power, 0, 0, true)]
    public void KnownKeysResolveToExpectedMatrixPositions(
        Dct3KeyMap keyMap,
        Dct3Key key,
        int column,
        int row,
        bool power)
    {
        Dct3KeyBinding binding = Dct3KeyMaps.GetBinding(key, keyMap);

        Assert.Equal(column, binding.Column);
        Assert.Equal(row, binding.Row);
        Assert.Equal(power, binding.Power);
    }

    [Theory]
    [InlineData("navi", Dct3Key.Main)]
    [InlineData("ok", Dct3Key.Main)]
    [InlineData("previous", Dct3Key.Up)]
    [InlineData("next", Dct3Key.Down)]
    [InlineData("clear", Dct3Key.Clear)]
    [InlineData("#", Dct3Key.Hash)]
    public void ParsesSharedKeyAliases(string name, Dct3Key expected)
    {
        Assert.True(Dct3KeyMaps.TryParseKey(name, out Dct3Key key));
        Assert.Equal(expected, key);
    }

    [Fact]
    public void Explicit3310KeyMapIsPreserved()
    {
        Dct3PhoneSettings settings = new(KeyMap: Dct3KeyMap.Nokia3310);

        Assert.Equal(Dct3KeyMap.Nokia3310, Dct3KeyMaps.Resolve(ReadOnlySpan<byte>.Empty, settings));
    }
}
