using System.Text.Json;
using System.Text.Json.Serialization;
using MashiroLauncher.Core.Versions.Rules;

namespace MashiroLauncher.Core.Versions.Mojang;

[JsonConverter(typeof(ArgumentTokenConverter))]
public abstract record ArgumentToken;

public sealed record StringArgument(string Value) : ArgumentToken;

public sealed record ConditionalArgument(
    IReadOnlyList<Rule> Rules,
    IReadOnlyList<string> Values) : ArgumentToken;

public sealed class ArgumentTokenConverter : JsonConverter<ArgumentToken>
{
    public override ArgumentToken Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new StringArgument(reader.GetString()!);

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            var rules = root.GetProperty("rules").Deserialize<List<Rule>>(options)
                ?? new List<Rule>();
            var valueElem = root.GetProperty("value");
            var values = valueElem.ValueKind switch
            {
                JsonValueKind.String => (IReadOnlyList<string>)[valueElem.GetString()!],
                JsonValueKind.Array => valueElem.EnumerateArray().Select(v => v.GetString()!).ToList(),
                _ => throw new JsonException(
                    $"ArgumentToken.value must be string or array, got {valueElem.ValueKind}"),
            };
            return new ConditionalArgument(rules, values);
        }

        throw new JsonException($"Unexpected token in ArgumentToken: {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, ArgumentToken value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case StringArgument s:
                writer.WriteStringValue(s.Value);
                break;
            case ConditionalArgument c:
                writer.WriteStartObject();
                writer.WritePropertyName("rules");
                JsonSerializer.Serialize(writer, c.Rules, options);
                writer.WritePropertyName("value");
                if (c.Values.Count == 1)
                    writer.WriteStringValue(c.Values[0]);
                else
                    JsonSerializer.Serialize(writer, c.Values, options);
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException($"Unsupported ArgumentToken type: {value.GetType()}");
        }
    }
}
