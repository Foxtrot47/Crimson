using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crimson.Utils;

public sealed class BigIntegerJsonConverter : JsonConverter<BigInteger>
{
    public override BigInteger Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var stringValue = reader.GetString();
                if (BigInteger.TryParse(stringValue, out var result))
                    return result;

                throw new JsonException($"Unable to convert \"{stringValue}\" to BigInteger");

            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var longValue))
                    return new BigInteger(longValue);

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
        writer.WriteStringValue(value.ToString());
    }
}
