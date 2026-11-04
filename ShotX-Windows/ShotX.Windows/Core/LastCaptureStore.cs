using System.Windows;

namespace ShotX.Windows.Core;

public static class LastCaptureStore
{
    public static Rect? LastRect { get; set; }

    public static bool HasPrevious => LastRect.HasValue && LastRect.Value.Width > 5 && LastRect.Value.Height > 5;
}
