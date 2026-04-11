using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AvaPlayer.Helpers;

public static class ColorExtractor
{
    private static readonly Color DefaultStart = Color.FromRgb(0x14, 0x0E, 0x1E);
    private static readonly Color DefaultEnd = Color.FromRgb(0x0D, 0x09, 0x1A);

    public static LinearGradientBrush DefaultBackground() => CreateBrush(DefaultStart, DefaultEnd);

    public static LinearGradientBrush ExtractBackground(Bitmap bitmap)
    {
        if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
        {
            return DefaultBackground();
        }

        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var stride = width * 4;
        var byteCount = stride * height;
        var buffer = Marshal.AllocHGlobal(byteCount);

        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, byteCount, stride);

            double totalWeight = 0;
            double redSum = 0;
            double greenSum = 0;
            double blueSum = 0;

            const int samples = 16;

            for (var y = 0; y < samples; y++)
            {
                var pixelY = (height - 1) * y / (samples - 1);

                for (var x = 0; x < samples; x++)
                {
                    var pixelX = (width - 1) * x / (samples - 1);
                    var offset = pixelY * stride + pixelX * 4;

                    var blue = Marshal.ReadByte(buffer, offset);
                    var green = Marshal.ReadByte(buffer, offset + 1);
                    var red = Marshal.ReadByte(buffer, offset + 2);
                    var alpha = Marshal.ReadByte(buffer, offset + 3);

                    if (alpha < 24)
                    {
                        continue;
                    }

                    var max = Math.Max(red, Math.Max(green, blue));
                    var min = Math.Min(red, Math.Min(green, blue));
                    var saturation = max == 0 ? 0 : (max - min) / (double)max;
                    var weight = saturation * saturation + 0.05;

                    redSum += red * weight;
                    greenSum += green * weight;
                    blueSum += blue * weight;
                    totalWeight += weight;
                }
            }

            if (totalWeight <= double.Epsilon)
            {
                return DefaultBackground();
            }

            var extracted = Color.FromRgb(
                (byte)Math.Round(redSum / totalWeight),
                (byte)Math.Round(greenSum / totalWeight),
                (byte)Math.Round(blueSum / totalWeight));

            var darkened = ClampLuminance(extracted, 45);
            if (GetLuminance(darkened) < 8)
            {
                return DefaultBackground();
            }

            return CreateBrush(darkened, Scale(darkened, 0.65));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static LinearGradientBrush CreateBrush(Color start, Color end) =>
        new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(start, 0),
                new GradientStop(end, 1)
            }
        };

    private static Color ClampLuminance(Color color, double maxLuminance)
    {
        var luminance = GetLuminance(color);
        if (luminance <= maxLuminance || luminance <= double.Epsilon)
        {
            return color;
        }

        var scale = maxLuminance / luminance;
        return Scale(color, scale);
    }

    private static double GetLuminance(Color color) =>
        color.R * 0.299 + color.G * 0.587 + color.B * 0.114;

    private static Color Scale(Color color, double factor) =>
        Color.FromRgb(
            (byte)Math.Clamp(Math.Round(color.R * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(color.G * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(color.B * factor), 0, 255));
}
