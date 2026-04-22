using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemplateCli.Models;

public sealed class TolerantUpdateStatusConverter : JsonConverter<UpdateStatus>
{
    public override UpdateStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (Enum.TryParse<UpdateStatus>(value, ignoreCase: true, out var status))
                return status;

            return UpdateStatus.None;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            var intValue = reader.GetInt32();
            if (Enum.IsDefined(typeof(UpdateStatus), intValue))
                return (UpdateStatus)intValue;

            return UpdateStatus.None;
        }

        return UpdateStatus.None;
    }

    public override void Write(Utf8JsonWriter writer, UpdateStatus value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(TolerantUpdateStatusConverter)])]
[JsonSerializable(typeof(TemplateCliConfig))]
[JsonSerializable(typeof(UpdateState))]
public partial class TemplateCliJsonContext : JsonSerializerContext;
