using System.Windows;
using System.Windows.Controls;

namespace CadReviewUI;

public partial class MainWindow
{
    void BulkTreatment_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_task is not { } task || _busy) return;
        // This action must not commit a pending cell edit (which can also include a crossed-out row).
        if (ItemsGrid.CurrentItem is ItemRow editing && ItemsGrid.CurrentColumn?.GetCellContent(editing) is TextBox)
        {
            Status("请先按 Enter 提交或 Esc 取消正在编辑的单元格，再批量替换。");
            return;
        }

        var selected = Selected();
        if (selected.Count == 0)
        {
            Status("请先选中要处理的行（Ctrl 或 Shift 可多选），再批量替换。");
            return;
        }
        string fileId = (FileList.SelectedItem as FileRow)?.Id ?? selected[0].FileId;
        if (selected.Any(row => row.FileId != fileId))
        {
            Status("请先在左侧选择一张图纸，再选中该图纸内要处理的行。批量替换不跨图纸。");
            return;
        }

        var file = task.Files.Single(f => f.Id == fileId);
        var eligible = file.State == "不纳入本次" ? [] : selected.Where(row => !row.IsCrossedOut && row.Mode != Rules.Exclude).ToArray();
        var byValue = eligible.ToLookup(row => row.EditTreatment, StringComparer.Ordinal);
        var version = _editVersion;
        var dialog = new BulkTreatmentWindow(file.Name, selected.Count, byValue) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            Status("已取消批量替换，未修改明细。");
            return;
        }
        if (!ReferenceEquals(task, _task) || _editVersion != version)
        {
            Status("复核内容已变化，未替换。请重新选择并确认。");
            return;
        }

        int changed = 0;
        foreach (var row in byValue[dialog.CurrentValue])
        {
            string previous = row.ConfTreatment;
            row.EditTreatment = dialog.Replacement;
            if (row.ConfTreatment != previous) changed++;
        }
        Status(changed > 0
            ? $"已替换 {changed} 条表面处理。请核对人工修订，保存后可在详情中对照识别原值。"
            : "命中行与识别原值一致，未产生修订。");
        _view?.Refresh();
        UpdateStats();
    }
}
