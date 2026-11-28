using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

namespace DestinyLauncher.Linux;

public sealed partial class MainWindow
{
    internal Window CreateFileAccessDialog(string path)
    {
        var body = new Grid { Margin = new Thickness(24, 20, 24, 24), RowDefinitions = new RowDefinitions("*,Auto") };
        var content = new StackPanel();
        var lead = new Grid { ColumnDefinitions = new ColumnDefinitions("54,*"), Margin = new Thickness(0, 0, 0, 18) };
        lead.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(1), BorderBrush = Brush("#E0A396"), Background = Brush("#14E0A396"),
            Child = new TextBlock { Text = "!", FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = Brush("#E0A396"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var leadText = new StackPanel();
        leadText.Children.Add(new TextBlock { Text = ProfileFileAccessNotice.Heading, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        leadText.Children.Add(new TextBlock { Text = ProfileFileAccessNotice.Explanation, FontSize = 12, LineHeight = 19, Foreground = Brush("#B3AC9E"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        Grid.SetColumn(leadText, 1); lead.Children.Add(leadText); content.Children.Add(lead);
        var file = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        file.Children.Add(new TextBlock { Text = ProfileFileAccessNotice.PathLabel, FontSize = 10, Foreground = Brush("#B3AC9E") });
        file.Children.Add(new TextBlock { Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 7, 0, 6) });
        var pathBox = new TextBox
        {
            Name = "AffectedPath", Text = path, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 72, FontFamily = new FontFamily("Consolas, Liberation Mono, DejaVu Sans Mono"), FontSize = 11,
            Foreground = Brush("#B3AC9E"), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), MinHeight = 0
        };
        ScrollViewer.SetVerticalScrollBarVisibility(pathBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(pathBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(pathBox, "Ruta del archivo o carpeta afectados");
        file.Children.Add(pathBox);
        content.Children.Add(new Border { Child = file, BorderThickness = new Thickness(1), BorderBrush = Brush("#34352B"), Background = Brush("#10110E"), CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 0, 18) });
        content.Children.Add(new TextBlock { Text = ProfileFileAccessNotice.Instructions, FontSize = 13, LineHeight = 23, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = ProfileFileAccessNotice.Fallback, FontSize = 11, LineHeight = 18, Foreground = Brush("#B3AC9E"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        body.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var dialog = CreateDialog(ProfileFileAccessNotice.Title, body, 600, 460);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 20, 0, 0) };
        var copy = DialogButton(ProfileFileAccessNotice.Copy);
        copy.Click += async (_, _) =>
        {
            try
            {
                var clipboard = dialog.Clipboard ?? throw new InvalidOperationException();
                await clipboard.SetTextAsync(path); copy.Content = ProfileFileAccessNotice.Copied;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
            { copy.Content = ProfileFileAccessNotice.CopyFailed; }
        };
        var close = DialogButton(ProfileFileAccessNotice.Close, true); close.IsDefault = true; close.Click += (_, _) => dialog.Close();
        actions.Children.Add(copy); actions.Children.Add(close); Grid.SetRow(actions, 1); body.Children.Add(actions);
        dialog.Opened += (_, _) => close.Focus();
        return dialog;
    }

    private Task ShowFileAccessFailureAsync(ProfileConfigurationAccessException error)
        => ShowOwnedDialogAsync(CreateFileAccessDialog(error.AffectedPath));
}
