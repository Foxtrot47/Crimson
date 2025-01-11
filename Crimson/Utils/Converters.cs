using System;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;
using CommunityToolkit.WinUI.Converters;

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
