using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DestinyLauncher.Linux;

public static class RenderingQuality
{
    public static readonly AttachedProperty<bool> SmoothProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Smooth", typeof(RenderingQuality), false);

    static RenderingQuality()
    {
        TextBlock.FontSizeProperty.Changed.AddClassHandler<TextBlock>((text, _) => ApplyTextMetrics(text));
        TextBlock.FontFamilyProperty.Changed.AddClassHandler<TextBlock>((text, _) => ApplyTextMetrics(text));
        SmoothProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (!control.GetValue(SmoothProperty)) return;
            RenderOptions.SetBitmapInterpolationMode(control, BitmapInterpolationMode.HighQuality);
            TextOptions.SetTextRenderingMode(control, TextRenderingMode.Antialias);
            if (control is TextBlock text) ApplyTextMetrics(text);
        });
    }
    private static void ApplyTextMetrics(TextBlock text)
    {
        // Match the original WPF display-mode line boxes rather than Skia's taller
        // default leading; explicit page styles can still set their own line height.
        var georgia = text.FontFamily.Name.Contains("Georgia", StringComparison.OrdinalIgnoreCase);
        var height = (georgia, text.FontSize) switch
        {
            (false, 9) => 10.4, (false, 13) => 16.8, (false, 14) => 20,
            (true, 18) => 21.6, (true, 23) => 27.2,
            _ => double.NaN
        };
        text.SetValue(TextBlock.LineHeightProperty, height, BindingPriority.Style);
    }
    public static bool GetSmooth(Control control) => control.GetValue(SmoothProperty);
    public static void SetSmooth(Control control, bool value) => control.SetValue(SmoothProperty, value);
}

