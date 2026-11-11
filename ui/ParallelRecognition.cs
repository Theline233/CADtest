using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CadReviewUI;

internal sealed record ParallelTask(string Id, string Drawing, string Output);
internal sealed record ParallelTaskState(ParallelTask Task, string Status, string? Reason);
internal sealed record ParallelSnapshot(string Status, string? Reason, IReadOnlyList<ParallelTaskState> Tasks);
internal sealed record ParallelBatch(string Output, string Manifest, IReadOnlyList<ParallelTask> Tasks);
internal sealed record ParallelResult(int ExitCode, bool WorkspaceBusy, string? Error, ParallelSnapshot? Snapshot);
internal sealed record ParallelCapability(bool Supported, string? Error, string? Python, string? Node);
internal sealed record ParallelLimits(int MemoryMiB, int TimeoutSeconds, int CpuPercent);

// The coordinator owns its output directory, task directories and all child Jobs.
// This client only writes a unique manifest and, after the batch directory exists,
// a create-new cancellation marker. It never kills a running coordinator.
internal static class ParallelRecognition
{
    static string Runner => Path.Combine(Engine.Root, @"work\segmented-20261109\runner.py");

    internal static async Task<ParallelCapability> ProbeAsync(CancellationToken ct)
    {
        if (!File.Exists(Runner)) return new(false, null, null, null);
        var pair = Engine.FindParallelRuntime();
        if (pair == null) return new(false, "找不到同时包含 Python 和 Node 的识别运行时。", null, null);
        try
        {
            var help = await Engine.RunAsync(pair.Value.Python, new[] { "-B", "-X", "utf8", Runner, "--help" }, null, ct);
            ct.ThrowIfCancellationRequested();
            if (help.ExitCode != 0) return new(false, "无法检查并行识别接口：" + help.StdErr.Trim(), null, null);
            string text = help.StdOut + "\n" + help.StdErr;
            if (!text.Contains("--parallel-batch", StringComparison.Ordinal)) return new(false, null, null, null);
            if (!text.Contains("--max-parallel", StringComparison.Ordinal) || !text.Contains("--cancel-file", StringComparison.Ordinal))
                return new(false, "识别器的并行接口不完整：需要 --parallel-batch、--max-parallel 和 --cancel-file。", null, null);
            return new(true, null, pair.Value.Python, pair.Value.Node);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(false, "检查并行识别接口失败：" + ex.Message, null, null); }
    }

    // The coordinator owns admission and uses the same budget in both builds.
    // The portable overlay remains responsible for legacy single-task/review work.
    internal static ParallelLimits Limits() => Limits(Environment.ProcessorCount, Environment.GetEnvironmentVariable("CADREVIEW_CPU_PERCENT"));
    internal static ParallelLimits Limits(int logicalCores) => Limits(logicalCores, null);
    internal static ParallelLimits Limits(int logicalCores, string? manualCpu)
    {
        int cpu = int.TryParse(manualCpu, NumberStyles.None, CultureInfo.InvariantCulture, out int requested) && requested is >= 1 and <= 100
            ? requested : logicalCores <= 2 ? 100 : 75;
        return new ParallelLimits(3072, 300, cpu);
    }

