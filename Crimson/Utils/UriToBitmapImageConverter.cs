using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Crimson.Utils;

public sealed class UriToBitmapImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language) =>
        value is Uri uri ? new BitmapImage(uri) : null;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
