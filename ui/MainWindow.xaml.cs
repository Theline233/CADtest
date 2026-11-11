using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CadReviewUI;

public partial class MainWindow : Window
{
    sealed record OrderTab(string Folder, string DisplayName, string ToolTip, string SessionDir, string ManifestSha256);
    readonly ObservableCollection<OrderTab> _orderTabs = [];
    // 已打开任务留在内存中，切换时直接复用，避免每次重新读取和解析任务数据。
    readonly Dictionary<string, TaskSession> _sessionCache = new(StringComparer.OrdinalIgnoreCase);
    bool _suspendRefresh;
    bool _changingOrderTab, _changingItemMode;
    TaskSession? _task;
    ICollectionView? _view;
    CancellationTokenSource? _runCts;
    bool _parallelRunActive;
    bool _dirty, _busy;
    long _editVersion;
    int _recentLoadVersion;
    bool _recentRemoving;
    readonly bool _isDemo;
    readonly Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> _showDialog;
    readonly Func<TaskSession, string> _createImportBackup;
    readonly Func<int, int, int, string, bool> _confirmExport;
    internal Func<string> ExportFolderFactory { get; set; } = TaskSession.NewConfirmedFolder;
    internal Func<IReadOnlyList<string>, string, Action<string>, CancellationToken, Task<RecognizeOutcome>> RecognitionRunner { get; set; } = Engine.RecognizeAsync;
    internal Func<IReadOnlyList<ItemRow>, string?> CrossedReasonPrompt { get; set; }
    internal Func<ItemRow, IReadOnlyList<DsDuplicateOccurrence>, DsDuplicateOccurrence?> DuplicateChoicePrompt { get; set; }
    internal Func<string, TaskSession, SafeExportDestination?> DestinationSelector { get; set; }
    internal Action<string> OpenExportSelection { get; set; } = path =>
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
    internal Action<SafeExportDestination, string, string, string?> RecordExportSelection { get; set; } =
        (destination, folder, hash, backup) => destination.WriteReceipt(folder, hash, backup);
    internal IReadOnlyCollection<string> ExternalProtectedInputs { get; set; } = [];
    readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(15) };
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };

    static readonly string[] Filters = ["全部", "待复核", "采用图纸", "人工修订", "不纳入", "图面打X", "重复图号", "有校验问题", "原值缺项"];

    // 筛选胶囊：显示名称与当前范围内的条数。
    public sealed class FilterOption(string key) : Notify
    {
        int _count;
        public string Key { get; } = key;
        // 标记类筛选在没有对应明细时自动隐藏，减少干扰。
        public bool Optional => Key is "图面打X" or "重复图号" or "有校验问题" or "原值缺项";
        public int Count { get => _count; set => Set(ref _count, value); }
        Visibility _visibility = Visibility.Visible;
        public Visibility Visibility { get => _visibility; set => Set(ref _visibility, value); }
        public override string ToString() => Key;
    }
    readonly List<FilterOption> _filterOptions = Filters.Select(f => new FilterOption(f)).ToList();
    string? CurrentFilter => (FilterBox.SelectedItem as FilterOption)?.Key;

    public MainWindow() : this(false, null, null) { }

    public MainWindow(bool demo) : this(demo, null, null) { }

    // Dialog and backup delegates let the same UI paths be exercised without showing windows.
    internal MainWindow(bool demo,
        Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult>? showDialog,
        Func<TaskSession, string>? createImportBackup) : this(demo, showDialog, createImportBackup, null) { }

    internal MainWindow(bool demo,
        Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult>? showDialog,
        Func<TaskSession, string>? createImportBackup,
        Func<int, int, int, string, bool>? confirmExport)
    {
        _isDemo = demo;
        _showDialog = showDialog ?? ((message, title, buttons, icon) =>
            MessageBox.Show(this, message, title, buttons, icon,
                buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.OK));
        _createImportBackup = createImportBackup ?? CreateImportBackup;
        _confirmExport = confirmExport ?? ((items, files, excluded, scope) =>
            new ConfirmExportWindow(items, files, excluded, scope) { Owner = this }.ShowDialog() == true);
        DestinationSelector = ChooseExportDestination;
        BatchChooser = ShowBatchChooser;
        BatchFolderSelector = ChooseBatchFolder;
        CrossedReasonPrompt = rows => Prompt.Ask(this, "图面打X：改为纳入",
            $"所选 {rows.Count} 行在图面被识别为整框打X，默认不纳入。\n请先对照原图，并填写实际纳入原因（多行共用）：", true);
        DuplicateChoicePrompt = (row, choices) =>
        {
            var dialog = new DuplicateChoiceWindow(row, choices) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.Choice : null;
        };
        InitializeComponent();
        OrderTabs.ItemsSource = _orderTabs;
        if (_isDemo)
        {
            Title = "【演示模式：禁止校验和导出】" + Title;
            DisableDemoActions(this);
        }
        FilterBox.ItemsSource = _filterOptions;
        FilterBox.SelectedIndex = 0;
        FileStateBox.ItemsSource = Rules.FileStates;
        _autosave.Tick += (_, _) => { if (_dirty && !_busy) SaveNow(auto: true); };
        _autosave.Start();
        _refresh.Tick += (_, _) => { _refresh.Stop(); _task?.RefreshFileSummaries(); UpdateStats(); };
        ReviewerBox.Text = Environment.UserName;
    }

    // Keep the UI disabled as well as guarding the execution paths.
    void DisableDemoActions(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject node) continue;
            if (node is Button button && button.Content is string label &&
                label is "校验" or "导出确认明细" or "确认并导出 Excel" or "识别设置…" or "API 设置…" or "图片 API 复查…")
            {
                button.IsEnabled = false;
                button.ToolTip = "演示模式不能执行此操作。";
            }
            DisableDemoActions(node);
        }
        if (parent != this) return;
        // 菜单项不在窗口逻辑树中，单独禁用。
        foreach (var item in new[] { RecognitionSettingsMenu, ApiSettingsMenu, ApiReviewMenu,
                     WelcomeRecognitionSettingsMenu, WelcomeApiSettingsMenu, WelcomeApiReviewMenu })
        {
            item.IsEnabled = false;
            item.ToolTip = "演示模式不能执行此操作。";
        }
    }

    // ───────────────────────── 窗口外观 ─────────────────────────
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Windows 11 上请求系统圆角窗口；旧系统调用失败时忽略。
    void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        }
        catch (Exception) { }
    }

    void CaptionClose_Click(object sender, RoutedEventArgs e) => Close();
    void CaptionMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void CaptionMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    static readonly System.Windows.Media.Animation.IEasingFunction SmoothEase = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

    // ───────────────────────── 栏宽拖动 ─────────────────────────
    const double SidebarDefault = 260, InspectorDefault = 330;
    double _inspectorWidth = InspectorDefault;

    void SidebarSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        double max = Math.Max(200, Math.Min(480, Workspace.ActualWidth - InspectorPanel.ActualWidth - 520));
        SidebarColumn.Width = new GridLength(Math.Clamp(SidebarColumn.ActualWidth + e.HorizontalChange, 200, max));
    }

    void SidebarSplitter_Reset(object sender, MouseButtonEventArgs e)
    {
        SidebarColumn.Width = new GridLength(SidebarDefault);
        ConstrainLayout();
        SaveLayout();
    }

    void InspectorSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        double max = Math.Max(260, Math.Min(680, Workspace.ActualWidth - SidebarColumn.ActualWidth - 520));
        SetInspectorWidth(Math.Clamp(_inspectorWidth - e.HorizontalChange, 260, max));
    }

    void InspectorSplitter_Reset(object sender, MouseButtonEventArgs e)
    {
        SetInspectorWidth(InspectorDefault);
        ConstrainLayout();
        SaveLayout();
    }

    void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    { ConstrainLayout(); SaveLayout(); }

    void SetInspectorWidth(double width)
    {
        _inspectorWidth = width;
        InspectorPanel.BeginAnimation(WidthProperty, null);
        InspectorPanel.Width = width;
        InspectorContent.Width = width;
    }

    // 栏宽保存在存档目录的 ui-layout.json，下次启动沿用；读写失败不影响使用。
    string LayoutPath => Path.Combine(Engine.SessionsRoot, "ui-layout.json");

    sealed record LayoutState(double Sidebar, double Inspector, bool InspectorVisible);
    bool _constrainingLayout;

    void Workspace_SizeChanged(object sender, SizeChangedEventArgs e) => ConstrainLayout();
    bool? _compactToolbar;
    void Content_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WorkspaceHeader == null || TableToolbar == null || WorkspaceHeader.ActualWidth <= 0) return;
        bool compact = WorkspaceHeader.ActualWidth < 740;
        if (_compactToolbar == compact) return;
        _compactToolbar = compact;
        Grid.SetColumn(HeaderActions, compact ? 0 : 1);
        Grid.SetRow(HeaderActions, compact ? 1 : 0);
        HeaderActions.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        HeaderActions.Margin = compact ? new Thickness(0, 8, 0, 8) : new Thickness(0);
        HeaderSummary.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0, 0, 16, 0);
        Grid.SetColumn(SearchTools, compact ? 0 : 1);
        Grid.SetRow(SearchTools, compact ? 1 : 0);
        SearchTools.Margin = compact ? new Thickness(0, 5, 0, 0) : new Thickness(0);
    }

    void ConstrainLayout(bool showingInspector = false)
    {
        if (_constrainingLayout) return;
        _constrainingLayout = true;
        try
        {
            double available = Workspace.ActualWidth > 0 ? Workspace.ActualWidth : Math.Max(MinWidth, Width) - 2;
            bool visible = showingInspector || InspectorPanel.Visibility == Visibility.Visible;
            double sidebar = double.IsFinite(SidebarColumn.Width.Value) ? SidebarColumn.Width.Value : SidebarDefault;
            double inspector = double.IsFinite(_inspectorWidth) ? _inspectorWidth : InspectorDefault;
            double maxSidebar = Math.Max(200, Math.Min(480, available - 520 - (visible ? 260 : 0)));
            SidebarColumn.Width = new GridLength(Math.Clamp(sidebar, 200, maxSidebar));
            double maxInspector = visible ? Math.Max(260, Math.Min(680, available - 520 - SidebarColumn.Width.Value)) : 680;
            double next = Math.Clamp(inspector, 260, maxInspector);
            if (Math.Abs(next - _inspectorWidth) > 0.1 || !double.IsFinite(_inspectorWidth)) SetInspectorWidth(next);
        }
        finally { _constrainingLayout = false; }
    }

    void LoadLayout()
    {
        try
        {
            if (!File.Exists(LayoutPath)) return;
            var state = JsonSerializer.Deserialize<LayoutState>(File.ReadAllText(LayoutPath));
            if (state == null) return;
            SidebarColumn.Width = new GridLength(Math.Clamp(state.Sidebar, 200, 480));
            SetInspectorWidth(Math.Clamp(state.Inspector, 260, 680));
            if (!state.InspectorVisible) InspectorPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception) { }
        ConstrainLayout();
    }

    void SaveLayout()
    {
        try
        {
            Directory.CreateDirectory(Engine.SessionsRoot);
            File.WriteAllText(LayoutPath, JsonSerializer.Serialize(new LayoutState(SidebarColumn.Width.IsAbsolute ? SidebarColumn.Width.Value : SidebarColumn.ActualWidth,
                _inspectorWidth, InspectorPanel.Visibility == Visibility.Visible)));
        }
        catch (Exception) { }
    }

    // 检查器以宽度动画滑入/收起。
    void InspectorToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = InspectorPanel.Visibility != Visibility.Visible || InspectorPanel.Width < 1;
        SetInspector(show);
    }

    void SetInspector(bool show)
    {
        if (show) ConstrainLayout(showingInspector: true);
        double full = _inspectorWidth;
        InspectorContent.Width = full;
        var animation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? full : 0, Duration = TimeSpan.FromMilliseconds(show ? 360 : 260), EasingFunction = SmoothEase,
        };
        if (show)
        {
            if (InspectorPanel.Visibility != Visibility.Visible) InspectorPanel.Width = 0;
            InspectorPanel.Visibility = Visibility.Visible;
            SaveLayout();
        }
        else animation.Completed += (_, _) =>
        {
            if (InspectorPanel.Width < 1) InspectorPanel.Visibility = Visibility.Collapsed;
            SaveLayout();
        };
        InspectorPanel.BeginAnimation(WidthProperty, animation);
    }

    // 带下拉菜单的按钮：左键直接弹出菜单。
    void DropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != ItemsGrid) return;
        int count = ItemsGrid.SelectedItems.Count;
        SelectionText.Text = count == 0 ? "未选择" : $"已选 {count} 行";
    }

    // 行号按当前显示顺序编排；筛选、排序或滚动复用行时都会重新计算。
    void ItemsGrid_LoadingRow(object? sender, DataGridRowEventArgs e) =>
        e.Row.Header = (e.Row.GetIndex() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    void ShowNoteSection()
    {
        if (InspectorPanel.Visibility != Visibility.Visible || InspectorPanel.Width < _inspectorWidth) SetInspector(true);
        Dispatcher.BeginInvoke(() => NoteSection.BringIntoView(), DispatcherPriority.Loaded);
    }

    // ───────────────────────── 启动 / 退出 ─────────────────────────
    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var notReady = Engine.CheckReady();
        var noRecognize = Engine.RecognizeScript == null ? "找不到识别入口脚本 " + Engine.SegmentedScript
            : Engine.FindShell() == null ? "找不到 PowerShell，无法启动识别。" : null;
        WelcomeNotice.Text = _isDemo ? "演示模式：仅预览，不能新建识别、调用 API、校验或导出。" : notReady ?? noRecognize ?? "";
        NewButton.IsEnabled = !_isDemo && notReady == null && noRecognize == null;
        LoadLayout();
        _ = RefreshRecentListAsync();
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy || _recentRemoving)
        {
            _showDialog("操作仍在进行，请等待完成后再退出。", "正在处理", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }
        if (_parallelRunActive && _runCts != null)
        {
            e.Cancel = true;
            if (_runCts.IsCancellationRequested)
            {
                _showDialog("正在等待识别任务停止并保存状态，请稍后再退出。", "正在取消识别", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this, "识别还在运行。取消识别并等待任务收尾吗？", "CAD 图纸复核工具", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (!SaveBeforeLeaving("退出")) return;
            _runCts.Cancel();
            RunningTitle.Text = "正在取消识别，请等待任务收尾…";
            return;
        }
        if (_runCts != null &&
            MessageBox.Show(this, "识别还在运行，退出会中断它。确定退出吗？", "CAD 图纸复核工具", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        if (!SaveBeforeLeaving("退出")) { e.Cancel = true; return; }
        _runCts?.Cancel();
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && _task != null)
        {
            if (!_busy) SaveNow();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && Workspace.IsVisible && !_busy)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control && NewButton.IsEnabled)
        {
            NewRecognize_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // ───────────────────────── 状态辅助 ─────────────────────────
    // 状态行始终更新；操作结果另以底部轻提示显示（自动保存等后台消息除外）。
    void Status(string text, bool toast = true)
    {
        StatusText.Text = text;
        if (toast && !text.StartsWith("已自动保存", StringComparison.Ordinal)) ShowToast(text);
    }

    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };

    void ShowToast(string text)
    {
        if (!IsLoaded) return;
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        ToastShift.BeginAnimation(TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = SmoothEase });
        _toastTimer.Stop();
        _toastTimer.Tick -= HideToast;
        _toastTimer.Tick += HideToast;
        _toastTimer.Start();
    }

    void HideToast(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => { if (!_toastTimer.IsEnabled) Toast.Visibility = Visibility.Collapsed; };
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Workspace.IsEnabled = !busy;
    }

    void AppendLog(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RunningLog.AppendText(line + Environment.NewLine);
            RunningLog.ScrollToEnd();
            LogBox.AppendText(line + Environment.NewLine);
        });
    }

    void ShowWelcome()
    {
        Workspace.Visibility = Visibility.Collapsed;
        Welcome.Visibility = Visibility.Visible;
        ResumeOrderButton.Visibility = _task != null ? Visibility.Visible : Visibility.Collapsed;
        if (WorkflowHint != null) WorkflowHint.Text = "先选择图纸；识别后核对结果，最后导出新的 Excel 明细。";
        _ = RefreshRecentListAsync();
    }

    async Task RefreshRecentListAsync()
    {
        int version = ++_recentLoadVersion;
        if (RecentList.ItemsSource == null)
        {
            RecentEmpty.Text = "正在加载最近任务…";
            RecentEmpty.ToolTip = null;
            RecentEmpty.Visibility = Visibility.Visible;
        }
        try
        {
            var recent = await TaskSession.LoadRecentDisplayAsync();
            if (version != _recentLoadVersion) return;
            RecentList.ItemsSource = recent;
            RecentEmpty.Text = "还没有最近任务";
            RecentEmpty.ToolTip = null;
            RecentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (version != _recentLoadVersion) return;
            RecentList.ItemsSource = null;
            RecentEmpty.Text = "最近任务暂时无法读取";
            RecentEmpty.ToolTip = ex.Message;
            RecentEmpty.Visibility = Visibility.Visible;
        }
    }

    void Home_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _runCts != null || !SaveBeforeLeaving("返回首页")) return;
        ShowWelcome();
    }

    void ResumeOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_task == null || _busy || _runCts != null) return;
        Welcome.Visibility = Visibility.Collapsed;
        Workspace.Visibility = Visibility.Visible;
    }

    void ApiSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_runCts != null) { Status("请等待识别结束后再打开设置。"); return; }
        if (_isDemo) { Status("演示模式不能修改 API 配置。"); return; }
        var window = new ApiSettingsWindow { Owner = this };
        window.ShowDialog();
    }

    void ApiReview_Click(object sender, RoutedEventArgs e)
    {
        if (_runCts != null) { Status("请等待识别结束后再打开图片复查。"); return; }
        if (_isDemo) { Status("演示模式不能调用 API。"); return; }
        var row = ItemsGrid.SelectedItem as ItemRow;
        var context = row == null ? "未选择本地记录；请自行核对截图与图纸。"
            : $"本地当前行：{row.OrigDrawing}　名称：{row.OrigName}　材质：{row.OrigMaterial}　数量：{row.OrigQuantity} {row.OrigUnit}　工艺：{row.OrigTreatment}";
        var window = new ApiReviewWindow { Owner = this, LocalContext = context };
        window.ShowDialog();
    }

    void RecognitionSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isDemo || _busy || _runCts != null) return;
        try { new RecognitionSettingsWindow(new RecognitionSettingsStore()) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { _showDialog("无法读取识别设置：" + ex.Message, "识别设置", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ───────────────────────── 新建识别 ─────────────────────────
    async void NewRecognize_Click(object sender, RoutedEventArgs e)
    {
        if (_isDemo || _busy || _runCts != null || _recentRemoving) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择要识别的 DWG 图纸（可多选）", Filter = "DWG 图纸 (*.dwg)|*.dwg", Multiselect = true, CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        await ImportDrawingsAsync(dialog.FileNames);
    }

    void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        bool allowed = !_isDemo && !_busy && _runCts == null && !_recentRemoving && NewButton.IsEnabled;
        e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    void DropOverlay_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if (_isDemo || _busy || _runCts != null || _recentRemoving || !NewButton.IsEnabled) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await ImportDrawingsAsync(paths);
    }

    // File picker and drag-and-drop share admission and the same queue.
    internal async Task ImportDrawingsAsync(IReadOnlyList<string> selected)
    {
        if (_isDemo || _busy || _runCts != null || _recentRemoving) return;
        if (!SaveBeforeLeaving("导入图纸")) return;
        List<string> drawings;
        try { drawings = selected.Select(Path.GetFullPath).ToList(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            _showDialog("无法读取所选路径：" + ex.Message, "这些图纸暂时不能识别", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var problems = Engine.CheckDrawings(drawings);
        if (drawings.Distinct(StringComparer.OrdinalIgnoreCase).Count() != drawings.Count)
            problems.Add("同一路径的图纸被重复选择，请只选择一次。");
        if (problems.Count > 0)
        {
            _showDialog(string.Join(Environment.NewLine, problems), "这些图纸暂时不能识别", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var outside = drawings.Where(f => !f.StartsWith(Engine.Root + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
        if (outside.Count > 0 &&
            _showDialog($"识别引擎只处理 {Engine.Root} 内的文件。\n将把 {outside.Count} 份图纸复制到 {Engine.InputsRoot}，原文件不会改动。继续吗？",
                "复制图纸", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        RunningLog.Clear();
        RunningPanel.ClearValue(Border.BackgroundProperty);
        RunningPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        RunningPanel.VerticalAlignment = VerticalAlignment.Stretch;
        RunningPanel.Width = double.NaN;
        RunningPanel.Margin = new Thickness(0);
        RunningContent.Width = 640;
        RunningContent.VerticalAlignment = VerticalAlignment.Top;
        RunningContent.Margin = new Thickness(0, 56, 0, 0);
        RunningLog.Height = 260;
        RunningPanel.Visibility = Visibility.Visible;
        _runCts = new CancellationTokenSource();
        var cts = _runCts;
        var failures = new List<string>();
        string? firstOpened = null;
        int succeeded = 0;
        bool cancelled = false;
        bool usedParallel = false;
        try
        {
            ParallelCapability? capability = null;
            if (drawings.Count > 1)
            {
                RunningTitle.Text = "正在检查批量识别接口…";
                capability = await ParallelRecognition.ProbeAsync(cts.Token);
                if (capability.Error != null) throw new InvalidOperationException(capability.Error);
                if (!capability.Supported) AppendLog("当前识别器没有并行接口，继续使用原有逐张队列。");
            }
            if (capability?.Supported == true)
            {
                usedParallel = true;
                _parallelRunActive = true;
                var result = await ImportParallelAsync(drawings, capability, failures, cts.Token);
                succeeded = result.Succeeded;
                firstOpened = result.FirstOpened;
            }
            else
            {
            for (int i = 0; i < drawings.Count && !cts.IsCancellationRequested; i++)
            {
                string name = Path.GetFileName(drawings[i]);
                RunningTitle.Text = $"正在识别 {i + 1} / {drawings.Count}：{name}";
                AppendLog($"[{i + 1}/{drawings.Count}] {name}");
                try
                {
                    var stageKey = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
                    var staged = Engine.StageDrawings([drawings[i]], stageKey);
                    RecognizeOutcome result;
                    while (true)
                    {
                        var key = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
                        var outputDir = Path.Combine(Engine.OutputsRoot, "ui-" + key);
                        result = await RecognitionRunner(staged, outputDir, AppendLog, cts.Token);
                        if (result.Ok || cts.IsCancellationRequested ||
                            !(result.Message.Contains("workspace_busy", StringComparison.OrdinalIgnoreCase) ||
                              result.Message.Contains("工作区正被另一个", StringComparison.Ordinal))) break;
                        RunningTitle.Text = $"等待其他识别或校验结束（{i + 1} / {drawings.Count}）：{name}";
                        AppendLog("工作区正忙，稍后自动重试；可以取消等待。");
                        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                        RunningTitle.Text = $"正在识别 {i + 1} / {drawings.Count}：{name}";
                    }
                    if (!result.Ok)
                    {
                        if (cts.IsCancellationRequested) break;
                        failures.Add(name + "：" + result.Message);
                        continue;
                    }
                    if (!TryOpenTask(result.ReviewFolder)) { failures.Add(name + "：识别完成，但无法打开复核任务。"); continue; }
                    firstOpened ??= result.ReviewFolder;
                    succeeded++;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
                catch (Exception ex) { failures.Add(name + "：" + ex.Message); }
            }
            }
            cancelled = cts.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { cancelled = true; }
        catch (Exception ex) { failures.Add("批量识别：" + ex.Message); cancelled = cts.IsCancellationRequested; }
        finally
        {
            _parallelRunActive = false;
            _runCts = null;
            cts.Dispose();
            RunningPanel.Visibility = Visibility.Collapsed;
        }
        if (firstOpened != null && (!usedParallel || _task == null)) TryOpenTask(firstOpened);
        string summary = $"已完成 {succeeded} / {drawings.Count} 张图纸。";
        if (cancelled) summary += " 已取消剩余识别，已完成的任务仍保留。";
        if (failures.Count > 0)
            _showDialog(summary + "\n\n未完成：\n" + string.Join("\n", failures),
                "部分图纸未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        Status(summary);
    }

    async Task<(int Succeeded, string? FirstOpened)> ImportParallelAsync(IReadOnlyList<string> drawings,
        ParallelCapability capability, List<string> failures, CancellationToken ct)
    {
        int maxParallel = new RecognitionSettingsStore().Load();
        var limits = ParallelRecognition.Limits();
        AppendLog($"批量资源：每任务内存上限 {limits.MemoryMiB} MiB，整机 CPU 预算 {limits.CpuPercent}%，单阶段超时 {limits.TimeoutSeconds} 秒；" +
            (maxParallel == 0 ? "并行数自动。" : $"最多同时 {maxParallel} 张。"));
        var staged = new List<string>();
        foreach (string drawing in drawings)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string key = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
                staged.Add(Engine.StageDrawings([drawing], key)[0]);
            }
            catch (Exception ex) { failures.Add(Path.GetFileName(drawing) + "：准备图纸失败：" + ex.Message); }
        }
        if (staged.Count == 0) return (0, null);

        int opened = 0;
        string? firstOpened = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ParallelRecognition.Prepare(staged, Engine.OutputsRoot);
            AppendLog("批次记录：" + batch.Output);
            var lastStates = new Dictionary<string, string>(StringComparer.Ordinal);
            var handled = new HashSet<string>(StringComparer.Ordinal);
            void Update(ParallelSnapshot snapshot)
            {
                foreach (var state in snapshot.Tasks)
                {
                    string name = Path.GetFileName(state.Task.Drawing);
                    string description = state.Status + (string.IsNullOrWhiteSpace(state.Reason) ? "" : "：" + state.Reason);
                    if (!lastStates.TryGetValue(state.Task.Id, out var old) || old != description)
                    {
                        lastStates[state.Task.Id] = description;
                        AppendLog($"[{state.Task.Id}] {name}：{description}");
                    }
                    if (state.Status != "passed" || !handled.Add(state.Task.Id)) continue;
                    string? review = TaskSession.FindReviewFolder(state.Task.Output);
                    if (review == null)
                    {
                        failures.Add(name + "：状态显示完成，但复核任务文件不完整：" + state.Task.Output);
                        continue;
                    }
                    bool ok = firstOpened == null ? TryOpenTask(review) : RegisterCompletedTab(review);
                    if (!ok) { failures.Add(name + "：识别完成，但无法打开复核任务：" + review); continue; }
                    firstOpened ??= review;
                    opened++;
                    // Keep the progress/cancel panel available while allowing the
                    // already-opened tabs to be reviewed and saved.
                    RunningPanel.Background = Brushes.Transparent;
                    RunningPanel.HorizontalAlignment = HorizontalAlignment.Right;
                    RunningPanel.VerticalAlignment = VerticalAlignment.Bottom;
                    RunningPanel.Width = 400;
                    RunningPanel.Margin = new Thickness(0, 0, 16, 44);
                    RunningContent.Width = 376;
                    RunningContent.Margin = new Thickness(0);
                    RunningLog.Height = 120;
                }
                int passed = snapshot.Tasks.Count(t => t.Status == "passed");
                int running = snapshot.Tasks.Count(t => t.Status == "running");
                int queued = snapshot.Tasks.Count(t => t.Status == "queued");
                RunningTitle.Text = ct.IsCancellationRequested ? "正在取消识别，请等待任务收尾…" :
                    $"识别完成 {passed} / {staged.Count}，运行 {running}，等待 {queued}";
            }
            var result = await ParallelRecognition.RunAsync(batch, capability.Python!, capability.Node!, maxParallel,
                limits, AppendLog, Update, ct);
            if (result.WorkspaceBusy && !ct.IsCancellationRequested)
            {
                RunningTitle.Text = "等待其他识别或校验结束…";
                AppendLog("工作区正忙，5 秒后用新批次目录重试；可以取消等待。");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            if (result.Error != null) failures.Add(result.Error + " 批次记录：" + batch.Output);
            if (result.Snapshot != null)
            {
                foreach (var state in result.Snapshot.Tasks)
                {
                    if (state.Status == "failed" || (state.Status == "cancelled" && !ct.IsCancellationRequested))
                        failures.Add(Path.GetFileName(state.Task.Drawing) + "：" + state.Status +
                            (string.IsNullOrWhiteSpace(state.Reason) ? "" : "：" + state.Reason) + "；记录：" + state.Task.Output);
                }
            }
            break;
        }
        return (opened, firstOpened);
    }

    // Add later completed tasks without switching away from a row the user is editing.
    bool RegisterCompletedTab(string review)
    {
        try
        {
            if (_orderTabs.Any(t => string.Equals(t.Folder, review, StringComparison.OrdinalIgnoreCase))) return true;
            var task = TaskSession.Open(review);
            var shared = _orderTabs.FirstOrDefault(t => string.Equals(t.SessionDir, task.SessionDir, StringComparison.OrdinalIgnoreCase));
            if (shared == null) _sessionCache[task.Folder] = task;
            if (shared != null)
            {
                if (string.Equals(shared.ManifestSha256, task.ManifestSha256, StringComparison.OrdinalIgnoreCase)) return true;
                _showDialog("新任务与已打开任务使用同一存档位置，但来源不同；请先关闭已打开的任务。",
                    "无法同时打开任务", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            string name = task.Files[0].Name + (task.Files.Count == 1 ? "" : $" 等 {task.Files.Count} 份");
            _orderTabs.Add(new OrderTab(task.Folder, name, string.Join("\n", task.Files.Select(f => f.Name + "  " + f.Path)) + "\n任务：" + task.Folder,
                task.SessionDir, task.ManifestSha256));
            try { task.RememberRecent(); }
            catch (Exception ex) { AppendLog("最近任务列表未更新：" + ex.Message); }
            return true;
        }
        catch (Exception ex)
        {
            _showDialog(ex.Message, "无法打开任务", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    void CancelRun_Click(object sender, RoutedEventArgs e)
    {
        _runCts?.Cancel();
        if (_parallelRunActive) RunningTitle.Text = "正在取消识别，请等待任务收尾…";
    }

    // ───────────────────────── 打开任务 ─────────────────────────
    void OpenTask_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择复核任务目录（含 source.json 和 图纸复核.xlsx，或其上一级）", InitialDirectory = Engine.OutputsRoot };
        if (dialog.ShowDialog(this) == true) OpenTask(dialog.FolderName);
    }

    // 单击最近任务即打开；点在移除按钮或滚动条上时不打开。
    void RecentList_ItemClick(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _recentRemoving || _runCts != null) return;
        RecentTaskView? clicked = null;
        for (var node = e.OriginalSource as DependencyObject; node != null && node != RecentList;)
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar) return;
            if (node is ListBoxItem { DataContext: RecentTaskView task }) { clicked = task; break; }
            node = node is Visual visual ? VisualTreeHelper.GetParent(visual)
                : node is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(node);
        }
        if (clicked != null) OpenTask(clicked.Folder);
    }

    void RecentList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _busy || _recentRemoving || _runCts != null) return;
        if (RecentList.SelectedItem is RecentTaskView task) { e.Handled = true; OpenTask(task.Folder); }
    }

    async void RecentRemove_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_busy || _recentRemoving || sender is not Button { DataContext: RecentTaskView task }) return;
        _recentRemoving = true;
        RecentList.IsEnabled = false;
        try
        {
            bool removed = await Task.Run(() => TaskSession.RemoveRecent(task));
            await RefreshRecentListAsync();
            if (!removed)
                _showDialog("最近任务已发生变化，列表已刷新。", "最近任务", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _showDialog("无法从最近任务中删除这条记录：" + ex.Message + "\n图纸和复核数据没有被删除。",
                "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _recentRemoving = false; RecentList.IsEnabled = true; }
    }

    public void OpenTask(string folder) => TryOpenTask(folder);

    bool TryOpenTask(string folder)
    {
        if (_busy) { Status("操作仍在进行，请等待完成后再切换任务。"); return false; }
        string? review;
        try { review = TaskSession.FindReviewFolder(folder); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            _showDialog(ex.Message, "无法打开任务", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (review == null)
        {
            _showDialog("该目录里没有 source.json 和 图纸复核.xlsx：" + folder, "无法打开任务", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var tab = _orderTabs.FirstOrDefault(t => string.Equals(t.Folder, review, StringComparison.OrdinalIgnoreCase));
        if (_task != null && string.Equals(_task.Folder, review, StringComparison.OrdinalIgnoreCase))
        {
            SelectOrderTab(tab);
            Welcome.Visibility = Visibility.Collapsed;
            Workspace.Visibility = Visibility.Visible;
            return true;
        }
        if (!SaveBeforeLeaving("切换任务")) return false;
        bool fromCache = _sessionCache.TryGetValue(review, out var cachedTask);
        TaskSession task;
        if (fromCache) task = cachedTask!;
        else
        {
            try { task = TaskSession.Open(review); }
            catch (Exception ex)
            {
                _showDialog(ex.Message, "无法打开任务", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        var sharedArchive = _orderTabs.FirstOrDefault(t =>
            string.Equals(t.SessionDir, task.SessionDir, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(t.Folder, task.Folder, StringComparison.OrdinalIgnoreCase));
        if (sharedArchive != null)
        {
            if (string.Equals(sharedArchive.ManifestSha256, task.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                return TryOpenTask(sharedArchive.Folder);
            _showDialog("这个任务与已打开任务使用同一存档位置，但来源不同；为避免混淆，请先关闭已打开的任务。",
                "无法同时打开任务", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (tab == null)
        {
            string name = task.Files[0].Name + (task.Files.Count == 1 ? "" : $" 等 {task.Files.Count} 份");
            tab = new OrderTab(task.Folder, name, string.Join("\n", task.Files.Select(f => f.Name + "  " + f.Path)) + "\n任务：" + task.Folder,
                task.SessionDir, task.ManifestSha256);
            _orderTabs.Add(tab);
        }
        SelectOrderTab(tab);
        _sessionCache[review] = task;
        bool hadSaved = !fromCache && File.Exists(task.DecisionsPath);
        if (_task != null) _task.Changed -= OnTaskChanged;
        _task = task;
        task.Changed += OnTaskChanged;
        string? recentError = null;
        if (!fromCache)
        {
            try { task.RememberRecent(); }
            catch (Exception ex) { recentError = ex.Message; }
        }
        _dirty = false;

        // 先在抑制刷新的状态下重置筛选、搜索和图纸选择，最后只刷新一次表格。
        _suspendRefresh = true;
        try
        {
            FileList.ItemsSource = task.Files;
            UpdateFilesSection();
            FilterBox.SelectedIndex = 0;
            SearchBox.Clear();
            FileList.SelectedIndex = task.Files.Count == 1 ? 0 : -1;
        }
        finally { _suspendRefresh = false; }
        _view = CollectionViewSource.GetDefaultView(task.Items);
        _view.Filter = Match;
        ItemsGrid.ItemsSource = _view;
        ItemsGrid.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(220))
            { EasingFunction = SmoothEase });
        // 标题直接显示图纸名（多图任务显示“首张 等 N 份”），演示模式加前缀。
        TitleText.Text = (_isDemo ? "演示 · " : "") + Path.GetFileNameWithoutExtension(task.Files[0].Name) +
            (task.Files.Count == 1 ? "" : $" 等 {task.Files.Count} 份");
        TitleText.ToolTip = $"{task.Data.Title} · {task.Data.Files.Count} 份图纸 · {task.Data.Items.Count} 条候选\n{task.Folder}";
        ErrorList.ItemsSource = null;
        IssueCountRun.Text = "";
        ErrorTab.Header = "待处理问题";
        LogBox.Clear();
        Welcome.Visibility = Visibility.Collapsed;
        Workspace.Visibility = Visibility.Visible;
        Workspace.IsEnabled = true;
        UpdateFileEditor();
        UpdateStats();
        if (fromCache)
        {
            Status("已切换到 " + tab.DisplayName, toast: false);
            return true;
        }
        int crossedDefaults = task.Items.Count(r => r.IsCrossedOut && r.Mode == Rules.Exclude);
        var loadStatus = task.LoadNote ?? (hadSaved ? "已载入上次保存的复核内容" :
            crossedDefaults > 0 ? $"已载入识别结果；{crossedDefaults} 条图面打X的明细默认不纳入，其余待复核。" : "已载入识别结果，所有明细均待复核");
        Status(loadStatus + (recentError == null ? "" : "；最近任务列表未更新：" + recentError), toast: false);

        // 没有界面存档，但任务目录里已有人用 Excel 填过的复核表 → 提示导入
        var excel = Path.Combine(task.Folder, "已复核.xlsx");
        if (!hadSaved && File.Exists(excel))
            _ = ImportFrom(excel);
        return true;
    }

    void SelectOrderTab(OrderTab? tab)
    {
        _changingOrderTab = true;
        try { OrderTabs.SelectedItem = tab; }
        finally { _changingOrderTab = false; }
    }

    void OrderTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingOrderTab || e.Source != OrderTabs || OrderTabs.SelectedItem is not OrderTab tab) return;
        if (!TryOpenTask(tab.Folder))
            SelectOrderTab(_orderTabs.FirstOrDefault(t => _task != null &&
                string.Equals(t.Folder, _task.Folder, StringComparison.OrdinalIgnoreCase)));
    }

    void CloseOrderTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_busy || _runCts != null || sender is not Button { DataContext: OrderTab tab }) return;
        int index = _orderTabs.IndexOf(tab);
        if (index < 0) return;
        bool active = _task != null && string.Equals(_task.Folder, tab.Folder, StringComparison.OrdinalIgnoreCase);
        if (active)
        {
            if (!SaveBeforeLeaving("关闭标签页")) return;
            var next = _orderTabs.FirstOrDefault(t => !ReferenceEquals(t, tab));
            if (next != null && !TryOpenTask(next.Folder)) return;
            if (next == null)
            {
                _task!.Changed -= OnTaskChanged;
                _task = null;
                _view = null;
                ItemsGrid.ItemsSource = null;
                FileList.ItemsSource = null;
                UpdateFilesSection();
                _dirty = false;
            }
        }
        _changingOrderTab = true;
        try { _orderTabs.Remove(tab); }
        finally { _changingOrderTab = false; }
        _sessionCache.Remove(tab.Folder);
        if (_task == null) ShowWelcome();
    }

    async void ImportExcel_Click(object sender, RoutedEventArgs e)
    {
        if (_task == null || _busy) return;
        if (_runCts != null) { Status("请等待识别结束后再导入复核表。"); return; }
        var dialog = new OpenFileDialog { Title = "选择已填写的复核表", Filter = "Excel 复核表 (*.xlsx)|*.xlsx", InitialDirectory = _task.Folder };
        if (dialog.ShowDialog(this) == true) await ImportFrom(dialog.FileName);
    }

    async Task ImportFrom(string xlsx)
    {
        if (_task == null || _busy) return;
        if (_runCts != null) { Status("请等待识别结束后再导入复核表。"); return; }
        var task = _task;
        if (_showDialog($"导入“{Path.GetFileName(xlsx)}”会替换当前所有明细和文件的复核决定，包括未保存的修改。\n" +
            "确认后将先保存当前决定并生成可恢复的导入前快照。是否继续？",
            "导入 Excel 复核", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        if (!SaveNow()) return;
        string backup;
        try { backup = _createImportBackup(task); }
        catch (Exception ex)
        {
            Status("导入已停止：无法创建导入前快照。");
            _showDialog("导入前快照创建失败，未导入。\n" + ex.Message, "导入 Excel 复核", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        long version = _editVersion;
        SetBusy(true);
        Status("正在后台校验导入文件，请等待…");
        try
        {
            var prepared = await task.ReadImportFromExcelAsync(xlsx);
            if (!ReferenceEquals(_task, task) || _editVersion != version)
                throw new InvalidOperationException("当前任务或决定在导入期间发生变化，未应用导入内容。");
            task.ApplyPreparedImport(prepared);
            var c = task.Counts();
            Status($"已导入 {Path.GetFileName(xlsx)}：待复核 {c.Pending}，采用 {c.Adopt}，修订 {c.Revise}，不纳入 {c.Exclude}；导入前快照：{backup}");
        }
        catch (Exception ex)
        {
            Status("导入失败，导入前快照：" + backup);
            _showDialog("导入失败：" + ex.Message + "\n导入前快照：" + backup,
                "导入 Excel 复核", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    // Copy the just-saved decisions while preventing a concurrent replacement.
    // A .partial file is never presented as a completed recoverable snapshot.
    static string CreateImportBackup(TaskSession task)
    {
        var current = task.Snapshot();
        using var manifest = new FileStream(task.Manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(manifest)), task.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("当前任务的来源已变化，不能创建快照。");
        using var source = new FileStream(task.DecisionsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = JsonDocument.Parse(source);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("已保存决定不是有效对象，不能创建快照。");

        static Dictionary<string, JsonElement> Unique(JsonElement element, StringComparer comparer)
        {
            var result = new Dictionary<string, JsonElement>(comparer);
            foreach (var property in element.EnumerateObject())
                if (!result.TryAdd(property.Name, property.Value))
                    throw new InvalidDataException("已保存决定包含重复字段，不能创建快照。");
            return result;
        }

        var properties = Unique(document.RootElement, StringComparer.OrdinalIgnoreCase);
        if (!properties.TryGetValue("Items", out var itemObject) || itemObject.ValueKind != JsonValueKind.Object ||
            !properties.TryGetValue("Files", out var fileObject) || fileObject.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("已保存决定缺少记录，不能创建快照。");
        foreach (var records in new[] { itemObject, fileObject })
            foreach (var record in Unique(records, StringComparer.Ordinal).Values)
            {
                if (record.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("已保存决定包含无效记录，不能创建快照。");
                Unique(record, StringComparer.OrdinalIgnoreCase);
            }

        var saved = document.RootElement.Deserialize<DecisionSet>(Json.Options)
            ?? throw new InvalidDataException("已保存的决定为空，不能创建快照。");
        if (saved.Schema is not (1 or 2) || saved.TaskId != task.Data.TaskId ||
            !string.Equals(saved.ManifestSha256, task.ManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            saved.Items == null || saved.Files == null ||
            saved.Items.Any(p => !current.Items.ContainsKey(p.Key) || p.Value == null || !Rules.Modes.Contains(p.Value.Mode)) ||
            saved.Files.Any(p => !current.Files.ContainsKey(p.Key) || p.Value == null || !Rules.FileStates.Contains(p.Value.State)))
            throw new InvalidDataException("已保存决定与当前任务不一致，不能创建快照。");

        var hasRevision = properties.TryGetValue("ArchiveRevision", out var revision);
        var hasChecksum = properties.TryGetValue("ArchiveContentSha256", out var checksum);
        if (saved.Schema == 2 || hasRevision || hasChecksum)
        {
            string actualChecksum = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(saved, Json.Options)));
            if (!hasRevision || !hasChecksum || revision.ValueKind != JsonValueKind.String ||
                checksum.ValueKind != JsonValueKind.String || !Guid.TryParseExact(revision.GetString(), "N", out _) ||
                !string.Equals(checksum.GetString(), actualChecksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("已保存决定的完整性校验失败，不能创建快照。");
        }

        static bool Same<T>(T left, T right) =>
            JsonSerializer.Serialize(left, Json.Options) == JsonSerializer.Serialize(right, Json.Options);

        foreach (var pair in current.Items)
        {
            if (!saved.Items.TryGetValue(pair.Key, out var value))
            {
                if (!pair.Value.IsBlank) throw new InvalidDataException("已保存决定与当前明细不一致，不能创建快照。");
            }
            else if (value == null || !Same(pair.Value, value))
                throw new InvalidDataException("已保存决定与当前明细不一致，不能创建快照。");
        }
        foreach (var pair in current.Files)
            if (!saved.Files.TryGetValue(pair.Key, out var value) || value == null || !Same(pair.Value, value))
                throw new InvalidDataException("已保存决定与当前文件核查不一致，不能创建快照。");

        string name = $"ui-decisions-before-import-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json";
        string backup = Path.Combine(task.SessionDir, name);
        string partial = backup + ".partial";
        source.Position = 0;
        using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.WriteThrough))
        {
            source.CopyTo(target);
            target.Flush(true);
            source.Position = 0;
            target.Position = 0;
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(source), SHA256.HashData(target)))
                throw new IOException("导入前快照校验失败。");
        }
        File.Move(partial, backup, false);
        return backup;
    }

    // ───────────────────────── 过滤 / 统计 ─────────────────────────
    bool Match(object o)
    {
        var row = (ItemRow)o;
        if (FileList.SelectedItem is FileRow f && row.FileId != f.Id) return false;
        if (!MatchesFilter(row, CurrentFilter)) return false;
        var q = SearchBox.Text.Trim();
        if (q == "") return true;
        return Contains(row.OrigDrawing, q) || Contains(row.OrigName, q) || Contains(row.OrigMaterial, q) ||
               Contains(row.EditDrawing, q) || Contains(row.EditName, q) || Contains(row.EditMaterial, q) || Contains(row.FileName, q);
    }

    static bool MatchesFilter(ItemRow row, string? filter)
    {
        switch (filter)
        {
            case "待复核" when row.Mode != Rules.Pending: return false;
            case "采用图纸" when row.Mode != Rules.Adopt: return false;
            case "人工修订" when row.Mode != Rules.Revise: return false;
            case "不纳入" when row.Mode != Rules.Exclude: return false;
            case "图面打X" when !row.IsCrossedOut: return false;
            case "重复图号" when !row.HasDuplicate: return false;
            case "有校验问题" when !row.HasError: return false;
            case "原值缺项" when row.IsCompleteOriginal: return false;
        }
        return true;
    }

    static bool Contains(string s, string q) => s.Contains(q, StringComparison.OrdinalIgnoreCase);

    void Filter_Changed(object sender, EventArgs e)
    {
        // 筛选胶囊始终保持一项选中。
        if (sender == FilterBox && FilterBox.SelectedItem == null) { FilterBox.SelectedIndex = 0; return; }
        if (_suspendRefresh) return;
        _view?.Refresh();
        UpdateFilterVisibility();
        if (_task != null) UpdateShownCount();
    }

    // 标记类筛选没有对应明细且未被选中时隐藏。
    void UpdateFilterVisibility()
    {
        foreach (var option in _filterOptions)
            option.Visibility = option.Optional && option.Count == 0 && !ReferenceEquals(FilterBox.SelectedItem, option)
                ? Visibility.Collapsed : Visibility.Visible;
    }

    void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendRefresh) return;
        UpdateFileEditor();
        _view?.Refresh();
        UpdateStats();
    }

    void AllFiles_Click(object sender, RoutedEventArgs e) => FileList.SelectedItem = null;

    void UpdateFileEditor()
    {
        bool has = FileList.SelectedItem is FileRow;
        FileFields.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        FileHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (FileGuidanceText == null) return;
        if (FileList.SelectedItem is not FileRow file)
        {
            FileGuidanceText.Text = "请选择左侧图纸，查看图纸提示并按实际情况完成核查。";
            return;
        }
        bool empty = _task != null && !_task.Items.Any(i => i.FileId == file.Id);
        bool needsReview = file.State == "待核查" || ((file.RequiresAcknowledgement || empty) &&
            (file.State != "已核查" || !Rules.Meaningful(file.Note)));
        FileGuidanceText.Text = file.State == "不纳入本次"
            ? "这张图纸不会导出。请在“更多图纸选项”中写明不纳入的原因。"
            : HasUnresolvedFileNote(file) || (needsReview && !CanConfirmFileSimply(file))
                ? "这张图纸还有需要具体处理的内容，请展开“更多图纸选项”查看。"
            : "请用 AutoCAD 打开原图，核对明细和重复图框的计入范围；导出时确认即可。";
    }

    void UpdateStats()
    {
        if (_task == null) return;
        var c = _task.Counts();
        int total = _task.Items.Count;
        int done = total - c.Pending;
        ReviewProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
            new System.Windows.Media.Animation.DoubleAnimation(total == 0 ? 0 : (double)done / total, TimeSpan.FromMilliseconds(500))
            { EasingFunction = SmoothEase });
        ProgressText.Text = $"已处理 {done} / {total}";
        ProgressPercent.Text = total == 0 ? "" : $"{Math.Round(100.0 * done / total)}%";
        // 胶囊上的数字按当前图纸范围计算，不受搜索影响。
        var scope = FileList.SelectedItem is FileRow f ? _task.Items.Where(i => i.FileId == f.Id).ToList() : _task.Items.ToList();
        foreach (var option in _filterOptions)
            option.Count = scope.Count(row => MatchesFilter(row, option.Key));
        UpdateFilterVisibility();
        UpdateShownCount();
        UpdateWorkflowHint();
        UpdateFileEditor();
    }

    void UpdateShownCount()
    {
        if (_task == null) return;
        var c = _task.Counts();
        int shown = _view?.Cast<object>().Count() ?? 0;
        // 标题下的一行摘要：范围 + 条数；各状态条数已显示在筛选上，不再重复。
        string scope = _task.Files.Count <= 1 ? "" :
            FileList.SelectedItem is FileRow f ? f.Name + " · " : $"{_task.Files.Count} 张图纸 · ";
        StatsText.Text = $"{scope}共 {_task.Items.Count} 条" + (shown == _task.Items.Count ? "" : $"，显示 {shown} 条");
        ActiveTaskSummary = (_task.Files.Count > 1 ? $"{_task.Files.Count} 张图纸 · " : "") +
            $"{_task.Items.Count} 条 · " + (c.Pending == 0 ? "已全部处理" : $"待复核 {c.Pending}");
    }

    // 侧栏“已打开的图纸”中选中项下方显示的摘要。
    public static readonly DependencyProperty ActiveTaskSummaryProperty =
        DependencyProperty.Register(nameof(ActiveTaskSummary), typeof(string), typeof(MainWindow), new PropertyMetadata(""));
    public string ActiveTaskSummary
    {
        get => (string)GetValue(ActiveTaskSummaryProperty);
        set => SetValue(ActiveTaskSummaryProperty, value);
    }

    // 新任务一张图纸一个任务，图纸分区只在历史多图任务中显示。
    void UpdateFilesSection()
    {
        int count = _task?.Files.Count ?? 0;
        FilesSection.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        FilesSectionTitle.Text = $"本任务内的图纸（{count} 张）";
    }

    void UpdateWorkflowHint()
    {
        if (_task == null || WorkflowHint == null) return;
        var excluded = _task.Files.Where(f => f.State == "不纳入本次").Select(f => f.Id).ToHashSet();
        int pending = _task.Items.Count(i => i.Mode == Rules.Pending && !excluded.Contains(i.FileId));
        var filesWithItems = _task.Items.Select(i => i.FileId).ToHashSet();
        var filesWithIncluded = _task.Items.Where(i => i.Mode is Rules.Adopt or Rules.Revise).Select(i => i.FileId).ToHashSet();
        var filesToCheck = _task.Files.Where(f =>
        {
            if (excluded.Contains(f.Id)) return false;
            bool empty = !filesWithItems.Contains(f.Id);
            bool included = filesWithIncluded.Contains(f.Id);
            return (included || empty) && (f.State == "待核查" ||
                ((f.RequiresAcknowledgement || empty) && (f.State != "已核查" || !Rules.Meaningful(f.Note))));
        }).ToList();
        int needDetails = filesToCheck.Count(f => !filesWithItems.Contains(f.Id) || !CanConfirmFileSimply(f));
        int simplePending = _task.Items.Count(i => CanPreviewAdopt(i, excluded));
        WorkflowHint.Text = pending > 0 && simplePending == pending
            ? $"请对照原图核对 {pending} 条明细，核对后点击右上角“确认并导出 Excel”。"
            : pending > 0
            ? $"还有 {pending - simplePending} 条明细需要单独处理；有缺项请直接在表格中补全。"
            : needDetails > 0
                ? $"还有 {needDetails} 张图纸需要处理具体事项，请点击导出后按提示查看。"
            : filesToCheck.Count > 0
                ? $"请对照原图核对 {filesToCheck.Count} 张图纸，完成后点击右上角“导出 Excel”并确认。"
                : "核对完成后，点击右上角“导出 Excel”，确认后即可生成明细表。";
    }

    void OnTaskChanged()
    {
        _editVersion++;
        _dirty = true;
        _refresh.Stop();
        _refresh.Start();
    }

    // ───────────────────────── 批量操作 ─────────────────────────
    List<ItemRow> Selected() => ItemsGrid.SelectedItems.Cast<ItemRow>().ToList();

    void DuplicateSingle_Click(object sender, RoutedEventArgs e)
    {
        if (_task == null || _busy) return;
        if (!CommitActiveEdit()) return;
        var selected = Selected();
        if (selected.Count != 1 || !selected[0].HasDuplicate)
        { Status("请先选中一条带“重复”标记的明细。"); return; }
        var row = selected[0];
        if (row.Mode == Rules.Exclude || row.IsCrossedOut)
        { _showDialog("该明细当前不纳入。请先处理排除状态；整框打X明细仍须通过“打X：改为纳入…”填写实际原因。", "请先处理排除", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var choices = DuplicateQuantity.SingleChoices(row);
        if (choices.Count == 0)
        { _showDialog("没有可选择的正常图框数量，或重复图框信息不完整；请对照原图人工处理。", "无法快捷选择", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var choice = DuplicateQuantity.UnambiguousSingle(row, choices) ?? DuplicateChoicePrompt(row, choices);
        if (choice == null) { Status("已取消选择，明细未改变。"); return; }
        bool replaceUnit = DuplicateQuantity.SingleReplacesUnitOverride(row, choice);
        if (replaceUnit)
        {
            string previousUnit = row.ConfUnit == ItemRow.ClearOverride ? "已清空" : row.EditUnit;
            string selectedUnit = string.IsNullOrWhiteSpace(choice.Unit) ? "未提供（将清空单位）" : choice.Unit.Trim();
            if (_showDialog($"当前单位已有人工决定：{previousUnit}。\n所选图框 {choice.FrameIndex} 的单位：{selectedUnit}。\n\n是否使用所选图框的单位？选择“否”会保留当前决定。",
                "确认更改已有单位", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            { Status("已取消选择，原数量和单位决定未改变。"); return; }
        }
        if (!DuplicateQuantity.ApplySingle(row, choice, replaceUnit))
        { _showDialog("图框或排除状态发生变化，请重新选择。", "无法快捷选择", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Status("已按所选一处记录数量和单位；请对照原图核对。");
        _view?.Refresh();
        UpdateStats();
    }

    void DuplicateSum_Click(object sender, RoutedEventArgs e)
    {
        if (_task == null || _busy) return;
        if (!CommitActiveEdit()) return;
        var selected = Selected();
        if (selected.Count != 1 || !selected[0].HasDuplicate)
        { Status("请先选中一条带“重复”标记的明细。"); return; }
        if (!DuplicateQuantity.ApplySum(selected[0]))
        { _showDialog("这条明细的默认累加不可用，或当前已排除。请对照原图处理。", "无法改回累加", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Status("已恢复默认累加；其他人工修订保留，请对照原图核对。");
        _view?.Refresh();
        UpdateStats();
    }

    void ItemsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not ItemRow row ||
            e.EditingElement is not TextBox editor) return;
        var before = row.ToDecision();
        var binding = editor.GetBindingExpression(TextBox.TextProperty);
        if (binding == null) return;
        binding.UpdateSource();
        if (Validation.GetHasError(editor)) { row.Apply(before); e.Cancel = true; return; }
        var after = row.ToDecision();
        bool changed = (before.DrawingNumber, before.Name, before.Material, before.Treatment, before.Quantity, before.Unit) !=
            (after.DrawingNumber, after.Name, after.Material, after.Treatment, after.Quantity, after.Unit);
        if (!changed || !row.IsCrossedOut || (row.Mode != Rules.Exclude && row.HasCrossedOutInclusionReason)) return;
        // Entering edit mode or pressing Escape must not create a decision.
        if (!MarkCrossedIncluded(row))
        {
            row.Apply(before); binding.UpdateTarget(); e.Cancel = true; return;
        }
        row.Mode = row.HasOverride ? Rules.Revise : Rules.Pending;
        Status("已将这条打X明细改为纳入（已自动记录），请对照原图核对。");
    }

    // 把处理方式应用到若干行；打X明细改为纳入时自动记录说明。返回（已改变，未改变）行数。
    internal (int Changed, int Skipped) ApplyMode(IReadOnlyList<ItemRow> targets, string selected)
    {
        int changed = 0, skipped = 0;
        foreach (var target in targets)
        {
            if (string.Equals(target.Mode, selected, StringComparison.Ordinal)) continue;
            if (target.IsCrossedOut && (selected is Rules.Adopt or Rules.Revise) && !MarkCrossedIncluded(target)) { skipped++; continue; }
            target.Mode = selected;
            if (string.Equals(target.Mode, selected, StringComparison.Ordinal)) changed++; else skipped++;
        }
        return (changed, skipped);
    }

    // 为打X明细写入“直接改为纳入”的自动说明；保留用户此前写的说明。
    bool MarkCrossedIncluded(ItemRow row)
    {
        if (!row.IsCrossedOut || row.HasCrossedOutInclusionReason) return true;
        string previous = row.Note.Trim();
        bool generated = previous.Length == 0 ||
            string.Equals(previous, row.Src.DefaultDecision?.Note?.Trim(), StringComparison.Ordinal) ||
            previous is ItemRow.AutoEditNote or ItemRow.AutoAdoptNote or ItemRow.AutoExcludeNote;
        string note = generated ? ItemRow.AutoCrossedIncludeNote : previous + Environment.NewLine + ItemRow.AutoCrossedIncludeNote;
        if (LooksUnresolved(note))
        {
            _showDialog("这条明细还有未解决的历史说明，请先在右侧详情的“历史说明”中处理。",
                "请先处理历史说明", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        row.Note = note;
        return true;
    }

    void ItemMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingItemMode || sender is not ComboBox { DataContext: ItemRow row, SelectedItem: string selected } combo ||
            string.Equals(selected, row.Mode, StringComparison.Ordinal)) return;
        bool userAction = combo.IsKeyboardFocusWithin || combo.IsDropDownOpen;
        // 用户在已多选的某一行上改处理方式时，对所有选中行一并应用。
        var targets = userAction && ItemsGrid.SelectedItems.Count > 1 && ItemsGrid.SelectedItems.Contains(row)
            ? Selected() : [row];
        var (changed, skipped) = ApplyMode(targets, selected);
        if (!string.Equals(row.Mode, combo.SelectedItem as string, StringComparison.Ordinal))
        {
            _changingItemMode = true;
            try { combo.SetCurrentValue(ComboBox.SelectedItemProperty, row.Mode); }
            finally { _changingItemMode = false; }
        }
        if (targets.Count > 1)
            Status($"已将 {changed} 行设为“{selected}”" + (skipped > 0 ? $"，{skipped} 行未改动" : ""));
        else if (row.IsCrossedOut && selected is Rules.Adopt or Rules.Revise)
            Status("已将这条打X明细改为纳入（已自动记录），请对照原图核对。");
    }

    void ReincludeCrossed_Click(object sender, RoutedEventArgs e)
    {
        if (_task == null || _busy) return;
        var selected = Selected();
        var rows = selected.Where(r => r.IsCrossedOut).ToList();
        if (rows.Count == 0)
        {
            Status("请先选中带“打X”标记的明细。");
            return;
        }
        string? answer = CrossedReasonPrompt(rows);
        if (answer == null) { Status("已取消，原处理方式未改变。"); return; }
        string reason = answer.Trim();
        if (!Rules.Meaningful(reason) || LooksUnresolved(reason) ||
            reason is ItemRow.AutoEditNote or ItemRow.AutoAdoptNote or ItemRow.AutoExcludeNote ||
            rows.Any(r => string.Equals(reason, r.Src.DefaultDecision?.Note?.Trim(), StringComparison.Ordinal)))
        {
            _showDialog("请填写明确的实际纳入原因，不能使用默认排除说明或自动操作记录。", "请填写纳入原因",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var decisions = new List<(ItemRow Row, string Note)>();
        foreach (var row in rows)
        {
            string previous = row.Note.Trim();
            string confirmation = "用户选择纳入图面打X明细，原因：" + reason;
            bool generated = previous.Length == 0 ||
                string.Equals(previous, row.Src.DefaultDecision?.Note?.Trim(), StringComparison.Ordinal) ||
                previous is ItemRow.AutoEditNote or ItemRow.AutoAdoptNote or ItemRow.AutoExcludeNote;
            string note = generated ? confirmation : previous.Contains(confirmation, StringComparison.Ordinal)
                ? previous : previous + Environment.NewLine + confirmation;
            if (LooksUnresolved(note))
            {
                _showDialog("所选明细还有未解决的历史说明。请先在右侧详情的“已保存的历史说明”中处理，再填写纳入原因。",
                    "请先处理历史说明", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            decisions.Add((row, note));
        }
        foreach (var decision in decisions)
        {
            decision.Row.Note = decision.Note;
            decision.Row.Mode = decision.Row.HasOverride ? Rules.Revise : decision.Row.IsCompleteOriginal ? Rules.Adopt : Rules.Pending;
        }
        Status($"已记录 {rows.Count} 条打X明细的纳入原因；有缺项时请继续补全并对照原图。");
        _view?.Refresh();
        UpdateStats();
    }

    void BulkAdopt_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected();
        if (rows.Count == 0) { Status("先在表格里选中要采用的行（Ctrl/Shift 可多选）"); return; }
        // 打X默认排除（用户未另行选择）的行视为可直接采用；用户自己设为不纳入的行不动。
        static bool DefaultCrossedExclusion(ItemRow r) => r.IsCrossedOut && r.Mode == Rules.Exclude &&
            string.Equals(r.Note.Trim(), r.Src.DefaultDecision?.Note?.Trim(), StringComparison.Ordinal);
        var ok = rows.Where(r => r.IsCompleteOriginal && r.Mode != Rules.Revise &&
            (r.Mode != Rules.Exclude || DefaultCrossedExclusion(r))).ToList();
        int revised = rows.Count(r => r.Mode == Rules.Revise);
        int excluded = rows.Count(r => r.Mode == Rules.Exclude && !DefaultCrossedExclusion(r));
        int missing = rows.Count(r => r.Mode != Rules.Revise && !(r.Mode == Rules.Exclude && !DefaultCrossedExclusion(r)) && !r.IsCompleteOriginal);
        if (ok.Count == 0)
        {
            _showDialog("选中的行里没有可以直接采用的：\n" + (missing > 0 ? $"· {missing} 行原值有缺项（图号/数量/单位/材质/工艺），请在表格中补全或设为不纳入。\n" : "") +
                (revised > 0 ? $"· {revised} 行已是人工修订，未改动。\n" : "") +
                (excluded > 0 ? $"· {excluded} 行已设为不纳入，未改动。" : ""), "采用图纸", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int adopted = 0;
        foreach (var r in ok)
        {
            if (r.IsCrossedOut && !MarkCrossedIncluded(r)) continue;
            r.ConfDrawing = r.ConfName = r.ConfMaterial = r.ConfTreatment = r.ConfQuantity = r.ConfUnit = "";
            r.Mode = Rules.Adopt;
            if (r.Mode == Rules.Adopt) adopted++;
        }
        int skipped = rows.Count - adopted;
        Status($"已采用 {adopted} 行" + (skipped > 0 ? $"，{skipped} 行因缺项、已修订或已设为不纳入而跳过" : "") + "；导出时会再次列出核对提示。");
        _view?.Refresh();
    }

    void BulkExclude_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected();
        if (rows.Count == 0) { Status("先在表格里选中要排除的行"); return; }
        foreach (var r in rows) r.Mode = Rules.Exclude;
        Status($"已将 {rows.Count} 行标记为不纳入");
        _view?.Refresh();
    }

    void ClearOldItemNote_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is not ItemRow row || _busy) return;
        string old = row.Note.Trim();
        if (old.Length == 0) { Status("选中行没有历史说明。"); return; }
        if (_showDialog("请先核对并处理这条历史说明中的内容：\n\n" + old +
            "\n\n确定清除这条说明吗？原图和识别来源不会改变。", "处理历史说明",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        row.Note = row.Mode switch
        {
            Rules.Revise when row.HasOverride => ItemRow.AutoEditNote,
            Rules.Adopt when row.Src.RequiresReview => ItemRow.AutoAdoptNote,
            Rules.Exclude when row.IsCrossedOut => row.Src.DefaultDecision?.Note ?? ItemRow.AutoExcludeNote,
            Rules.Exclude => ItemRow.AutoExcludeNote,
            _ => ""
        };
        Status("已处理历史说明；请继续对照原图核对。");
    }

    void BulkReset_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected();
        if (rows.Count == 0) return;
        if (rows.Any(r => r.Mode != Rules.Pending || r.HasOverride) &&
            MessageBox.Show(this, $"把选中的 {rows.Count} 行恢复为待复核，并清除已填的确认值和说明吗？", "恢复待复核", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var r in rows)
        {
            r.ConfDrawing = r.ConfName = r.ConfMaterial = r.ConfTreatment = r.ConfQuantity = r.ConfUnit = "";
            r.Note = "";
            r.Mode = Rules.Pending;
        }
        Status($"已恢复 {rows.Count} 行为待复核");
        _view?.Refresh();
    }

    // ───────────────────────── 打开图纸 ─────────────────────────
    void OpenDrawing_Click(object sender, RoutedEventArgs e)
    {
        string? path = (FileList.SelectedItem as FileRow)?.Path;
        if (path == null && ItemsGrid.SelectedItem is ItemRow row)
            path = _task?.Files.FirstOrDefault(f => f.Id == row.FileId)?.Path;
        if (path == null) { Status("先选中一份图纸或一行明细"); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Clipboard.SetText(path);
            MessageBox.Show(this, "本机没有可打开 DWG 的程序（" + ex.Message + "）。\n图纸路径已复制到剪贴板：\n" + path, "打开图纸", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ───────────────────────── 保存 / 校验 / 导出 ─────────────────────────
    void Save_Click(object sender, RoutedEventArgs e) { if (!_busy) SaveNow(); }

    bool SaveNow(bool auto = false)
    {
        if (_task == null) return true;
        if (!auto && !CommitActiveEdit()) return false;
        try
        {
            _task.Save();
            _dirty = false;
            Status((auto ? "已自动保存 " : "已保存 ") + DateTime.Now.ToString("HH:mm:ss"));
            return true;
        }
        catch (Exception ex)
        {
            Status("保存失败：" + ex.Message);
            if (!auto) _showDialog(ex.Message + "\n当前决定仍保留在界面中，请处理后重试。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    bool SaveBeforeLeaving(string action)
    {
        if (_task != null && !CommitActiveEdit()) return false;
        if (_task == null || !_dirty || SaveNow()) return true;
        if (_showDialog($"当前决定未能保存。是否将本窗口的决定另存为独立恢复会话后{action}？\n" +
            "选“否”会留在当前窗口；另一窗口的存档不会被覆盖。",
            "保留当前决定", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        try
        {
            string recoveryRoot = _task.SaveRecoverySession();
            _showDialog($"当前决定已保存到独立恢复会话：\n{recoveryRoot}\n\n" +
                $"恢复时可运行 CadReviewUI.exe --sessions \"{recoveryRoot}\" --open \"{_task.Folder}\"；原共享存档未被覆盖。",
                "恢复会话已保存", MessageBoxButton.OK, MessageBoxImage.Information);
            _dirty = false;
            return true;
        }
        catch (Exception ex)
        {
            Status("恢复副本保存失败，当前决定仍在窗口中。");
            _showDialog("无法保存可恢复的独立副本，操作已取消。\n" + ex.Message,
                "恢复副本保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    bool CommitActiveEdit()
    {
        if (ItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
            ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true)) return true;
        Status("请先完成当前单元格的编辑，再保存或切换。");
        return false;
    }

    Task<ValidationDto?> RunValidate() => RunValidateCore(null, null);

    async Task<ValidationDto?> RunValidateCore(string? previewPath, IReadOnlySet<string>? proposedIds)
    {
        if (_task == null || _busy) return null;
        if (_runCts != null) { Status("请等待识别结束后再校验或导出。"); return null; }
        if (_isDemo) { Status("演示模式不能校验或导出。"); return null; }
        var task = _task;
        if (!SaveNow()) return null;
        List<string> problems;
        string? writtenHash;
        try { problems = previewPath == null ? task.WriteReviewed(out writtenHash) : task.WritePreviewReviewed(previewPath, proposedIds!, out writtenHash); }
        catch (Exception ex) { _showDialog("准备导出失败：" + ex.Message, "无法导出", MessageBoxButton.OK, MessageBoxImage.Error); return null; }
        // Never validate/export a workbook after a local write error. In
        // particular, a rejected quantity must not silently fall back to CAD.
        if (problems.Count > 0)
        {
            var blocked = new ValidationDto { Status = "blocked", Errors = problems };
            ShowErrors(_task.MapErrors(problems));
            _view?.Refresh();
            _task.RefreshFileSummaries();
            UpdateStats();
            return blocked;
        }
        SetBusy(true);
        Status("正在校验（会核对图纸文件的哈希，大图可能要几秒）…");
        ValidationDto dto;
        try { dto = previewPath == null ? await task.ValidateWithReviewLockAsync(writtenHash!) :
                await task.ValidatePreviewWithReviewLockAsync(previewPath, writtenHash!); }
        catch (Exception ex)
        {
            Status("核对文件发生变化，未导出。");
            _showDialog("无法完成导出检查：" + ex.Message, "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally { SetBusy(false); }
        var errors = problems.Concat(dto.Errors).ToList();
        if (dto.Status == "failed") errors.Insert(0, dto.Error ?? "引擎返回失败");
        ShowErrors(_task.MapErrors(errors));
        _view?.Refresh();
        _task.RefreshFileSummaries();
        UpdateStats();
        return dto;
    }

    void ShowErrors(List<ErrorEntry> entries)
    {
        ErrorList.ItemsSource = entries.Select(e => e with { Text = ExplainIssue(e) }).ToList();
        ErrorTab.Header = entries.Count == 0 ? "待处理问题" : $"待处理问题 ({entries.Count})";
        IssueCountRun.Text = entries.Count == 0 ? "" : $"  · {entries.Count} 项待处理";
        if (entries.Count > 0) { BottomTabs.SelectedItem = ErrorTab; IssuesPanel.IsExpanded = true; }
    }

    string ExplainIssue(ErrorEntry entry)
    {
        string text = entry.Text;
        if (entry.FileId != null && (text.Contains("文件诊断/零明细需核查并说明") || text.Contains("文件仍被标记为待核查")))
        {
            var file = _task?.Files.FirstOrDefault(f => f.Id == entry.FileId);
            string name = file?.Name ?? entry.FileId;
            if (_task != null && !_task.Items.Any(i => i.FileId == entry.FileId))
                return $"图纸“{name}”没有识别到明细。请查看原图；如不需导出这张图纸，可在“更多图纸选项”中选择不纳入并说明原因。";
            if (file != null && !CanConfirmFileSimply(file))
                return $"图纸“{name}”还有需要具体处理的内容。请展开“更多图纸选项”，查看提示或已有核查说明后再导出。";
            return $"请对照原图核对图纸“{name}”的明细；完成后点击导出并确认即可。";
        }
        if (entry.FileId != null && text.Contains("整份排除必须说明原因"))
            return "整张图纸不纳入时，请在“更多图纸选项”的核查说明中填写原因。";
        if (entry.FileId != null && text.Contains("父图和子件同时纳入"))
            return "父图和子件都要导出时，请在“更多图纸选项”中填写两者的计价范围说明。";
        if (entry.ItemId != null && text.Contains("尚未复核"))
            return "这条明细还未核对：请核对原图，并选择采用图纸、人工修订或不纳入。";
        if (entry.ItemId != null && text.Contains("仍有缺值"))
            return "这条明细仍有缺项：请对照原图，直接在表格中补充图号、数量、单位、材质或工艺。";
        if (entry.ItemId != null && text.Contains("必须填写复核说明"))
            return "这条明细仍需处理，请对照原图检查并在表格中修改，或选择处理方式。";
        if (entry.ItemId != null && text.Contains("不纳入须说明原因"))
            return "请重新选择这条明细的处理方式。";
        if (entry.ItemId != null && text.Contains("确认数量"))
            return "这条明细的数量无效或精度超出范围，请直接在表格的“数量”列修改。";
        return text;
    }

    async void Validate_Click(object sender, RoutedEventArgs e)
    {
        var dto = await RunValidate();
        if (dto == null) return;
        switch (dto.Status)
        {
            case "ready":
                Status($"校验通过：可导出 {dto.Accepted.Count} 条，排除 {dto.Excluded.Count} 条");
                MessageBox.Show(this, $"校验通过。\n将导出 {dto.Accepted.Count} 条确认明细，另有 {dto.Excluded.Count} 条被排除。", "校验", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case "nothing_to_export":
                Status("校验通过，但所有明细都被排除，没有可导出的内容");
                break;
            default:
                Status($"还有 {ErrorList.Items.Count} 项需要处理，其中 {dto.Pending} 条尚未核对（见“待处理问题”页）");
                break;
        }
    }

    const string ExportConfirmationNote = "用户确认已对照原图核对本次明细及计入范围（含重复图框）；读取提示的影响未由程序确认。";

    static string ConfirmationNoteFor(FileRow file)
    {
        var parts = new List<string>();
        var reader = Regex.Match(file.Diagnostics, @"([0-9]+)条原生读取器通知");
        var text = Regex.Match(file.Diagnostics, @"([0-9]+)条文字格式/编码警告");
        if (reader.Success) parts.Add("读取提示 " + reader.Groups[1].Value + " 项");
        if (text.Success) parts.Add("文字格式提示 " + text.Groups[1].Value + " 项");
        return ExportConfirmationNote + (parts.Count == 0 ? "" : " " + string.Join("、", parts) + "。");
    }

    // 锁定所选文件并检查受保护路径；已有文件需确认替换，拒绝时返回 null。
    SafeExportDestination? AcquireDestination(string path, TaskSession task)
    {
        string originals = Path.Combine(Engine.Root, "图纸与要求");
        var protectedFiles = task.Files.Select(f => f.Path)
            .Concat(new[] { task.Manifest, task.SourceXlsx, task.ReviewedPath, task.DecisionsPath,
                originals, Path.Combine(Engine.Root, "1.一次发货"), Path.Combine(Engine.Root, "2.二次发货") })
            .Concat(ExternalProtectedInputs)
            .Concat(Directory.Exists(originals) ? Directory.EnumerateFiles(originals, "*.xlsx") : []);
        var chosen = SafeExportDestination.Acquire(path, protectedFiles, Engine.Root);
        if (chosen.Replacing && _showDialog("文件已存在：\n" + path +
            "\n\n确定替换这个文件吗？原文件会保留为同目录备份。", "确认替换文件", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            chosen.Dispose();
            return null;
        }
        return chosen;
    }

    // ───────────────────────── 批量导出 ─────────────────────────
    // 依次切换到每张已打开的图纸，沿用单张导出的全部检查与“导出前确认”，保存到同一文件夹。
    async void BatchExport_Click(object sender, RoutedEventArgs e) => await BatchExportAsync();

    internal async Task BatchExportAsync()
    {
        if (_busy || _runCts != null) return;
        if (_isDemo) { Status("演示模式不能校验或导出。"); return; }
        if (_orderTabs.Count == 0) { Status("还没有打开的图纸。"); return; }
        if (!Rules.Meaningful(ReviewerBox.Text.Trim()))
        {
            _showDialog("请先填写右上角的复核人姓名，再导出。", "请填写复核人", MessageBoxButton.OK, MessageBoxImage.Information);
            ReviewerBox.Focus();
            return;
        }
        if (_task != null && !CommitActiveEdit()) return;
        // 先勾选要导出的图纸（序号与侧栏一致），再选保存文件夹。
        var choices = _orderTabs.Select((t, i) => new BatchExportWindow.Choice(i + 1, t.DisplayName, t.ToolTip, t)).ToList();
        var picked = BatchChooser(choices, null);
        if (picked == null || picked.Count == 0) return;
        var tabs = picked.Select(c => (Number: c.Number, Tab: (OrderTab)c.Key)).ToList();
        string? folder = BatchFolderSelector(tabs.Count);
        if (folder == null) return;

        var results = new List<string>();
        int exportedCount = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previousSelector = DestinationSelector;
        DestinationSelector = (defaultName, task) => AcquireBatchDestination(folder, defaultName, task, usedNames);
        try
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                var (number, tab) = tabs[i];
                if (!_orderTabs.Contains(tab)) continue;
                Status($"批量导出 {i + 1} / {tabs.Count}：{tab.DisplayName}", toast: false);
                if (!TryOpenTask(tab.Folder)) { results.Add($"{number}. {tab.DisplayName}：无法打开，已跳过"); continue; }
                _lastExportedPath = _lastExportWarning = null;
                if (await ExportCurrentAsync(batch: true))
                {
                    exportedCount++;
                    results.Add($"{number}. {tab.DisplayName}：已导出为 {Path.GetFileName(_lastExportedPath)}" +
                        (_lastExportWarning == null ? "" : "（文件已保存，内部保存记录写入失败）"));
                }
                else results.Add($"{number}. {tab.DisplayName}：未导出（{StatusText.Text}）");
            }
        }
        finally { DestinationSelector = previousSelector; }
        Status($"批量导出完成：{exportedCount} / {tabs.Count} 张已导出");
        if (exportedCount > 0)
        {
            try { OpenBatchExportFolder(folder); }
            catch (Exception) { }
        }
        _showDialog($"批量导出完成：{exportedCount} / {tabs.Count} 张已导出。\n保存位置：{folder}\n\n" + string.Join("\n", results),
            "批量导出", MessageBoxButton.OK, exportedCount == tabs.Count ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // 勾选窗口（测试时可替换）：返回勾选的图纸，取消返回 null。
    internal Func<IReadOnlyList<BatchExportWindow.Choice>, object?, IReadOnlyList<BatchExportWindow.Choice>?> BatchChooser { get; set; }
    internal Func<int, string?> BatchFolderSelector { get; set; }
    internal Action<string> OpenBatchExportFolder { get; set; } = folder =>
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });

    string? ChooseBatchFolder(int count)
    {
        var dialog = new OpenFolderDialog { Title = $"选择 {count} 张图纸的保存文件夹（仅本机磁盘，不支持网络共享或映射盘）" };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    IReadOnlyList<BatchExportWindow.Choice>? ShowBatchChooser(IReadOnlyList<BatchExportWindow.Choice> choices, object? current)
    {
        var window = new BatchExportWindow(choices, current) { Owner = this };
        return window.ShowDialog() == true ? window.Chosen : null;
    }

    // 批量导出时自动使用默认文件名；同一批次内重名时追加 (2)、(3)…
    SafeExportDestination? AcquireBatchDestination(string folder, string defaultName, TaskSession task, HashSet<string> usedNames)
    {
        string baseName = Path.GetFileNameWithoutExtension(defaultName);
        string name = defaultName;
        for (int n = 2; !usedNames.Add(name); n++) name = $"{baseName} ({n}).xlsx";
        try { return AcquireDestination(Path.Combine(folder, name), task); }
        catch (Exception ex)
        {
            _showDialog("不能使用这个保存位置：" + ex.Message, "批量导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    SafeExportDestination? ChooseExportDestination(string defaultName, TaskSession task)
    {
        var dialog = new SaveFileDialog
        {
            Title = "选择保存位置（仅本机磁盘，不支持网络共享或映射盘）",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx", AddExtension = true, FileName = defaultName,
            // The WPF built-in overwrite prompt runs before FileOk. Ask here instead,
            // after locking the exact selected file, so its answer cannot authorize
            // a different file appearing while the prompt is on screen.
            OverwritePrompt = false, CreateTestFile = false, CheckPathExists = true
        };
        SafeExportDestination? chosen = null;
        dialog.FileOk += (_, e) =>
        {
            chosen?.Dispose();
            chosen = null;
            try
            {
                chosen = AcquireDestination(dialog.FileName, task);
                if (chosen == null) { e.Cancel = true; return; }
            }
            catch (Exception ex)
            {
                chosen?.Dispose();
                chosen = null;
                e.Cancel = true;
                _showDialog("不能使用这个保存位置：" + ex.Message, "请选择其他位置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        try
        {
            if (dialog.ShowDialog(this) == true && chosen != null) return chosen;
            chosen?.Dispose();
            return null;
        }
        catch { chosen?.Dispose(); throw; }
    }

    static bool CanPreviewAdopt(ItemRow row, ISet<string> excludedFiles) =>
        !excludedFiles.Contains(row.FileId) && (DuplicateQuantity.CanProposeDefault(row) ||
        (!row.IsCrossedOut && row.Mode == Rules.Pending && row.IsCompleteOriginal && !row.HasOverride &&
        row.Src.RequiresReviewSpecified && !row.Src.RequiresReview && row.Src.WarningsSpecified && string.IsNullOrWhiteSpace(row.Warnings) &&
        string.IsNullOrWhiteSpace(row.Note)));

    static bool TryAcceptedFiles(TaskSession task, ValidationDto dto, out Dictionary<string, int> files,
        IReadOnlySet<string>? proposedIds = null)
    {
        files = new Dictionary<string, int>(StringComparer.Ordinal);
        var known = task.Files.Where(f => f.State != "不纳入本次").Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var items = task.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in dto.Accepted)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("file_id", out var value) ||
                value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()) || !known.Contains(value.GetString()!) ||
                !item.TryGetProperty("id", out var itemId) || itemId.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(itemId.GetString()) || !seen.Add(itemId.GetString()!) ||
                !items.TryGetValue(itemId.GetString()!, out var row) || row.FileId != value.GetString() ||
                !(row.Mode is Rules.Adopt or Rules.Revise || (row.Mode == Rules.Pending && proposedIds?.Contains(row.Id) == true)))
                return false;
            string id = value.GetString()!;
            files[id] = files.GetValueOrDefault(id) + 1;
        }
        return dto.Accepted.Count > 0 && files.Count > 0;
    }

    static bool LooksUnresolved(string? value) => Rules.LooksUnresolved(value);

    static bool HasUnresolvedFileNote(FileRow file) => LooksUnresolved(file.Note);

    // A simple confirmation covers the standard frame-association notice and
    // count-only reader/text-format notices. Explicit missing frames, bad values,
    // extraction problems or unknown diagnostics still need individual handling.
    static bool CanConfirmFileSimply(FileRow file)
    {
        if (HasUnresolvedFileNote(file)) return false;
        return HasOnlyRoutineDiagnostics(file);
    }

    static bool HasOnlyRoutineDiagnostics(FileRow file)
    {
        var pieces = file.Diagnostics.Split('；', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0) return !file.RequiresAcknowledgement;
        foreach (string piece in pieces)
        {
            if (piece is "图框范围与字段归属由路径几何推断，须对照原图核查" or
                "图框范围与字段归属由路径/几何推断，须对照原图核查" or
                "本次未发现需要文件级确认的诊断") continue;
            if (Regex.IsMatch(piece, @"\A[1-9][0-9]*条原生读取器通知需核查，完整内容保留在原报告及投影的notifications中\z") ||
                Regex.IsMatch(piece, @"\A[1-9][0-9]*条文字格式/编码警告保留在诊断记录\z") ||
                Regex.IsMatch(piece, @"\A[1-9][0-9]*个图框整框打X，只出现在这些图框的图号默认不纳入（可改回）\z") ||
                Regex.IsMatch(piece, @"\A本文件有[1-9][0-9]*组重复图号，数量请核对\z") ||
                Regex.IsMatch(piece, @"\A同一图纸内有 [1-9][0-9]* 组重复图号\z")) continue;
            return false;
        }
        return true;
    }

    static bool TryExcludedFiles(TaskSession task, ValidationDto dto, out Dictionary<string, int> files)
    {
        files = new Dictionary<string, int>(StringComparer.Ordinal);
        var known = task.Files.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var items = task.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var excludedFiles = task.Files.Where(f => f.State == "不纳入本次").Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in dto.Excluded)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("file_id", out var value) ||
                value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()) || !known.Contains(value.GetString()!) ||
                !item.TryGetProperty("id", out var itemId) || itemId.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(itemId.GetString()) || !seen.Add(itemId.GetString()!) ||
                !items.TryGetValue(itemId.GetString()!, out var row) || row.FileId != value.GetString() ||
                (row.Mode != Rules.Exclude && !excludedFiles.Contains(row.FileId)))
                return false;
            string id = value.GetString()!;
            files[id] = files.GetValueOrDefault(id) + 1;
        }
        return true;
    }

    static string BuildConfirmationScope(TaskSession task, ValidationDto dto, IReadOnlyDictionary<string, int> included,
        IReadOnlyDictionary<string, int> excluded)
    {
        var lines = new List<string>();
        int length = 0;
        void AddLine(string line)
        {
            length = checked(length + line.Length + Environment.NewLine.Length);
            if (length > 1_000_000) throw new InvalidDataException("本次明细范围太长，无法完整显示。请减少本次处理的图纸数量后重试。");
            lines.Add(line);
        }
        var rows = task.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var includedRows = dto.Accepted.GroupBy(x => x.GetProperty("file_id").GetString()!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => rows[x.GetProperty("id").GetString()!]).ToList(), StringComparer.Ordinal);
        var omitted = dto.Excluded.GroupBy(x => x.GetProperty("file_id").GetString()!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var duplicateNames = task.Files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in task.Files)
        {
            string label = file.Id + " · " + file.Name;
            string path = duplicateNames.Contains(file.Name) ? Environment.NewLine + "    路径：" + file.Path : "";
            string note = file.Note.Trim();
            if (note.Contains(ExportConfirmationNote, StringComparison.Ordinal))
                note = note.Replace(ConfirmationNoteFor(file), "", StringComparison.Ordinal).Replace(ExportConfirmationNote, "", StringComparison.Ordinal).Trim();
            string prior = note.Length > 0 ? Environment.NewLine + "    已有说明：" + note : "";
            string parent = string.IsNullOrWhiteSpace(file.ParentChildNote) ? "" : Environment.NewLine + "    父子范围：" + file.ParentChildNote.Trim();
            int omit = excluded.GetValueOrDefault(file.Id);
            if (included.TryGetValue(file.Id, out int count))
            {
                var notices = new List<string>();
                var reader = Regex.Match(file.Diagnostics, @"([0-9]+)条原生读取器通知");
                var text = Regex.Match(file.Diagnostics, @"([0-9]+)条文字格式/编码警告");
                var frames = Regex.Match(file.Diagnostics, @"([0-9]+)个图框未确定唯一图号");
                var numbers = Regex.Match(file.Diagnostics, @"([0-9]+)个非有限数字已留空");
                var issues = Regex.Match(file.Diagnostics, @"([0-9]+)条原生提取问题");
                var crossed = Regex.Match(file.Diagnostics, @"([0-9]+)个图框整框打X");
                if (reader.Success || text.Success) notices.Add("识别时有提示，可能有遗漏");
                if (frames.Success) notices.Add(frames.Groups[1].Value + " 个图框未确定图号");
                if (numbers.Success) notices.Add(numbers.Groups[1].Value + " 处数字未能读取");
                if (issues.Success) notices.Add("读取问题 " + issues.Groups[1].Value + " 项");
                if (crossed.Success) notices.Add(crossed.Groups[1].Value + " 个图框标记打X，请对照原图核对");
                if (!HasOnlyRoutineDiagnostics(file) && !frames.Success && !numbers.Success && !issues.Success)
                    notices.Add("有其他图纸提示，请查看图纸选项");
                string notice = notices.Count > 0 ? "；" + string.Join("、", notices) : "";
                string omittedText = omit > 0 ? $"；另有 {omit} 条不导出" : "";
                AddLine($"• {label}：导出 {count} 条{omittedText}{notice}{path}{prior}{parent}");
                foreach (var row in includedRows[file.Id].Where(r => r.Mode == Rules.Revise))
                    AddLine($"    人工修改：{row.EditDrawing}；名称 {row.EditName}；材质 {row.EditMaterial}；工艺 {row.EditTreatment}；数量 {row.EditQuantity} {row.EditUnit}");
                foreach (var row in includedRows[file.Id].Where(r => r.HasDuplicate))
                    AddLine($"    重复图号 {row.EditDrawing}，本次数量 {row.EditQuantity} {row.EditUnit}：{row.DuplicateDetails}");
                foreach (var row in includedRows[file.Id].Where(r => r.Src.RequiresReview ||
                    !r.Src.RequiresReviewSpecified || !r.Src.WarningsSpecified || !string.IsNullOrWhiteSpace(r.Warnings)))
                    AddLine($"    请对照原图核对 {row.EditDrawing}：" +
                        (string.IsNullOrWhiteSpace(row.Warnings) ? "该行的核对提示或提示状态不完整。" : row.Warnings.Trim()));
                foreach (var group in includedRows[file.Id].Where(r => !string.IsNullOrWhiteSpace(r.Note) &&
                    !r.Note.StartsWith("在界面", StringComparison.Ordinal) && !r.Note.StartsWith("在表格", StringComparison.Ordinal))
                    .GroupBy(r => r.Note.Trim(), StringComparer.Ordinal))
                {
                    string drawings = string.Join("、", group.Select(r => Rules.Meaningful(r.OrigDrawing) ? r.OrigDrawing : "未识别图号"));
                    AddLine($"    已有记录（{drawings}）：{group.Key}");
                }
            }
            else if (file.State == "不纳入本次") AddLine($"• {label}：整张图纸不导出{path}{prior}");
            else if (omit > 0) AddLine($"• {label}：{omit} 条明细不导出{path}{prior}");
            else if (!task.Items.Any(i => i.FileId == file.Id)) AddLine($"• {label}：没有识别到明细，本次不导出{path}{prior}");
            if (omitted.TryGetValue(file.Id, out var omittedRows))
            {
                foreach (var item in omittedRows)
                {
                    var row = rows[item.GetProperty("id").GetString()!];
                    string drawing = Rules.Meaningful(row.OrigDrawing) ? row.OrigDrawing : "未识别图号";
                    string reason = item.TryGetProperty("reason", out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString() ?? "" : file.State == "不纳入本次" ? file.Note : row.Note;
                    string originalCrossedReason = row.IsCrossedOut ? row.Src.DefaultDecision?.Note?.Trim() ?? "" : "";
                    if (originalCrossedReason.Length > 0 && !reason.Contains(originalCrossedReason, StringComparison.Ordinal))
                        reason += (reason.Length == 0 ? "" : "；") + originalCrossedReason;
                    AddLine($"    不导出：{drawing}（{reason}）");
                }
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    static bool TryFileConfirmationError(TaskSession task, string error, ISet<string> acceptedFiles, out FileRow? file)
    {
        file = null;
        int separator = error.IndexOfAny([':', '：']);
        if (separator <= 0) return false;
        string id = error[..separator].Trim(), reason = error[(separator + 1)..].Trim();
        if (reason is not ("文件仍被标记为待核查" or "文件诊断/零明细需核查并说明") || !acceptedFiles.Contains(id))
            return false;
        file = task.Files.FirstOrDefault(f => f.Id == id && f.State != "不纳入本次");
        return file != null && CanConfirmFileSimply(file);
    }

    static string DecisionSignature(ValidationDto dto) =>
        string.Join("\n", dto.Accepted.Select(item => item.GetRawText()).OrderBy(x => x, StringComparer.Ordinal)) + "\n--EXCLUDED--\n" +
        string.Join("\n", dto.Excluded.Select(item => item.GetRawText()).OrderBy(x => x, StringComparer.Ordinal));

    static bool HasCompleteDecisionCoverage(TaskSession task, ValidationDto dto)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in dto.Accepted.Concat(dto.Excluded))
        {
            if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(id.GetString()) || !ids.Add(id.GetString()!)) return false;
        }
        return ids.Count == task.Items.Count && task.Items.All(i => ids.Contains(i.Id));
    }

    void ShowExportProblem(TaskSession task, ValidationDto dto, ISet<string> acceptedFiles)
    {
        Status(dto.Status == "nothing_to_export" ? "没有可导出的明细" : $"还有 {ErrorList.Items.Count} 项需要处理，未导出");
        if (dto.Status == "blocked")
        {
            var entries = ErrorList.Items.OfType<ErrorEntry>().ToList();
            int index = dto.Errors.FindIndex(error => !TryFileConfirmationError(task, error, acceptedFiles, out _));
            var first = index >= 0 && index < entries.Count ? entries[index] : entries.FirstOrDefault();
            if (first != null) FocusIssue(first);
            string explanation = first?.Text ?? "请查看下方的待处理问题。";
            if (WorkflowHint != null) WorkflowHint.Text = "先处理下面提示的问题，完成后再点击“确认并导出 Excel”。";
            _showDialog($"还有 {ErrorList.Items.Count} 项需要处理，暂未导出。\n\n{explanation}\n\n处理后再点击“确认并导出 Excel”。",
                "先完成核对", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (dto.Status == "nothing_to_export")
            _showDialog("当前没有需要导出的明细。请确认是否把所有明细都设为不纳入。", "没有可导出的明细", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            _showDialog("暂时无法完成导出检查：" + (dto.Error ?? string.Join("\n", dto.Errors.Take(3))), "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    async void Export_Click(object sender, RoutedEventArgs e) => await ExportCurrentAsync(batch: false);

    string? _lastExportedPath, _lastExportWarning;

    // 导出当前任务；batch 为 true 时由批量导出调用：不弹出完成提示，也不逐个打开文件夹。
    async Task<bool> ExportCurrentAsync(bool batch)
    {
        if (_task == null || _busy) return false;
        if (_runCts != null) { Status("请等待识别结束后再校验或导出。"); return false; }
        if (_isDemo) { Status("演示模式不能校验或导出。"); return false; }
        var task = _task;
        var reviewer = ReviewerBox.Text.Trim();
        if (!Rules.Meaningful(reviewer)) { _showDialog("请先填写右上角的复核人姓名，再导出。", "请填写复核人", MessageBoxButton.OK, MessageBoxImage.Information); ReviewerBox.Focus(); return false; }
        var omittedFiles = task.Files.Where(f => f.State == "不纳入本次").Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var proposed = task.Items.Where(i => CanPreviewAdopt(i, omittedFiles)).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        string? previewPath = proposed.Count == 0 ? null : task.NewPreviewPath();
        try
        {
        var dto = await RunValidateCore(previewPath, proposed);
        if (dto == null) return false;
        if (!Regex.IsMatch(dto.ReviewSha256 ?? "", @"\A[0-9A-Fa-f]{64}\z") && (dto.Status is "ready" or "blocked"))
        {
            if (dto.Status == "blocked" && dto.Accepted.Count == 0)
                ShowExportProblem(task, dto, new HashSet<string>(StringComparer.Ordinal));
            else
                _showDialog("无法确认复核结果的版本，请重新打开任务后再试。", "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!TryAcceptedFiles(task, dto, out var acceptedFiles, proposed))
        {
            if (dto.Status == "ready" && dto.Accepted.Count > 0)
                _showDialog("无法确认明细所属图纸，请重新打开任务后再试。", "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            else ShowExportProblem(task, dto, acceptedFiles.Keys.ToHashSet(StringComparer.Ordinal));
            return false;
        }
        var acceptedIds = acceptedFiles.Keys.ToHashSet(StringComparer.Ordinal);
        if (!TryExcludedFiles(task, dto, out var excludedFiles))
        {
            _showDialog("无法确认不导出的明细范围，请重新打开任务后再试。", "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var unresolved = task.Files.FirstOrDefault(f => HasUnresolvedFileNote(f) ||
            (acceptedIds.Contains(f.Id) && LooksUnresolved(f.ParentChildNote)));
        if (unresolved != null)
        {
            var issue = new ErrorEntry($"图纸“{unresolved.Name}”已有待核内容，请在“更多图纸选项”中处理后再导出。", null, unresolved.Id);
            FocusIssue(issue);
            _showDialog(issue.Text, "先完成核对", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var excludedFileIds = task.Files.Where(f => f.State == "不纳入本次").Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var unresolvedItem = task.Items.FirstOrDefault(i => !excludedFileIds.Contains(i.FileId) &&
            i.Mode != Rules.Pending && LooksUnresolved(i.Note));
        if (unresolvedItem != null)
        {
            var issue = new ErrorEntry("这条明细的说明还有待确认内容，请核对并处理后再导出。", unresolvedItem.Id, null);
            FocusIssue(issue);
            _showDialog(issue.Text, "先完成核对", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var unknown = task.Files.FirstOrDefault(f => acceptedIds.Contains(f.Id) && !HasOnlyRoutineDiagnostics(f) &&
            (f.State != "已核查" || !Rules.Meaningful(f.Note)));
        if (unknown != null)
        {
            var issue = new ErrorEntry($"图纸“{unknown.Name}”还有需要具体处理的内容，请在“更多图纸选项”查看后再导出。", null, unknown.Id);
            FocusIssue(issue);
            _showDialog(issue.Text, "先完成核对", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var filesToConfirm = new List<FileRow>();
        if (dto.Status == "blocked")
        {
            foreach (string error in dto.Errors)
            {
                if (!TryFileConfirmationError(task, error, acceptedIds, out var file) || file == null)
                {
                    ShowExportProblem(task, dto, acceptedIds);
                    return false;
                }
                if (!filesToConfirm.Any(f => f.Id == file.Id)) filesToConfirm.Add(file);
            }
            if (filesToConfirm.Count == 0) { ShowExportProblem(task, dto, acceptedIds); return false; }
        }
        else if (dto.Status != "ready" || dto.Errors.Count != 0) { ShowExportProblem(task, dto, acceptedIds); return false; }
        if (dto.Pending != 0 || !HasCompleteDecisionCoverage(task, dto))
        {
            _showDialog("还有明细没有完成处理，请核对后再导出。", "先完成核对", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!ReferenceEquals(_task, task))
        {
            Status("任务已改变，本次未导出。");
            return false;
        }
        long version = _editVersion;
        string decisionSignature = DecisionSignature(dto);
        string scope;
        try
        {
            scope = BuildConfirmationScope(task, dto, acceptedFiles, excludedFiles);
            if (proposed.Count > 0)
                scope = $"本次包含所有图纸，不限于当前显示的明细。确认后，{proposed.Count} 条待复核明细将按图纸识别值采用。" +
                    Environment.NewLine + Environment.NewLine + scope;
            int duplicateSums = task.Items.Count(i => proposed.Contains(i.Id) && DuplicateQuantity.CanProposeSum(i));
            if (duplicateSums > 0)
                scope = $"确认后将批量采用 {duplicateSums} 条重复图号的默认累加数量；请逐条核对以下各处图框及计入范围。" +
                    Environment.NewLine + Environment.NewLine + scope;
        }
        catch (InvalidDataException ex)
        {
            _showDialog(ex.Message, "请分批导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        bool confirmed;
        try { confirmed = _confirmExport(dto.Accepted.Count, acceptedFiles.Count, dto.Excluded.Count, scope); }
        catch (Exception ex)
        {
            _showDialog("无法显示导出确认：" + ex.Message, "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!confirmed) { Status("已返回核对，没有导出。"); return false; }
        SafeExportDestination? selected;
        try { selected = DestinationSelector(SafeExportDestination.DefaultName(task, acceptedFiles), task); }
        catch (Exception ex)
        {
            Status("无法选择保存位置，未导出。");
            _showDialog("无法选择保存位置：" + ex.Message, "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (selected == null) { Status("已取消选择保存位置，没有导出。"); return false; }
        using var destination = selected;
        if (!ReferenceEquals(_task, task) || _editVersion != version)
        {
            _showDialog("明细在确认期间发生变化，请重新核对后再导出。", "请重新确认", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (filesToConfirm.Count != 0 || proposed.Count != 0)
        {
            ValidationDto rechecked;
            SetBusy(true);
            Status("正在准备导出，请稍候…");
            try { rechecked = previewPath == null ? await task.ValidateWithReviewLockAsync(dto.ReviewSha256!) :
                    await task.ValidatePreviewWithReviewLockAsync(previewPath, dto.ReviewSha256!); }
            catch (Exception ex)
            {
                _showDialog("图纸或明细在确认期间发生变化，请重新核对后再导出。\n" + ex.Message,
                    "请重新确认", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally { SetBusy(false); }
            string beforeErrors = string.Join("\n", dto.Errors.OrderBy(x => x, StringComparer.Ordinal));
            string afterErrors = string.Join("\n", rechecked.Errors.OrderBy(x => x, StringComparer.Ordinal));
            if (!ReferenceEquals(_task, task) || _editVersion != version || rechecked.Status != dto.Status ||
                rechecked.Pending != dto.Pending || !string.Equals(beforeErrors, afterErrors, StringComparison.Ordinal) ||
                !string.Equals(decisionSignature, DecisionSignature(rechecked), StringComparison.Ordinal) ||
                !string.Equals(dto.ReviewSha256, rechecked.ReviewSha256, StringComparison.OrdinalIgnoreCase))
            {
                _showDialog("图纸或明细在确认期间发生变化，请重新核对后再导出。", "请重新确认", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        bool recorded = false;
        foreach (var row in task.Items.Where(i => proposed.Contains(i.Id)))
        {
            if (!CanPreviewAdopt(row, omittedFiles))
            {
                _showDialog("明细在确认期间发生变化，请重新核对后再导出。", "请重新确认", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            bool duplicateDefault = DuplicateQuantity.CanProposeDefault(row);
            row.Mode = Rules.Adopt;
            if (duplicateDefault) row.Note = DuplicateQuantity.DefaultNote(row);
            recorded = true;
        }
        foreach (var file in filesToConfirm)
        {
            if (file.State != "已核查") { file.State = "已核查"; recorded = true; }
            var note = file.Note.Trim();
            if (!note.Contains(ExportConfirmationNote, StringComparison.Ordinal))
            {
                string confirmation = ConfirmationNoteFor(file);
                file.Note = note.Length == 0 ? confirmation : note + Environment.NewLine + confirmation;
                recorded = true;
            }
        }
        if (recorded)
        {
            dto = await RunValidate();
            if (dto == null) return false;
            if (dto.Status != "ready" || dto.Errors.Count != 0) { ShowExportProblem(task, dto, acceptedIds); return false; }
            if (!Regex.IsMatch(dto.ReviewSha256 ?? "", @"\A[0-9A-Fa-f]{64}\z"))
            {
                _showDialog("无法确认复核结果的版本，请重新打开任务后再试。", "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!TryAcceptedFiles(task, dto, out var confirmedFiles) || !TryExcludedFiles(task, dto, out _) ||
                !acceptedIds.SetEquals(confirmedFiles.Keys) || !string.Equals(decisionSignature, DecisionSignature(dto), StringComparison.Ordinal))
            {
                _showDialog("明细在确认后发生变化，请重新核对后再导出。", "请重新确认", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        SetBusy(true);
        Status("正在导出…");
        ValidationDto exported;
        string? folder = null, backup = null, receiptFailure = null;
        try
        {
            folder = ExportFolderFactory();
            exported = await task.ExportQuotationWithArchiveLockAsync(folder, reviewer, dto.ReviewSha256!);
            if (exported.Status != "ready" || !File.Exists(Path.Combine(folder, "确认明细.xlsx")))
                throw new IOException("引擎没有完成导出：" + (exported.Error ?? string.Join("\n", exported.Errors.Take(5))));
            if (!string.Equals(exported.ReviewSha256, dto.ReviewSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("导出结果的复核版本不一致，未复制。");
            string trustedHash = exported.VerifiedFormattedSha256 ?? throw new InvalidDataException("缺少格式化工作簿的独立校验哈希，未复制。");
            string trustedAssociationHash = exported.VerifiedAssociationSha256 ?? throw new InvalidDataException("缺少关联记录的独立校验哈希，未复制。");
            backup = await Task.Run(() => destination.Publish(folder, task.ManifestSha256, dto.ReviewSha256!, task.Data.TaskId, trustedHash, trustedAssociationHash));
            try { await Task.Run(() => RecordExportSelection(destination, folder, trustedHash, backup)); }
            catch (Exception ex) { receiptFailure = ex.Message; }
        }
        catch (Exception ex)
        {
            Status("导出已停止：" + ex.Message);
            _showDialog("未能完成所选位置的导出，当前窗口决定仍保留。\n" + ex.Message +
                (folder != null && Directory.Exists(folder) ? "\n\n引擎输出保留在：" + folder : ""),
                "导出已停止", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { SetBusy(false); }
        Status(receiptFailure == null ? "已导出：" + destination.Path : "文件已保存，但内部保存记录写入失败。");
        string resultMessage = $"已导出 {exported.Accepted.Count} 条确认明细（排除 {exported.Excluded.Count} 条）。\n\n{destination.Path}" +
            (backup == null ? "" : "\n\n替换前文件备份：" + backup);
        _lastExportedPath = destination.Path;
        _lastExportWarning = receiptFailure;
        if (batch) return true;
        string? openFailure = null;
        try { OpenExportSelection(destination.Path); }
        catch (Exception ex)
        {
            openFailure = ex.Message;
            if (receiptFailure == null) Status("已保存，但无法自动打开所在文件夹。");
        }
        if (receiptFailure != null || openFailure != null)
        {
            if (receiptFailure != null) resultMessage += "\n\n文件已保存，但内部保存记录写入失败：" + receiptFailure;
            if (openFailure != null) resultMessage += "\n\n无法自动打开所在文件夹：" + openFailure;
            _showDialog(resultMessage, "文件已保存", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else _showDialog(resultMessage, "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
        }
        finally { if (previewPath != null) task.DeleteOwnPreview(previewPath); }
    }

    // ───────────────────────── 校验问题跳转 ─────────────────────────
    void ErrorList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ErrorList.SelectedItem is not ErrorEntry entry || _task == null) return;
        FocusIssue(entry);
    }

    void FocusIssue(ErrorEntry entry)
    {
        if (_task == null) return;
        if (entry.ItemId != null)
        {
            var row = _task.Items.FirstOrDefault(i => i.Id == entry.ItemId);
            if (row == null) return;
            if (_view != null && !_view.Cast<object>().Contains(row))
            {
                FileList.SelectedItem = null;
                FilterBox.SelectedIndex = 0;
                SearchBox.Clear();
                _view.Refresh();
            }
            ItemsGrid.SelectedItem = row;
            ItemsGrid.ScrollIntoView(row);
            if (entry.Text.Contains("说明", StringComparison.Ordinal))
                ShowNoteSection();
            string? header = entry.Text.Contains("数量", StringComparison.Ordinal) ? "数量" : null;
            var column = header == null ? null : ItemsGrid.Columns.FirstOrDefault(c => Equals(c.Header, header));
            if (column != null) ItemsGrid.ScrollIntoView(row, column);
        }
        else if (entry.FileId != null)
        {
            FileList.SelectedItem = _task.Files.FirstOrDefault(f => f.Id == entry.FileId);
            FileList.ScrollIntoView(FileList.SelectedItem);
            if (entry.Text.Contains("更多图纸选项") || entry.Text.Contains("计价范围") || entry.Text.Contains("不纳入时"))
            {
                AdvancedFileOptions.IsExpanded = true;
                AdvancedFileOptions.BringIntoView();
            }
        }
    }

    // ───────────────────────── 演示/截图（开发验收用，仅改内存状态） ─────────────────────────
    public void ApplyDemo()
    {
        if (!_isDemo) throw new InvalidOperationException("只能在演示模式中应用演示数据。");
        if (_task == null) return;
        var rows = _task.Items;
        var complete = rows.Where(r => r.IsCompleteOriginal).Take(3).ToList();
        foreach (var r in complete.Take(2)) { r.Note = "已对照图纸核对"; r.Mode = Rules.Adopt; }
        var incomplete = rows.FirstOrDefault(r => !r.IsCompleteOriginal);
        if (incomplete != null)
        {
            incomplete.ConfTreatment = "无";
            incomplete.ConfUnit = "EA";
            incomplete.Note = "缺项由人工补全";
        }
        if (complete.Count > 2) { complete[2].Note = "与本次报价无关"; complete[2].Mode = Rules.Exclude; }
        var withErr = rows.Skip(8).FirstOrDefault();
        if (withErr != null)
        {
            var entries = _task.MapErrors([$"{withErr.Id}: 尚未复核", $"{_task.Files[0].Id}: 文件仍被标记为待核查"]);
            ShowErrors(entries);
        }
        FileList.SelectedIndex = Math.Min(1, _task.Files.Count - 1);
        _view?.Refresh();
        _task.RefreshFileSummaries();
        UpdateStats();
        var pick = _view?.Cast<ItemRow>().ElementAtOrDefault(3);
        if (pick != null) ItemsGrid.SelectedItem = pick;
        BottomTabs.SelectedIndex = 0;
    }

    // ───────────────────────── 截图（验收用） ─────────────────────────
    public async Task RenderToPngAsync(string png)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(600);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        int w = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        var bmp = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(png))!);
        using var fs = File.Create(png);
        encoder.Save(fs);
    }
}

/// <summary>列表序号：把从 0 开始的 AlternationIndex 显示为 1、2、3…</summary>
public sealed class PlusOneConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is int index ? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
