using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noks.AvaloniaApp.Controls;

/// <summary>
/// Draws the static phone shell from generated 3310 SVG path data.
/// </summary>
internal sealed class PhoneShellControl : Control
{
    internal const double PhoneWidth = 402;
    internal const double PhoneHeight = 958;

    private static readonly Geometry PhoneBody = PhoneShellData.CreatePhoneBody();
    private static readonly Geometry OuterShellHighlight = PhoneShellData.CreateOuterShellHighlight();
    private static readonly Geometry OuterShellDark = PhoneShellData.CreateOuterShellDark();
    private static readonly Geometry OuterShellLight = PhoneShellData.CreateOuterShellLight();
    private static readonly Geometry UpperShellShade = PhoneShellData.CreateUpperShellShade();
    private static readonly Geometry UpperShellBase = PhoneShellData.CreateUpperShellBase();
    private static readonly Geometry UpperShellFace = PhoneShellData.CreateUpperShellFace();
    private static readonly Geometry KeypadBed = PhoneShellData.CreateKeypadBed();
    private static readonly Geometry KeypadCap = Transform(
        PhoneShellData.CreateKeypadCap(),
        new Matrix(1, 0, 0, 1, 35, 15));
    private static readonly Geometry NokiaN = LogoGeometry(PhoneShellData.CreateNokiaN());
    private static readonly Geometry NokiaO = LogoGeometry(PhoneShellData.CreateNokiaO());
    private static readonly Geometry NokiaKStem = LogoGeometry(PhoneShellData.CreateNokiaKStem());
    private static readonly Geometry NokiaKArm = LogoGeometry(PhoneShellData.CreateNokiaKArm());
    private static readonly Geometry NokiaI = LogoGeometry(PhoneShellData.CreateNokiaI());
    private static readonly Geometry NokiaA = LogoGeometry(PhoneShellData.CreateNokiaA());
    private static readonly Geometry SpeakerHousing = PhoneShellData.CreateSpeakerHousing();
    private static readonly Geometry LcdUpperOuterShadow = PhoneFaceGeometry.CreateLcdWindowShadow(0, -17);
    private static readonly Geometry LcdUpperInnerShadow = PhoneFaceGeometry.CreateLcdWindowShadow(0, -7);
    private static readonly Geometry LcdLeftHighlight = PhoneFaceGeometry.CreateLcdWindowShadow(-5, -3);
    private static readonly Geometry LcdRightShadow = PhoneFaceGeometry.CreateLcdWindowShadow(5, 0);
    private static readonly Geometry LcdLowerHighlight = PhoneFaceGeometry.CreateLcdWindowShadow(0, 5);

    private static readonly IBrush BodyBrush = Gradient("#626971", "#202c3a");
    private static readonly IBrush OuterHighlightBrush = Gradient(
        ("#8496a0", 0), ("#a0a9b1", 0.9068), ("#2b2d30", 1));
    private static readonly IBrush OuterDarkBrush = Gradient(
        ("#222527", 0), ("#364049", 0.96999), ("#5c353f48", 1));
    private static readonly IBrush OuterLightBrush = Gradient(
        ("#676e74", 0), ("#364049", 0.96999), ("#5c353f48", 1));
    private static readonly IBrush OuterSideLightBrush = Gradient(
        new RelativePoint(0.96724, 0.55644, RelativeUnit.Relative),
        new RelativePoint(0, 0.55644, RelativeUnit.Relative),
        ("#676e74", 0), ("#364049", 0.96999), ("#5c353f48", 1));
    private static readonly IBrush KeypadCapBrush = Gradient(
        new RelativePoint(-0.03437, 0.5, RelativeUnit.Relative),
        new RelativePoint(1, 0.5, RelativeUnit.Relative),
        ("#404952", 0), ("#57616b", 1));
    private static readonly IBrush LogoPlateBrush = Gradient(
        new RelativePoint(1, 0.5, RelativeUnit.Relative),
        new RelativePoint(0, 0.5, RelativeUnit.Relative),
        ("#3e4349", 0), ("#49515a", 1));
    private static readonly IBrush SpeakerHousingBrush = Gradient(
        new RelativePoint(1, 0.54137, RelativeUnit.Relative),
        new RelativePoint(0, 0.54137, RelativeUnit.Relative),
        ("#38414a", 0), ("#636b73", 1));
    private static readonly IBrush WhiteHighlightBrush = new SolidColorBrush(Color.Parse("#ffffff"));
    private static readonly IBrush UpperShadeBrush = new SolidColorBrush(Color.Parse("#3c3c37"));
    private static readonly IBrush UpperBaseBrush = new SolidColorBrush(Color.Parse("#b7b8b0"));
    private static readonly IBrush UpperFaceBrush = new SolidColorBrush(Color.Parse("#c7c8be"));
    private static readonly IBrush KeypadBedBrush = new SolidColorBrush(Color.Parse("#353331"));
    private static readonly IBrush LogoPlateBaseBrush = new SolidColorBrush(Color.Parse("#464d54"));
    private static readonly IBrush NokiaBrush = new SolidColorBrush(Color.Parse("#dadad2"));
    private static readonly IBrush SpeakerShadowBrush = new SolidColorBrush(Color.Parse("#15181a"));
    private static readonly IBrush SpeakerSlotBrush = new SolidColorBrush(Color.Parse("#272c30"));
    private static readonly IPen SpeakerSlotLightPen = new Pen(
        new SolidColorBrush(Color.Parse("#45ffffff")),
        1.5);
    private static readonly IPen SpeakerSlotDarkPen = new Pen(
        new SolidColorBrush(Color.Parse("#59000000")),
        1.5);
    private static readonly EllipseGeometry[] SpeakerSlots =
    [
        new(new Rect(191.074, 146, 15.852, 8)),
        new(new Rect(188.63, 119, 20.74, 8)),
        new(new Rect(188.63, 67, 20.74, 8)),
        new(new Rect(190.259, 41, 17.482, 8)),
        new(new Rect(187, 93, 24, 8)),
    ];

