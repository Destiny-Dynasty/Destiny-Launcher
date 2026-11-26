using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace DestinyLauncher.Linux;

public sealed partial class MainWindow
{
    private async Task LoadNewsAsync()
    {
        var token = _uiLifetime.Token;
        try
        {
            var cached = await _newsFeed.ReadCacheAsync(token);
            if (cached is not null) SetNewsSlides(cached);
            token.ThrowIfCancellationRequested();
            var fresh = await _newsFeed.RefreshAsync(token);
            SetNewsSlides(fresh);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { LogVideoError("Novedades: se conserva el contenido disponible (" + ex.GetType().Name + ")."); }
    }
    private void SetNewsSlides(IReadOnlyList<LinuxNewsSlide> slides)
    {
        if (_uiLifetime.IsCancellationRequested) { foreach (var slide in slides) slide.Image.Dispose(); return; }
        var retainedId = _newsSlides.Count > 0 ? _newsSlides[_newsIndex].Id : null;
        SettleNewsTransition();
        var old = _newsSlides;
        NewsImage.Source = null;
        _newsSlides = slides;
        NewsCard.IsVisible = slides.Count > 0;
        NewsPrevious.IsVisible = NewsNext.IsVisible = NewsDots.IsVisible = slides.Count > 1;
        if (slides.Count > 0)
        {
            var index = slides.Select((slide, i) => (slide, i)).FirstOrDefault(item => item.slide.Id == retainedId);
            ShowNews(index.slide is null ? 0 : index.i);
        }
        foreach (var slide in old) slide.Image.Dispose();
        UpdateNewsPlayback();
    }
    private bool _newsHovered, _newsTransitioning;
    private int _newsGeneration;
    private (int Index, int Direction)? _pendingNews;
    private int NewsNavigationIndex => _pendingNews?.Index ?? _newsIndex;

    private void ConfigureNewsInteraction()
    {
        NewsCaptionSurface.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) }];
        NewsHoverControls.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(100) }];
        NewsCard.GotFocus += (_, _) => UpdateNewsInteraction();
        NewsCard.LostFocus += (_, _) => UpdateNewsInteraction();
    }
    private void NewsPrevious_Click(object? sender, RoutedEventArgs e) => ShowNews((NewsNavigationIndex + _newsSlides.Count - 1) % _newsSlides.Count, -1);
    private void NewsNext_Click(object? sender, RoutedEventArgs e) => ShowNews((NewsNavigationIndex + 1) % _newsSlides.Count);
    private void News_Entered(object? sender, PointerEventArgs e) { _newsHovered = true; UpdateNewsInteraction(); }
    private void News_Exited(object? sender, PointerEventArgs e) { _newsHovered = false; UpdateNewsInteraction(); }
    private void UpdateNewsInteraction()
    {
        var reveal = _newsHovered || NewsCard.IsKeyboardFocusWithin;
        NewsCaptionSurface.Opacity = reveal ? 1 : 0;
        NewsHoverControls.Opacity = reveal ? 1 : 0;
        NewsHoverControls.IsHitTestVisible = reveal;
        UpdateNewsPlayback();
    }
    private void UpdateNewsPlayback()
    {
        _newsTimer.Stop();
        if (!_uiLifetime.IsCancellationRequested && !App.IsVisualAudit && IsVisible && WindowState != WindowState.Minimized && IsActive && _page == "home" && !_isBusy && !_newsHovered && !NewsCard.IsKeyboardFocusWithin && !_newsTransitioning && _newsSlides.Count > 1)
            _newsTimer.Start();
    }
    private void News_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_newsSlides.Count == 0) return;
        if (e.Key == Key.Left) { NewsPrevious_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Right) { NewsNext_Click(sender, e); e.Handled = true; }
    }
    private void News_Released(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || e.Source is not Visual source || source.GetVisualAncestors().Any(control => control is Button)) return;
        if (_newsSlides.Count > 0 && _newsSlides[_newsIndex].Link is { } link) OpenPath(link.AbsoluteUri);
    }
    private void ShowNews(int index, int direction = 1)
    {
        if (_newsTransitioning) { _pendingNews = (index, direction); return; }
        if (_newsSlides.Count == 0 || (index == _newsIndex && ReferenceEquals(NewsImage.Source, _newsSlides[index].Image))) return;
        var old = NewsImage.Source as Bitmap;
        var next = _newsSlides[index].Image;
        _newsIndex = index;
        NewsImage.Source = next; NewsCaption.Text = _newsSlides[index].Caption;
        UpdateNewsDots();
        if (old is not null && !App.IsVisualAudit && IsActive && _page == "home")
        {
            var distance = direction < 0 ? -NewsCard.Bounds.Width : NewsCard.Bounds.Width;
            NewsPreviousImage.Source = old;
            var incoming = new TranslateTransform(distance, 0);
            var outgoing = new TranslateTransform();
            NewsImage.RenderTransform = incoming; NewsPreviousImage.RenderTransform = outgoing;
            incoming.Transitions = [new DoubleTransition { Property = TranslateTransform.XProperty, Duration = TimeSpan.FromMilliseconds(500), Easing = new CubicEaseOut() }];
            outgoing.Transitions = [new DoubleTransition { Property = TranslateTransform.XProperty, Duration = TimeSpan.FromMilliseconds(500), Easing = new CubicEaseOut() }];
            _newsTransitioning = true; var generation = ++_newsGeneration;
            incoming.X = 0; outgoing.X = -distance;
            UpdateNewsPlayback();
            _ = FinishNewsTransitionAsync(generation);
        }
        else UpdateNewsPlayback();
    }
    private async Task FinishNewsTransitionAsync(int generation)
    {
        try { await Task.Delay(500, _uiLifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (generation != _newsGeneration) return;
        var pending = _pendingNews;
        SettleNewsTransition();
        if (pending is { } next && next.Index != _newsIndex) ShowNews(next.Index, next.Direction);
        UpdateNewsPlayback();
    }
    private void SettleNewsTransition()
    {
        ++_newsGeneration; _newsTransitioning = false; _pendingNews = null;
        NewsImage.RenderTransform = null; NewsPreviousImage.RenderTransform = null;
        NewsPreviousImage.Source = null;
    }
    private void UpdateNewsDots()
    {
        if (NewsDots.Children.Count != _newsSlides.Count)
        {
            NewsDots.Children.Clear();
            for (var i = 0; i < _newsSlides.Count; i++)
            {
                var slide = i;
                var dot = new Button { Width = 24, Height = 24, MinHeight = 0, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Content = new Border { Height = 5, CornerRadius = new CornerRadius(3) } };
                dot.Classes.Add("dot");
                dot.Click += (_, _) => ShowNews(slide, slide < _newsIndex ? -1 : 1);
                NewsDots.Children.Add(dot);
            }
        }
        for (var i = 0; i < _newsSlides.Count; i++)
        {
            var dot = (Button)NewsDots.Children[i];
            ToolTip.SetTip(dot, _newsSlides[i].Caption);
            var indicator = (Border)dot.Content!;
            indicator.Width = i == _newsIndex ? 16 : 5;
            indicator.Background = i == _newsIndex ? ActiveNewsDot : InactiveNewsDot;
        }
    }
    private static readonly IBrush ActiveNewsDot = Brush("#F1D398");
    private static readonly IBrush InactiveNewsDot = Brush("#B3D5C7AB");
}


