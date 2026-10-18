using System.Windows;
using System.Windows.Media;

namespace DestinyLauncher;

/// <summary>Subtle divisions on the rail, aligned with the slider thumb's range.</summary>
public sealed class RamTickBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RamTickBar),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var pen = new Pen((Brush)FindResource("Ink"), 1);
        var selectedX = ActualWidth * (Value - 2) / 14;
        drawing.PushOpacity(.45);
        for (var value = 4; value < 16; value += 2)
        {
            var x = ActualWidth * (value - 2) / 14;
            // Do not paint over the thumb when a division coincides with its position.
            if (Math.Abs(x - selectedX) < 9) continue;
            drawing.DrawLine(pen, new Point(x, ActualHeight / 2 - 3), new Point(x, ActualHeight / 2 + 3));
        }
        drawing.Pop();
    }
}
