using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DestinyLauncher.Models;

namespace DestinyLauncher;

public partial class NewsCarousel : UserControl
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private IReadOnlyList<NewsSlide> _slides = [];
    private int _index;
    private bool _suspended;
    private bool _transitioning;
    private int _transitionGeneration;
    private (int Index, int Direction)? _pendingSelection;
    private Window? _window;
    internal int CurrentIndex => _index;
    internal int SlideCount => _slides.Count;
    internal bool IsPlaying => _timer.IsEnabled;
    internal bool CaptionRevealed => IsMouseOver || IsKeyboardFocusWithin;
    private int NavigationIndex => _pendingSelection?.Index ?? _index;

    public NewsCarousel()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => { if (CanPlay) Select((_index + 1) % _slides.Count, animate: true, direction: 1); else _timer.Stop(); };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => { if (!IsVisible) SettleTransition(); UpdatePlayback(); };
        MouseEnter += (_, _) => UpdateInteraction();
        MouseLeave += (_, _) => UpdateInteraction();
        IsKeyboardFocusWithinChanged += (_, _) => UpdateInteraction();
    }

    internal void SetSlides(IReadOnlyList<NewsSlide> slides)
    {
        var previousId = _slides.Count > 0 ? _slides[_index].Id : null;
        _slides = slides;
        Indicators.Children.Clear();
        Visibility = slides.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (slides.Count == 0)
        {
            _timer.Stop(); ResetMotion(); _pendingSelection = null;
            CurrentImage.Source = null; CaptionText.Text = ""; return;
        }
        for (var i = 0; i < slides.Count; i++)
        {
            var dot = new RadioButton { Style = (Style)FindResource("SlideDot"), Tag = i, GroupName = "News-" + GetHashCode() };
            AutomationProperties.SetName(dot, $"Novedad {i + 1}: {slides[i].Caption}");
            dot.Click += (_, _) => { Select((int)dot.Tag, animate: true, direction: Math.Sign((int)dot.Tag - NavigationIndex)); UpdatePlayback(); };
            Indicators.Children.Add(dot);
        }
        Indicators.Visibility = slides.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.Visibility = NextButton.Visibility = slides.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var retained = slides.Select((s, i) => (s, i)).FirstOrDefault(x => x.s.Id == previousId);
        Select(retained.s is null ? 0 : retained.i, animate: false);
        UpdatePlayback();
    }

    internal void SuspendPlayback(bool suspended) { _suspended = suspended; UpdatePlayback(); }
    private bool CanPlay => IsLoaded && IsVisible && _slides.Count > 1 && !_suspended && !_transitioning
        && !CaptionRevealed && _window is { IsActive: true, WindowState: not WindowState.Minimized } && SystemParameters.ClientAreaAnimation;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.Activated += WindowStateChanged; _window.Deactivated += WindowStateChanged; _window.StateChanged += WindowStateChanged;
        }
        SystemParameters.StaticPropertyChanged += SystemPreferenceChanged;
        UpdatePlayback();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        SettleTransition();
        if (_window is not null)
        {
            _window.Activated -= WindowStateChanged; _window.Deactivated -= WindowStateChanged; _window.StateChanged -= WindowStateChanged;
        }
        SystemParameters.StaticPropertyChanged -= SystemPreferenceChanged;
        _window = null;
    }
    private void SystemPreferenceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.InvokeAsync(() =>
        { if (!SystemParameters.ClientAreaAnimation) SettleTransition(); UpdatePlayback(); });
    }
    private void WindowStateChanged(object? sender, EventArgs e)
    {
        if (_window is not { IsActive: true, WindowState: not WindowState.Minimized }) SettleTransition();
        UpdatePlayback();
    }
    private void UpdatePlayback() { _timer.Stop(); if (CanPlay) _timer.Start(); }

    private void UpdateInteraction()
    {
        var reveal = CaptionRevealed;
        HoverControls.IsHitTestVisible = reveal;
        Fade(HoverControls, reveal ? 1 : 0, 100);
        Fade(CaptionSurface, reveal ? 1 : 0, 140);
        UpdatePlayback();
    }
    private static void Fade(UIElement element, double value, int milliseconds)
    {
        var previous = element.Opacity;
        element.BeginAnimation(OpacityProperty, null); element.Opacity = value;
        if (SystemParameters.ClientAreaAnimation)
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(previous, value, TimeSpan.FromMilliseconds(milliseconds)) { FillBehavior = FillBehavior.Stop });
    }
    private void Select(int index, bool animate, int direction = 1)
    {
        if (_slides.Count == 0) return;
        if (_transitioning && animate)
        {
            // Finish the visible swipe before following the latest request. Rapid clicks never
            // replace either half of an image that is already moving across the viewport.
            _pendingSelection = index == _index ? null : (index, direction);
            return;
        }
        if (animate && index == _index) return;
        var outgoing = CurrentImage.Source;
        ResetMotion(); _pendingSelection = null;
        _index = index;
        var slide = _slides[index];
        CurrentImage.Source = slide.Image;
        var width = PictureViewport.ActualWidth;
        if (animate && IsVisible && SystemParameters.ClientAreaAnimation && outgoing is not null && width > 0)
        {
            _transitioning = true; _timer.Stop();
            var generation = _transitionGeneration;
            var distance = direction < 0 ? -width : width;
            PreviousImage.Source = outgoing;
            var incoming = new TranslateTransform(); CurrentImage.RenderTransform = incoming;
            var leaving = new TranslateTransform(-distance, 0); PreviousImage.RenderTransform = leaving;
            var duration = TimeSpan.FromMilliseconds(500);
            var enter = new DoubleAnimation(distance, 0, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            enter.Completed += (_, _) =>
            {
                if (generation != _transitionGeneration) return;
                var pending = _pendingSelection;
                ResetMotion(); _pendingSelection = null;
                if (pending is { } next && next.Index != _index) Select(next.Index, true, next.Direction);
                UpdatePlayback();
            };
            // The two opaque images share the same easing and duration, so their edges stay
            // together throughout the full-width swipe, including the soft final deceleration.
            leaving.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -distance, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            incoming.BeginAnimation(TranslateTransform.XProperty, enter);
        }
        CaptionText.Text = slide.Caption;
        OpenSlideButton.Cursor = slide.Link is null ? Cursors.Arrow : Cursors.Hand;
        AutomationProperties.SetName(OpenSlideButton, slide.Link is null ? slide.Caption : $"{slide.Caption} · Abrir noticia");
        AutomationProperties.SetItemStatus(this, $"{index + 1} de {_slides.Count} · {slide.Id}");
        for (var i = 0; i < Indicators.Children.Count; i++) ((RadioButton)Indicators.Children[i]).IsChecked = i == index;
    }
    private void ResetMotion()
    {
        ++_transitionGeneration;
        _transitioning = false;
        foreach (var image in new[] { CurrentImage, PreviousImage })
        {
            if (image.RenderTransform is TranslateTransform transform) transform.BeginAnimation(TranslateTransform.XProperty, null);
            image.RenderTransform = Transform.Identity;
        }
        PreviousImage.Source = null;
    }
    private void SettleTransition()
    {
        var pending = _pendingSelection;
        ResetMotion(); _pendingSelection = null;
        if (pending is { } next && next.Index < _slides.Count) Select(next.Index, animate: false);
    }
    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_slides.Count == 0) return;
        Select((NavigationIndex + _slides.Count - 1) % _slides.Count, true, direction: -1); UpdatePlayback();
    }
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_slides.Count == 0) return;
        Select((NavigationIndex + 1) % _slides.Count, true, direction: 1); UpdatePlayback();
    }
    private void Carousel_KeyDown(object sender, KeyEventArgs e)
    {
        if (_slides.Count < 1) return;
        if (e.Key == Key.Left) { Previous_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.Right) { Next_Click(this, e); e.Handled = true; }
    }
    private void OpenSlide_Click(object sender, RoutedEventArgs e)
    {
        if (_slides.Count == 0 || _slides[_index].Link is not { Scheme: "https" } uri) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { LauncherDialog.Inform(Window.GetWindow(this), "No se pudo abrir la noticia", "No se pudo abrir el navegador predeterminado. Vuelve a intentarlo más tarde."); }
    }
    private void Picture_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SettleTransition();
        PictureViewport.Clip = new RectangleGeometry(new Rect(PictureViewport.RenderSize), 8, 8);
        UpdatePlayback();
    }
}
