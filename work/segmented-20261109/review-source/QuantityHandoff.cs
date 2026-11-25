using System.Text.Json.Serialization;

// A blank field is intentionally left for Excel, not adopted as zero or guessed.
public sealed record QuantityHandoff(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("drawing_number")] string DrawingNumber,
    [property: JsonPropertyName("quantity_blank")] bool QuantityBlank,
    [property: JsonPropertyName("unit_blank")] bool UnitBlank)
{
    [JsonIgnore] public string MissingLabel => QuantityBlank && UnitBlank ? "数量、单位" : QuantityBlank ? "数量" : "单位";
}
