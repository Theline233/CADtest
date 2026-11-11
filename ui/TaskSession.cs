using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CadReviewUI;

public sealed record ErrorEntry(string Text, string? ItemId, string? FileId)
{
    public override string ToString() => Text;
}

public sealed record RecentTask(string Folder, string Title, DateTime OpenedUtc)
{
    public override string ToString() => Title;
}

/// <summary>一次复核任务：识别引擎产出的 review 目录（source.json + 图纸复核.xlsx）加上人工决定。
/// 不含任何界面代码，界面和自测共用。原 review 目录里的文件一律只读，
/// 人工决定及各活动会话的工作簿保存在 outputs\ui-sessions\&lt;任务ID&gt;\。</summary>
public sealed partial class TaskSession
{
    public string Folder { get; }
    public string Manifest { get; }
    public string SourceXlsx { get; }
    public string ManifestSha256 { get; }
    public EngineKind Kind { get; }
    public Dataset Data { get; }
    public string SessionDir { get; }
    public List<ItemRow> Items { get; } = [];
    public List<FileRow> Files { get; } = [];
    public string? LoadNote { get; private set; }

    public string DecisionsPath => Path.Combine(SessionDir, "ui-decisions.json");
    public string ReviewedPath => Path.Combine(SessionDir, "snapshots", _sessionId, "已复核.xlsx");

    // A session's baseline is the exact archive it loaded, including the distinction
    // between a missing file and an unreadable one. All cooperating processes use
    // the same lock file; an old executable that does not take this lock cannot be
    // made safe by this code alone.
    readonly string _sessionId = Guid.NewGuid().ToString("N");
    readonly object _operationLock = new();
    string? _archiveHash;
    string? _blockedArchive;
    string? _lastSaveFailure;
    string? _lastRecoveryContentHash;
    string? _lastRecoveryPath;
    string ArchiveLockPath => Path.Combine(SessionDir, ".ui-decisions.lock");

    public event Action? Changed;

    TaskSession(string folder, string manifest, string xlsx, Dataset data, string sha)
    {
        Folder = folder; Manifest = manifest; SourceXlsx = xlsx; Data = data; ManifestSha256 = sha;
        Kind = Engine.KindFor(folder);
        SessionDir = Path.Combine(Engine.SessionsRoot, Sanitize(data.TaskId));
    }

    static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>folder 可以是 review 目录，也可以是它的上一级（识别输出根目录）。</summary>
    public static string? FindReviewFolder(string folder)
    {
        foreach (var candidate in new[] { folder, Path.Combine(folder, "review") })
            if (File.Exists(Path.Combine(candidate, "source.json")) && File.Exists(Path.Combine(candidate, "图纸复核.xlsx")))
                return Path.GetFullPath(candidate);
        return null;
    }

    public static TaskSession Open(string folder)
    {
        var review = FindReviewFolder(folder) ?? throw new InvalidDataException("该目录里没有 source.json 和 图纸复核.xlsx：" + folder);
        var manifest = Path.Combine(review, "source.json");
        // Read and hash the same locked bytes, so a concurrent source change cannot
        // pair one dataset with another dataset's hash.
        using var source = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(source, Encoding.UTF8, true, leaveOpen: true);
        var data = JsonSerializer.Deserialize<Dataset>(reader.ReadToEnd(), Json.Options)
            ?? throw new InvalidDataException("source.json 内容为空。");
        if (data.SchemaVersion != 1 || string.IsNullOrWhiteSpace(data.TaskId) || data.Files == null || data.Files.Count == 0 || data.Items == null)
            throw new InvalidDataException("source.json 格式不受支持（schema " + data.SchemaVersion + "）");
        source.Position = 0;
        var session = new TaskSession(review, manifest, Path.Combine(review, "图纸复核.xlsx"), data,
            Convert.ToHexString(SHA256.HashData(source)));
        Directory.CreateDirectory(session.SessionDir);
        session.Build();
        return session;
    }

