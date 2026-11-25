using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClosedXML.Excel;

namespace CadReviewUI;

internal static class DirectExportSelfTest
{
    internal static async Task<int> RunAsync(string output, string review)
    {
        Directory.CreateDirectory(output);
        Engine.SessionsRoot = Path.Combine(output, "sessions");
        var messages = new List<string>();
        var window = new MainWindow(false, (_, title, _, _) => { messages.Add(title); return MessageBoxResult.No; },
            null, (_, _, _, _) => { messages.Add("复核确认"); return false; })
        {
            Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.OpenTask(review);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            ((TextBox)window.FindName("ReviewerBox")).Text = "";
            var task = (TaskSession)typeof(MainWindow).GetField("_task",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            bool synthetic = task.Data.TaskId.StartsWith("SYNTHETIC-QUANTITY-", StringComparison.Ordinal);
            if (synthetic)
            {
                var missing = task.Items.Single(r => r.Id == "MISSING");
                missing.EditDrawing = "";
                missing.EditMaterial = "";
                missing.EditName = "人工修改";
                var invalid = task.Items.Single(r => r.Id == "BOTH");
                invalid.EditQuantity = "-7";
                if (!task.MoveItem(invalid, -1))
                    throw new InvalidOperationException("无法调整合成明细顺序。");
            }
            var before = task.Items.Select(r => (r.Mode, r.Note)).ToArray();
            var button = (Button)window.FindName("ExportButton");
            int choices = 0;
            window.DestinationSelector = (_, _) => { choices++; return null; };
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (choices != 1 || messages.Count != 0 || Directory.EnumerateFiles(output, "*.xlsx").Any())
                throw new InvalidDataException("取消保存时不应生成文件或弹出复核提示。");
            string? path = null;
            window.DestinationSelector = (name, current) =>
            {
                path = Path.Combine(output, name);
                return SafeExportDestination.Acquire(path, [current.Manifest, current.SourceXlsx], Engine.Root);
            };
            window.OpenExportSelection = _ => { };
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int i = 0; i < 300 && messages.Count == 0 && !File.Exists(path); i++)
                await Task.Delay(50);
            if (path == null || !File.Exists(path) || messages.Count != 0)
                throw new InvalidDataException("主导出按钮未直接保存；弹窗：" + string.Join("、", messages));
            if (!before.SequenceEqual(task.Items.Select(r => (r.Mode, r.Note))))
                throw new InvalidDataException("直接导出更改了复核决定。");
            using var book = new XLWorkbook(path);
            var ws = book.Worksheet("报价单样式");
            if (!ws.Cell("C2").GetString().StartsWith("【待核对】", StringComparison.Ordinal))
                throw new InvalidDataException("缺少待核对标记。");
            if (synthetic)
            {
                if (ws.Cell("C6").GetString() != "SYNTHETIC-BOTH" || !ws.Cell("C7").IsEmpty() ||
                    !ws.Cell("E7").IsEmpty() || ws.Cell("D7").GetString() != "人工修改" ||
                    !ws.Cell("G6").IsEmpty() || !ws.Cell("G7").IsEmpty() ||
                    ws.Cell("G8").GetDouble() != 0 || ws.Cell("G9").GetDouble() != 7 || !ws.Cell("C10").IsEmpty())
                    throw new InvalidDataException("缺项、人工修改、数量或打 X 排除与当前明细不一致。");
                ws.Cell("G6").Value = 3;
                ws.Cell("H6").Value = 2;
                book.RecalculateAllFormulas();
                if (ws.Cell("I6").GetDouble() != 6 || ws.Cell("I54").GetDouble() != 6)
                    throw new InvalidDataException("补填后金额或合计公式未正确计算。");
            }
            await window.RenderToPngAsync(Path.Combine(output, "workspace.png"));
            messages.Add($"PASS 主按钮不弹复核窗口，保存成功、复核决定不变：{path}");
            return 0;
        }
        catch (Exception ex) { messages.Add(ex.ToString()); return 1; }
        finally { window.Close(); File.WriteAllLines(Path.Combine(output, "checks.txt"), messages); }
    }
}
