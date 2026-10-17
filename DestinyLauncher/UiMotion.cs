using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DestinyLauncher;

/// <summary>Event-driven feedback honoring the Windows animation preference.</summary>
public static class UiMotion
{
    public static readonly DependencyProperty FeedbackProperty = DependencyProperty.RegisterAttached(
        "Feedback", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnFeedbackChanged));
    public static bool GetFeedback(DependencyObject obj) => (bool)obj.GetValue(FeedbackProperty);
    public static void SetFeedback(DependencyObject obj, bool value) => obj.SetValue(FeedbackProperty, value);
    private static void OnFeedbackChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        if (obj is not FrameworkElement element) return;
        if ((bool)e.NewValue)
        {
            element.PreviewMouseLeftButtonDown += Press;
            element.PreviewMouseLeftButtonUp += Release;
            element.MouseLeave += Release;
            element.LostMouseCapture += Release;
        }
        else
        {
            element.PreviewMouseLeftButtonDown -= Press;
            element.PreviewMouseLeftButtonUp -= Release;
            element.MouseLeave -= Release;
            element.LostMouseCapture -= Release;
        }
    }
    private static void Press(object sender, MouseButtonEventArgs e) => Move((FrameworkElement)sender, 1, 80);
    private static void Release(object sender, MouseEventArgs e) => Move((FrameworkElement)sender, 0, 140);
    private static void Move(FrameworkElement element, double y, int milliseconds)
    {
        if (element.RenderTransform is not TranslateTransform transform)
            element.RenderTransform = transform = new TranslateTransform();
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        if (!SystemParameters.ClientAreaAnimation) { transform.Y = 0; return; }
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
    internal static void Enter(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        if (!SystemParameters.ClientAreaAnimation) return;
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.4, 1, TimeSpan.FromMilliseconds(180)));
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
    internal static void UpdateProgress(ProgressBar bar, double value)
    {
        var previous = bar.Value;
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        bar.Value = value;
        if (!SystemParameters.ClientAreaAnimation || value < previous) return;
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
            new DoubleAnimation(previous, value, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
    }
}