    void Build()
    {
        DecisionSet? saved = null;
        using (AcquireArchiveLock())
        {
            try
            {
                var bytes = ReadArchive();
                if (bytes != null)
                {
                    saved = ReadDecisionSet(bytes);
                    _archiveHash = Hash(bytes);
                }
            }
            catch (Exception ex)
            {
                _blockedArchive = ex.Message;
                LoadNote = "此前复核存档无法读取或与当前任务不符，已保留并禁止覆盖（" + ex.Message + "）。";
            }
        }
        var fileNames = Data.Files.ToDictionary(f => f.Id, f => f.Name);
        var hasItems = Data.Items.Select(i => i.FileId).ToHashSet();
        foreach (var f in Data.Files)
        {
            var d = saved != null && saved.Files.TryGetValue(f.Id, out var fd) ? fd
                : new FileDecision { State = f.RequiresAcknowledgement || !hasItems.Contains(f.Id) ? "待核查" : "无需确认" };
            var row = new FileRow(f, d);
            row.Edited += _ => Changed?.Invoke();
            Files.Add(row);
        }
        foreach (var i in Data.Items)
        {
            // 没有存档时，按识别结果的初始处理方式填写（图面打X默认不纳入，用户可改回）。
            // A saved archive deliberately omits blank/pending decisions.
            // Once it exists, an omitted row is the user's saved pending
            // choice, not a reason to apply the initial default again.
            var d = saved != null
                ? saved.Items.TryGetValue(i.Id, out var id) ? id : new ItemDecision()
                : i.DefaultDecision is { Mode: Rules.Exclude } dd && !string.IsNullOrWhiteSpace(dd.Note)
                    ? new ItemDecision { Mode = Rules.Exclude, Note = dd.Note }
                    : new ItemDecision();
            var row = new ItemRow(i, fileNames.GetValueOrDefault(i.FileId, i.FileId), d);
            row.Edited += _ => Changed?.Invoke();
            Items.Add(row);
        }
        RefreshFileSummaries();
    }

    public void RefreshFileSummaries()
    {
        foreach (var f in Files)
        {
            var mine = Items.Where(i => i.FileId == f.Id).ToList();
            var pending = mine.Count(i => i.Mode == Rules.Pending);
            f.Summary = mine.Count == 0 ? "无候选明细" : pending == 0 ? $"{mine.Count} 条，已全部处理" : $"{mine.Count} 条，待复核 {pending}";
        }
    }

    public (int Pending, int Adopt, int Revise, int Exclude) Counts() =>
        (Items.Count(i => i.Mode == Rules.Pending), Items.Count(i => i.Mode == Rules.Adopt),
         Items.Count(i => i.Mode == Rules.Revise), Items.Count(i => i.Mode == Rules.Exclude));

    public (Dictionary<string, ItemDecision> Items, Dictionary<string, FileDecision> Files) Snapshot() =>
        (Items.ToDictionary(i => i.Id, i => i.ToDecision()), Files.ToDictionary(f => f.Id, f => f.ToDecision()));

    public void Save()
    {
        lock (_operationLock)
        {
            DecisionSet? decisions = null;
            try
            {
                var (items, files) = Snapshot();
                decisions = new DecisionSet
                {
                    Schema = 2,
                    TaskId = Data.TaskId,
                    ManifestSha256 = ManifestSha256,
                    SavedUtc = DateTime.UtcNow,
                    Items = items.Where(kv => !kv.Value.IsBlank).ToDictionary(kv => kv.Key, kv => kv.Value),
                    Files = files,
                };
                using var source = LockCurrentManifest();
                using var archiveLock = AcquireArchiveLock();
                CheckArchiveBaseline();
                var bytes = EncodeDecisionSet(decisions);
                var newHash = Hash(bytes);
                var temp = WriteTemp(SessionDir, ".decisions-", ".tmp", bytes);
                try
                {
                    Publish(temp, DecisionsPath, _archiveHash != null, Path.Combine(SessionDir, "history"), "ui-decisions", ".json");
                }
                finally { DeleteTemp(temp); }
                _archiveHash = newHash;
                _lastSaveFailure = null;
            }
            catch (Exception ex)
            {
                var recovery = decisions == null ? null : TryWriteRecovery(decisions);
                _lastSaveFailure = ex.Message;
                var hint = recovery == null ? "未能另存应急副本。" : "应急副本：" + recovery + "。";
                throw new IOException("复核存档未保存：" + ex.Message + " 当前窗口中的人工决定仍在，请勿关闭窗口。" + hint, ex);
            }
        }
    }

