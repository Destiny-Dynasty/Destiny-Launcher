using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace DestinyLauncher.Linux;

internal static class UiMotion
{
    internal static void Initialize()
    {
        Button.IsPressedProperty.Changed.AddClassHandler<Button>((button, _) =>
        {
            if (App.IsVisualAudit || button.Classes.Any(name => name is "chrome" or "carousel" or "dot" or "edition")) return;
            var target = button.IsPressed ? 1 : 0;
            var transform = button.RenderTransform as TranslateTransform ?? new TranslateTransform();
            button.RenderTransform = transform;
            transform.Transitions = [new DoubleTransition { Property = TranslateTransform.YProperty, Duration = TimeSpan.FromMilliseconds(button.IsPressed ? 80 : 140), Easing = new QuadraticEaseOut() }];
            transform.Y = target;
        });
    }
    internal static void Enter(Control body)
    {
        if (App.IsVisualAudit) return;
        var transform = new TranslateTransform(0, 5);
        body.Transitions = null; body.Opacity = .4; body.RenderTransform = transform;
        body.Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }];
        transform.Transitions = [new DoubleTransition { Property = TranslateTransform.YProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new QuadraticEaseOut() }];
        Dispatcher.UIThread.Post(() => { body.Opacity = 1; transform.Y = 0; }, DispatcherPriority.Render);
    }
}
