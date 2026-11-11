using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CadReviewUI;

// This is a presentation/decision adapter. It never invents a sum or changes
// the recognition source; a sum is usable only when the complete metadata and
// the frozen CAD default agree with one another.
public static class DuplicateQuantity
{
    const string NotePrefix = "重复图号数量：";
    static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "未提供" :
        value.Trim().Replace('\r', ' ').Replace('\n', ' ');
    static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "未识别";
    static bool Same(string? a, string? b) => string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    public static bool IsDuplicate(ItemRow row) => row.Src.Duplicate is { Count: > 1 };

    static bool CompleteStructure(DsDuplicate? d) => d != null && d.Count > 1 && d.Occurrences != null &&
        d.Occurrences.Count == d.Count && d.Occurrences.All(o => o != null && !string.IsNullOrWhiteSpace(o.FrameIndex) && o.CrossedOut.HasValue) &&
        d.Occurrences.Select(o => o.FrameIndex!.Trim()).Distinct(StringComparer.Ordinal).Count() == d.Count;

    public static string OccurrenceText(DsDuplicateOccurrence o)
    {
        string status = o.CrossedOut == true ? "打X，不参与累加" :
            o.CrossedOut == null ? "打X状态未提供" :
            o.IncludedInSum == true ? "未打X，可参与数量核对" : o.IncludedInSum == false ? "未计入数量范围" : "计入资格未提供";
        return $"图框 {Text(o.FrameIndex)}：{Number(o.Value)} {Text(o.Unit)}；范围 {Text(o.Scope)}；原文 {Text(o.Expression)}；{status}";
    }

    public static string Describe(ItemRow row)
    {
        var d = row.Src.Duplicate;
        if (d == null || d.Count <= 1) return "本行没有重复图框信息。";
        var occurrences = d.Occurrences ?? [];
        int included = occurrences.Count(o => o != null && o.CrossedOut == false && o.IncludedInSum == true);
        int active = occurrences.Count(o => o != null && o.CrossedOut == false);
        string includedText = HasValidSum(row) ? $"默认累加计入 {included} 处" :
            d.Mode == "single" ? $"默认数量计入 {included} 处" : $"可参与数量核对 {included} 处";
        var lines = new List<string> { $"物理图框 {d.Count} 处；未打X {active} 处；{includedText}。" };
        if (!CompleteStructure(d) || (d.ActiveCount.HasValue && d.ActiveCount != active))
            lines.Add("重复图框资料不完整或数量不一致，不能快捷选择；请对照原图人工处理。");
        lines.AddRange(occurrences.Where(o => o != null).Select(OccurrenceText));
        if (HasValidSum(row)) lines.Add(string.IsNullOrWhiteSpace(d.Unit)
            ? $"默认累加数量：{Number(d.Sum)}；单位未提供，请补齐后导出。"
            : $"默认累加：{Number(d.Sum)} {Text(d.Unit)}。");
        else if (d.Mode == "single") lines.Add("只有一处正常图框按原数量计入；打X图框不计入。");
        else if (d.Sum.HasValue) lines.Add($"识别出的默认数量为 {Number(d.Sum)}；单位或来源信息不完整，请人工处理。");
        else lines.Add("默认累加不可用，请对照原图处理。");
        if (d.SumCandidate.HasValue && !d.Sum.HasValue)
            lines.Add($"算术合计候选：{Number(d.SumCandidate)}（仅供核对，不能直接采用）。");
        return string.Join(Environment.NewLine, lines);
    }

    public static bool HasValidSum(ItemRow row)
    {
        var d = row.Src.Duplicate;
        if (!CompleteStructure(d) || row.IsCrossedOut || d!.Mode is not (null or "sum") || d.Sum is not decimal sum || sum < 0 ||
            row.Src.Cad.Quantity != sum || !Same(d.Unit, row.Src.Cad.Unit) ||
            (!string.IsNullOrWhiteSpace(d.Unit) && !Rules.Meaningful(d.Unit))) return false;
        var active = d.Occurrences.Where(o => o.CrossedOut == false).ToList();
        if (active.Count == 0 || active.Any(o => o.IncludedInSum != true || o.Value is null or < 0 ||
            !Same(o.Unit, d.Unit) || (!string.IsNullOrWhiteSpace(o.Unit) && !Rules.Meaningful(o.Unit)) ||
            !Rules.Meaningful(o.Scope) || !Same(o.Scope, active[0].Scope)) ||
            d.Occurrences.Any(o => o.CrossedOut == true && o.IncludedInSum != false) ||
            (d.ActiveCount.HasValue && d.ActiveCount != active.Count)) return false;
        try { return active.Sum(o => o.Value!.Value) == sum; }
        catch (OverflowException) { return false; }
    }

