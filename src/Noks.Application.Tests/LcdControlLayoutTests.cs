using Avalonia;
using Noks.AvaloniaApp;
using Noks.Dct3.Display;
using Noks.AvaloniaApp.Controls;

namespace Noks.Application.Tests;

public sealed class LcdControlLayoutTests
{
    [Fact]
    public void Reference_canvas_uses_the_3310_pixel_aspect_ratio()
    {
        Size bounds = LcdControl.FitCorrectedPixelBounds(new Size(252, 166));

        Assert.Equal(252, bounds.Width, 10);
        Assert.Equal(166, bounds.Height, 10);
        Assert.Equal(
            LcdControl.PhysicalPixelHeightToWidth,
            (bounds.Height / Pcd8544.Height) / (bounds.Width / Pcd8544.Width),
            10);
    }

    [Fact]
    public void Corrected_lcd_fits_inside_the_phone_window()
    {
        Size available = new(269, 183);
        Size bounds = LcdControl.FitCorrectedPixelBounds(available);

        Assert.True(bounds.Width <= available.Width);
        Assert.True(bounds.Height <= available.Height);
        Assert.Equal(LcdControl.CorrectedDisplayAspectRatio, bounds.Width / bounds.Height, 10);
    }
}
