using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CadReviewUI;

public sealed record RunOutcome(int ExitCode, string StdOut, string StdErr);

public sealed record RecognizeOutcome(bool Ok, string ReviewFolder, string Message);

/// <summary>Accuracy：直接调用 work\accuracy-20261109 的校验程序（旧任务）。
/// Segmented：任务自带冻结的校验程序，必须经 work\segmented-20261109\review_guard.py 调用（分段版任务）。</summary>
public enum EngineKind { Accuracy, Segmented }

/// <summary>调用现有的识别/校验/导出引擎。界面本身不做识别，也不改引擎文件。</summary>
public static class Engine
{
    public static readonly string Root = WorkspaceConfiguration.ResolveRoot();
    public static string OutputsRoot => Path.Combine(Root, "outputs");
    // 自测时改到临时目录，避免污染真实复核存档
    public static string SessionsRoot { get; set; } = Path.Combine(OutputsRoot, "ui-sessions");
    public static string InputsRoot => Path.Combine(Root, "ui-inputs");

    // 与引擎默认限制一致，提前在界面里拦截，避免整批被拒绝
    public const int MaxFiles = 20;
    public const double MaxFileMiB = 64, MaxBatchMiB = 256;

    static string SegmentedDir => Path.Combine(Root, @"work\segmented-20261109");
    static string AccuracyDir => Path.Combine(Root, @"work\accuracy-20261109");
    public static string SegmentedScript => Path.Combine(SegmentedDir, "RunAccuracy.ps1");
    public static string SegmentedGuard => Path.Combine(SegmentedDir, "review_guard.py");
    public static string AccuracyScript => Path.Combine(AccuracyDir, "RunAccuracy.ps1");
    public static string AccuracyReviewExe => Path.Combine(AccuracyDir, @"review-native\CadReviewWorkflow.exe");

    static Engine() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string? RecognizeScript =>
        File.Exists(SegmentedScript) ? SegmentedScript : File.Exists(AccuracyScript) ? AccuracyScript : null;

