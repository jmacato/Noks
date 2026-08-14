using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Noks.Dct3.Display;
using Noks.AvaloniaApp.Emulation;

namespace Noks.AvaloniaApp.Controls;

public sealed class LcdControl : Image
{
    public static readonly IBrush BackgroundOnBrush = new SolidColorBrush(Color.Parse("#a9e000"));
    public static readonly IBrush BackgroundOffBrush = new SolidColorBrush(Color.Parse("#536315"));

    internal const double PhysicalPixelHeightToWidth = 83d / 72d;
    internal const double CorrectedDisplayAspectRatio = 126d / 83d;
    private const int BytesPerPixel = 4;
    private const byte LitBlue = 0x01;
    private const byte LitGreen = 0x01;
    private const byte LitRed = 0x01;
    private const byte BackgroundOnBlue = 0x00;
    private const byte BackgroundOnGreen = 0xE0;
    private const byte BackgroundOnRed = 0xA9;
    private const byte BackgroundOffBlue = 0x15;
    private const byte BackgroundOffGreen = 0x63;
    private const byte BackgroundOffRed = 0x53;
    private const int BitmapBufferCount = 3;

    private readonly WriteableBitmap[] bitmaps;
    private readonly byte[] pixels = new byte[Pcd8544.Width * Pcd8544.Height * BytesPerPixel];
    private PhoneEmulator emulator;
    private bool? backlightOverride;
    private int nextBitmapIndex;

    public LcdControl(PhoneEmulator emulator)
    {
        this.emulator = emulator;
        bitmaps = Enumerable.Range(0, BitmapBufferCount)
            .Select(static _ => new WriteableBitmap(
                new PixelSize(Pcd8544.Width, Pcd8544.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque))
            .ToArray();
        Stretch = Stretch.Fill;
        StretchDirection = StretchDirection.Both;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        UpdateBitmap();
    }

    public PhoneEmulator Emulator
    {
        get => emulator;
        set
        {
            emulator = value;
            UpdateBitmap();
        }
    }

    public bool? BacklightOverride
    {
        get => backlightOverride;
        set
        {
            if (backlightOverride == value)
            {
                return;
            }

            backlightOverride = value;
            UpdateBitmap();
        }
    }

    internal void SetBacklightOverrideWithoutRefresh(bool? value)
    {
        backlightOverride = value;
    }

    public void UpdateBitmap()
    {
        PhoneEmulator.LcdFrame frame = emulator.Frame;
        bool backlightOn = backlightOverride ?? emulator.PeripheralState.LcdBacklightOn;
        byte backgroundBlue = backlightOn ? BackgroundOnBlue : BackgroundOffBlue;
        byte backgroundGreen = backlightOn ? BackgroundOnGreen : BackgroundOffGreen;
        byte backgroundRed = backlightOn ? BackgroundOnRed : BackgroundOffRed;
        bool displayPixels = !frame.PowerDown || frame.DataWrites > 0;
        int destination = 0;

        for (int y = 0; y < Pcd8544.Height; y++)
        {
            for (int x = 0; x < Pcd8544.Width; x++)
            {
                bool lit = displayPixels && frame.GetPixel(x, y);
                pixels[destination++] = lit ? LitBlue : backgroundBlue;
                pixels[destination++] = lit ? LitGreen : backgroundGreen;
                pixels[destination++] = lit ? LitRed : backgroundRed;
                pixels[destination++] = byte.MaxValue;
            }
        }

        int sourceRowBytes = Pcd8544.Width * BytesPerPixel;
        WriteableBitmap nextBitmap = bitmaps[nextBitmapIndex];
        nextBitmapIndex = (nextBitmapIndex + 1) % bitmaps.Length;
        using (ILockedFramebuffer framebuffer = nextBitmap.Lock())
        {
            if (framebuffer.RowBytes == sourceRowBytes)
            {
                Marshal.Copy(pixels, 0, framebuffer.Address, pixels.Length);
            }
            else
            {
                for (int y = 0; y < Pcd8544.Height; y++)
                {
                    Marshal.Copy(
                        pixels,
                        y * sourceRowBytes,
                        IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes),
                        sourceRowBytes);
                }
            }
        }

        Source = nextBitmap;
    }

    protected override Size MeasureOverride(Size availableSize)
        => FitCorrectedPixelBounds(availableSize);

    internal static Size FitCorrectedPixelBounds(Size availableSize)
    {
        bool hasWidth = double.IsFinite(availableSize.Width) && availableSize.Width > 0;
        bool hasHeight = double.IsFinite(availableSize.Height) && availableSize.Height > 0;
        if (!hasWidth && !hasHeight)
        {
            return new Size(Pcd8544.Width * 3, Pcd8544.Height * 3 * PhysicalPixelHeightToWidth);
        }

        double width = hasWidth
            ? availableSize.Width
            : availableSize.Height * CorrectedDisplayAspectRatio;
        double height = hasHeight
            ? availableSize.Height
            : availableSize.Width / CorrectedDisplayAspectRatio;
        if (width / height > CorrectedDisplayAspectRatio)
        {
            width = height * CorrectedDisplayAspectRatio;
        }
        else
        {
            height = width / CorrectedDisplayAspectRatio;
        }

        return new Size(width, height);
    }
}
