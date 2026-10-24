using System.Windows;
using System.Windows.Controls;

namespace DestinyLauncher;

internal static class AccountTypeLabel
{
    internal static FrameworkElement Create(bool microsoft) => microsoft ? MicrosoftBrand.Label() : new TextBlock
    {
        Text = "Offline", FontSize = 10, Foreground = ProfileDialog.ResourceBrush("Muted"),
        VerticalAlignment = VerticalAlignment.Center
    };
}