    public static string? FindShell() => WorkspaceConfiguration.FindExecutable(
        "CADTEST_POWERSHELL", null, "pwsh.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe"));

    public static string? FindPython() => WorkspaceConfiguration.FindExecutable(
        "CADTEST_PYTHON", Path.Combine(Root, @"runtime\python\python.exe"), "python.exe", "python3.exe");

    internal static (string Python, string Node)? FindParallelRuntime()
    {
        var python = FindPython();
        var node = WorkspaceConfiguration.FindExecutable("CADTEST_NODE",
            Path.Combine(Root, @"runtime\node\node.exe"), "node.exe");
        return python == null || node == null ? null : (python, node);
    }

    internal static void ConfigureChild(ProcessStartInfo start)
    {
        start.Environment["CADTEST_ROOT"] = Root;
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
    }

    /// <summary>分段任务证据和保护组件完整性分别判断；不完整时禁止退回旧引擎。</summary>
    public static EngineKind KindFor(string reviewFolder) => KindForCore(reviewFolder, SegmentedGuard);

    static EngineKind KindForCore(string reviewFolder, string guard)
    {
        var root = Directory.GetParent(Path.GetFullPath(reviewFolder))?.FullName ?? throw new InvalidDataException("无法确定任务目录。");
        var recognition = Path.Combine(root, "recognition");
        var frozen = Path.Combine(recognition, "frozen-source");
        var protocol = Path.Combine(recognition, "protocol.json");
        var frozenManifest = Path.Combine(frozen, "manifest.json");
        if (Directory.Exists(protocol) || Directory.Exists(frozenManifest))
            throw new InvalidDataException("任务协议或冻结清单不是文件，无法安全选择校验引擎。");
        bool HasPath(string path) => File.Exists(path) || Directory.Exists(path);
        bool segmented = new[] { "review-native", "review-source", "projector", "projector-source" }
            .Any(name => HasPath(Path.Combine(frozen, name))) ||
            HasPath(Path.Combine(frozen, @"work\segmented-20261109"));
        // A copied review directory can still point at the original task's frozen
        // sources. Never classify it as legacy just because its sibling
        // recognition directory was not copied along with it.
        var sourceManifest = Path.Combine(Path.GetFullPath(reviewFolder), "source.json");
        if (File.Exists(sourceManifest))
        {
            try
            {
                using var stream = File.OpenRead(sourceManifest);
                using var source = JsonDocument.Parse(stream);
                if (source.RootElement.ValueKind != JsonValueKind.Object ||
                    !source.RootElement.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
                    throw new JsonException("来源清单没有 sources 数组");
                var localFrozen = Path.GetFullPath(frozen).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                foreach (var item in sources.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String)
                        throw new JsonException("来源路径无效");
                    var raw = path.GetString() ?? throw new JsonException("来源路径为空");
                    var normalized = raw.Replace('/', '\\');
                    bool frozenSegment = new[] { @"\frozen-source\review-native\", @"\frozen-source\review-source\",
                        @"\frozen-source\projector\", @"\frozen-source\projector-source\", @"\frozen-source\work\segmented-20261109\" }
                        .Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase));
                    if (!frozenSegment) continue;
                    segmented = true;
                    if (!Path.GetFullPath(raw).StartsWith(localFrozen, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("来源清单指向其他分段任务的冻结文件，请打开原任务目录，不能降级到旧引擎。");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException("任务来源清单无法读取或损坏，无法安全选择校验引擎：" + sourceManifest, ex);
            }
        }
        if (File.Exists(protocol))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(protocol));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("protocol 不是对象");
                var obj = doc.RootElement;
                segmented |= obj.TryGetProperty("frozen_manifest_sha256", out _) ||
                             obj.TryGetProperty("extractor_projection_contract_sha256", out _);
                if (obj.TryGetProperty("code_sha256", out var code) && code.ValueKind == JsonValueKind.Object)
                    segmented |= code.EnumerateObject().Any(p => p.Name.Replace('/', '\\').Contains(@"\work\segmented-20261109\", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new InvalidDataException("任务协议无法读取或损坏，无法安全选择校验引擎：" + protocol, ex);
            }
        }
        if (File.Exists(frozenManifest))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(frozenManifest));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("frozen manifest 不是对象");
                segmented |= doc.RootElement.EnumerateObject().Any(p =>
                {
                    var name = p.Name.Replace('/', '\\');
                    return new[] { @"review-native\", @"review-source\", @"projector\", @"projector-source\", @"work\segmented-20261109\" }
                        .Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new InvalidDataException("冻结清单无法读取或损坏，无法安全选择校验引擎：" + frozenManifest, ex);
            }
        }
        if (!segmented) return EngineKind.Accuracy;
        var exe = Path.Combine(frozen, @"review-native\CadReviewWorkflow.exe");
        if (!File.Exists(guard)) throw new FileNotFoundException("分段任务的复核守护程序缺失，不能使用旧引擎：" + guard);
        if (!File.Exists(protocol)) throw new FileNotFoundException("分段任务的协议缺失，不能使用旧引擎：" + protocol);
        if (!File.Exists(exe)) throw new FileNotFoundException("分段任务的冻结校验程序缺失，不能使用旧引擎：" + exe);
        if (!File.Exists(frozenManifest)) throw new FileNotFoundException("分段任务的冻结清单缺失，不能使用旧引擎：" + frozenManifest);
        return EngineKind.Segmented;
    }

    public static string? CheckReady()
    {
        if (!Directory.Exists(Root)) return "找不到工作区 " + Root;
        if (!File.Exists(AccuracyReviewExe) && !File.Exists(SegmentedGuard)) return "找不到校验/导出程序：" + AccuracyReviewExe;
        return null;
    }

    // ───────────── 进程运行 ─────────────
    /// <summary>运行并实时回传每一行输出。不同引擎输出编码不一（有的 UTF-8，有的系统代码页），
    /// 所以按字节读，每行单独先按 UTF-8 严格解码，失败再回退系统代码页。</summary>
    public static async Task<RunOutcome> RunAsync(string file, IEnumerable<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = Root,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return await RunLinesAsync(psi, onLine, ct);
    }

    static async Task<RunOutcome> RunLinesAsync(ProcessStartInfo psi, Action<string>? onLine, CancellationToken ct)
    {
        psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.CreateNoWindow = true; psi.WorkingDirectory = Root;
        ConfigureChild(psi);
        using var process = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        process.Start();
        using var reg = ct.Register(() => { try { process.Kill(true); } catch { /* 已退出 */ } });
        var pumpOut = PumpLinesAsync(process.StandardOutput.BaseStream, so, onLine);
        var pumpErr = PumpLinesAsync(process.StandardError.BaseStream, se, onLine);
        await Task.WhenAll(pumpOut, pumpErr);
        await process.WaitForExitAsync();
        return new RunOutcome(process.ExitCode, so.ToString(), se.ToString());
    }

    static async Task PumpLinesAsync(Stream stream, StringBuilder sink, Action<string>? onLine)
    {
        using var line = new MemoryStream();
        var buffer = new byte[4096];
        void Emit()
        {
            var text = Decode(line.ToArray()).TrimEnd('\r');
            line.SetLength(0);
            lock (sink) sink.AppendLine(text);
            onLine?.Invoke(text);
        }
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
            for (int i = 0; i < n; i++)
            {
                if (buffer[i] == (byte)'\n') Emit();
                else line.WriteByte(buffer[i]);
            }
        if (line.Length > 0) Emit();
    }

    // 引擎输出编码取决于启动环境：有 UTF-8 控制台时是 UTF-8，没有控制台（界面程序）时是系统代码页（GBK）。
    // 所以按字节读取，先严格按 UTF-8 解码，失败再回退到系统代码页。
    static async Task<RunOutcome> RunCaptureAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.CreateNoWindow = true; psi.WorkingDirectory = Root;
        ConfigureChild(psi);
        using var process = new Process { StartInfo = psi };
        process.Start();
        using var reg = ct.Register(() => { try { process.Kill(true); } catch { /* 已退出 */ } });
        var stdout = ReadAllAsync(process.StandardOutput.BaseStream);
        var stderr = ReadAllAsync(process.StandardError.BaseStream);
        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync();
        return new RunOutcome(process.ExitCode, Decode(await stdout), Decode(await stderr));
    }

    static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    public static string Decode(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage).GetString(bytes); }
        return text.TrimStart('\uFEFF');
    }

    /// <summary>输出里的 JSON 段：守护脚本会在 JSON 后再追加一行“复核操作记录：…”，所以取第一个 { 到最后一个 }。</summary>
    public static string? ExtractJson(string text)
    {
        int a = text.IndexOf('{'), b = text.LastIndexOf('}');
        return a >= 0 && b > a ? text[a..(b + 1)] : null;
    }

    static ValidationDto Parse(RunOutcome run)
    {
        var results = new List<ValidationDto>();
        foreach (var text in new[] { run.StdOut, run.StdErr })
        {
            var json = ExtractJson(text);
            if (json == null) continue;
            try
            {
                var dto = JsonSerializer.Deserialize<ValidationDto>(json, Json.Options);
                if (dto != null && !string.IsNullOrWhiteSpace(dto.Status)) results.Add(dto);
            }
            catch (JsonException) { /* 尝试下一个 */ }
        }
        var detail = (run.StdErr.Trim() + "\n" + run.StdOut.Trim()).Trim();
        if (detail.Contains("workspace_busy"))
            return new ValidationDto { Status = "failed", Error = "工作区正被另一个识别/复核任务占用（runner.lock），本次没有执行。请等对方任务结束后重试。" };

        // 守护程序可在子进程已打印 ready 后因哈希/资源保护失败而非零退出。
        // 任何明确的失败优先；只有约定的退出码与状态组合才能继续复核/导出。
        var reportedFailure = results.FirstOrDefault(d => d.Status == "failed");
        if (reportedFailure == null)
        {
            var dto = results.FirstOrDefault();
            if (dto != null && ((run.ExitCode == 0 && dto.Status is "ready" or "nothing_to_export") ||
                                (run.ExitCode == 2 && dto.Status == "blocked"))) return dto;
        }
        var reason = reportedFailure?.Error;
        if (string.IsNullOrWhiteSpace(reason)) reason = detail.Length > 500 ? detail[^500..] : detail;
        return new ValidationDto { Status = "failed", Error = $"引擎执行失败或结果异常（退出码 {run.ExitCode}）：{reason}" };
    }

    // ───────────── 校验 / 导出 ─────────────
    static ProcessStartInfo ReviewCommand(EngineKind kind, string mode, string manifest, string review, string? output, string? reviewer)
    {
        var extra = new List<string>();
        if (output != null) { extra.Add("--output"); extra.Add(output); extra.Add("--reviewer"); extra.Add(reviewer ?? ""); }

        if (kind == EngineKind.Accuracy)
        {
            var psi = new ProcessStartInfo(AccuracyReviewExe);
            foreach (var a in new[] { mode, "--manifest", manifest, "--review", review }.Concat(extra)) psi.ArgumentList.Add(a);
            return psi;
        }

        // 直接启动守护程序，避免 cmd 展开复核人或路径中的 %、&、引号等字符。
        // 分段版冻结的 .NET 程序明确设置 UTF-8；Python 也显式使用 UTF-8。
        if (!File.Exists(SegmentedGuard)) throw new FileNotFoundException("分段任务的复核守护程序缺失：" + SegmentedGuard);
        var python = FindPython() ?? throw new FileNotFoundException("找不到 Python。请安装 Python 3.11+ 或设置 CADTEST_PYTHON。");
        var guarded = new ProcessStartInfo(python) { WorkingDirectory = SegmentedDir };
        guarded.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in new[] { "-B", "-X", "utf8", SegmentedGuard, mode, "--manifest", manifest, "--review", review }.Concat(extra))
            guarded.ArgumentList.Add(a);
        return guarded;
    }

    static void EnsureReviewKind(EngineKind kind, string manifest)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(manifest)) ?? throw new InvalidDataException("无法确定复核任务目录。");
        if (KindFor(folder) != kind) throw new InvalidOperationException("任务的引擎类型已改变，请重新打开任务；本次未执行校验或导出。");
    }

    public static async Task<ValidationDto> ValidateAsync(EngineKind kind, string manifest, string review, CancellationToken ct = default)
    {
        try { EnsureReviewKind(kind, manifest); return Parse(await RunCaptureAsync(ReviewCommand(kind, "validate", manifest, review, null, null), ct)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { return new ValidationDto { Status = "failed", Error = ex.Message }; }
    }

    public static async Task<ValidationDto> ExportAsync(EngineKind kind, string manifest, string review, string output, string reviewer, CancellationToken ct = default)
    {
        try { EnsureReviewKind(kind, manifest); return Parse(await RunCaptureAsync(ReviewCommand(kind, "export", manifest, review, output, reviewer), ct)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { return new ValidationDto { Status = "failed", Error = ex.Message }; }
    }

    // ───────────── 识别 ─────────────
    /// <summary>检查图纸是否满足引擎的入口限制（工作区内、数量与大小）。返回问题列表，空表示通过。</summary>
    public static List<string> CheckDrawings(IReadOnlyList<string> drawings)
    {
        var problems = new List<string>();
        if (drawings.Count == 0) problems.Add("没有选择图纸");
        if (drawings.Count > MaxFiles) problems.Add($"一次最多 {MaxFiles} 份图纸，当前 {drawings.Count} 份");
        double total = 0;
        foreach (var d in drawings)
        {
            if (!File.Exists(d)) { problems.Add("文件不存在：" + d); continue; }
            if (!d.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) problems.Add("不是 DWG 文件：" + Path.GetFileName(d));
            var mib = new FileInfo(d).Length / 1048576.0;
            total += mib;
            if (mib > MaxFileMiB) problems.Add($"{Path.GetFileName(d)} 为 {mib:F1} MiB，超过单文件 {MaxFileMiB} MiB 限制");
        }
        if (total > MaxBatchMiB) problems.Add($"图纸合计 {total:F1} MiB，超过整批 {MaxBatchMiB} MiB 限制");
        return problems;
    }

    /// <summary>把工作区外的图纸复制到 ui-inputs（引擎只接受工作区内路径），原文件不动。</summary>
    public static List<string> StageDrawings(IEnumerable<string> drawings, string stamp)
    {
        var staged = new List<string>();
        var dir = Path.Combine(InputsRoot, stamp);
        foreach (var d in drawings)
        {
            var full = Path.GetFullPath(d);
            if (full.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase)) { staged.Add(full); continue; }
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, Path.GetFileName(full));
            if (File.Exists(target)) throw new IOException("同名图纸重复：" + Path.GetFileName(full));
            File.Copy(full, target, false);
            staged.Add(target);
        }
        return staged;
    }

    // -File 不能向 PowerShell 的 string[] 参数传递多个独立值，会静默丢掉后续图纸。
    // 固定命令读取仅含路径的 JSON 环境变量，再显式绑定各参数；用户输入不作为脚本拼接。
    static ProcessStartInfo RecognitionCommand(string shell, string script, IReadOnlyList<string> drawings,
        string output, string python, string node)
    {
        const string command = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " +
            "$OutputEncoding=[Console]::OutputEncoding; try { " +
            "$c=ConvertFrom-Json $env:CADTEST_RECOGNIZE_REQUEST; " +
            "& $c.script -Mode Recognize -Drawing @($c.drawings) -Output $c.output -Python $c.python -Node $c.node; " +
            "if (-not $?) { exit 1 }; if ($null -eq $LASTEXITCODE) { exit 0 }; exit $LASTEXITCODE " +
            "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var psi = new ProcessStartInfo(shell);
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            psi.ArgumentList.Add(a);
        psi.Environment["CADTEST_RECOGNIZE_REQUEST"] = JsonSerializer.Serialize(new { script, drawings, output, python, node });
        return psi;
    }

    public static async Task<RecognizeOutcome> RecognizeAsync(IReadOnlyList<string> drawings, string outputDir,
        Action<string> log, CancellationToken ct)
    {
        var shell = FindShell();
        if (shell == null) return new(false, "", "找不到 PowerShell 7。请安装 PowerShell 7 或设置 CADTEST_POWERSHELL。");
        var script = RecognizeScript;
        if (script == null) return new(false, "", "找不到识别入口脚本：" + SegmentedScript);
        var runtime = FindParallelRuntime();
        if (runtime == null) return new(false, "", "找不到 Python 或 Node.js。请安装运行时并加入 PATH，或设置 CADTEST_PYTHON / CADTEST_NODE。");
        var python = runtime.Value.Python;
        var node = runtime.Value.Node;
        var run = await RunLinesAsync(RecognitionCommand(shell, script, drawings, outputDir, python, node), log, ct);
        var review = Path.Combine(outputDir, "review");
        if (run.ExitCode == 0 && File.Exists(Path.Combine(review, "source.json")) && File.Exists(Path.Combine(review, "图纸复核.xlsx")))
            return new(true, review, "识别完成");
        return new(false, review, Diagnose(outputDir, run));
    }

    static string Diagnose(string outputDir, RunOutcome run)
    {
        var sb = new StringBuilder();
        foreach (var name in new[] { "admission.json", "batch-status.json", "review-status.json" })
        {
            var p = Path.Combine(outputDir, "recognition", name);
            if (File.Exists(p)) sb.AppendLine(File.ReadAllText(p));
        }
        var joined = sb + run.StdOut + run.StdErr;
        if (joined.Contains("workspace_busy"))
            return "工作区正被另一个识别任务占用（runner.lock），本次没有开始识别。请等对方任务结束后重试。";
        var tail = (run.StdErr.Trim() + "\n" + run.StdOut.Trim()).Trim();
        return "识别没有完成（退出码 " + run.ExitCode + "）。" + (tail.Length > 600 ? tail[^600..] : tail);
    }
}
