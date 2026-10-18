using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DestinyLauncher.Services;

namespace DestinyLauncher;

// Shared by the account list and popup. Each realized row loads only while visible.
internal sealed class AccountIdentity : Grid
{
    internal static readonly DependencyProperty HeadServiceProperty = DependencyProperty.RegisterAttached(
        "HeadService", typeof(PlayerHeadService), typeof(AccountIdentity),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));
    private readonly Image _head;
    private readonly TextBlock _initial;
    private readonly TextBlock _name;
    private CancellationTokenSource? _request;
    public AccountIdentity()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        ColumnDefinitions.Add(new ColumnDefinition());
        var avatar = new Border { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left,
            Background = ProfileDialog.ResourceBrush("PanelSoft"), BorderBrush = ProfileDialog.ResourceBrush("Line"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(3) };
        var layers = new Grid();
        _initial = new TextBlock { Foreground = ProfileDialog.ResourceBrush("GoldBright"), FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _head = new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(_head, BitmapScalingMode.NearestNeighbor);
        layers.Children.Add(_initial); layers.Children.Add(_head); avatar.Child = layers; Children.Add(avatar);
        _name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        SetColumn(_name, 1); Children.Add(_name);
        DataContextChanged += async (_, _) =>
        {
            CancelRequest();
            _name.Text = DataContext as string ?? "";
            _initial.Text = _name.Text.Length > 0 ? _name.Text[..1].ToUpperInvariant() : "D";
            _head.Source = null; _head.Visibility = Visibility.Collapsed; _initial.Visibility = Visibility.Visible;
            if (IsLoaded) await LoadHeadAsync();
        };
        Loaded += async (_, _) => await LoadHeadAsync();
        Unloaded += (_, _) => CancelRequest();
    }
    internal async Task LoadHeadAsync()
    {
        CancelRequest();
        if (GetValue(HeadServiceProperty) is not PlayerHeadService service || DataContext is not string name) return;
        _request = new CancellationTokenSource();
        var token = _request.Token;
        try
        {
            void Display(ImageSource? image)
            {
                if (image is null || token.IsCancellationRequested || !Equals(DataContext, name)) return;
                _head.Source = image; _head.Visibility = Visibility.Visible; _initial.Visibility = Visibility.Collapsed;
            }
            Display(await service.GetCachedHeadAsync(name, token));
            Display(await service.GetHeadAsync(name, token));
        }
        catch (OperationCanceledException) { }
        catch { /* Keep the cached head or initial; portraits never block account actions. */ }
    }
    private void CancelRequest() { _request?.Cancel(); _request?.Dispose(); _request = null; }
}
