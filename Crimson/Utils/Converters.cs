using System;
using System.Globalization;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;
using CommunityToolkit.WinUI.Converters;
using Microsoft.UI.Xaml.Media;

namespace Crimson.Utils;


public class BoolToVisibilityConverter : BoolToObjectConverter
{
    public BoolToVisibilityConverter()
    {
        TrueValue = Visibility.Visible;
        FalseValue = Visibility.Collapsed;
    }
}

public class BoolToInverseVisibilityConverter : BoolToObjectConverter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BoolToInverseVisibilityConverter"/> class.
    /// </summary>
    public BoolToInverseVisibilityConverter()
    {
        TrueValue = Visibility.Collapsed;
        FalseValue = Visibility.Visible;
    }
}

public class DriveSpaceColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool canInstall)
        {
            // Get colors from theme resources to respect light/dark mode
            if (canInstall)
            {
                return Application.Current.Resources["SystemFillColorSuccessBrush"] as SolidColorBrush;
            }
            else
            {
                return Application.Current.Resources["SystemFillColorCriticalBrush"] as SolidColorBrush;
            }
        }

        // Return default color if input is invalid
        return Application.Current.Resources["TextFillColorPrimaryBrush"] as SolidColorBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
