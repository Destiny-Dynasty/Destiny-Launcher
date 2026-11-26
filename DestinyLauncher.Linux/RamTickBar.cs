using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DestinyLauncher.Linux;

public sealed class RamTickBar : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<RamTickBar, double>(nameof(Value), 8);
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<RamTickBar, double>(nameof(Minimum), 1);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<RamTickBar, double>(nameof(Maximum), 16);
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    static RamTickBar() => AffectsRender<RamTickBar>(ValueProperty, MinimumProperty, MaximumProperty);
    public override void Render(DrawingContext drawing)
    {
        base.Render(drawing);
        var range = Maximum - Minimum;
        if (range <= 0) return;
        var selected = Bounds.Width * (Value - Minimum) / range;
        var step = Math.Max(2, (int)Math.Ceiling(range / 32 / 2) * 2);
        using var opacity = drawing.PushOpacity(.45);
        var pen = new Pen(new SolidColorBrush(Color.Parse("#F2EBDD")), 1);
        for (var value = (Math.Floor(Minimum / step) + 1) * step; value < Maximum; value += step)
        {
            var x = Bounds.Width * (value - Minimum) / range;
            if (Math.Abs(x - selected) >= 9) drawing.DrawLine(pen, new Point(x, Bounds.Height / 2 - 3), new Point(x, Bounds.Height / 2 + 3));
        }
    }
}
