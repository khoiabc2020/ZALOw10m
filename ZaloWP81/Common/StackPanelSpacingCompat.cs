using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace ZaloUWP.Common
{
    /// <summary>
    /// Provides StackPanel spacing on older Windows 10 Mobile runtimes that do not
    /// implement IStackPanel4 / StackPanel.Spacing.
    /// Call once after the panel's children have been added.
    /// </summary>
    internal static class StackPanelSpacingCompat
    {
        public static void Apply(StackPanel panel, double spacing)
        {
            if (panel == null || spacing <= 0) return;

            for (int i = 0; i < panel.Children.Count - 1; i++)
            {
                FrameworkElement child = panel.Children[i] as FrameworkElement;
                if (child == null) continue;

                Thickness margin = child.Margin;
                child.Margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom + spacing);
            }
        }
    }
}
