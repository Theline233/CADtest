using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CadReviewUI;

/// <summary>沿用分发版的单图识别和复核资源上限；多图并行仍由 Python 协调器统一准入。
/// 上限不是预分配内存，也不保证不影响同机其他程序。规则与 r12 便携版一致。</summary>
public sealed record ResourcePlan(int LogicalCores, long TotalMiB, long AvailableMiB, long CommitAvailableMiB,
    int MemoryMiB, int CpuPercent, int TimeoutSeconds)
{
    /// <summary>仅当设置了高级覆盖环境变量时为 true（默认完全自动，使用者不需要设置任何东西）。</summary>
    public bool Overridden { get; init; }

    /// <summary>低于此值时，特别复杂的大图可能因内存上限而失败（历史最坏 2055 MiB 再留余量）。</summary>
    public const int WarnBelowMiB = 2560;

    public string Describe() => string.Format(CultureInfo.InvariantCulture,
        "本机 {0} 个逻辑核，内存 {1:F1} GiB（当前可用 {2:F1} GiB）→ 本次{6}使用：内存上限 {3:F1} GiB，CPU 上限 {4}%，单阶段超时 {5} 秒",
        LogicalCores, TotalMiB / 1024.0, AvailableMiB / 1024.0, MemoryMiB / 1024.0, CpuPercent, TimeoutSeconds,
        Overridden ? "按环境变量覆盖后" : "自动");

    // 只在自动计算的结果偏低时提示；手动覆盖时上限和“可用内存”无关，不提示
    public string? Warning => !Overridden && MemoryMiB < WarnBelowMiB
        ? string.Format(CultureInfo.InvariantCulture,
            "提示：本机当前可用内存较少（{0:F1} GiB），特别复杂的图纸可能因内存不足而失败。可关闭其他程序后重试。", AvailableMiB / 1024.0)
        : null;
}

public static class ResourceAuto
{
    // 已验证的基准值（开发机上的默认值）
    public const int BaseMemoryMiB = 3072, BaseTimeoutSeconds = 300, BaseCpuPercent = 25;
    const int MinMemoryMiB = 1024, MaxMemoryMiB = 8192, MaxTimeoutSeconds = 1200;

    /// <summary>纯函数，便于测试：由核数、物理内存、可用物理内存、可用提交量推出上限。</summary>
    public static ResourcePlan Compute(int logicalCores, long totalMiB, long availableMiB, long commitAvailableMiB)
    {
        logicalCores = Math.Max(1, logicalCores);
        // 3 核及以上给整机的 75%；2 核及以下不限制（识别器本身以低于正常的优先级运行，不影响前台程序）
        int cpu = logicalCores <= 2 ? 100 : 75;
        // 可用内存的 80%，再不超过可用提交量的 80%；夹在 [1, 8] GiB
        double byPhysical = availableMiB * 0.80;
        double byCommit = commitAvailableMiB > 0 ? commitAvailableMiB * 0.8 : double.MaxValue;
        int mem = (int)Math.Clamp(Math.Min(byPhysical, byCommit), MinMemoryMiB, MaxMemoryMiB);
        // 核数少时每个阶段会更慢：8 核及以上保持 300 秒，之后按比例放大，最多 20 分钟（只是防卡死的兜底）
        int timeout = (int)Math.Clamp(Math.Round(BaseTimeoutSeconds * Math.Max(1.0, 8.0 / logicalCores)), BaseTimeoutSeconds, MaxTimeoutSeconds);
        return new ResourcePlan(logicalCores, totalMiB, availableMiB, commitAvailableMiB, mem, cpu, timeout);
    }

    public static ResourcePlan Current()
    {
        var s = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var plan = !GlobalMemoryStatusEx(ref s)
            ? Compute(Environment.ProcessorCount, BaseMemoryMiB * 2L, BaseMemoryMiB * 2L, 0) // 取不到时按保守的中等配置
            : Compute(Environment.ProcessorCount, (long)(s.ullTotalPhys / 1048576.0), (long)(s.ullAvailPhys / 1048576.0), (long)(s.ullAvailPageFile / 1048576.0));
        return ApplyOverrides(plan);
    }

    /// <summary>高级覆盖（排查问题用，默认不设置）：CADREVIEW_MEMORY_MIB、CADREVIEW_CPU_PERCENT、CADREVIEW_TIMEOUT_SECONDS。无效值忽略。</summary>
    static ResourcePlan ApplyOverrides(ResourcePlan plan)
    {
        static int? Read(string name, int min, int max) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max ? v : null;
        var mem = Read("CADREVIEW_MEMORY_MIB", 256, 16384);
        var cpu = Read("CADREVIEW_CPU_PERCENT", 1, 100);
        var timeout = Read("CADREVIEW_TIMEOUT_SECONDS", 30, 3600);
        if (mem == null && cpu == null && timeout == null) return plan;
        return plan with { MemoryMiB = mem ?? plan.MemoryMiB, CpuPercent = cpu ?? plan.CpuPercent, TimeoutSeconds = timeout ?? plan.TimeoutSeconds, Overridden = true };
    }

