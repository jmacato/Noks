using Avalonia;
using Avalonia.Media;
using Avalonia.Skia;
using Noks.AvaloniaApp;
using Noks.AvaloniaApp.Controls;
using Noks.Application.Input;

namespace Noks.Application.Tests;

public sealed class PhoneFaceGeometryTests
{
    static PhoneFaceGeometryTests()
    {
        SkiaPlatform.Initialize();
    }

    [Theory]
    [InlineData(PhoneKey.Digit1, 80, 657, 126, 630)]
    [InlineData(PhoneKey.Digit3, 319, 657, 272, 630)]
    [InlineData(PhoneKey.Main, 200, 530, 115, 505)]
    [InlineData(PhoneKey.Cancel, 90, 550, 137, 524)]
    [InlineData(PhoneKey.Left, 323, 547, 260, 596)]
    [InlineData(PhoneKey.Right, 260, 596, 323, 547)]
    internal void Path_geometry_limits_the_hit_region(
        PhoneKey key,
        double insideX,
        double insideY,
        double outsideX,
        double outsideY)
    {
        Geometry geometry = PhoneFaceGeometry.Create(key);

        Assert.True(geometry.FillContains(new Point(insideX, insideY)));
        Assert.False(geometry.FillContains(new Point(outsideX, outsideY)));
    }

    [Fact]
    public void All_key_paths_stay_inside_the_phone_face()
    {
        Rect phoneFace = new(0, 0, 402, 958);

        foreach (PhoneKey key in Enum.GetValues<PhoneKey>())
        {
            Rect bounds = PhoneFaceGeometry.Create(key).Bounds;

            Assert.True(bounds.Width > 0, $"{key} has no width.");
            Assert.True(bounds.Height > 0, $"{key} has no height.");
            Assert.True(phoneFace.Contains(bounds), $"{key} is outside the phone face: {bounds}.");
        }
    }

    [Theory]
    [InlineData(PhoneKey.Digit1, 80, 657, 126, 630)]
    [InlineData(PhoneKey.Digit3, 319, 657, 272, 630)]
    [InlineData(PhoneKey.Main, 200, 530, 115, 505)]
    [InlineData(PhoneKey.Cancel, 90, 550, 137, 524)]
    [InlineData(PhoneKey.Left, 323, 547, 260, 596)]
    [InlineData(PhoneKey.Right, 260, 596, 323, 547)]
    internal void Key_control_uses_the_original_path_for_local_hit_testing(
        PhoneKey key,
        double insideX,
        double insideY,
        double outsideX,
        double outsideY)
    {
        PhoneKeyFaceControl control = CreateKeyControl(key);

        Assert.True(control.HitTest(ToLocal(control, insideX, insideY)));
        Assert.False(control.HitTest(ToLocal(control, outsideX, outsideY)));
    }

    [Fact]
    public void Key_controls_use_tight_bounds_instead_of_phone_sized_bounds()
    {
        const double phoneArea = 402 * 958;

        foreach (PhoneKey key in Enum.GetValues<PhoneKey>())
        {
            PhoneKeyFaceControl control = CreateKeyControl(key);
            double controlArea = control.Width * control.Height;

            Assert.True(controlArea < phoneArea * 0.08, $"{key} uses oversized bounds: {control.PhoneBounds}.");
        }
    }

    private static PhoneKeyFaceControl CreateKeyControl(PhoneKey key)
        => new(
            key,
            PhoneFaceGeometry.Create(key),
            Brushes.Black,
            1,
            Brushes.Transparent,
            new Pen(Brushes.Transparent, 2));

    private static Point ToLocal(PhoneKeyFaceControl control, double phoneX, double phoneY)
        => new(phoneX - control.PhoneBounds.X, phoneY - control.PhoneBounds.Y);
}