    /// <summary>将当前窗口决定保存为可单独打开的恢复会话，不改写共享存档。</summary>
    public string SaveRecoverySession()
    {
        lock (_operationLock)
        {
            using var manifest = LockCurrentManifest();
            var (items, files) = Snapshot();
            var decisions = new DecisionSet
            {
                Schema = 2, TaskId = Data.TaskId, ManifestSha256 = ManifestSha256, SavedUtc = DateTime.UtcNow,
                Items = items.Where(kv => !kv.Value.IsBlank).ToDictionary(kv => kv.Key, kv => kv.Value),
                Files = files,
            };
            byte[] bytes = EncodeDecisionSet(decisions);
            string recoveryRoot = Path.Combine(SessionDir, "recovery", "session-" + Guid.NewGuid().ToString("N"));
            string recoveryDir = Path.Combine(recoveryRoot, Sanitize(Data.TaskId));
            Directory.CreateDirectory(recoveryDir);
            string temp = WriteTemp(recoveryDir, ".recovery-", ".tmp", bytes);
            string target = Path.Combine(recoveryDir, "ui-decisions.json");
            try
            {
                byte[] written = File.ReadAllBytes(temp);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(written)))
                    throw new IOException("恢复副本写入校验失败。");
                _ = ReadDecisionSet(written);
                File.Move(temp, target, false);
                byte[] reopened = File.ReadAllBytes(target);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(reopened)))
                    throw new IOException("恢复副本重开校验失败。");
                _ = ReadDecisionSet(reopened);
                return recoveryRoot;
            }
            finally { DeleteTemp(temp); }
        }
    }

    /// <summary>兼容旧调用；导出期间仍锁定复核表，但不检查上次校验的哈希。</summary>
    public Task<ValidationDto> ExportWithArchiveLockAsync(string output, string reviewer, CancellationToken ct = default) =>
        ExportWithArchiveLockCoreAsync(output, reviewer, null, ct);

    /// <summary>仅导出与上次校验哈希一致的复核表，并在整个导出期间保持文件锁。</summary>
    public Task<ValidationDto> ExportWithArchiveLockAsync(string output, string reviewer, string? expectedReviewSha256, CancellationToken ct = default)
    {
        if (!ValidReviewHash(expectedReviewSha256))
            throw new ArgumentException("必须提供有效的复核表 SHA-256，请重新校验后再导出。", nameof(expectedReviewSha256));
        return ExportWithArchiveLockCoreAsync(output, reviewer, expectedReviewSha256, ct);
    }

    /// <summary>先由冻结引擎生成原始确认记录，再在同一把锁内生成客户样式文件。</summary>
    public Task<ValidationDto> ExportQuotationWithArchiveLockAsync(string output, string reviewer, string? expectedReviewSha256, CancellationToken ct = default)
    {
        if (!ValidReviewHash(expectedReviewSha256))
            throw new ArgumentException("必须提供有效的复核表 SHA-256，请重新校验后再导出。", nameof(expectedReviewSha256));
        return ExportWithArchiveLockCoreAsync(output, reviewer, expectedReviewSha256, ct, true);
    }

    /// <summary>锁定刚写入的复核表并校验其哈希，直到校验引擎结束。</summary>
    public Task<ValidationDto> ValidateWithReviewLockAsync(string expectedSha256, CancellationToken ct = default) =>
        ValidateReviewCoreAsync(ReviewedPath, expectedSha256, ct);

    internal Task<ValidationDto> ValidatePreviewWithReviewLockAsync(string path, string expectedSha256, CancellationToken ct = default)
    {
        RequireOwnPreviewPath(path);
        return ValidateReviewCoreAsync(path, expectedSha256, ct);
    }

    async Task<ValidationDto> ValidateReviewCoreAsync(string reviewPath, string expectedSha256, CancellationToken ct)
    {
        if (!ValidReviewHash(expectedSha256))
            throw new ArgumentException("必须提供有效的复核表 SHA-256，请重新写入复核表。", nameof(expectedSha256));
        var held = await AcquireReviewLocksAsync(expectedSha256, ct, reviewPath).ConfigureAwait(false);
        using (held.Manifest)
        using (held.ArchiveLock)
        using (held.ArchiveFile)
        using (held.ReviewedFile)
        {
            ct.ThrowIfCancellationRequested();
            var result = await Engine.ValidateAsync(Kind, Manifest, reviewPath, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result == null || !string.Equals(result.ReviewSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                if (result?.Status == "failed" && !string.IsNullOrWhiteSpace(result.Error))
                    throw new InvalidDataException("校验引擎未能完成验证：" + result.Error);
                throw new InvalidDataException("校验引擎返回的复核表 SHA-256 缺失或不一致，请重新核对并校验。");
            }
            return result;
        }
    }

    static bool ValidReviewHash(string? hash) => hash != null && hash.Length == 64 && hash.All(Uri.IsHexDigit);

    async Task<ValidationDto> ExportWithArchiveLockCoreAsync(string output, string reviewer, string? expectedReviewSha256, CancellationToken ct,
        bool quotationStyle = false)
    {
        var held = await AcquireReviewLocksAsync(expectedReviewSha256, ct, ReviewedPath).ConfigureAwait(false);
        using (held.Manifest)
        using (held.ArchiveLock)
        using (held.ArchiveFile)
        using (held.ReviewedFile)
        {
            ct.ThrowIfCancellationRequested();
            string engineFolder = output;
            if (quotationStyle)
            {
                if (Directory.Exists(output) || File.Exists(output))
                    throw new IOException("导出目录已存在，未覆盖任何文件；请重新导出。");
                Directory.CreateDirectory(output);
                engineFolder = Path.Combine(output, "复核记录");
            }
            var result = await Engine.ExportAsync(Kind, Manifest, ReviewedPath, engineFolder, reviewer, ct).ConfigureAwait(false);
            result.VerifiedFormattedSha256 = null;
            result.VerifiedAssociationSha256 = null;
            if (quotationStyle && result.Status == "ready")
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var verified = await Task.Run(() => QuotationFormatter.WriteVerified(engineFolder, output, result, Data), ct).ConfigureAwait(false);
                    result.VerifiedFormattedSha256 = verified.Sha256;
                    result.VerifiedAssociationSha256 = verified.AssociationSha256;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new IOException("确认记录已保存在“复核记录”文件夹，但没有生成报价样式文件：" + ex.Message, ex);
                }
            }
            return result;
        }
    }

    Task<(FileStream Manifest, FileStream ArchiveLock, FileStream ArchiveFile, FileStream ReviewedFile)>
        AcquireReviewLocksAsync(string? expectedReviewSha256, CancellationToken ct, string reviewPath)
    {
        return Task.Run(() =>
        {
            lock (_operationLock)
            {
                if (_lastSaveFailure != null)
                    throw new InvalidOperationException("上次保存失败，不能校验或导出；当前窗口决定仍保留。");
                if (!File.Exists(reviewPath))
                    throw new InvalidOperationException("当前会话没有已写入的复核表，不能校验或导出。");

                var manifest = LockCurrentManifest();
                FileStream? archiveLock = null, archiveFile = null, reviewedFile = null;
                try
                {
                    archiveLock = AcquireArchiveLock();
                    CheckArchiveBaseline();
                    if (_archiveHash == null)
                        throw new InvalidOperationException("当前会话尚无已保存决定，不能校验或导出。");
                    // Also hold the actual archive read-only: older programs may not take
                    // the cooperative lock, but cannot replace an open file without delete sharing.
                    archiveFile = new FileStream(DecisionsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    string heldHash = Convert.ToHexString(SHA256.HashData(archiveFile));
                    if (!string.Equals(heldHash, _archiveHash, StringComparison.Ordinal))
                        throw new IOException("存档已被其他会话或程序更改，不能导出。");
                    // Lock the same bytes that the engine will read. Without write or
                    // delete sharing, another process cannot edit or replace the file.
                    reviewedFile = new FileStream(reviewPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (expectedReviewSha256 != null)
                    {
                        string reviewedHash = Convert.ToHexString(SHA256.HashData(reviewedFile));
                        if (!string.Equals(reviewedHash, expectedReviewSha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("复核表发生变化，请重新核对并校验。");
                    }
                    return (Manifest: manifest, ArchiveLock: archiveLock, ArchiveFile: archiveFile, ReviewedFile: reviewedFile);
                }
                catch
                {
                    reviewedFile?.Dispose();
                    archiveFile?.Dispose();
                    archiveLock?.Dispose();
                    manifest.Dispose();
                    throw;
                }
            }
        }, ct);
    }

    /// <summary>把当前决定写入“已复核.xlsx”，返回无法写入的问题（如数量不是数字）。</summary>
    public List<string> WriteReviewed() => WriteReviewed(out _);

    /// <summary>发布复核表后返回写出内容的 SHA-256；失败或未发布时哈希为空。</summary>
    public List<string> WriteReviewed(out string? writtenSha256) => WriteReviewedCore(ReviewedPath, null, out writtenSha256);

    internal string NewPreviewPath() => Path.Combine(Path.GetDirectoryName(ReviewedPath)!,
        ".unconfirmed-preview-" + Guid.NewGuid().ToString("N") + ".xlsx");

    void RequireOwnPreviewPath(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetDirectoryName(ReviewedPath), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith(".unconfirmed-preview-", StringComparison.Ordinal) ||
            !path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("无效的本会话预览文件路径。");
    }

    internal void DeleteOwnPreview(string path)
    {
        RequireOwnPreviewPath(path);
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal List<string> WritePreviewReviewed(string path, IReadOnlySet<string> proposedIds, out string? writtenSha256)
    {
        RequireOwnPreviewPath(path);
        if (File.Exists(path)) throw new IOException("预览文件已存在，不能覆盖。");
        return WriteReviewedCore(path, proposedIds, out writtenSha256);
    }

    List<string> WriteReviewedCore(string target, IReadOnlySet<string>? proposedIds, out string? writtenSha256)
    {
        writtenSha256 = null;
        lock (_operationLock)
        {
            if (_lastSaveFailure != null)
                throw new InvalidOperationException("上次保存失败，请先处理并重新保存；当前人工决定仍在窗口中。（" + _lastSaveFailure + "）");
            using var source = LockCurrentManifest();
            using var archiveLock = AcquireArchiveLock();
            CheckArchiveBaseline();
            var problems = new List<string>();
            var (items, files) = Snapshot();
            foreach (var row in Items.Where(r => r.NeedsCrossedOutInclusionReason))
                if (!files.TryGetValue(row.FileId, out var fileDecision) || fileDecision.State != "不纳入本次")
                    problems.Add($"{row.Id}: 图面打X的明细要纳入时，请在表格中把这一行的处理方式改为“采用图纸”或“人工修订”。");
            if (problems.Count != 0) return problems;
            // An empty confirmation cell means "use the original" to existing
            // frozen engines. A user-cleared value must never silently revert
            // or be emitted as the internal draft marker.
            foreach (var row in Items.Where(r => r.HasClearedOverride))
            {
                bool excluded = row.Mode == Rules.Exclude ||
                    (files.TryGetValue(row.FileId, out var fileDecision) && fileDecision.State == "不纳入本次");
                if (!excluded)
                    problems.Add($"{row.Id}: 已清空 {string.Join("、", row.ClearedFieldNames)}；请填写有效值或恢复识别值后再导出（现有引擎不支持将已有名称改为空）。");
                else if (items.TryGetValue(row.Id, out var decision))
                {
                    if (decision.DrawingNumber == ItemRow.ClearOverride) decision.DrawingNumber = "";
                    if (decision.Name == ItemRow.ClearOverride) decision.Name = "";
                    if (decision.Material == ItemRow.ClearOverride) decision.Material = "";
                    if (decision.Treatment == ItemRow.ClearOverride) decision.Treatment = "";
                    if (decision.Quantity == ItemRow.ClearOverride) decision.Quantity = "";
                    if (decision.Unit == ItemRow.ClearOverride) decision.Unit = "";
                }
            }
            if (problems.Count != 0) return problems;
            if (proposedIds != null)
            {
                var rows = Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
                foreach (string id in proposedIds)
                {
                    if (!rows.TryGetValue(id, out var row) || !items.TryGetValue(id, out var decision) ||
                        decision.Mode != Rules.Pending || row.IsCrossedOut || row.HasOverride || !row.IsCompleteOriginal ||
                        !row.Src.RequiresReviewSpecified || !row.Src.WarningsSpecified || !string.IsNullOrWhiteSpace(row.Note) ||
                        ((row.Src.RequiresReview || !string.IsNullOrWhiteSpace(row.Warnings)) && !DuplicateQuantity.CanProposeDefault(row)) ||
                        !files.TryGetValue(row.FileId, out var file) || file.State == "不纳入本次")
                        throw new InvalidOperationException("有明细不适合直接确认，请重新核对。");
                    decision.Mode = Rules.Adopt;
                    if (DuplicateQuantity.CanProposeDefault(row)) decision.Note = DuplicateQuantity.DefaultNote(row);
                }
            }
            var dir = Path.GetDirectoryName(ReviewedPath)!;
            Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, ".review-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                ReviewIo.Write(SourceXlsx, temp, items, files, problems);
                if (problems.Count != 0) return problems;
                string hash;
                using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    stream.Flush(flushToDisk: true);
                    stream.Position = 0;
                    hash = Convert.ToHexString(SHA256.HashData(stream));
                }
                if (proposedIds != null) File.Move(temp, target, false);
                else Publish(temp, target, File.Exists(target), Path.Combine(dir, "history"), "已复核", ".xlsx");
                writtenSha256 = hash;
                return problems;
            }
            finally { DeleteTemp(temp); }
        }
    }

    FileStream AcquireArchiveLock()
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(ArchiveLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(50);
            }
            catch (IOException ex) { throw new IOException("复核存档正被占用，请稍后重试。", ex); }
        }
    }

    FileStream LockCurrentManifest()
    {
        var stream = new FileStream(Manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), ManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("当前任务的 source.json 已变化，请重新打开任务。");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    byte[]? ReadArchive()
    {
        try
        {
            using var stream = new FileStream(DecisionsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
    }

    void CheckArchiveBaseline()
    {
        if (_blockedArchive != null) throw new InvalidDataException("此前存档不可覆盖：" + _blockedArchive);
        var current = ReadArchive();
        var currentHash = current == null ? null : Hash(current);
        if (!string.Equals(currentHash, _archiveHash, StringComparison.Ordinal))
            throw new IOException("存档已被其他会话或程序更改，请保留当前内容并重新核对。");
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    DecisionSet ReadDecisionSet(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var text = reader.ReadToEnd();
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("复核存档不是对象。");
        var properties = UniqueProperties(root, StringComparer.OrdinalIgnoreCase);
        if (!properties.ContainsKey("TaskId") || !properties.ContainsKey("ManifestSha256") ||
            !properties.TryGetValue("Items", out var itemObject) || !properties.TryGetValue("Files", out var fileObject) ||
            itemObject.ValueKind != JsonValueKind.Object || fileObject.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("复核存档缺少必要字段。");
        foreach (var records in new[] { itemObject, fileObject })
            foreach (var record in UniqueProperties(records, StringComparer.Ordinal).Values)
            {
                if (record.ValueKind != JsonValueKind.Object) throw new InvalidDataException("复核存档包含无效记录。");
                UniqueProperties(record, StringComparer.OrdinalIgnoreCase);
            }
        var saved = JsonSerializer.Deserialize<DecisionSet>(text, Json.Options) ?? throw new InvalidDataException("复核存档为空。");
        if (saved.Schema is not (1 or 2) || !string.Equals(saved.TaskId, Data.TaskId, StringComparison.Ordinal) ||
            !string.Equals(saved.ManifestSha256, ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("复核存档的任务或来源版本不符。");
        if (saved.Items == null || saved.Files == null) throw new InvalidDataException("复核存档的记录为空。");
        var itemIds = Data.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var fileIds = Data.Files.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        if (saved.Items.Any(kv => !itemIds.Contains(kv.Key) || kv.Value == null ||
            !Rules.Modes.Contains(kv.Value.Mode) || kv.Value.DrawingNumber == null || kv.Value.Name == null ||
            kv.Value.Material == null || kv.Value.Treatment == null || kv.Value.Quantity == null || kv.Value.Unit == null || kv.Value.Note == null) ||
            saved.Files.Any(kv => !fileIds.Contains(kv.Key) || kv.Value == null || !Rules.FileStates.Contains(kv.Value.State) ||
                kv.Value.Note == null || kv.Value.ParentChildNote == null))
            throw new InvalidDataException("复核存档包含不属于当前任务或无效的记录。");
        var hasRevision = properties.TryGetValue("ArchiveRevision", out var revision);
        var hasChecksum = properties.TryGetValue("ArchiveContentSha256", out var checksum);
        if (saved.Schema == 2 && (!hasRevision || !hasChecksum))
            throw new InvalidDataException("复核存档缺少完整性校验信息。");
        if (hasRevision || hasChecksum)
        {
            if (!hasRevision || !hasChecksum || revision.ValueKind != JsonValueKind.String || checksum.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(revision.GetString(), "N", out _) ||
                !string.Equals(checksum.GetString(), DecisionContentHash(saved), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("复核存档的完整性校验失败。");
        }
        return saved;
    }

    static Dictionary<string, JsonElement> UniqueProperties(JsonElement element, StringComparer comparer)
    {
        var result = new Dictionary<string, JsonElement>(comparer);
        foreach (var property in element.EnumerateObject())
            if (!result.TryAdd(property.Name, property.Value)) throw new InvalidDataException("复核存档包含重复字段：" + property.Name);
        return result;
    }

    static string DecisionContentHash(DecisionSet decisions) => Hash(JsonSerializer.SerializeToUtf8Bytes(decisions, Json.Options));

    static byte[] EncodeDecisionSet(DecisionSet decisions) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        decisions.Schema, decisions.TaskId, decisions.ManifestSha256, decisions.SavedUtc, decisions.Items, decisions.Files,
        ArchiveRevision = Guid.NewGuid().ToString("N"), ArchiveContentSha256 = DecisionContentHash(decisions),
    }, Json.Options);

    string? TryWriteRecovery(DecisionSet decisions)
    {
        try
        {
            // SavedUtc changes on every retry; do not produce repeated copies when
            // the actual in-memory decisions have not changed.
            var contentHash = Hash(JsonSerializer.SerializeToUtf8Bytes(new
                { decisions.TaskId, decisions.ManifestSha256, decisions.Items, decisions.Files }, Json.Options));
            if (contentHash == _lastRecoveryContentHash && _lastRecoveryPath != null && File.Exists(_lastRecoveryPath))
                return _lastRecoveryPath;
            var dir = Path.Combine(SessionDir, "recovery");
            Directory.CreateDirectory(dir);
            var temp = WriteTemp(dir, ".recovery-", ".tmp", EncodeDecisionSet(decisions));
            try
            {
                var target = Path.Combine(dir, "人工决定-" + _sessionId + "-" + Guid.NewGuid().ToString("N") + ".json");
                File.Move(temp, target);
                _lastRecoveryContentHash = contentHash;
                _lastRecoveryPath = target;
                return target;
            }
            finally { DeleteTemp(temp); }
        }
        catch { return null; }
    }

    static string WriteTemp(string directory, string prefix, string suffix, byte[] bytes)
    {
        var path = Path.Combine(directory, prefix + Guid.NewGuid().ToString("N") + suffix);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return path;
        }
        catch { DeleteTemp(path); throw; }
    }

    static void Publish(string temp, string target, bool exists, string historyDir, string prefix, string extension)
    {
        if (!exists) { File.Move(temp, target); return; }
        Directory.CreateDirectory(historyDir);
        var backup = Path.Combine(historyDir, prefix + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfffffff") + "-" + Guid.NewGuid().ToString("N") + extension);
        File.Replace(temp, target, backup, ignoreMetadataErrors: true);
    }

    static void DeleteTemp(string path)
    {
        try { File.Delete(path); } catch { /* A failed cleanup must not hide the original failure. */ }
    }

    internal sealed record PreparedImport(TaskSession Owner,
        Dictionary<string, ItemDecision> Items, Dictionary<string, FileDecision> Files);

    // Only workbook and dataset data are touched on the worker thread; bound rows are not.
    PreparedImport ReadImportFromExcel(string xlsx)
    {
        using var manifest = LockCurrentManifest();
        var (items, files) = ReviewIo.Read(xlsx, SourceXlsx, Manifest, ManifestSha256, Data.TaskId, Data.SchemaVersion);
        if (items.Count != Data.Items.Count || files.Count != Data.Files.Count ||
            Data.Items.Any(row => !items.ContainsKey(row.Id)) || Data.Files.Any(row => !files.ContainsKey(row.Id)))
            throw new InvalidDataException("导入记录与当前任务不一致。");
        return new PreparedImport(this, items, files);
    }

    internal Task<PreparedImport> ReadImportFromExcelAsync(string xlsx) =>
        Task.Run(() => ReadImportFromExcel(xlsx));

    // Called on the UI thread. Hold the archive lock while checking and applying,
    // so an update made by another window during validation cannot be overwritten.
    internal void ApplyPreparedImport(PreparedImport prepared)
    {
        if (!ReferenceEquals(prepared.Owner, this)) throw new InvalidDataException("导入结果不属于当前任务。");
        lock (_operationLock)
        {
            if (_lastSaveFailure != null)
                throw new InvalidOperationException("上次保存失败，请先处理后再导入。");
            using var manifest = LockCurrentManifest();
            using var archiveLock = AcquireArchiveLock();
            CheckArchiveBaseline();
            if (prepared.Items.Count != Items.Count || prepared.Files.Count != Files.Count ||
                Items.Any(row => !prepared.Items.ContainsKey(row.Id)) || Files.Any(row => !prepared.Files.ContainsKey(row.Id)))
                throw new InvalidDataException("导入记录与当前任务不一致。");
            foreach (var row in Items) row.Apply(prepared.Items[row.Id]);
            foreach (var row in Files) row.Apply(prepared.Files[row.Id]);
            RefreshFileSummaries();
            Changed?.Invoke();
        }
    }

    public void ImportFromExcel(string xlsx) => ApplyPreparedImport(ReadImportFromExcel(xlsx));

    public void ClearErrors()
    {
        foreach (var i in Items) i.ErrorText = "";
        foreach (var f in Files) f.ErrorText = "";
    }

    /// <summary>把引擎返回的“键: 说明”错误挂到对应的行/文件上。</summary>
    public List<ErrorEntry> MapErrors(IEnumerable<string> errors)
    {
        ClearErrors();
        var byItem = Items.ToDictionary(i => i.Id);
        var byFile = Files.ToDictionary(f => f.Id);
        var result = new List<ErrorEntry>();
        foreach (var err in errors)
        {
            string? itemId = null, fileId = null;
            var cut = err.IndexOfAny([':', '：']);
            var key = (cut > 0 ? err[..cut] : "").Trim();
            if (byItem.TryGetValue(key, out var item)) { itemId = item.Id; item.ErrorText = Join(item.ErrorText, err[(cut + 1)..].Trim()); }
            else if (byFile.TryGetValue(key, out var file)) { fileId = file.Id; file.ErrorText = Join(file.ErrorText, err[(cut + 1)..].Trim()); }
            else if (key.StartsWith('('))
            {
                // "(S01, 图号): 同文件最终图号重复…" 形式
                var comma = key.IndexOf(',');
                var fid = comma > 1 ? key[1..comma].Trim() : "";
                if (byFile.TryGetValue(fid, out var f2)) { fileId = f2.Id; f2.ErrorText = Join(f2.ErrorText, err); }
            }
            result.Add(new ErrorEntry(err, itemId, fileId));
        }
        return result;
    }

    static string Join(string a, string b) => a == "" ? b : a + "；" + b;

    public static string NewConfirmedFolder() =>
        Path.Combine(Engine.OutputsRoot, "ui-confirmed-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);

}
