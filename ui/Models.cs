using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CadReviewUI;

// ---- 识别引擎产出的数据集（source.json），只读取界面需要的字段，未知字段忽略 ----
public sealed class CadVals
{
    [JsonPropertyName("drawing_number")] public string DrawingNumber { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("material")] public string? Material { get; set; }
    [JsonPropertyName("treatment")] public string? Treatment { get; set; }
    [JsonPropertyName("quantity")] public decimal? Quantity { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
}

public sealed class DsItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("file_id")] public string FileId { get; set; } = "";
    [JsonPropertyName("cad")] public CadVals Cad { get; set; } = new();
    string _warnings = "";
    [JsonPropertyName("warnings")] public string Warnings
    {
        get => _warnings;
        set { _warnings = value ?? ""; WarningsSpecified = value != null; }
    }
    [JsonIgnore] public bool WarningsSpecified { get; private set; }
    bool _requiresReview;
    [JsonPropertyName("requires_review")] public bool RequiresReview
    {
        get => _requiresReview;
        set { _requiresReview = value; RequiresReviewSpecified = true; }
    }
    // A missing field is not evidence that a new row has no review warning.
    [JsonIgnore] public bool RequiresReviewSpecified { get; private set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("parents")] public string[] Parents { get; set; } = [];
    [JsonPropertyName("quantity_evidence")] public string QuantityEvidence { get; set; } = "";
    [JsonPropertyName("treatment_evidence")] public string TreatmentEvidence { get; set; } = "";
    [JsonPropertyName("reference_info")] public string ReferenceInfo { get; set; } = "";
    // Optional in schema 1: old, frozen tasks have no duplicate metadata.
    [JsonPropertyName("duplicate")] public DsDuplicate? Duplicate { get; set; }
    // 识别引擎给出的初始处理方式（目前只有“图面整框打X → 不纳入”）。旧任务没有此字段。
    [JsonPropertyName("default_decision")] public DsDefaultDecision? DefaultDecision { get; set; }
}

public sealed class DsDuplicate
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("active_count")] public int? ActiveCount { get; set; }
    [JsonPropertyName("occurrences")] public List<DsDuplicateOccurrence> Occurrences { get; set; } = [];
    [JsonPropertyName("sum")] public decimal? Sum { get; set; }
    [JsonPropertyName("sum_candidate")] public decimal? SumCandidate { get; set; }
    [JsonPropertyName("single")] public decimal? Single { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
}

public sealed class DsDuplicateOccurrence
{
    [JsonPropertyName("frame_index"), JsonConverter(typeof(FrameIndexConverter))] public string? FrameIndex { get; set; }
    [JsonPropertyName("value")] public decimal? Value { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("expression")] public string? Expression { get; set; }
    [JsonPropertyName("crossed_out")] public bool? CrossedOut { get; set; }
    [JsonPropertyName("included_in_sum")] public bool? IncludedInSum { get; set; }
}

// Frame identifiers from the extractor can be numbers or strings (e.g. a
// subpart identifier). Preserve either representation without guessing a new
// ordinal or silently erasing a string identifier.
public sealed class FrameIndexConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.Number)
        {
            using var number = JsonDocument.ParseValue(ref reader);
            return number.RootElement.GetRawText();
        }
        throw new JsonException("图框序号必须是数字或字符串。");
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public sealed class DsDefaultDecision
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

public sealed class DsFile
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("diagnostics")] public string Diagnostics { get; set; } = "";
    [JsonPropertyName("requires_acknowledgement")] public bool RequiresAcknowledgement { get; set; }
}

public sealed class Dataset
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
    [JsonPropertyName("task_id")] public string TaskId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("files")] public List<DsFile> Files { get; set; } = [];
    [JsonPropertyName("items")] public List<DsItem> Items { get; set; } = [];
}

