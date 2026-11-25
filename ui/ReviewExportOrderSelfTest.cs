using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClosedXML.Excel;

namespace CadReviewUI;

internal static class ReviewExportOrderSelfTest
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task<int> RunAsync(string output, string geometryReview, string legacyReview)
    {
        if (Directory.Exists(output)) throw new IOException("Use a new test output.");
        Directory.CreateDirectory(output);
        Engine.SessionsRoot = Path.Combine(output, "sessions");
        var log = new List<string>();
        var dialogs = new List<string>();
        var windows = new List<MainWindow>();
        bool markYes = false;
        int scopePrompts = 0, treatmentPrompts = 0, publications = 0;
        void Check(string title, bool pass)
        {
            log.Add((pass ? "PASS " : "FAIL ") + title);
            if (!pass) throw new InvalidDataException(title);
        }
        MainWindow Open(string review)
        {
            var window = new MainWindow(false,
                (message, title, _, _) =>
                {
                    dialogs.Add(title + ": " + message);
                    return title == "确认无需表面处理" && markYes ? MessageBoxResult.Yes : MessageBoxResult.No;
                }, null, (_, _, _, _) => { scopePrompts++; return true; })
            {
                Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Width = 1350,
            };
            windows.Add(window);
            window.ExportFolderFactory = () => Path.Combine(output, $"archive-{++publications}");
            window.OpenExportSelection = _ => { };
            window.OpenBatchExportFolder = _ => { };
            window.Show();
            window.OpenTask(review);
            ((TextBox)window.FindName("ReviewerBox")).Text = "SYNTHETIC CFB REVIEW ONLY";
            return window;
        }
        TaskSession Current(MainWindow window) => (TaskSession)typeof(MainWindow).GetField("_task", Private)!.GetValue(window)!;
        object? Invoke(MainWindow window, string name, params object?[] args) =>
            typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
        void Choose(MainWindow window, string tag, string method = "ReviewOrderMode_Click") =>
            Invoke(window, method, new MenuItem { Tag=tag }, new RoutedEventArgs());
        async Task<ValidationDto> Validate(MainWindow window) =>
            (await (Task<ValidationDto?>)Invoke(window, "RunValidate")!)!;
        Task<bool> Export(MainWindow window) => (Task<bool>)Invoke(window, "ExportCurrentAsync", false)!;
        static string Keys(IEnumerable<ItemRow> rows) => string.Concat(rows.Select(r => r.OrigDrawing[^1]));
        static string Included(ValidationDto dto) => string.Concat(dto.Accepted.Select(r => r.GetProperty("final")
            .GetProperty("drawing_number").GetString()![^1]));
        static void AttachDestination(MainWindow window, string path) =>
            window.DestinationSelector = (_, task) => SafeExportDestination.Acquire(path, [task.Manifest, task.SourceXlsx], Engine.Root);
        try
        {
            var window = Open(geometryReview);
            var task = Current(window);
            var grid = (DataGrid)window.FindName("ItemsGrid");
            Check("generated DWG: nine physical frames yielded seven distinct records", task.Items.Count == 7 &&
                task.Items.All(r => r.Src.FrameLocation?.IsUsable == true) && Keys(task.Items) == "DBFAECX");
            var idAndQuantity = task.Items.ToDictionary(r => r.Id, r => (r.OrigDrawing, r.OrigQuantity, r.OrigUnit, r.FileId));
            ItemRow Row(char key) => task.Items.Single(r => r.OrigDrawing[^1] == key);
            Check("three A copies retain active quantity 1+3 and exclude X quantity 12; anchor at active frame", Row('A').OrigQuantity == "4" &&
                Row('A').Src.Duplicate is { Count: 3, ActiveCount: 2 } && Row('A').Src.FrameLocation!.Bounds[0] == 0);
            Check("independent crossed-out drawing defaults to exclusion", Row('X').IsCrossedOut && Row('X').Mode == Rules.Exclude);
            Choose(window, ReviewOrder.Horizontal);
            Check("horizontal: A B C D E F X", Keys(task.Items) == "ABCDEFX" && Keys(grid.Items.OfType<ItemRow>()) == "ABCDEFX");
            var horizontal = await Validate(window);
            Check("reorder never turns pending rows into approved rows", horizontal.Status == "blocked" && horizontal.Pending == 6);
            Choose(window, ReviewOrder.Vertical);
            Check("vertical: A D B E C F X", Keys(task.Items) == "ADBECFX" && Keys(grid.Items.OfType<ItemRow>()) == "ADBECFX");
            Choose(window, ReviewOrder.Original);
            Check("original: D B F A E C X", Keys(task.Items) == "DBFAECX");
            Choose(window, ReviewOrder.Vertical);

            grid.SelectedItem = Row('B');
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            var noneButton = (Button)window.FindName("MarkTreatmentNotRequiredButton");
            noneButton.ApplyTemplate();
            Check("explicit no-treatment button is visible in the review inspector", noneButton.IsVisible);
            noneButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("cancelling explicit no treatment leaves existing value and review untouched",
                Row('B').ConfTreatment == "" && Row('B').Mode == Rules.Pending);
            markYes = true;
            noneButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("explicit no treatment is a human revision; original zinc preserved",
                Row('B').Mode == Rules.Revise && Row('B').ConfTreatment == TreatmentPolicy.None && Row('B').OrigTreatment == "镀白锌");
            grid.SelectedItem = Row('D');
            Invoke(window, "BulkAdopt_Click", window, new RoutedEventArgs());
            Check("explicit selected adopt accepts a treatment-only gap but does not mark no treatment",
                Row('D').Mode == Rules.Adopt && Row('D').ConfTreatment == "");
            Row('E').ConfTreatment = TreatmentPolicy.PendingEntry;
            window.ApplyMode([Row('A'), Row('C'), Row('F')], Rules.Adopt);
            const string zincOverride = "镀碱性蓝白锌";
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(Row('A'));
            grid.SelectedItems.Add(Row('C'));
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var replaceZinc = window.Dispatcher.InvokeAsync(() =>
            {
                var dialog = window.OwnedWindows.OfType<BulkTreatmentWindow>().SingleOrDefault();
                if (dialog is null) return false;
                ((TextBox)dialog.FindName("ReplacementBox")).Text = zincOverride;
                if (((TextBlock)dialog.FindName("MatchText")).Text != "命中 2 条")
                {
                    dialog.Close();
                    return false;
                }
                var button = (Button)dialog.FindName("ReplaceButton");
                dialog.UpdateLayout();
                button.ApplyTemplate();
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return true;
            }, DispatcherPriority.ApplicationIdle);
            Invoke(window, "BulkTreatment_Click", window, new RoutedEventArgs(Button.ClickEvent));
            if (!replaceZinc.Task.IsCompleted) replaceZinc.Abort();
            Check("bulk treatment in a geometrically ordered task updates two selected rows, not the other decisions",
                replaceZinc.Task.IsCompletedSuccessfully && replaceZinc.Task.Result &&
                Row('A').EditTreatment == zincOverride && Row('C').EditTreatment == zincOverride &&
                Row('A').OrigTreatment == "镀白锌" && Row('F').EditTreatment == "镀白锌" &&
                Row('B').ConfTreatment == TreatmentPolicy.None && Row('D').ConfTreatment == "" &&
                Row('E').ConfTreatment == TreatmentPolicy.PendingEntry);
            foreach (string warning in new[] { "工艺参数不一致，请核对原文", "同图号各处工艺不一致", "工艺标注归属不明，未填入工艺" })
                Check("recognition conflict cannot be treated as an optional blank: " + warning,
                    TreatmentPolicy.Classify(null, null, warning, "工艺原文") == TreatmentPolicy.Conflict);
            foreach (var file in task.Files) { file.State = "已核查"; file.Note = "合成测试核查范围。"; }
            Choose(window, ReviewOrder.Horizontal);
            var acceptedHorizontal = await Validate(window);
            Check("horizontal validated output order differs from vertical", Included(acceptedHorizontal) == "ABCDEF");
            Choose(window, ReviewOrder.Vertical);
            var vertical = await Validate(window);
            Check("vertical snapshot preserves all eligible rows and separates exceptions",
                vertical.Status == "ready" && Included(vertical) == "ADBECF" &&
                vertical.TreatmentExceptions.Select(x => (x.DrawingNumber[^1], x.Disposition))
                    .SequenceEqual(new[] { ('D', TreatmentPolicy.Missing), ('E', TreatmentPolicy.Unconfirmed) }));
            string firstHash = vertical.ReviewSha256!;
            var denied = Path.Combine(output, "no-approval");
            var noApproval = await task.ExportQuotationWithArchiveLockAsync(denied, "SYNTHETIC", firstHash);
            Check("direct export cannot waive treatment or emit either XLSX without approval", noApproval.Status == "blocked" &&
                !Directory.EnumerateFiles(denied, "*.xlsx", SearchOption.AllDirectories).Any());

            // A subtler manual adjustment: with only B visible, swap B with
            // its true preceding neighbour D, not another visible row.
            var search = (TextBox)window.FindName("SearchBox");
            search.Text = "CFB-B";
            grid.SelectedItem = Row('B');
            Choose(window, "-1", "ReviewOrderMove_Click");
            Check("manual move applies to full order while searching", Keys(task.Items) == "ABDECFX" &&
                Keys(grid.Items.OfType<ItemRow>()) == "B");
            grid.SelectedItem = Row('B');
            Choose(window, "1", "ReviewOrderMove_Click");
            Check("manual move down reverses the adjustment", Keys(task.Items) == "ADBECFX");
            grid.SelectedItem = Row('B');
            Choose(window, "-1", "ReviewOrderMove_Click");
            var manual = await Validate(window);
            Check("reviewed XLSX carries the saved manual order, not the filtered row", manual.Status == "ready" &&
                Included(manual) == "ABDECF" && manual.ReviewSha256 != firstHash);
            var stale = Path.Combine(output, "stale-approval");
            var staleApproval = await task.ExportQuotationWithArchiveLockAsync(stale, "SYNTHETIC", manual.ReviewSha256!,
                allowBlankTreatmentFor: firstHash);
            Check("approval for previous order/version rejected by backend", staleApproval.Status == "blocked" &&
                staleApproval.BlankTreatmentApprovedReviewSha256 == null &&
                !Directory.EnumerateFiles(stale, "*.xlsx", SearchOption.AllDirectories).Any());

            var rawSnapshot = task.ReviewedPath;
            window.Close();
            windows.Remove(window);
            window = Open(geometryReview);
            task = Current(window);
            grid = (DataGrid)window.FindName("ItemsGrid");
            search = (TextBox)window.FindName("SearchBox");
            Check("reopened UI restores saved manual order, row identity, source quantity, and explicit treatment",
                task.OrderMode(task.Files.Single().Id) == ReviewOrder.Manual && Keys(grid.Items.OfType<ItemRow>()) == "ABDECFX" &&
                task.Items.All(r => idAndQuantity[r.Id] == (r.OrigDrawing, r.OrigQuantity, r.OrigUnit, r.FileId)) &&
                Row('B').ConfTreatment == TreatmentPolicy.None &&
                Row('A').EditTreatment == zincOverride && Row('C').EditTreatment == zincOverride);
            grid.SelectedItem = Row('B');
            await window.RenderToPngAsync(Path.Combine(output, "review-order-and-no-treatment.png"));
            ICollectionView view = (ICollectionView)grid.ItemsSource;
            view.SortDescriptions.Add(new SortDescription(nameof(ItemRow.OrigDrawing), ListSortDirection.Descending));
            Check("temporary column sort shows reverse numbers without changing saved export order",
                Keys(grid.Items.OfType<ItemRow>()) == "XFEDCBA" && Keys(task.Items) == "ABDECFX");
            search.Text = "CFB-D";
            Check("search leaves one visible row", Keys(grid.Items.OfType<ItemRow>()) == "D");
            await window.RenderToPngAsync(Path.Combine(output, "review-window.png"));

            // Hard errors stop before the treatment prompt or exports.
            var selectedPath = Path.Combine(output, "selected.xlsx");
            AttachDestination(window, selectedPath);
            window.BlankTreatmentPrompt = _ => { treatmentPrompts++; return true; };
            foreach (var (column, set) in new (string, Action<string>)[]
            {
                ("图号", value => Row('D').EditDrawing = value),
                ("材质", value => Row('D').EditMaterial = value),
                ("单位", value => Row('D').EditUnit = value),
                ("数量", value => Row('D').EditQuantity = value),
            })
            {
                var previous = Row('D').ToDecision();
                set("");
                Check("hard clear blocked before exception dialog: " + column, !await Export(window) && treatmentPrompts == 0 &&
                    !File.Exists(selectedPath));
                Row('D').Apply(previous);
            }
            Row('D').Mode = Rules.Pending;
            Check("pending gap row cannot be included by the soft dialog", !await Export(window) && treatmentPrompts == 0);
            window.ApplyMode([Row('D')], Rules.Adopt);
            var priorE = Row('E').ToDecision();
            Row('E').ConfTreatment = "表面处理冲突待确认";
            var conflict = await Validate(window);
            Check("explicit treatment conflict is still a hard error", conflict.Status == "blocked" &&
                conflict.Errors.Any(e => e.Contains("表面处理冲突")) && conflict.TreatmentExceptions.All(x => x.Id != Row('E').Id));
            Row('E').Apply(priorE);
            Row('E').ConfDrawing = Row('D').OrigDrawing;
            Check("duplicate final drawing numbers block before the dialog", !await Export(window) && treatmentPrompts == 0);
            Row('E').Apply(priorE);

            List<string> prompted = [];
            bool continueExport = false;
            bool ActualTreatmentPrompt(IReadOnlyList<string> rows)
            {
                treatmentPrompts++;
                prompted = rows.ToList();
                var dialog = new ConfirmBlankTreatmentWindow(rows) { Owner = window, ShowActivated = false };
                dialog.Dispatcher.BeginInvoke(() =>
                {
                    var root = (FrameworkElement)dialog.Content;
                    var margin = root.Margin;
                    double width = root.ActualWidth + margin.Left + margin.Right;
                    double height = root.ActualHeight + margin.Top + margin.Bottom;
                    var dpi = VisualTreeHelper.GetDpi(root);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX),
                        (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    var visual = new DrawingVisual();
                    using (var canvas = visual.RenderOpen())
                    {
                        canvas.DrawRectangle(dialog.Background, null, new Rect(0, 0, width, height));
                        var content = new VisualBrush(root)
                        {
                            ViewboxUnits = BrushMappingMode.Absolute,
                            Viewbox = new Rect(0, 0, width, height),
                            Stretch = Stretch.Fill,
                        };
                        canvas.DrawRectangle(content, null, new Rect(0, 0, width, height));
                    }
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(output, "treatment-confirmation.png"))) encoder.Save(stream);
                    if (continueExport) ((Button)dialog.FindName("ContinueButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.DialogResult = false;
                }, DispatcherPriority.ApplicationIdle);
                return dialog.ShowDialog() == true;
            }
            window.BlankTreatmentPrompt = ActualTreatmentPrompt;
            Check("return from the real treatment dialog produces no customer export", !await Export(window) && !File.Exists(selectedPath));
            Check("dialog lists only affected records and separates the two statuses",
                prompted.Count == 2 && prompted[0].Contains("CFB-D") && prompted[0].Contains("未识别到工艺") &&
                prompted[1].Contains("CFB-E") && prompted[1].Contains("工艺待确认") && prompted.All(x => !x.Contains("CFB-B")));
            int beforeRace = publications;
            window.BlankTreatmentPrompt = _ => { Row('F').ConfMaterial = "CHANGED DURING CONFIRM"; return true; };
            Check("changing decisions during soft confirmation aborts before export", !await Export(window) &&
                publications == beforeRace && !File.Exists(selectedPath));
            Row('F').ConfMaterial = ""; Row('F').Mode = Rules.Adopt;
            window.BlankTreatmentPrompt = ActualTreatmentPrompt;
            continueExport = true;
            Check("continue exports styled Excel despite search and temporary sort", await Export(window) && File.Exists(selectedPath));
            Check("soft permission was not persisted as a decision", Row('D').ConfTreatment == "" &&
                Row('E').ConfTreatment == TreatmentPolicy.PendingEntry);
            File.WriteAllText(Path.Combine(output,"exports.json"), JsonSerializer.Serialize(new {
                core = Path.Combine(output, $"archive-{publications}", "复核记录", "确认明细.xlsx"),
                styled = selectedPath, orderedReview = rawSnapshot,
                review = task.ReviewedPath, ids = task.Items.Select(r => r.Id), origin = task.Manifest,
            }, Json.Options));

            var legacySource = Json.Read<Dataset>(Path.Combine(legacyReview, "source.json"));
            var legacySession = Path.Combine(Engine.SessionsRoot, legacySource.TaskId);
            Directory.CreateDirectory(legacySession);
            File.Copy(Path.Combine(Path.GetDirectoryName(legacyReview)!, "ui-decisions.json"),
                Path.Combine(legacySession, "ui-decisions.json"));
            window.OpenTask(legacyReview);
            var legacy = Current(window);
            Check("legacy dataset and archive open without geometry", legacy.Items.Count == 2 &&
                legacy.Items.All(r => r.Src.FrameLocation == null) && legacy.LoadNote == null &&
                legacy.Items[0].Mode == Rules.Adopt);
            Check("legacy geometry ordering unavailable, retains original", !legacy.SetOrder(legacy.Files.Single().Id, ReviewOrder.Vertical));
            var legacyIds = legacy.Items.Select(i => i.Id).Reverse().ToArray();
            Check("legacy permits manual reorder", legacy.MoveItem(legacy.Items[1], -1));
            foreach (var row in legacy.Items) window.ApplyMode([row], Rules.Adopt);
            var legacyFile = legacy.Files.Single();
            legacyFile.ParentChildNote = "";
            var parentBlocked = await Validate(window);
            Check("parent-child scope still blocks before export when unset", parentBlocked.Status == "blocked" &&
                parentBlocked.Errors.Any(x => x.Contains("父图和子件同时纳入")));
            legacyFile.ParentChildNote = "合成测试：分列报父件与备件。";
            var legacyValidation = await Validate(window);
            Check("legacy task still saves and validates in changed order", legacyValidation.Status == "ready" &&
                legacyValidation.Accepted.Select(x => x.GetProperty("id").GetString()).SequenceEqual(legacyIds));
            var originalEngine = await Engine.ValidateAsync(EngineKind.Segmented, legacy.Manifest, legacy.ReviewedPath);
            Check("legacy frozen engine still validates unchanged workbook schema and order", originalEngine.Status == "ready" &&
                originalEngine.Accepted.Select(x => x.GetProperty("id").GetString()).SequenceEqual(legacyIds));
            var reopened = TaskSession.Open(legacyReview);
            Check("legacy order survives reopen under guarded archive schema", reopened.Items.Select(r => r.Id).SequenceEqual(legacyIds));
            // Make this selected legacy task need a separate blank decision.
            legacy.Items[0].EditTreatment = "";
            var batch = Path.Combine(output, "batch");
            Directory.CreateDirectory(batch);
            window.BatchFolderSelector = _ => batch;
            window.BatchChooser = (choices, _) => choices;
            scopePrompts = treatmentPrompts = 0;
            window.BlankTreatmentPrompt = rows => { treatmentPrompts++; return rows.Any(x => x.Contains("CFB-D")); };
            await window.BatchExportAsync();
            Check("batch confirms each selected task independently and skips the task whose treatment prompt was cancelled",
                scopePrompts == 2 && treatmentPrompts == 2 && Directory.GetFiles(batch, "*.xlsx").Length == 1 &&
                File.Exists(Path.Combine(batch, "layout.xlsx")));

            // Exercise both formulas with prices in memory; leave exported prices blank.
            using var checks = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "exports.json")));
            using var styles = new XLWorkbook(selectedPath);
            using var core = new XLWorkbook(checks.RootElement.GetProperty("core").GetString()!);
            var s = styles.Worksheet("报价单样式"); var c = core.Worksheet("确认明细");
            for (int i = 0; i < 6; i++) { s.Cell(6+i,8).Value=i+1; c.Cell(8+i,8).Value=i+1; }
            styles.RecalculateAllFormulas(); core.RecalculateAllFormulas();
            Check("in-memory prices: styled amounts and total, core amounts, zero and all six rows",
                Enumerable.Range(0,6).All(i => s.Cell(6+i,9).GetDouble()==c.Cell(8+i,9).GetDouble()) && s.Cell(54,9).GetDouble()==76);
            return 0;
        }
        catch (Exception ex) { log.Add("ERROR " + ex); return 1; }
        finally
        {
            foreach (var window in windows) window.Close();
            File.WriteAllLines(Path.Combine(output,"checks.txt"), log.Concat(dialogs.Select(x => "DIALOG " + x)), new UTF8Encoding(false));
        }
    }
}
