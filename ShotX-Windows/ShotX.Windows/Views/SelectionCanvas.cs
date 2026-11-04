using System;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShotX.Windows.Views;

public class SelectionCanvas : Canvas
{
    public Action<DrawingContext>? CustomRender { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        CustomRender?.Invoke(dc);
    }
}
