using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace ZaloUWP.Converters
{
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public bool IsInverted { get; set; }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool val = value is bool b && b;
            if (IsInverted) val = !val;
            return val ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is Visibility vis)
            {
                bool b = vis == Visibility.Visible;
                return IsInverted ? !b : b;
            }
            return false;
        }
    }

    public class NullToVisibilityConverter : IValueConverter
    {
        public bool IsInverted { get; set; }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool hasValue = value != null && !string.IsNullOrEmpty(value.ToString());
            if (IsInverted) hasValue = !hasValue;
            return hasValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class MessageBubbleAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool isOutgoing = value is bool b && b;
            return isOutgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class TabColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush ActiveBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 136, 255)); // Vibrant Zalo Blue
        private static readonly SolidColorBrush InactiveBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 117, 117, 117)); // Gray

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int selected && parameter is string paramStr && int.TryParse(paramStr, out int target))
            {
                return selected == target ? ActiveBrush : InactiveBrush;
            }
            return InactiveBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class IntToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int count)
            {
                return count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
