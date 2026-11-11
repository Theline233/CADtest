using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class NumericSanitization
{
    public string Policy => "Non-finite CAD numbers are null, never zero; count covers all report fields.";
    public int ReplacedCount { get; set; }
}

public sealed class FiniteDoubleConverter(NumericSanitization audit) : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDouble();
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsFinite(value)) writer.WriteNumberValue(value);
        else { audit.ReplacedCount++; writer.WriteNullValue(); }
    }
}
