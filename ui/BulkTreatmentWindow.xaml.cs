using System.Windows;

namespace CadReviewUI;

public partial class BulkTreatmentWindow : Window
{
    readonly ILookup<string, ItemRow> _eligible;
    internal string CurrentValue => CurrentBox.Text;
    internal string Replacement => ReplacementBox.Text.Trim();

    internal BulkTreatmentWindow(string drawingName, int selectedCount, ILookup<string, ItemRow> eligible)
    {
        _eligible = eligible;
        InitializeComponent();
        DrawingText.Text = drawingName;
        DrawingText.ToolTip = drawingName;
        int skipped = selectedCount - eligible.Sum(group => group.Count());
        ScopeText.Text = $"已选 {selectedCount} 条；只修改当前图纸的选中行。" +
            (skipped > 0 ? $"打 X 或不纳入的 {skipped} 条不参与匹配。" : "");
        CurrentBox.Text = eligible.FirstOrDefault()?.Key ?? "";
        CurrentBox.TextChanged += (_, _) => UpdatePreview();
        ReplacementBox.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    void Window_Loaded(object sender, RoutedEventArgs e) => ReplacementBox.Focus();

    void UpdatePreview()
    {
        int count = _eligible[CurrentValue].Count();
        MatchText.Text = $"命中 {count} 条";
        string? problem = count == 0
            ? (_eligible.Count == 0 ? "选中行均为打 X 或不纳入，未改动。" : "没有选中行的当前值与原值完全一致。")
            : Replacement.Length == 0 ? "请填写新值。"
            : Replacement.Contains(ItemRow.ClearOverride, StringComparison.Ordinal) ? "新值包含复核保留标记，请重新输入。"
            : Replacement == CurrentValue ? "原值与新值相同，无需替换。"
            : null;
        ReplaceButton.IsEnabled = problem == null;
        PreviewText.Text = problem ?? "确认后记录为人工编辑，识别原值和原文保留。";
    }

    void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (ReplaceButton.IsEnabled) DialogResult = true;
    }
}
