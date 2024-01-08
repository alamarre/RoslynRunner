using System.Text.Json;
using System.Text.Json.Serialization;

public class JsonRawStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument jsonDoc = JsonDocument.ParseValue(ref reader);
        var text = jsonDoc.RootElement.GetRawText();
        Console.WriteLine(text);
        return text;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteRawValue(value);
    }
}
