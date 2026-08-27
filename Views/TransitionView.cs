using System.Windows;
using System.Windows.Media;

namespace WallpaperCycle.Views;

internal sealed class TransitionView : FrameworkElement
{
    private ImageSource? _newImage;
    private double _radius;

    public void SetImages(ImageSource? oldImage, ImageSource newImage)
    {
        // Old image is unused: outside the circle we leave the layered window
        // fully transparent so the real desktop (wallpaper + icons) shows through.
        _ = oldImage;
        _newImage = newImage;
        InvalidateVisual();
    }

    public void SetRadius(double radius)
    {
        _radius = radius;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
            return;

        // Transparent clear — critical for per-pixel layered windows so pixels
        // outside the iris do not cover icons or other windows.
        drawingContext.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(size));

        if (_newImage is null || _radius <= 0)
            return;

        var rect = new Rect(size);
        var center = new System.Windows.Point(size.Width / 2.0, size.Height / 2.0);
        var clip = new EllipseGeometry(center, _radius, _radius);
        clip.Freeze();

        drawingContext.PushClip(clip);
        drawingContext.DrawImage(_newImage, CoverRect(_newImage, rect));
        drawingContext.Pop();
    }

    private static Rect CoverRect(ImageSource image, Rect dest)
    {
        var width = image.Width;
        var height = image.Height;
        if (width <= 0 || height <= 0)
            return dest;

        var scale = Math.Max(dest.Width / width, dest.Height / height);
        var drawWidth = width * scale;
        var drawHeight = height * scale;
        return new Rect(
            dest.X + (dest.Width - drawWidth) / 2.0,
            dest.Y + (dest.Height - drawHeight) / 2.0,
            drawWidth,
            drawHeight);
    }
}
