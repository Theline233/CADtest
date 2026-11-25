using System.Globalization;
using System.Windows;

namespace CadReviewUI;

public partial class MainWindow
{
    async void Export_Click(object sender, RoutedEventArgs e) => await ExportDraftAsync(batch: false);

    async Task<bool> ExportDraftAsync(bool batch)
    {
        if (_task == null || _busy || _isDemo) return false;
        if (!CommitActiveEdit()) return false;
        var task = _task;
        var files = task.Files.Where(f => f.State != "不纳入本次").Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        // Take the task's actual export order, not the screen's filter or sort.
        // This is a read-only snapshot: it never adopts pending rows or approves files.
        var rows = task.Items.Where(r => files.Contains(r.FileId) && r.Mode != Rules.Exclude &&
            (!r.IsCrossedOut || (r.Mode is Rules.Adopt or Rules.Revise && r.HasCrossedOutInclusionReason)))
            .Select(r => new ExactQuotationRow(r.Id, r.FileId, DraftText(r.EditDrawing), DraftText(r.EditName),
                DraftText(r.EditMaterial), TreatmentPolicy.HasConfirmedValue(r.EditTreatment) ? r.EditTreatment.Trim() : null,
                DraftQuantity(r.EditQuantity), DraftText(r.EditUnit), r.FileName))
            .ToArray();
        if (rows.Length == 0)
        {
            Status("没有可导出的明细：未识别到明细，或已全部排除。");
            return false;
        }

        try
        {
            var included = rows.GroupBy(r => r.FileId).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var fileName = Path.GetFileNameWithoutExtension(SafeExportDestination.DefaultName(task, included)) + "_待核对.xlsx";
            using var destination = DestinationSelector(fileName, task);
            if (destination == null)
            {
                Status("已取消保存，未导出。");
                return false;
            }
            SetBusy(true);
            Status("正在导出 Excel…");
            string? backup = await Task.Run(() => destination.PublishDraft(QuotationFormatter.BuildDraft(rows)));
            _lastExportedPath = destination.Path;
            _lastExportWarning = null;
            Status($"已导出 {rows.Length} 条待核对明细：{destination.Path}" +
                (backup == null ? "" : $"（原文件备份：{backup}）"));
            return true;
        }
        catch (Exception ex)
        {
            Status("导出失败：" + ex.Message);
            if (!batch) _showDialog(ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { SetBusy(false); }
    }

    static string? DraftText(string value) => Rules.Meaningful(value) ? value.Trim() : null;

    static decimal? DraftQuantity(string value)
    {
        string text = value.Trim();
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            ItemRow.SameQuantity(text, number.ToString(CultureInfo.InvariantCulture)) &&
            ExactTemplateWriter.QuantityFitsExcel(number) ? number : null;
    }
}
