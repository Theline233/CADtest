using System.Windows;
using System.Windows.Controls;

namespace CadReviewUI;

// CFB review controls do not change grid editing, selection, or filter semantics.
public partial class MainWindow
{
    FileRow? OrderFile() => FileList.SelectedItem as FileRow ??
        (_task?.Files.Count == 1 ? _task.Files[0] :
            _task?.Files.FirstOrDefault(f => f.Id == (ItemsGrid.SelectedItem as ItemRow)?.FileId));

    void ReviewOrderMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var file = OrderFile();
        var mode = file == null ? null : _task?.OrderMode(file.Id);
        bool geometry = file != null && _task!.Items.Any(r => r.FileId == file.Id && r.Src.FrameLocation?.IsUsable == true);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            string? option = item.Tag as string;
            item.IsEnabled = option switch
            {
                ReviewOrder.Horizontal or ReviewOrder.Vertical => geometry,
                ReviewOrder.Original => file != null,
                _ => ItemsGrid.SelectedItems.Count == 1,
            };
            if (mode != null && option == mode) item.Header = ((string)item.Header).TrimEnd(' ', '✓') + " ✓";
            else item.Header = ((string)item.Header).TrimEnd(' ', '✓');
        }
    }

    void ReviewOrderMode_Click(object sender, RoutedEventArgs e)
    {
        if (_task == null || _busy || !CommitActiveEdit() || sender is not MenuItem { Tag: string mode }) return;
        var file = OrderFile();
        if (file == null) { Status("请先选中一张图纸或一行明细。"); return; }
        if (!_task.SetOrder(file.Id, mode)) { Status("没有可排序的图框位置；可选中明细手动调整。"); return; }
        ShowReviewOrder();
        Status(mode switch
        {
            ReviewOrder.Vertical => "导出顺序：纵向优先，先一列从上到下，再到右侧一列。",
            ReviewOrder.Horizontal => "导出顺序：横向优先，先一行从左到右，再到下方一行。",
            _ => "已恢复图纸的原顺序。",
        });
    }

    void ReviewOrderMove_Click(object sender, RoutedEventArgs e)
    {
        if (_task == null || _busy || !CommitActiveEdit() || ItemsGrid.SelectedItems.Count != 1 ||
            ItemsGrid.SelectedItem is not ItemRow row || sender is not MenuItem { Tag: string offset }) return;
        int direction = offset == "-1" ? -1 : 1;
        if (!_task.MoveItem(row, direction))
        { Status(direction < 0 ? "已在该图纸的第一行。" : "已在该图纸的最后一行。"); return; }
        ShowReviewOrder();
        Status(direction < 0 ? "已在图纸的导出顺序中上移一位（含隐藏行）。" :
            "已在图纸的导出顺序中下移一位（含隐藏行）。");
    }

    void ShowReviewOrder()
    {
        var selected = ItemsGrid.SelectedItem;
        // Clearing a temporary column sort reveals the newly saved reading
        // order; any active filter/search still applies only to the display.
        if (_view?.SortDescriptions.Count > 0) _view.SortDescriptions.Clear();
        foreach (var column in ItemsGrid.Columns) column.SortDirection = null;
        _view?.Refresh();
        if (selected != null && _view?.Contains(selected) == true) ItemsGrid.SelectedItem = selected;
        UpdateStats();
    }

    void MarkTreatmentNotRequired_Click(object sender, RoutedEventArgs e)
    {
        if (_task?.Kind != EngineKind.Segmented || _busy || !CommitActiveEdit() || ItemsGrid.SelectedItem is not ItemRow row)
            return;
        if (row.Mode == Rules.Exclude || row.NeedsCrossedOutInclusionReason ||
            _task.Files.Any(f => f.Id == row.FileId && f.State == "不纳入本次"))
        { Status("这条明细不纳入，请先处理排除状态。"); return; }
        if (_showDialog($"已对照原图确认 {row.EditDrawing} 无需表面处理？\n将记录为人工修订，保留识别原值，Excel 工艺栏留空。",
                "确认无需表面处理", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        row.ConfTreatment = TreatmentPolicy.None;
        Status("已标记无需表面处理，Excel 工艺栏将留空。");
    }

    internal Func<IReadOnlyList<string>, bool>? BlankTreatmentPrompt { get; set; }

    bool ConfirmTreatmentExceptions(TaskSession task, ValidationDto dto)
    {
        var items = task.Items.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var issue in dto.TreatmentExceptions)
        {
            if (!TreatmentPolicy.NeedsBlankApproval(issue.Disposition) || !seen.Add(issue.Id) ||
                !items.TryGetValue(issue.Id, out var row) || row.FileId != issue.FileId || row.EditDrawing.Trim() != issue.DrawingNumber.Trim())
                throw new InvalidDataException("表面处理待确认名单与当前明细不一致，请重新校验。");
            notes.Add($"{issue.DrawingNumber}　{TreatmentPolicy.Label(issue.Disposition)}\n{row.FileName} · 记录 {issue.Id}");
        }
        return BlankTreatmentPrompt != null ? BlankTreatmentPrompt(notes) :
            new ConfirmBlankTreatmentWindow(notes) { Owner = this }.ShowDialog() == true;
    }
}
