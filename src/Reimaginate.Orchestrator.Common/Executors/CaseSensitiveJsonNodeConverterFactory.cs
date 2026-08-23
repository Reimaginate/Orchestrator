using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Reimaginate.Orchestrator.Common.Executors;

internal sealed class CaseSensitiveJsonNodeConverterFactory : JsonConverterFactory
{
    public static readonly CaseSensitiveJsonNodeConverterFactory Instance = new();

    private CaseSensitiveJsonNodeConverterFactory()
    {
    }

    public override bool CanConvert(Type typeToConvert)
        => typeToConvert == typeof(JsonNode)
           || typeToConvert == typeof(JsonObject)
           || typeToConvert == typeof(JsonArray);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(JsonObject))
        {
            return CaseSensitiveJsonNodeConverter<JsonObject>.Instance;
        }

        if (typeToConvert == typeof(JsonArray))
        {
            return CaseSensitiveJsonNodeConverter<JsonArray>.Instance;
        }

        return CaseSensitiveJsonNodeConverter<JsonNode>.Instance;
    }

    private sealed class CaseSensitiveJsonNodeConverter<TNode> : JsonConverter<TNode>
        where TNode : JsonNode
    {
        public static readonly CaseSensitiveJsonNodeConverter<TNode> Instance = new();

        public override TNode? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            var node = JsonNode.Parse(
                ref reader,
                new JsonNodeOptions { PropertyNameCaseInsensitive = false });

            return node as TNode
                   ?? throw new JsonException($"The JSON value could not be converted to {typeToConvert}.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            TNode value,
            JsonSerializerOptions options)
            => value.WriteTo(writer, options);
    }
}
