using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CadReviewUI;

internal static class BulkTreatmentSelfTest
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    const string Original = "镀白锌", Replacement = "镀碱性蓝白锌", Interim = "合成中间修订";
    const string EntryName = "批量替换表面处理";

    internal static async Task<int> RunAsync(string output, string review, string otherReview)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || !output.StartsWith(Engine.OutputsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a new output below the workspace outputs.");
        Directory.CreateDirectory(output);
        Engine.SessionsRoot = Path.Combine(output, "sessions");
        var checks = new List<string>();
        var dialogs = new List<string>();
        MainWindow? lifetime = null;
        int exportsConfirmed = 0;
        try
        {
            MainWindow Show()
            {
                lifetime = new MainWindow(false,
                    (message, title, _, _) => { dialogs.Add(title + ": " + message); return MessageBoxResult.No; },
                    null, (items, files, excluded, _) =>
                    {
                        exportsConfirmed++;
                        Check("existing export confirmation covers 9 accepted, 3 excluded, 1 drawing", items == 9 && files == 1 && excluded == 3);
                        return true;
                    })
                { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                lifetime.Show();
                return lifetime;
            }

            var window = Show();
            ((TextBox)window.FindName("ReviewerBox")).Text = "SYNTHETIC TEST ONLY";
            var grid = (DataGrid)window.FindName("ItemsGrid");
            var column = grid.Columns.OfType<DataGridTextColumn>().Single(c => (c.Binding as Binding)?.Path.Path == nameof(ItemRow.EditTreatment));
            var originalOrder = column.SortDirection;
            async Task Idle() => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            TaskSession Current() => (TaskSession)typeof(MainWindow).GetField("_task", Private)!.GetValue(window)!;
            string Status() => ((TextBlock)window.FindName("StatusText")).Text;
            bool Save() => (bool)typeof(MainWindow).GetMethod("SaveNow", Private)!.Invoke(window, [false])!;
            Button Entry() => FindEntry(grid) ?? throw new InvalidOperationException("Treatment header action is not rendered");
            void Select(params ItemRow[] rows)
            {
                grid.SelectedItems.Clear();
                foreach (var row in rows) grid.SelectedItems.Add(row);
            }
            async Task NoDialog(string expectedMessage)
            {
                Click(Entry());
                await Idle();
                Check(expectedMessage, Status().Contains(expectedMessage, StringComparison.Ordinal) && !window.OwnedWindows.OfType<BulkTreatmentWindow>().Any());
            }
            void OnDialog(Action<BulkTreatmentWindow> interact)
            {
                Exception? failed = null;
                var inspect = window.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = window.OwnedWindows.OfType<BulkTreatmentWindow>().SingleOrDefault();
                    if (dialog == null) { failed = new InvalidOperationException("Dialog did not open"); return; }
                    try { interact(dialog); }
                    catch (Exception ex) { failed = ex; dialog.Close(); }
                }, DispatcherPriority.ApplicationIdle);
                // Raise the real header Button event synchronously so this dispatcher
                // callback runs inside ShowDialog's nested dispatcher frame.
                Entry().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!inspect.Task.IsCompleted) inspect.Abort();
                if (failed != null) throw failed;
                Check("header opens the actual modal", inspect.Task.IsCompletedSuccessfully);
            }
            void Preview(BulkTreatmentWindow dialog, string old, string next, int hits, bool canReplace)
            {
                ((TextBox)dialog.FindName("CurrentBox")).Text = old;
                ((TextBox)dialog.FindName("ReplacementBox")).Text = next;
                Check($"preview: {old} → {next}, {hits} hits, confirm={canReplace}",
                    ((TextBlock)dialog.FindName("MatchText")).Text == $"命中 {hits} 条" && ButtonIn(dialog, "ReplaceButton").IsEnabled == canReplace);
            }

            window.OpenTask(otherReview);
            var other = Current();
            Check("other task is synthetic", other.Data.TaskId.StartsWith("SYNTHETIC-CFB03-", StringComparison.Ordinal));
            string otherBefore = Decision(other.Items.Single());
            window.OpenTask(review);
            var task = Current();
            Check("primary task is synthetic and differs from other task", task.Data.TaskId.StartsWith("SYNTHETIC-CFB03-", StringComparison.Ordinal) && task.Data.TaskId != other.Data.TaskId);
            var rows = task.Items.ToDictionary(r => r.Id);
            ItemRow Row(int n) => rows["ITEM-" + n];
            var fileList = (ListBox)window.FindName("FileList");
            var six = Enumerable.Range(1, 6).Select(Row).ToArray();
            var f1 = task.Files.Single(f => f.Id == "F1");
            f1.State = "已核查";
            Row(2).Mode = Rules.Adopt;
            Row(3).ConfName = "SYNTHETIC prior name";
            Row(3).Note = "合成既有修改说明";
            Row(9).Mode = Rules.Exclude;
            Row(11).EditTreatment = Original;
            var f2 = task.Files.Single(f => f.Id == "F2");
            f2.State = "不纳入本次";
            f2.Note = "合成图纸排除说明";
            Check("baseline save", Save());
            string archiveBefore = File.ReadAllText(task.DecisionsPath);
            var before = rows.ToDictionary(pair => pair.Key, pair => Decision(pair.Value));

            grid.ScrollIntoView(Row(1), column);
            await Idle();
            Check("column action is visible and column sorting remains available", Entry().IsVisible && Entry().ActualWidth > 50 && column.CanUserSort);
            Select();
            await NoDialog("请先选中要处理的行");
            window.Width = window.MinWidth;
            await Idle();
            var more = (Button)window.FindName("RowMoreButton");
            var menuEntry = more.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "表面处理：批量替换…"));
            menuEntry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check("minimum-width window has the same action in More", more.IsVisible && more.ActualWidth > 40 && Status().Contains("请先选中要处理的行"));
            window.Width = 1500;
            await Idle();
            Select(Row(1), Row(12));
            await NoDialog("批量替换不跨图纸");
            Select(Row(12));
            OnDialog(d =>
            {
                Preview(d, Original, Replacement, 0, false);
                Click(ButtonIn(d, "CancelButton"));
            });

            fileList.SelectedItem = f1;
            var nameColumn = grid.Columns.OfType<DataGridTextColumn>().Single(c => (c.Binding as Binding)?.Path.Path == nameof(ItemRow.EditName));
            Select(Row(10));
            grid.CurrentCell = new DataGridCellInfo(Row(10), nameColumn);
            grid.ScrollIntoView(Row(10), nameColumn);
            await Idle();
            grid.Focus();
            Check("begin an actual edit on a default-excluded X row", grid.BeginEdit());
            ((TextBox)nameColumn.GetCellContent(Row(10))).Text = "SYNTHETIC uncommitted X edit";
            await NoDialog("请先按 Enter 提交或 Esc 取消");
            grid.CancelEdit(DataGridEditingUnit.Cell);
            grid.CancelEdit(DataGridEditingUnit.Row);
            Check("batch action did not commit X edit or include the X row", Decision(Row(10)) == before[Row(10).Id]);

            grid.ScrollIntoView(Row(1), column);
            await Idle();
            var mixed = six.Concat([Row(8), Row(9), Row(10)]).ToArray();
            Select(mixed);
            OnDialog(d =>
            {
                Check("first eligible current value fills the original field", ((TextBox)d.FindName("CurrentBox")).Text == Original);
                Check("preview exposes drawing, selection size and two excluded rows",
                    ((TextBlock)d.FindName("DrawingText")).Text == f1.Name && ((TextBlock)d.FindName("ScopeText")).Text.Contains("2 条不参与"));
                Preview(d, "镀白", Replacement, 0, false);
                Preview(d, Original + " ", Replacement, 0, false);
                Preview(d, Original, "", 6, false);
                Preview(d, Original, Original, 6, false);
                Preview(d, Original, Replacement, 6, true);
                Render(d, Path.Combine(output, "six-matches.png"));
                Click(ButtonIn(d, "CancelButton"));
            });
            Check("cancel leaves decisions and persisted archive untouched", rows.All(p => Decision(p.Value) == before[p.Key]) &&
                File.ReadAllText(task.DecisionsPath) == archiveBefore);

            Select(Row(1), Row(8), Row(11));
            OnDialog(d => { Preview(d, Original, Interim, 2, true); Click(ButtonIn(d, "ReplaceButton")); });
            Check("partial mixed selection edits current value, including prior revision, not CAD value",
                Row(1).EditTreatment == Interim && Row(11).EditTreatment == Interim && Row(11).OrigTreatment == "喷漆" &&
                Decision(Row(8)) == before[Row(8).Id] && Decision(Row(7)) == before[Row(7).Id] && Decision(Row(2)) == before[Row(2).Id]);
            Select(Row(1), Row(11));
            OnDialog(d => { Preview(d, Interim, Original, 2, true); Click(ButtonIn(d, "ReplaceButton")); });
            Check("return to CAD follows existing edit path; prior separate revision survives",
                Decision(Row(1)) == before[Row(1).Id] && Decision(Row(11)) == before[Row(11).Id]);

            Select(mixed);
            OnDialog(d => { Preview(d, Original, Replacement, 6, true); Click(ButtonIn(d, "ReplaceButton")); });
            Check("completion count is reported without changing grid sort", Status().Contains("已替换 6 条") && column.SortDirection == originalOrder);
            Check("exactly six records were changed, none from another task",
                rows.Where(p => Decision(p.Value) != before[p.Key]).Select(p => p.Value).ToHashSet().SetEquals(six) &&
                Decision(other.Items.Single()) == otherBefore && !File.Exists(other.DecisionsPath));
            Check("all six use the normal revised decision, preserve originals and evidence",
                six.All(row => row.EditTreatment == Replacement && row.Mode == Rules.Revise && row.OrigTreatment == Original &&
                    row.TreatmentEvidence == "SYNTHETIC CAD: ZINC WHITE COATING") &&
                Row(1).Note == ItemRow.AutoEditNote && Row(3).EditName == "SYNTHETIC prior name" && Row(3).Note == "合成既有修改说明");
            await window.RenderToPngAsync(Path.Combine(output, "column-action.png"));
            Check("save confirmed edits", Save());
            window.Close();
            window = Show();
            window.OpenTask(review);
            var reopened = Current();
            Check("reopened task loaded all decisions from the persisted session",
                !ReferenceEquals(reopened, task) && reopened.LoadNote == null &&
                reopened.Items.All(row => Decision(row) == Decision(rows[row.Id]) && row.OrigTreatment == rows[row.Id].OrigTreatment));

            ((TextBox)window.FindName("ReviewerBox")).Text = "SYNTHETIC TEST ONLY";
            string destination = Path.Combine(output, "报价样式.xlsx");
            window.DestinationSelector = (_, _) => SafeExportDestination.Acquire(destination, [], Engine.Root);
            window.OpenExportSelection = _ => { };
            window.ExportFolderFactory = () => Path.Combine(output, "export");
            var export = (Task<bool>)typeof(MainWindow).GetMethod("ExportCurrentAsync", Private)!.Invoke(window, [false])!;
            Check("existing UI validation and styled export succeed after reopening", await export && File.Exists(destination) && exportsConfirmed == 1);
            return 0;
        }
        catch (Exception ex) { checks.Add("ERROR " + ex); return 1; }
        finally
        {
            lifetime?.Close();
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks.Concat(dialogs.Select(text => "DIALOG " + text)));
        }

        void Check(string label, bool passed)
        {
            checks.Add((passed ? "PASS " : "FAIL ") + label);
            if (!passed) throw new InvalidOperationException(label);
        }
    }

    static string Decision(ItemRow row) => JsonSerializer.Serialize(row.ToDecision(), Json.Options);
    static Button ButtonIn(Window window, string name) => (Button)window.FindName(name);
    static void Click(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    static Button? FindEntry(DependencyObject parent)
    {
        if (parent is Button button && AutomationProperties.GetName(button) == EntryName) return button;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindEntry(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var target = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        target.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
