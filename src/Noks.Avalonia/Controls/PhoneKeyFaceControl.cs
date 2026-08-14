using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering;
using Noks.Application.Input;

namespace Noks.AvaloniaApp.Controls;

internal sealed class PhoneKeyFaceControl : Control, ICustomHitTest
{
    private const double BoundsPadding = 3;
    private const double PressedOffset = 1.5;

    private readonly Geometry outline;
    private readonly IBrush outlineBrush;
    private readonly double outlineOpacity;
    private readonly IBrush pressedOverlayBrush;
    private readonly IPen pressedEdgePen;
    private readonly List<GeometryLayer> bodyLayers = [];
    private readonly List<LegendLayer> pathLegends = [];
    private readonly List<TextLegend> textLegends = [];
    private readonly TranslateTransform pressedTransform = new(0, PressedOffset);
    private bool backlightOn;
    private bool pressed;

    internal PhoneKeyFaceControl(
        PhoneKey key,
        Geometry outline,
        IBrush outlineBrush,
        double outlineOpacity,
        IBrush pressedOverlayBrush,
        IPen pressedEdgePen,
        bool showPressedOverlay = true)
    {
        Key = key;
        this.outline = outline;
        this.outlineBrush = outlineBrush;
        this.outlineOpacity = outlineOpacity;
        this.pressedOverlayBrush = pressedOverlayBrush;
        this.pressedEdgePen = pressedEdgePen;
        ShowPressedOverlay = showPressedOverlay;

        PhoneBounds = outline.Bounds.Inflate(BoundsPadding);
        Width = PhoneBounds.Width;
        Height = PhoneBounds.Height + PressedOffset;
        MinWidth = 0;
        MinHeight = 0;
        Margin = new Thickness(0);
        Focusable = false;
        ClipToBounds = true;
        Tag = key;
    }

    internal PhoneKey Key { get; }

    internal Rect PhoneBounds { get; }

    private bool ShowPressedOverlay { get; }

    internal void AddBodyLayer(Geometry geometry, IBrush brush, double opacity = 1)
    {
        bodyLayers.Add(new GeometryLayer(geometry, brush, opacity));
    }

    internal void AddPathLegend(Geometry geometry, IBrush offBrush, IBrush onBrush)
    {
        pathLegends.Add(new LegendLayer(geometry, offBrush, onBrush));
    }

    internal void AddTextLegend(
        string text,
        double phoneX,
        double phoneY,
        FontFamily fontFamily,
        double fontSize,
        FontWeight fontWeight,
        IBrush offBrush,
        IBrush onBrush,
        double letterSpacing = 0)
    {
        Typeface typeface = new(fontFamily, FontStyle.Normal, fontWeight);
        textLegends.Add(new TextLegend(
            new TextLayout(text, typeface, fontSize, offBrush, letterSpacing: letterSpacing),
            new TextLayout(text, typeface, fontSize, onBrush, letterSpacing: letterSpacing),
            new Point(phoneX, phoneY)));
    }

    internal void SetBacklight(bool value)
    {
        if (backlightOn == value)
        {
            return;
        }

        backlightOn = value;
        InvalidateVisual();
    }

    internal void SetPressed(bool value)
    {
        if (pressed == value)
        {
            return;
        }

        pressed = value;
        RenderTransform = value ? pressedTransform : null;
        InvalidateVisual();
    }

    public bool HitTest(Point point)
    {
        Point phonePoint = new(point.X + PhoneBounds.X, point.Y + PhoneBounds.Y);
        return outline.FillContains(phonePoint);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        using (context.PushTransform(Matrix.CreateTranslation(-PhoneBounds.X, -PhoneBounds.Y)))
        {
            using (context.PushOpacity(backlightOn ? 1 : 0.62))
            {
                DrawGeometry(context, outline, outlineBrush, outlineOpacity);
                foreach (GeometryLayer layer in bodyLayers)
                {
                    DrawGeometry(context, layer.Geometry, layer.Brush, layer.Opacity);
                }

                if (pressed && ShowPressedOverlay)
                {
                    using (context.PushGeometryClip(outline))
                    {
                        context.DrawGeometry(pressedOverlayBrush, pressedEdgePen, outline);
                    }
                }
            }

            foreach (LegendLayer legend in pathLegends)
            {
                context.DrawGeometry(backlightOn ? legend.OnBrush : legend.OffBrush, null, legend.Geometry);
            }

            foreach (TextLegend legend in textLegends)
            {
                (backlightOn ? legend.OnLayout : legend.OffLayout).Draw(context, legend.PhonePosition);
            }
        }
    }

    private static void DrawGeometry(
        DrawingContext context,
        Geometry geometry,
        IBrush brush,
        double opacity)
    {
        if (opacity >= 1)
        {
            context.DrawGeometry(brush, null, geometry);
            return;
        }

        using (context.PushOpacity(opacity))
        {
            context.DrawGeometry(brush, null, geometry);
        }
    }

    private sealed record GeometryLayer(Geometry Geometry, IBrush Brush, double Opacity);

    private sealed record LegendLayer(Geometry Geometry, IBrush OffBrush, IBrush OnBrush);

    private sealed record TextLegend(TextLayout OffLayout, TextLayout OnLayout, Point PhonePosition);
}
