using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        CloudSyncHttpService.NormalizeUtc(reader.GetDateTime());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(CloudSyncHttpService.NormalizeUtc(value).ToString("O"));
}