// ---- 人工复核决定（界面自己的存档，同时可写入复核 Excel 的黄色区） ----
public sealed class ItemDecision
{
    public string Mode { get; set; } = Rules.Pending;
    public string DrawingNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public string Material { get; set; } = "";
    public string Treatment { get; set; } = "";
    public string Quantity { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Note { get; set; } = "";

    public bool IsBlank =>
        Mode == Rules.Pending && DrawingNumber == "" && Name == "" && Material == "" &&
        Treatment == "" && Quantity == "" && Unit == "" && Note == "";
}

public sealed class FileDecision
{
    public string State { get; set; } = "待核查";
    public string Note { get; set; } = "";
    public string ParentChildNote { get; set; } = "";
}

public sealed class DecisionSet
{
    public int Schema { get; set; } = 1;
    public string TaskId { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public DateTime SavedUtc { get; set; }
    public Dictionary<string, ItemDecision> Items { get; set; } = [];
    public Dictionary<string, FileDecision> Files { get; set; } = [];
}

// ---- 校验/导出引擎的输出 ----
public sealed class ValidationDto
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("task_id")] public string TaskId { get; set; } = "";
    [JsonPropertyName("review_sha256")] public string? ReviewSha256 { get; set; }
    // Produced only by the in-process formatter after it validates the workbook.
    // An engine JSON payload cannot supply this value.
    [JsonIgnore] public string? VerifiedFormattedSha256 { get; internal set; }
    [JsonIgnore] public string? VerifiedAssociationSha256 { get; internal set; }
    [JsonPropertyName("pending")] public int Pending { get; set; }
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = [];
    [JsonPropertyName("accepted")] public List<JsonElement> Accepted { get; set; } = [];
    [JsonPropertyName("excluded")] public List<JsonElement> Excluded { get; set; } = [];
    // 引擎出错时输出 {"status":"failed","error":"..."}
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public static class Rules
{
    public const string Pending = "待复核", Adopt = "采用图纸", Revise = "人工修订", Exclude = "不纳入";
    public static readonly string[] Modes = [Pending, Adopt, Revise, Exclude];
    public static readonly string[] FileStates = ["待核查", "已核查", "无需确认", "不纳入本次"];

    static readonly string[] Unknown = ["待核", "待确认", "待定", "未知", "未确定", "未指定", "按图", "见图", "?", "？", "N/A"];

    // 与引擎校验保持一致：空白或“待核/未知”之类不算有效值
    public static bool Meaningful(string? s) =>
        !string.IsNullOrWhiteSpace(s) && !Unknown.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool LooksUnresolved(string? value)
    {
        string note = value?.Trim() ?? "";
        if (note.Length == 0) return false;
        if (!Meaningful(note)) return true;
        note = Regex.Replace(note, @"(?:无|没有|不存在|不再|已消除|已解决)(?:需|有)?(?:待核|未核|待确认|未确认|未解决|不确定|未确定|未知|冲突|待处理|未完成|待答复|存疑)", "");
        note = Regex.Replace(note, @"(?:冲突|疑问)(?:已解决|已确认|已核实)", "");
        return new[] { "待核", "未核", "待确认", "需确认", "需要确认", "未确认", "未解决", "不确定", "未确定", "未知", "无法判断", "无法确定", "需核查", "需要核查", "冲突", "尚未", "暂未", "待定", "存疑", "仍需", "尚需", "待处理", "未完成", "待答复", "需客户", "等待" }
            .Any(word => note.Contains(word, StringComparison.Ordinal));
    }

    public static bool Complete(CadVals c) =>
        Meaningful(c.DrawingNumber) && c.Quantity.HasValue && c.Quantity >= 0 &&
        Meaningful(c.Unit) && Meaningful(c.Material) && Meaningful(c.Treatment);

    public static List<string> MissingFields(CadVals c)
    {
        var m = new List<string>();
        if (!Meaningful(c.DrawingNumber)) m.Add("图号");
        if (!c.Quantity.HasValue) m.Add("数量");
        if (!Meaningful(c.Unit)) m.Add("单位");
        if (!Meaningful(c.Material)) m.Add("材质");
        if (!Meaningful(c.Treatment)) m.Add("工艺");
        return m;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("文件内容为空：" + path);

    public static void Write<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }
}