    private static readonly IEffect BlurOne = new BlurEffect { Radius = 1 };
    private static readonly IEffect BlurTwo = new BlurEffect { Radius = 2 };
    private static readonly IEffect BlurFivePointFive = new BlurEffect { Radius = 5.5 };
    private static readonly IEffect BlurSix = new BlurEffect { Radius = 6 };
    private static readonly IEffect BlurEight = new BlurEffect { Radius = 8 };
    private static readonly IEffect BlurTen = new BlurEffect { Radius = 10 };

    internal PhoneShellControl()
    {
        Width = PhoneWidth;
        Height = PhoneHeight;
        MinWidth = 0;
        MinHeight = 0;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        using (context.PushGeometryClip(PhoneBody))
        {
            context.DrawGeometry(BodyBrush, null, PhoneBody);
            DrawBlurredEllipse(context, WhiteHighlightBrush, new Rect(-13, 17, 105, 957), BlurTen, 0.4);
            DrawBlurredGeometry(context, OuterHighlightBrush, OuterShellHighlight, BlurTwo, 0.637);
            DrawBlurredGeometry(context, OuterDarkBrush, OuterShellDark, BlurTwo);
            DrawBlurredGeometry(context, OuterLightBrush, OuterShellLight, BlurTwo);
            DrawBlurredGeometry(context, OuterSideLightBrush, OuterShellLight, BlurTwo, 0.5);
            DrawGeometry(context, UpperShadeBrush, UpperShellShade, 0.6);
            context.DrawGeometry(UpperBaseBrush, null, UpperShellBase);
            context.DrawGeometry(UpperFaceBrush, null, UpperShellFace);
            context.DrawGeometry(KeypadBedBrush, null, KeypadBed);
            context.DrawGeometry(KeypadCapBrush, null, KeypadCap);
            DrawLcdBorder(context);

            DrawBlurredRoundedRectangle(
                context,
                LogoPlateBaseBrush,
                new Rect(134.5, 189.25, 130, 27),
                2,
                BlurOne);
            context.DrawRectangle(LogoPlateBrush, null, new Rect(134.5, 189.25, 130, 27), 2, 2);

            context.DrawGeometry(NokiaBrush, null, NokiaN);
            context.DrawGeometry(NokiaBrush, null, NokiaO);
            context.DrawGeometry(NokiaBrush, null, NokiaKStem);
            context.DrawGeometry(NokiaBrush, null, NokiaKArm);
            context.DrawGeometry(NokiaBrush, null, NokiaI);
            context.DrawGeometry(NokiaBrush, null, NokiaA);

            context.DrawGeometry(SpeakerHousingBrush, null, SpeakerHousing);
            DrawSpeakerSlots(context);
        }
    }

