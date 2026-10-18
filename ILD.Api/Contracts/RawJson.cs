using System.Text.Json;
using System.Text.Json.Serialization;

namespace ILD.Api.Contracts;

/// <summary>
/// JSON text that a response writes as it is, rather than re-encoding it, so a
/// client reads exactly the bytes the server rendered.
/// </summary>
[JsonConverter(typeof(RawJsonConverter))]
public sealed record RawJson(string Text);

internal sealed class RawJsonConverter : JsonConverter<RawJson>
{
    public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("RawJson is written, never read.");

    public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options)
        => writer.WriteRawValue(value.Text);
}
