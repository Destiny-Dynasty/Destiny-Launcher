using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DestinyLauncher;

internal static class DialogBackdrop
{
    private static readonly ConditionalWeakTable<Window, Layer> Layers = new();

    internal static void Attach(Window dialog)
    {
        Window? parent = null;
        dialog.Loaded += (_, _) =>
        {
            if (parent is not null || dialog.Owner is not { Content: FrameworkElement content } owner) return;
            parent = owner;
            if (Layers.TryGetValue(owner, out var existing)) { existing.Count++; return; }
            var layer = AdornerLayer.GetAdornerLayer(content);
            var shade = new Shade(content);
            var state = new Layer(content, content.Effect, layer, shade);
            Layers.Add(owner, state);
            content.SetCurrentValue(UIElement.EffectProperty, new BlurEffect { Radius = 4, RenderingBias = RenderingBias.Performance });
            layer?.Add(shade);
        };
        dialog.Closed += (_, _) =>
        {
            if (parent is null || !Layers.TryGetValue(parent, out var state) || --state.Count > 0) return;
            state.Adorners?.Remove(state.Shade);
            state.Content.SetCurrentValue(UIElement.EffectProperty, state.PreviousEffect);
            Layers.Remove(parent);
            parent = null;
        };
    }

    private sealed class Layer(FrameworkElement content, Effect? previousEffect, AdornerLayer? adorners, Shade shade)
    {
        internal readonly FrameworkElement Content = content;
        internal readonly Effect? PreviousEffect = previousEffect;
        internal readonly AdornerLayer? Adorners = adorners;
        internal readonly Shade Shade = shade;
        internal int Count = 1;
    }

    private sealed class Shade(UIElement content) : Adorner(content)
    {
        protected override void OnRender(DrawingContext drawingContext)
        {
            drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(92, 8, 9, 7)), null, new Rect(AdornedElement.RenderSize));
        }
    }
}
