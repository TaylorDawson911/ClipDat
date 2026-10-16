using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Clippr;

internal static class ComboBoxPopupPlacement
{
    internal static void Attach(ComboBox combo)
    {
        combo.MaxDropDownHeight = 280;
        combo.Loaded += (_, _) => Position(combo);
        combo.DropDownOpened += async (_, _) => await AlignAsync(combo);
        combo.DropDownClosed += (_, _) =>
        {
            if (FindPopup(combo) is { } popup) popup.VerticalOffset = 0;
        };
    }

    private static void Position(ComboBox combo)
    {
        combo.ApplyTemplate();
        if (FindPopup(combo) is not { } popup) return;
        popup.PlacementTarget = combo;
        popup.DesiredPlacement = PopupPlacementMode.BottomEdgeAlignedLeft;
    }

    private static async Task AlignAsync(ComboBox combo)
    {
        await Task.Delay(20);
        if (!combo.IsDropDownOpen || FindPopup(combo) is not { Child: FrameworkElement child } popup) return;
        try
        {
            var top = child.TransformToVisual(combo).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            var adjustment = combo.ActualHeight - top + 3;
            if (Math.Abs(adjustment) > 1) popup.VerticalOffset += adjustment;
        }
        catch (ArgumentException) { /* The popup can close before its visual tree is attached. */ }
    }

    private static Popup? FindPopup(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Popup popup) return popup;
            if (FindPopup(child) is { } found) return found;
        }
        return null;
    }
}