    private static void DrawLcdBorder(DrawingContext context)
    {
        // Restore filter-19 from the source SVG. The keypad-cap gradient remains
        // visible around the window and these layers emboss its edge.
        DrawBlurredGeometry(context, Brushes.Black, LcdUpperOuterShadow, BlurTen, 0.242745131);
        DrawBlurredGeometry(context, Brushes.Black, LcdUpperInnerShadow, BlurSix, 0.147141078);
        DrawBlurredGeometry(context, Brushes.White, LcdLeftHighlight, BlurFivePointFive, 0.216332654);
        DrawBlurredGeometry(context, Brushes.Black, LcdRightShadow, BlurEight, 0.35);
        DrawBlurredGeometry(context, Brushes.White, LcdLowerHighlight, BlurOne, 0.166618546);
    }

    private static void DrawSpeakerSlots(DrawingContext context)
    {
        DrawBlurredEllipse(context, SpeakerShadowBrush, new Rect(190.483, 145, 17.034, 10), BlurOne, 0.266);
        DrawBlurredEllipse(context, SpeakerShadowBrush, new Rect(187.793, 118, 22.414, 10), BlurOne, 0.266);
        DrawBlurredEllipse(context, SpeakerShadowBrush, new Rect(187.793, 66, 22.414, 10), BlurOne, 0.266);
        DrawBlurredEllipse(context, SpeakerShadowBrush, new Rect(189.586, 40, 18.828, 10), BlurOne, 0.266);
        DrawBlurredEllipse(context, SpeakerShadowBrush, new Rect(186, 92, 26, 10), BlurOne, 0.266);

        foreach (EllipseGeometry slot in SpeakerSlots)
        {
            DrawSpeakerSlot(context, slot);
        }
    }

    private static void DrawSpeakerSlot(DrawingContext context, EllipseGeometry clip)
    {
        Rect bounds = clip.Bounds;
        context.DrawEllipse(SpeakerSlotBrush, null, bounds);

        using (context.PushGeometryClip(clip))
        {
            context.DrawEllipse(
                null,
                SpeakerSlotLightPen,
                new Rect(bounds.X, bounds.Y - 1, bounds.Width, bounds.Height));
            context.DrawEllipse(
                null,
                SpeakerSlotDarkPen,
                new Rect(bounds.X, bounds.Y + 1, bounds.Width, bounds.Height));
        }
    }

    private static void DrawBlurredGeometry(
        DrawingContext context,
        IBrush brush,
        Geometry geometry,
        IEffect effect,
        double opacity = 1)
    {
        using (context.PushEffect(effect, geometry.Bounds))
        {
            DrawGeometry(context, brush, geometry, opacity);
        }
    }

    private static void DrawGeometry(
        DrawingContext context,
        IBrush brush,
        Geometry geometry,
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

    private static void DrawBlurredEllipse(
        DrawingContext context,
        IBrush brush,
        Rect bounds,
        IEffect effect,
        double opacity)
    {
        using (context.PushEffect(effect, bounds))
        using (context.PushOpacity(opacity))
        {
            context.DrawEllipse(brush, null, bounds);
        }
    }

    private static void DrawBlurredRoundedRectangle(
        DrawingContext context,
        IBrush brush,
        Rect bounds,
        double radius,
        IEffect effect)
    {
        using (context.PushEffect(effect, bounds))
        {
            context.DrawRectangle(brush, null, bounds, radius, radius);
        }
    }

    private static Geometry LogoGeometry(Geometry geometry)
        => Transform(geometry, new Matrix(0.655, 0, 0, 0.655, 144.16, 193.75));

    private static Geometry Transform(Geometry geometry, Matrix matrix)
    {
        geometry.Transform = new MatrixTransform(matrix);
        return geometry;
    }

    private static IBrush Gradient(string start, string end)
        => Gradient((start, 0), (end, 1));

    private static IBrush Gradient(params (string Color, double Offset)[] stops)
        => Gradient(
            new RelativePoint(0.5, 0, RelativeUnit.Relative),
            new RelativePoint(0.5, 1, RelativeUnit.Relative),
            stops);

    private static IBrush Gradient(
        RelativePoint start,
        RelativePoint end,
        params (string Color, double Offset)[] stops)
    {
        LinearGradientBrush brush = new()
        {
            StartPoint = start,
            EndPoint = end,
        };

        foreach ((string color, double offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(Color.Parse(color), offset));
        }

        return brush;
    }
}
