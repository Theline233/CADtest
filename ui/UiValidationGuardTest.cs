using System.Reflection;
using System.Text;

namespace CadReviewUI;

/// <summary>Isolated regression: invalid quantity must never be accepted by UI.</summary>
public static class UiValidationGuardTest
{
    public static async Task<int> RunAsync(string reviewFolder, string output)
    {
        if (Directory.Exists(output)) throw new IOException("Test output already exists.");
        Directory.CreateDirectory(output);
        Engine.SessionsRoot = Path.Combine(output, "sessions");
        var checks = new List<string>();
        void Check(string name, bool ok)
        {
            checks.Add((ok ? "PASS " : "FAIL ") + name);
            if (!ok) throw new InvalidOperationException(name);
        }
        try
        {
            var window = new MainWindow();
            window.OpenTask(reviewFolder);
            var field = typeof(MainWindow).GetField("_task", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Task field not found");
            var task = (TaskSession?)field.GetValue(window) ?? throw new InvalidOperationException("Task not opened");
            var row = task.Items.First(i => i.Src.Cad.Quantity is >= 0 && Rules.Meaningful(i.OrigDrawing));
            foreach (var item in task.Items) { item.Mode = Rules.Exclude; item.Note = "合成自测：排除"; }
            foreach (var file in task.Files)
            {
                file.State = file.Id == row.FileId ? "已核查" : "不纳入本次";
                file.Note = "合成自测：文件范围";
                file.ParentChildNote = "合成自测：范围";
            }
            row.ConfMaterial = "合成测试材料";
            row.ConfTreatment = "合成测试工艺";
            row.ConfUnit = Rules.Meaningful(row.OrigUnit) ? row.OrigUnit : "EA";
            row.ConfQuantity = "not-a-number";
            row.Note = "合成自测：无效数量";
            row.Mode = Rules.Revise;
            var problems = task.WriteReviewed();
            Check("invalid quantity produces a local write error", problems.Any(p => p.Contains("确认数量")));
            Check("failed write does not publish a review workbook", !File.Exists(task.ReviewedPath));
            row.ConfQuantity = "1";
            Check("valid quantity can be written", task.WriteReviewed().Count == 0 && File.Exists(task.ReviewedPath));
            var goodHash = TaskSession.Sha256(task.ReviewedPath);
            row.ConfQuantity = "not-a-number";
            problems = task.WriteReviewed();
            Check("invalid write leaves the previous valid workbook unchanged", problems.Count > 0 && TaskSession.Sha256(task.ReviewedPath) == goodHash);
            var method = typeof(MainWindow).GetMethod("RunValidate", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("UI validation method missing");
            var pending = (Task<ValidationDto?>?)method.Invoke(window, null)
                ?? throw new InvalidOperationException("UI validation did not return a task");
            var actual = await pending;
            Check("UI blocks before export on the local write error", actual?.Status == "blocked" && actual.Errors.Any(p => p.Contains("确认数量")));
            Check("user input remains available for correction", row.ConfQuantity == "not-a-number");
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks, new UTF8Encoding(false));
            return 0;
        }
        catch (Exception ex)
        {
            checks.Add("ERROR " + ex.Message);
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks, new UTF8Encoding(false));
            return 1;
        }
    }
}
