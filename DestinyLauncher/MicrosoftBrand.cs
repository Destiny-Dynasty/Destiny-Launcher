using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DestinyLauncher;

public static class MicrosoftBrand
{
    // Exact rectangles/colors of Microsoft's official 21x21 sign-in symbol.
    // Original SVG and provenance are retained in Assets/Branding.
    public static DrawingImage Symbol { get; } = CreateSymbol();
    private static DrawingImage CreateSymbol()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 21, 21))));
        foreach (var (x, y, color) in new[] { (1, 1, "#F25022"), (11, 1, "#7FBA00"), (1, 11, "#00A4EF"), (11, 11, "#FFB900") })
            drawing.Children.Add(new GeometryDrawing((Brush)new BrushConverter().ConvertFromString(color)!, null, new RectangleGeometry(new Rect(x, y, 9, 9))));
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
    internal static StackPanel Label(string text = "Microsoft", double fontSize = 10, double iconSize = 13)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new Image { Source = Symbol, Width = iconSize, Height = iconSize, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = text, FontSize = fontSize, Foreground = ProfileDialog.ResourceBrush("Muted"), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }
}
