using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace CadReviewUI;

/// <summary>
/// 界面交互回归（隔离存档）：打X明细直接纳入、多行批量改处理方式、批量采用、
/// 任务切换复用与耗时、表格及侧栏序号。用法：CadReviewUI.exe --interaction-test &lt;新输出目录&gt; &lt;review目录&gt;...
/// 第一个 review 目录须含图面打X明细。
/// </summary>
public static class InteractionSelfTest
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task<int> RunAsync(string output, IReadOnlyList<string> reviews)
    {
        if (Directory.Exists(output)) throw new IOException("Test output already exists.");
        Directory.CreateDirectory(output);
        Engine.SessionsRoot = Path.Combine(output, "sessions");
        var lines = new List<string>();
        var dialogs = new List<string>();
        void Check(string name, bool ok, string detail = "")
        {
            lines.Add((ok ? "PASS " : "FAIL ") + name + (detail.Length == 0 ? "" : " — " + detail));
            if (!ok) throw new InvalidOperationException(name);
        }
        try
        {
            var window = new MainWindow(false, (message, title, _, _) =>
            {
                dialogs.Add(title + "：" + message.Replace('\n', ' '));
                return MessageBoxResult.No;
            }, null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            var taskField = typeof(MainWindow).GetField("_task", Private)!;
            TaskSession Current() => (TaskSession)taskField.GetValue(window)!;
            async Task Idle() => await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // 1. 打开含打X明细的任务
            window.OpenTask(reviews[0]);
            await Idle();
            var first = Current();
            var crossed = first.Items.Where(r => r.IsCrossedOut).ToList();
            Check("样本含图面打X明细", crossed.Count > 0, $"{crossed.Count} 条");
            Check("打X明细默认不纳入", crossed.All(r => r.Mode == Rules.Exclude));

            // 2. 直接改处理方式即可纳入，无弹窗，自动记录说明
            int dialogsBefore = dialogs.Count;
            var (changed, skipped) = window.ApplyMode(crossed, Rules.Adopt);
            Check("打X明细直接改为采用图纸", changed == crossed.Count && skipped == 0 && crossed.All(r => r.Mode == Rules.Adopt),
                $"改变 {changed}，跳过 {skipped}");
            Check("自动记录纳入说明", crossed.All(r => r.Note.Contains(ItemRow.AutoCrossedIncludeNote, StringComparison.Ordinal) &&
                r.HasCrossedOutInclusionReason && !r.NeedsCrossedOutInclusionReason));
            Check("直接纳入不弹窗", dialogs.Count == dialogsBefore);

            // 3. 引擎校验不再要求补填打X原因
            var validate = typeof(MainWindow).GetMethod("RunValidate", Private)!;
            foreach (var row in first.Items.Where(r => !r.IsCrossedOut)) row.Mode = Rules.Adopt;
            var dto = await (Task<ValidationDto?>)validate.Invoke(window, null)!;
            Check("校验已运行", dto != null, dto?.Status ?? "null");
            var crossedIds = crossed.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var crossedErrors = dto!.Errors.Where(e => e.Contains("打X", StringComparison.Ordinal) ||
                crossedIds.Any(id => e.StartsWith(id + ":", StringComparison.Ordinal))).ToList();
            Check("校验未拦截直接纳入的打X明细", crossedErrors.Count == 0, crossedErrors.Count == 0 ? dto.Status : string.Join(" | ", crossedErrors));

            // 4. 改回不纳入后再批量采用：打X默认排除也能一并采用，不再二次确认
            foreach (var row in crossed) { row.Note = row.Src.DefaultDecision?.Note ?? ""; row.Mode = Rules.Exclude; }
            Check("已恢复打X默认不纳入", crossed.All(r => r.Mode == Rules.Exclude));
            var grid = (DataGrid)window.FindName("ItemsGrid");
            grid.SelectedItems.Clear();
            foreach (var row in crossed) grid.SelectedItems.Add(row);
            dialogsBefore = dialogs.Count;
            typeof(MainWindow).GetMethod("BulkAdopt_Click", Private)!.Invoke(window, [window, new RoutedEventArgs()]);
            Check("批量采用包含打X明细", crossed.All(r => r.Mode == Rules.Adopt || !r.IsCompleteOriginal),
                string.Join("、", crossed.Select(r => r.Mode)));
            Check("批量采用无二次确认", dialogs.Count == dialogsBefore);

            // 5. 多行批量改处理方式
            var some = first.Items.Take(Math.Min(3, first.Items.Count)).ToList();
            (changed, _) = window.ApplyMode(some, Rules.Exclude);
            Check("多行一次设为不纳入", some.All(r => r.Mode == Rules.Exclude), $"改变 {changed}");
            (changed, _) = window.ApplyMode(some, Rules.Pending);
            Check("多行一次改回待复核", some.All(r => r.Mode == Rules.Pending));

            // 6. 表格序号与侧栏序号
            await Idle();
            var row0 = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
            var row1 = grid.ItemContainerGenerator.ContainerFromIndex(1) as DataGridRow;
            Check("表格序号从 1 开始", row0?.Header as string == "1" && (row1 == null || row1.Header as string == "2"),
                $"{row0?.Header}/{row1?.Header}");

            // 7. 切换任务：首次打开与再次切换（复用内存中的任务）
            var timings = new List<string>();
            for (int i = 1; i < reviews.Count; i++)
            {
                var watch = Stopwatch.StartNew();
                window.OpenTask(reviews[i]);
                await Idle();
                timings.Add($"首次打开 {Path.GetFileName(Path.GetDirectoryName(reviews[i]))} {watch.ElapsedMilliseconds} ms");
            }
            var tabs = (ListBox)window.FindName("OrderTabs");
            await Idle();
            var tab0 = tabs.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            Check("侧栏序号从 1 开始", tab0 != null && ItemsControl.GetAlternationIndex(tab0) == 0 &&
                tabs.Items.Count == reviews.Count, $"{tabs.Items.Count} 项");
            var cachedTimes = new List<long>();
            for (int round = 0; round < 2; round++)
            {
                foreach (var review in reviews)
                {
                    var watch = Stopwatch.StartNew();
                    window.OpenTask(review);
                    await Idle();
                    cachedTimes.Add(watch.ElapsedMilliseconds);
                }
            }
            window.OpenTask(reviews[0]);
            await Idle();
            Check("再次切换复用同一任务对象（不重新读取）", ReferenceEquals(Current(), first));
            timings.Add("再次切换耗时（ms）：" + string.Join("、", cachedTimes));
            Check("再次切换平均不超过 400 ms", cachedTimes.Average() <= 400, $"平均 {cachedTimes.Average():F0} ms");
            lines.AddRange(timings.Select(t => "INFO " + t));

            // 8. 批量导出先列出已打开图纸供勾选（序号与侧栏一致）；取消勾选窗口不进入导出
            IReadOnlyList<BatchExportWindow.Choice>? offered = null;
            window.BatchChooser = (choices, _) => { offered = choices; return null; };
            typeof(MainWindow).GetMethod("BatchExport_Click", Private)!.Invoke(window, [window, new RoutedEventArgs()]);
            Check("批量导出列出全部已打开图纸供勾选", offered != null && offered.Count == tabs.Items.Count &&
                offered.Select(c => c.Number).SequenceEqual(Enumerable.Range(1, tabs.Items.Count)), $"{offered?.Count} 项");
            var chooser = new BatchExportWindow(offered!)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false,
            };
            chooser.Show();
            var list = (ListBox)chooser.FindName("ItemsList");
            var exportButton = (Button)chooser.FindName("ExportSelectedButton");
            Check("勾选窗口默认不勾选、导出按钮不可用", list.SelectedItems.Count == 0 && !exportButton.IsEnabled);
            list.SelectedItems.Add(offered![2 % offered.Count]);
            list.SelectedItems.Add(offered[0]);
            Check("勾选后显示数量并可导出", exportButton.IsEnabled && (exportButton.Content as string)?.Contains("2 张") == true,
                exportButton.Content as string ?? "");
            typeof(BatchExportWindow).GetMethod("SelectAll_Click", Private)!.Invoke(chooser, [chooser, new RoutedEventArgs()]);
            Check("全选", list.SelectedItems.Count == offered.Count);
            typeof(BatchExportWindow).GetMethod("SelectNone_Click", Private)!.Invoke(chooser, [chooser, new RoutedEventArgs()]);
            Check("全不选", list.SelectedItems.Count == 0 && !exportButton.IsEnabled);
            chooser.Close();

            // 9. 拖动调整左侧栏与详情栏宽度，并保存到 ui-layout.json
            var sidebar = (ColumnDefinition)window.FindName("SidebarColumn");
            var inspector = (Border)window.FindName("InspectorPanel");
            double sidebarBefore = sidebar.ActualWidth;
            typeof(MainWindow).GetMethod("SidebarSplitter_DragDelta", Private)!
                .Invoke(window, [window, new System.Windows.Controls.Primitives.DragDeltaEventArgs(80, 0)]);
            typeof(MainWindow).GetMethod("InspectorSplitter_DragDelta", Private)!
                .Invoke(window, [window, new System.Windows.Controls.Primitives.DragDeltaEventArgs(-100, 0)]);
            typeof(MainWindow).GetMethod("Splitter_DragCompleted", Private)!
                .Invoke(window, [window, new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false)]);
            await Idle();
            Check("左侧栏可拖宽", Math.Abs(sidebar.ActualWidth - (sidebarBefore + 80)) < 1, $"{sidebarBefore:F0} → {sidebar.ActualWidth:F0}");
            Check("详情栏可拖宽", Math.Abs(inspector.Width - 430) < 1, $"{inspector.Width:F0}");
            string layout = Path.Combine(Engine.SessionsRoot, "ui-layout.json");
            Check("栏宽已保存", File.Exists(layout) && File.ReadAllText(layout).Contains("430"), File.Exists(layout) ? File.ReadAllText(layout) : "无文件");
            using (var savedLayout = System.Text.Json.JsonDocument.Parse(File.ReadAllText(layout)))
                Check("保存的是调整后的侧栏宽度", Math.Abs(savedLayout.RootElement.GetProperty("Sidebar").GetDouble() - sidebar.ActualWidth) < 1);

            window.Close();
            lines.Add($"全部通过：{lines.Count(l => l.StartsWith("PASS"))} 项");
            return 0;
        }
        catch (Exception ex)
        {
            lines.Add("ERROR " + ex.GetBaseException().Message);
            return 1;
        }
        finally
        {
            lines.AddRange(dialogs.Select(d => "DIALOG " + d));
            File.WriteAllLines(Path.Combine(output, "checks.txt"), lines, new UTF8Encoding(false));
        }
    }
}
