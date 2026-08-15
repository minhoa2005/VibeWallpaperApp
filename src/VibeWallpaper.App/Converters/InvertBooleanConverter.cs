using Microsoft.UI.Xaml.Data;

namespace VibeWallpaper.App.Converters;

public sealed class InvertBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool flag && !flag;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is bool flag && !flag;
}
