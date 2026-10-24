using System.Windows;
using System.Windows.Media;

namespace DestinyLauncher;

/// <summary>Subtle divisions on the rail, aligned with the slider thumb's range.</summary>
public sealed class RamTickBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RamTickBar),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(RamTickBar), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RamTickBar), new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var pen = new Pen((Brush)FindResource("Ink"), 1);
        var range = Maximum - Minimum;
        if (range <= 0) return;
        var selectedX = ActualWidth * (Value - Minimum) / range;
        drawing.PushOpacity(.45);
        var step = Math.Max(2, (int)Math.Ceiling(range / 32 / 2) * 2);
        for (var value = (Math.Floor(Minimum / step) + 1) * step; value < Maximum; value += step)
        {
            var x = ActualWidth * (value - Minimum) / range;
            // Do not paint over the thumb when a division coincides with its position.
            if (Math.Abs(x - selectedX) < 9) continue;
            drawing.DrawLine(pen, new Point(x, ActualHeight / 2 - 3), new Point(x, ActualHeight / 2 + 3));
        }
        drawing.Pop();
    }
}
