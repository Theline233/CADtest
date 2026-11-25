// Shared by the workbook validator and the desktop. A disposition is a human
// decision, not a new recognized CAD value.
internal static class TreatmentPolicy
{
    public const string None = "无需表面处理";
    public const string PendingEntry = "待确认";
    public const string Confirmed = "confirmed", NotRequired = "not_required",
        Missing = "missing", Unconfirmed = "unconfirmed", Conflict = "conflict";

    static readonly string[] Unknown = ["待核", "待确认", "待定", "未知", "未确定", "未指定",
        "按图", "见图", "?", "？", "N/A", "未确认", "尚未确认", "未识别", "待核对"];
    static readonly string[] ScopeOrConflict = ["工艺范围需核对", "存在多个工艺要求", "工艺包含否定/排除",
        "重复图框工艺缺失或冲突", "原材料已处理，不等于零件工艺", "硬度或厚度参数的对应工艺不明确",
        "工艺参数不一致", "同图号各处工艺不一致", "工艺标注归属不明",
        "treatment conflict", "工艺冲突", "工艺有冲突", "工艺适用范围未核对", "工艺范围待核"];
    static readonly string[] Uncertain = ["待确认", "需确认", "未确认", "待核", "待定", "未确定", "尚未确定", "未知", "未识别", "无法确定", "冲突"];

    public static bool HasConfirmedValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim() != None &&
        !Unknown.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) &&
        !Uncertain.Any(word => value.Contains(word, StringComparison.Ordinal));

    public static string Classify(string? original, string? edit, string? warnings, string? evidence)
    {
        if (edit == None) return NotRequired;
        if (HasConfirmedValue(edit)) return Confirmed;
        if (ScopeOrConflict.Any(word => warnings?.Contains(word, StringComparison.OrdinalIgnoreCase) == true) ||
            edit?.Contains("冲突", StringComparison.Ordinal) == true || original?.Contains("冲突", StringComparison.Ordinal) == true)
            return Conflict;
        if (string.IsNullOrWhiteSpace(edit) && HasConfirmedValue(original)) return Confirmed;
        return string.IsNullOrWhiteSpace(edit) && string.IsNullOrWhiteSpace(original) &&
            string.IsNullOrWhiteSpace(evidence) && warnings?.Contains("候选", StringComparison.Ordinal) != true &&
            warnings?.Contains("工艺需要确认", StringComparison.Ordinal) != true &&
            warnings?.Contains("工艺含", StringComparison.Ordinal) != true &&
            warnings?.Contains("工艺原文", StringComparison.Ordinal) != true
            ? Missing : Unconfirmed;
    }

    public static bool NeedsBlankApproval(string? disposition) => disposition is Missing or Unconfirmed;
    public static string Label(string disposition) => disposition switch
    {
        NotRequired => None, Missing => "未识别到工艺", Unconfirmed => "工艺待确认",
        Conflict => "工艺冲突或适用范围待核", _ => "已有工艺",
    };
}