    internal static ParallelBatch Prepare(IReadOnlyList<string> drawings, string outputsRoot)
    {
        if (drawings.Count is < 1 or > 20) throw new ArgumentException("批次图纸数量无效。");
        string root = Path.GetFullPath(outputsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("找不到 outputs 目录：" + root);
        string key = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        string output = Path.Combine(root, "ui-batch-" + key);
        string manifest = Path.Combine(root, "ui-batch-" + key + "-manifest.json");
        var tasks = drawings.Select((drawing, index) => new ParallelTask($"D{index + 1:000}", Path.GetFullPath(drawing),
            Path.Combine(root, "ui-" + key + $"-D{index + 1:000}"))).ToArray();
        if (Directory.Exists(output) || File.Exists(output) || tasks.Any(t => Directory.Exists(t.Output) || File.Exists(t.Output)))
            throw new IOException("批次输出路径已存在，请重新尝试。");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tasks = tasks.Select(t => new { id = t.Id, drawing = t.Drawing, output = t.Output }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true });
        using (var stream = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        {
            stream.Write(json);
            stream.Flush(true);
        }
        return new ParallelBatch(output, manifest, tasks);
    }

    internal static ParallelSnapshot ReadSnapshot(string path, IReadOnlyList<ParallelTask> expected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("批次状态文件过大。");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        static string Required(JsonElement element, string key) =>
            element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()! : throw new InvalidDataException("批次状态缺少 " + key);
        static string? Optional(JsonElement element, string key) =>
            element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tasks", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("批次状态缺少 tasks 数组。");
        var byId = expected.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ParallelTaskState>();
        foreach (var item in entries.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("批次任务状态无效。");
            string id = Required(item, "id"), status = Required(item, "status");
            if (!seen.Add(id) || !byId.TryGetValue(id, out var task)) throw new InvalidDataException("批次任务 ID 不匹配。");
            if (status is not ("queued" or "running" or "passed" or "failed" or "cancelled"))
                throw new InvalidDataException("未知批次任务状态：" + status);
            string actualOutput = Path.GetFullPath(Required(item, "output"));
            if (!string.Equals(Path.GetFullPath(Required(item, "drawing")), task.Drawing, StringComparison.OrdinalIgnoreCase) ||
                !(string.Equals(actualOutput, task.Output, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(actualOutput, task.Output + "-memory-retry-01", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("批次任务路径与清单不一致：" + id);
            result.Add(new ParallelTaskState(task with { Output = actualOutput }, status, Optional(item, "blocked_reason")));
        }
        return new ParallelSnapshot(Required(root, "status"), Optional(root, "blocked_reason"), result);
    }

    internal static async Task<ParallelResult> RunAsync(ParallelBatch batch, string python, string node,
        int maxParallel, ParallelLimits limits, Action<string> log, Action<ParallelSnapshot> progress, CancellationToken ct,
        string? runnerOverride = null)
    {
        if (maxParallel is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(maxParallel));
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(batch.Output) || File.Exists(batch.Output) || batch.Tasks.Any(t => Directory.Exists(t.Output) || File.Exists(t.Output)))
            throw new IOException("批次或任务输出目录已存在，未启动识别。");
        string cancel = Path.Combine(batch.Output, "cancel.request");
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = Engine.Root,
        };
        Engine.ConfigureChild(start);
        foreach (string arg in new[] { "-B", "-X", "utf8", runnerOverride ?? Runner,
            "--output", batch.Output, "--parallel-batch", batch.Manifest, "--node", node,
            "--memory-mib", limits.MemoryMiB.ToString(CultureInfo.InvariantCulture),
            "--timeout", limits.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "--cpu-percent", limits.CpuPercent.ToString(CultureInfo.InvariantCulture),
            "--max-parallel", maxParallel.ToString(CultureInfo.InvariantCulture), "--cancel-file", cancel })
            start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        var tail = new StringBuilder();
        void Log(string text)
        {
            lock (tail) { tail.AppendLine(text); if (tail.Length > 12000) tail.Remove(0, tail.Length - 12000); }
            try { log(text); } catch { /* Logging must not abandon the child process. */ }
        }
        async Task Pump(StreamReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null) Log(line);
        }
        process.Start();
        var stdout = Pump(process.StandardOutput);
        var stderr = Pump(process.StandardError);
        var exit = process.WaitForExitAsync();
        ParallelSnapshot? latest = null;
        string? readError = null;
        bool cancelWritten = false, cancelNotice = false, cancelErrorShown = false, sawActive = false;
        void Poll()
        {
            try
            {
                var snapshot = ReadSnapshot(Path.Combine(batch.Output, "batch-status.json"), batch.Tasks);
                latest = snapshot; readError = null;
                if (snapshot.Tasks.Any(t => t.Status is "running" or "passed" or "failed")) sawActive = true;
                try { progress(snapshot); } catch (Exception ex) { Log("更新批次界面失败：" + ex.Message); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
            { readError = ex.Message; }
            if (!ct.IsCancellationRequested || cancelWritten || exit.IsCompleted) return;
            if (!cancelNotice) { Log("已请求取消，正在等待协调器停止各任务并保存状态。"); cancelNotice = true; }
            try
            {
                if (!Directory.Exists(batch.Output)) return; // The coordinator must claim the directory first.
                if ((File.GetAttributes(batch.Output) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("批次目录是重解析点，不能写入取消标记。");
                if (!File.Exists(cancel))
                {
                    using var marker = new FileStream(cancel, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    marker.Flush(true);
                }
                cancelWritten = true;
                Log("取消请求已送达，等待协调器收尾。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!cancelErrorShown) { Log("暂时无法写入取消请求，将继续等待并重试：" + ex.Message); cancelErrorShown = true; }
            }
        }
        while (!exit.IsCompleted)
        {
            Poll();
            await Task.WhenAny(exit, Task.Delay(300));
        }
        await exit;
        await Task.WhenAll(stdout, stderr);
        Poll();
        string captured;
        lock (tail) captured = tail.ToString();
        bool busy = !sawActive && !batch.Tasks.Any(t => Directory.Exists(t.Output) || File.Exists(t.Output) ||
            Directory.Exists(t.Output + "-memory-retry-01") || File.Exists(t.Output + "-memory-retry-01")) &&
            (captured.Contains("workspace_busy", StringComparison.OrdinalIgnoreCase) ||
             (latest?.Reason?.Contains("workspace_busy", StringComparison.OrdinalIgnoreCase) ?? false));
        string? error = null;
        if (!busy && (latest == null || readError != null)) error = "无法读取最终批次状态：" + (readError ?? "状态文件不存在");
        else if (!busy && latest!.Status == "blocked") error = "协调器未能启动批次：" + (latest.Reason ?? "原因未写入状态文件");
        else if (!busy && (latest!.Tasks.Count != batch.Tasks.Count || latest.Tasks.Any(t => t.Status is "queued" or "running")))
            error = "协调器已退出，但部分任务没有最终状态。";
        else if (!busy && process.ExitCode != 0 && latest!.Tasks.All(t => t.Status == "passed"))
            error = "协调器退出码为 " + process.ExitCode + "，但状态显示全部完成；请核对批次记录。";
        return new ParallelResult(process.ExitCode, busy, error, latest);
    }
}