    static string N(int v) => v.ToString(CultureInfo.InvariantCulture);

    static bool Flag(JsonElement stage, string name) =>
        stage.ValueKind == JsonValueKind.Object && stage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>识别失败时，看是不是内存不足或超时，给出使用者能看懂的说明；看不出来返回 null（沿用原来的技术性提示）。
    /// 依据：原生读取遇到内存上限时 stderr 为“Out of memory.”（退出码是 .NET 未处理异常，runner 不会标 memory_limit_exceeded）；
    /// 超时由 runner 标 timeout。本次实际使用的上限取自该任务的 protocol.json。</summary>
    public static string? ExplainFailure(string outputDir)
    {
        try
        {
            var recognition = Path.Combine(outputDir, "recognition");
            var samples = Path.Combine(recognition, "samples");
            if (!Directory.Exists(samples)) return null;
            bool memory = false, timeout = false;
            string? file = null;
            foreach (var dir in Directory.GetDirectories(samples))
            {
                bool hit = false;
                foreach (var log in Directory.GetFiles(dir, "*.stderr.log"))
                {
                    var text = File.ReadAllText(log);
                    if (text.Contains("Out of memory", StringComparison.OrdinalIgnoreCase) || text.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase))
                    { memory = true; hit = true; }
                }
                var runPath = Path.Combine(dir, "run.json");
                if (File.Exists(runPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(runPath));
                    foreach (var stage in doc.RootElement.EnumerateObject())
                    {
                        if (Flag(stage.Value, "memory_limit_exceeded")) { memory = true; hit = true; }
                        if (Flag(stage.Value, "timeout")) { timeout = true; hit = true; }
                        if (hit && file == null && stage.Value.ValueKind == JsonValueKind.Object && stage.Value.TryGetProperty("command", out var cmd) &&
                            cmd.ValueKind == JsonValueKind.Array)
                            foreach (var c in cmd.EnumerateArray())
                                if (c.ValueKind == JsonValueKind.String && (c.GetString() ?? "").EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                                { file = Path.GetFileName(c.GetString()!); break; }
                    }
                }
            }
            if (!memory && !timeout) return null;
            double memMiB = 0, seconds = 0;
            var protocol = Path.Combine(recognition, "protocol.json");
            if (File.Exists(protocol))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(protocol));
                if (doc.RootElement.TryGetProperty("limits", out var l) && l.ValueKind == JsonValueKind.Object)
                {
                    if (l.TryGetProperty("memory_mib", out var m) && m.TryGetDouble(out var mv)) memMiB = mv;
                    if (l.TryGetProperty("timeout_seconds", out var t) && t.TryGetDouble(out var tv)) seconds = tv;
                }
            }
            var plan = Current();
            var name = file != null ? "“" + file + "”" : "其中一份图纸";
            if (memory)
                return string.Format(CultureInfo.InvariantCulture,
                    "内存不足：处理{0}需要的内存超过了本次允许的上限（{1:F1} GiB；本机当前可用 {2:F1} GiB）。这份图纸未完成识别。请关闭其他占内存的程序后重试，或换一台内存更大的电脑处理这份图纸。",
                    name, memMiB / 1024.0, plan.AvailableMiB / 1024.0);
            return string.Format(CultureInfo.InvariantCulture,
                "处理超时：处理{0}时某个阶段超过了时间上限（{1:F0} 秒）。这台电脑处理它比较慢，没有产生任何结果。可稍后重试，或换一台更快的电脑。",
                name, seconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>旧版单图识别器（runner.py）的限制参数；不用于多图并行准入。</summary>
    public static IEnumerable<string> RecognizeArgs(ResourcePlan p) =>
        ["--memory-mib", N(p.MemoryMiB), "--timeout", N(p.TimeoutSeconds), "--cpu-percent", N(p.CpuPercent)];

    /// <summary>校验/导出守护脚本（review_guard.py）的限制参数；校验程序本身用得不多，内存至少给 2 GiB 上限。</summary>
    public static IEnumerable<string> GuardArgs(ResourcePlan p) =>
        ["--memory-mib", N(Math.Max(p.MemoryMiB, 2048)), "--timeout", N(p.TimeoutSeconds), "--cpu-percent", N(p.CpuPercent)];

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
