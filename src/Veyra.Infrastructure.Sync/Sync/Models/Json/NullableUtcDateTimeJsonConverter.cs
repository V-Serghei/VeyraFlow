using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class NullableUtcDateTimeJsonConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : CloudSyncHttpService.NormalizeUtc(reader.GetDateTime());

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(CloudSyncHttpService.NormalizeUtc(value.Value).ToString("O"));
    }
}
