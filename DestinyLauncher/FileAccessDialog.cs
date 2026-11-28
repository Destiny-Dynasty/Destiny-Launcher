using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DestinyLauncher.Models;
using DestinyLauncher.Services;

namespace DestinyLauncher;

internal sealed class FileAccessDialog : Window
{
    internal FileAccessDialog(string path, Action<string>? copyPath = null)
    {
        var body = new Grid { Margin = new Thickness(24, 20, 24, 24) };
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel();
        var lead = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        lead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        lead.ColumnDefinitions.Add(new ColumnDefinition());
        lead.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(1),
            BorderBrush = ProfileDialog.ResourceBrush("Error"), Background = new SolidColorBrush(Color.FromArgb(20, 224, 163, 150)),
            Child = new TextBlock { Text = "!", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = ProfileDialog.ResourceBrush("Error"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var leadText = new StackPanel();
        leadText.Children.Add(new TextBlock { Text = ProfileFileAccessNotice.Heading, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        leadText.Children.Add(new TextBlock { Text = ProfileFileAccessNotice.Explanation, FontSize = 12, LineHeight = 19, Foreground = ProfileDialog.ResourceBrush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        Grid.SetColumn(leadText, 1); lead.Children.Add(leadText); content.Children.Add(lead);
        var file = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        file.Children.Add(new TextBlock { Text = ProfileFileAccessNotice.PathLabel, FontSize = 10, Foreground = ProfileDialog.ResourceBrush("Muted") });
        file.Children.Add(new TextBlock { Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path, Margin = new Thickness(0, 7, 0, 6) });
        var pathBox = new TextBox
        {
            Name = "AffectedPath", Text = path, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 72, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = ProfileDialog.ResourceBrush("Muted"),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), MinHeight = 0
        };
        AutomationProperties.SetName(pathBox, "Ruta del archivo o carpeta afectados");
        file.Children.Add(pathBox);
        content.Children.Add(new Border { Child = file, BorderThickness = new Thickness(1), BorderBrush = ProfileDialog.ResourceBrush("Line"), Background = ProfileDialog.ResourceBrush("Canvas"), CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 0, 18) });
        content.Children.Add(new TextBlock { Text = ProfileFileAccessNotice.Instructions, FontSize = 13, LineHeight = 23, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = ProfileFileAccessNotice.Fallback, FontSize = 11, LineHeight = 18, Foreground = ProfileDialog.ResourceBrush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        body.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var copy = ProfileDialog.DialogButton(ProfileFileAccessNotice.Copy, false); copy.Margin = new Thickness(0, 0, 10, 0);
        copy.Click += (_, _) =>
        {
            try { (copyPath ?? Clipboard.SetText)(path); copy.Content = ProfileFileAccessNotice.Copied; }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            { copy.Content = ProfileFileAccessNotice.CopyFailed; }
        };
        var close = ProfileDialog.DialogButton(ProfileFileAccessNotice.Close, true); close.IsDefault = true; close.Click += (_, _) => Close();
        actions.Children.Add(copy); actions.Children.Add(close); Grid.SetRow(actions, 1); body.Children.Add(actions);
        DialogChrome.Create(this, ProfileFileAccessNotice.Title, body, 600, 460);
        Loaded += (_, _) => close.Focus();
    }
}

public partial class MainWindow
{
    internal void ShowFileAccessFailure(ProfileConfigurationAccessException error, Action<string>? copyPath = null)
        => new FileAccessDialog(error.AffectedPath, copyPath) { Owner = this }.ShowDialog();
}
