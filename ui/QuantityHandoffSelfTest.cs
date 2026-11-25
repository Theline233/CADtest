using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClosedXML.Excel;

namespace CadReviewUI;

// The same confirmation and formatter used by normal desktop export.
internal static class QuantityHandoffSelfTest
{
    internal static async Task<int> RunAsync(string output, string review)
    {
        Directory.CreateDirectory(output);
        Engine.SessionsRoot = Path.Combine(output, "sessions");
        var lines = new List<string>();
        void Check(string title, bool passed)
        {
            lines.Add((passed ? "PASS " : "FAIL ") + title);
            if (!passed) throw new InvalidDataException(title);
        }
        bool confirm = false;
        string scope = "";
        int confirmations = 0;
        var selected = Path.Combine(output, "quote.xlsx");
        var archive = Path.Combine(output, "archive");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var window = new MainWindow(false, (text,title,_,_) => { lines.Add(title + "：" + text); return MessageBoxResult.No; },
            null, (_,_,_,summary) => { scope = summary; confirmations++; return confirm; })
        {
            Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.ExportFolderFactory = () => archive;
            window.OpenExportSelection = _ => { };
            window.Show();
            window.OpenTask(review);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            ((TextBox)window.FindName("ReviewerBox")).Text = "SYNTHETIC QUANTITY TEST ONLY";
            var task = (TaskSession)typeof(MainWindow).GetField("_task",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
            Check("synthetic fixture only",task.Data.TaskId.StartsWith("SYNTHETIC-QUANTITY-",StringComparison.Ordinal));
            window.DestinationSelector = (_, current) => SafeExportDestination.Acquire(selected, [current.Manifest, current.SourceXlsx], Engine.Root);
            var handler = typeof(MainWindow).GetMethod("ExportCurrentAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
            Task<bool> Export() => ((Task<bool>)handler.Invoke(window,[false])!).WaitAsync(timeout.Token);

            Check("cancelling quantity handoff publishes nothing and does not adopt pending rows",
                !await Export() && confirmations == 1 && !File.Exists(selected) &&
                task.Items.First(r => r.Id == "MISSING").Mode == Rules.Pending);
            Check("confirmation says how many and what fields to fill in Excel",
                scope.Contains("3 条明细的数量或单位待补") && scope.Contains("Excel 补填") &&
                scope.Contains("SYNTHETIC-BOTH（数量、单位）"));

            var blocked = task.Items.Single(r => r.Id == "ZERO");
            string previous = blocked.ConfQuantity;
            blocked.ConfQuantity = "-2";
            blocked.Mode = Rules.Revise;
            confirm = true;
            Check("explicit invalid negative quantity cannot be exported as a blank",
                !await Export() && confirmations == 1 && !File.Exists(selected));
            blocked.ConfQuantity = previous;
            blocked.Mode = Rules.Pending;

            Check("one confirmation exports all four eligible rows without filling quantities in the app",
                await Export() && confirmations == 2 && File.Exists(selected));
            Check("quantity handoff does not invent values nor include crossed out rows",
                task.Items.Single(r => r.Id == "MISSING").EditQuantity == "" &&
                task.Items.Single(r => r.Id == "CROSSED").Mode == Rules.Exclude);
            var audited = await task.ValidateWithReviewLockAsync(
                TaskSession.Sha256(task.ReviewedPath), timeout.Token);
            Check("guarded snapshot lists all three missing-field cases", audited.Status == "ready" &&
                audited.QuantityHandoffs.Select(r => (r.Id, r.QuantityBlank, r.UnitBlank)).SequenceEqual(
                    new[]{("MISSING",true,false),("BOTH",true,true),("UNIT",false,true)}));
            string denied = Path.Combine(output,"stale-confirmation");
            var stale = await task.ExportQuotationWithArchiveLockAsync(denied, "SYNTHETIC ONLY", audited.ReviewSha256!,
                timeout.Token, allowBlankQuantityFor: new string('0',64));
            Check("stale approval publishes no workbook", stale.Status == "blocked" &&
                !Directory.EnumerateFiles(denied,"*.xlsx",SearchOption.AllDirectories).Any());

            var core = Path.Combine(archive,"复核记录","确认明细.xlsx");
            using var quote = new XLWorkbook(selected);
            using var confirmed = new XLWorkbook(core);
            var q = quote.Worksheet("报价单样式");
            var c = confirmed.Worksheet("确认明细");
            q.Cell("G6").Value = 3; q.Cell("H6").Value = 2;
            c.Cell("F8").Value = 3; c.Cell("H8").Value = 2;
            quote.RecalculateAllFormulas(); confirmed.RecalculateAllFormulas();
            Check("Excel entry calculates the existing line amount and total",
                q.Cell("I6").GetDouble() == 6 && q.Cell("I54").GetDouble() == 6 && c.Cell("I8").GetDouble() == 6);
            File.WriteAllText(Path.Combine(output,"exports.json"),JsonSerializer.Serialize(new{core,quote=selected},Json.Options));
            return 0;
        }
        catch (Exception ex) { lines.Add(ex.ToString()); return 1; }
        finally { window.Close(); File.WriteAllLines(Path.Combine(output,"checks.txt"),lines); }
    }
}
