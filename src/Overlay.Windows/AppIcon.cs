using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overlay.Windows;
internal static class AppIcon
{
    internal static ImageSource Image { get; } = LoadImage();
    private static ImageSource LoadImage()
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Overlay.Windows;component/Assets/app.ico"), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
        image.Freeze(); return image;
    }
    internal static System.Drawing.Icon Tray()
    {
        using Stream stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Overlay.Windows;component/Assets/app.ico"))!.Stream;
        using var icon = new System.Drawing.Icon(stream, 32, 32);
        return (System.Drawing.Icon)icon.Clone();
    }
}
