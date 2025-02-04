using System;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;
using CommunityToolkit.WinUI.Converters;
using Microsoft.UI.Xaml.Media;

namespace Crimson.Utils;

public class BigIntegerJsonConverter : JsonConverter<BigInteger>
{
    public override BigInteger Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                string stringValue = reader.GetString();
                // Try parsing the string as a BigInteger
                if (BigInteger.TryParse(stringValue, out BigInteger result))
                {
                    return result;
                }
                throw new JsonException($"Unable to convert \"{stringValue}\" to BigInteger");

            case JsonTokenType.Number:
                // Handle numeric values
                if (reader.TryGetInt64(out long longValue))
                {
                    return new BigInteger(longValue);
                }
                throw new JsonException("Number too large for Int64");

            default:
                throw new JsonException($"Unexpected token type: {reader.TokenType}");
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        BigInteger value,
        JsonSerializerOptions options)
    {
        // Write BigInteger as a string to preserve full precision
        writer.WriteStringValue(value.ToString());
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
