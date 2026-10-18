using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DestinyLauncher;

/// <summary>Major RAM markers share the slider thumb's exact horizontal range.</summary>
public sealed class RamTickBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RamTickBar),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var muted = (Brush)FindResource("Muted");
        var gold = (Brush)FindResource("Gold");
        var inactive = (Brush)FindResource("GoldDim");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var value = 2; value <= 16; value += 2)
        {
            var x = ActualWidth * (value - 2) / 14;
            drawing.DrawEllipse(value <= Value ? gold : inactive, null, new Point(x, 4), 2, 2);
            var label = new FormattedText(value.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, muted, dpi);
            drawing.DrawText(label, new Point(x - label.Width / 2, 11));
        }
    }
}