    public static List<DsDuplicateOccurrence> SingleChoices(ItemRow row)
    {
        var d = row.Src.Duplicate;
        if (!CompleteStructure(d) || row.IsCrossedOut) return [];
        return d!.Occurrences.Where(o => o.CrossedOut == false && o.Value is >= 0 &&
            (string.IsNullOrWhiteSpace(o.Unit) || Rules.Meaningful(o.Unit))).ToList();
    }

    public static DsDuplicateOccurrence? UnambiguousSingle(ItemRow row, IReadOnlyList<DsDuplicateOccurrence> choices)
    {
        var d = row.Src.Duplicate;
        // Do not silently choose a partial set when any non-X frame lacks a value.
        if (d == null || d.Single is not decimal one || choices.Count == 0 ||
            choices.Count != d.Occurrences.Count(o => o.CrossedOut == false) ||
            choices.Any(o => o.Value != one || !Same(o.Unit, choices[0].Unit) || !Same(o.Scope, choices[0].Scope))) return null;
        return choices[0];
    }

    static bool IsDuplicateActionNote(string value) =>
        (value.StartsWith(NotePrefix + "用户选择默认累加 ", StringComparison.Ordinal) && value.EndsWith("。", StringComparison.Ordinal)) ||
        (value.StartsWith(NotePrefix + "用户选择按图框 ", StringComparison.Ordinal) &&
            value.Contains(" 的一处计，数量 ", StringComparison.Ordinal) && value.Contains("，范围 ", StringComparison.Ordinal) &&
            value.EndsWith("。", StringComparison.Ordinal));

    static string PreviousNote(ItemRow row) => string.Join(Environment.NewLine,
        row.Note.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(s => !IsDuplicateActionNote(s) &&
                s is not (ItemRow.AutoEditNote or ItemRow.AutoAdoptNote)).Where(s => !string.IsNullOrWhiteSpace(s)));

    static string AddNote(string previous, string generated) => previous.Length == 0 ? generated : previous + Environment.NewLine + generated;
    public static string SumNote(ItemRow row) => $"{NotePrefix}用户选择默认累加 {Number(row.Src.Duplicate?.Sum)} {Text(row.Src.Duplicate?.Unit)}。";
    static string SingleNote(DsDuplicateOccurrence occurrence) =>
        $"{NotePrefix}用户选择按图框 {occurrence.FrameIndex} 的一处计，数量 {Number(occurrence.Value)} {Text(occurrence.Unit)}，范围 {Text(occurrence.Scope)}。";

    static bool CanChange(ItemRow row) => row.Mode != Rules.Exclude && !row.IsCrossedOut;
    // A same-unit choice changes quantity only; it must not undo a separately
    // entered unit or a deliberate clear. For genuinely different units, the
    // selected frame supplies a new override (or an explicit clear if missing).
    static string? SelectedUnitOverride(ItemRow row, DsDuplicateOccurrence occurrence)
    {
        if (Same(occurrence.Unit, row.OrigUnit)) return null;
        if (string.IsNullOrWhiteSpace(occurrence.Unit))
            return string.IsNullOrWhiteSpace(row.OrigUnit) ? null : ItemRow.ClearOverride;
        return occurrence.Unit.Trim();
    }

    public static bool SingleReplacesUnitOverride(ItemRow row, DsDuplicateOccurrence occurrence)
    {
        string? next = SelectedUnitOverride(row, occurrence);
        return next != null && !string.IsNullOrWhiteSpace(row.ConfUnit) && !Same(row.ConfUnit, next);
    }

    public static bool ApplySingle(ItemRow row, DsDuplicateOccurrence occurrence, bool acceptUnitReplacement = false)
    {
        if (!CanChange(row) || !SingleChoices(row).Contains(occurrence)) return false;
        if (SingleReplacesUnitOverride(row, occurrence) && !acceptUnitReplacement) return false;
        string? unit = SelectedUnitOverride(row, occurrence);
        string previous = PreviousNote(row);
        // A same-valued single is still an explicit override, including 0 + 0.
        row.ConfQuantity = Number(occurrence.Value);
        if (unit != null) row.ConfUnit = unit;
        row.Mode = Rules.Revise;
        row.Note = AddNote(previous, SingleNote(occurrence));
        return true;
    }

    public static bool ApplySum(ItemRow row)
    {
        if (!CanChange(row) || !HasValidSum(row)) return false;
        string previous = PreviousNote(row);
        row.ConfQuantity = "";
        // Restoring the quantity must never erase an independent unit edit or
        // the marker for a deliberately cleared unit.
        row.Mode = row.HasOverride ? Rules.Revise : row.IsCompleteOriginal ? Rules.Adopt : Rules.Pending;
        row.Note = AddNote(previous, SumNote(row));
        return true;
    }

    public static bool CanProposeSum(ItemRow row) => row.Mode == Rules.Pending && !row.HasOverride &&
        row.IsCompleteOriginal && row.Src.RequiresReviewSpecified && row.Src.WarningsSpecified &&
        string.IsNullOrWhiteSpace(row.Note) && HasValidSum(row);

    static bool HasValidSoleActiveDefault(ItemRow row)
    {
        var d = row.Src.Duplicate;
        if (!CompleteStructure(d) || row.IsCrossedOut || d!.Mode != "single" ||
            d.ActiveCount is not (null or 1) || d.Single is not decimal value || value < 0 ||
            row.Src.Cad.Quantity != value || !Rules.Meaningful(d.Unit) || !Same(d.Unit, row.Src.Cad.Unit)) return false;
        var active = d.Occurrences.Where(o => o.CrossedOut == false).ToList();
        return active.Count == 1 && active[0].IncludedInSum == true && active[0].Value == value &&
            Same(active[0].Unit, d.Unit) && d.Occurrences.Where(o => o.CrossedOut == true).All(o => o.IncludedInSum == false);
    }

    public static bool CanProposeDefault(ItemRow row) => CanProposeSum(row) ||
        (row.Mode == Rules.Pending && !row.HasOverride && row.IsCompleteOriginal &&
         row.Src.RequiresReviewSpecified && row.Src.WarningsSpecified && string.IsNullOrWhiteSpace(row.Note) &&
         HasValidSoleActiveDefault(row));

    public static string DefaultNote(ItemRow row) => HasValidSum(row) ? SumNote(row) :
        $"{NotePrefix}用户采用唯一未打X图框的数量 {Number(row.Src.Duplicate?.Single)} {Text(row.Src.Duplicate?.Unit)}。";
}

// Only available, non-X occurrences are shown. No choice is preselected.
public sealed class DuplicateChoiceWindow : Window
{
    readonly ListBox _choices = new() { MinHeight = 120, Margin = new Thickness(0, 8, 0, 8) };
    readonly Button _accept = new() { Content = "按所选图框计", IsEnabled = false, MinWidth = 120, Padding = new Thickness(10, 5, 10, 5) };
    public DsDuplicateOccurrence? Choice { get; private set; }

    public DuplicateChoiceWindow(ItemRow row, IReadOnlyList<DsDuplicateOccurrence> choices)
    {
        Title = "选择按哪一处计"; Width = 760; Height = 370; MinWidth = 480; MinHeight = 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new DockPanel { Margin = new Thickness(16) };
        var title = new TextBlock { Text = "各处数量、单位或范围不同，请对照原图选择一处。打X图框不在候选中。", TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(_accept); panel.Children.Add(buttons);
        foreach (var occurrence in choices) _choices.Items.Add(new ListBoxItem { Content = DuplicateQuantity.OccurrenceText(occurrence), Tag = occurrence });
        _choices.SelectionChanged += (_, _) => _accept.IsEnabled = _choices.SelectedItem != null;
        _accept.Click += (_, _) => { Choice = (_choices.SelectedItem as ListBoxItem)?.Tag as DsDuplicateOccurrence; DialogResult = Choice != null; };
        panel.Children.Add(_choices); Content = panel;
    }
}
