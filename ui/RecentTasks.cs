using System.Diagnostics;
using System.Text.Json;

namespace CadReviewUI;

/// <summary>首页用的只读展示；Entry 是删除时需要核对的原始记录。</summary>
public sealed record RecentTaskView(
    RecentTask Entry, string DisplayName, string Subtitle, string ToolTip, bool CanOpen)
{
    public string Folder => Entry.Folder;
}

public sealed partial class TaskSession
{
    // The on-disk schema remains the existing array of Folder/Title/OpenedUtc records.
    // Keep unknown properties on existing records when another record is changed.
    const int MaxRecentBytes = 4 * 1024 * 1024;
    // Small files are validated in full. Larger sources are streamed until the
    // file list and task metadata are found, regardless of total file length.
    const int FullSourceValidationBytes = 8 * 1024 * 1024;
    const int MaxRecentSourceTokenBytes = 8 * 1024 * 1024;
    const int MaxRecentSourceNames = 1000;
    const int MaxRecentSourceNameChars = 1024;
    static string RecentPath => Path.Combine(Engine.SessionsRoot, "recent.json");

    sealed record RecentRecord(RecentTask Entry, JsonElement Raw);
    sealed record RecentSnapshot(List<RecentRecord> Records, byte[]? Bytes);
    sealed record DrawingName(string Name, string Id);
    sealed record DrawingNames(List<DrawingName> Names, bool MetadataOnly);

    /// <summary>读取原始记录；文件损坏或不可访问时抛出说明，不将其当成空列表。</summary>
    public static List<RecentTask> LoadRecent() => ReadRecentSnapshot().Records.Select(r => r.Entry).ToList();

    /// <summary>只读构建显示信息，不打开任务、不创建存档或修改任何任务文件。</summary>
    public static List<RecentTaskView> LoadRecentDisplay() => LoadRecentDisplayCore(CancellationToken.None);

    /// <summary>界面建议在后台调用此方法，避免较慢的磁盘读取阻塞界面。</summary>
    public static Task<List<RecentTaskView>> LoadRecentDisplayAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadRecentDisplayCore(cancellationToken), cancellationToken);

    static List<RecentTaskView> LoadRecentDisplayCore(CancellationToken cancellationToken)
    {
        var snapshot = ReadRecentSnapshot();
        var views = new List<RecentTaskView>(snapshot.Records.Count);
        foreach (var record in snapshot.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            views.Add(BuildRecentView(record.Entry, cancellationToken));
        }
        return views;
    }

    public void RememberRecent()
    {
        try
        {
            using var recentLock = AcquireRecentLock();
            var snapshot = ReadRecentSnapshot();
            var matches = snapshot.Records.Where(r => SameRecentFolder(r.Entry.Folder, Folder)).ToList();
            var opened = DateTime.UtcNow;
            // Reopening the same task must never reuse the timestamp in a displayed
            // entry, including when the system clock has moved backwards.
            if (matches.Count != 0)
            {
                long lastTicks = matches.Max(r => r.Entry.OpenedUtc.Ticks);
                if (opened.Ticks <= lastTicks)
                {
                    if (lastTicks == DateTime.MaxValue.Ticks)
                        throw new InvalidDataException("该任务的最近打开时间无法更新，请先检查列表记录。");
                    opened = new DateTime(lastTicks + 1, DateTimeKind.Utc);
                }
            }
            var entry = new RecentTask(Folder, Data.Title + "（" + Data.Files.Count + " 份图纸，" + Data.Items.Count + " 条）", opened);
            var updated = snapshot.Records.Where(r => !SameRecentFolder(r.Entry.Folder, Folder)).ToList();
            updated.Insert(0, UpdatedRecentRecord(entry, matches.FirstOrDefault()));
            PublishRecent(snapshot, updated);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            throw new IOException("最近任务列表未更新，原有记录已保留：" + ex.Message, ex);
        }
    }

    /// <summary>仅移除选中版本的列表记录；任务、图纸、复核存档及历史成果均不删除。</summary>
    public static bool RemoveRecent(RecentTaskView selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        try
        {
            using var recentLock = AcquireRecentLock();
            var snapshot = ReadRecentSnapshot();
            int index = snapshot.Records.FindIndex(r => SameRecentFolder(r.Entry.Folder, selected.Entry.Folder)
                && r.Entry.OpenedUtc == selected.Entry.OpenedUtc);
            if (index < 0) return false;
            var updated = snapshot.Records.ToList();
            updated.RemoveAt(index);
            PublishRecent(snapshot, updated);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            throw new IOException("未能从最近任务列表移除记录，原有记录已保留：" + ex.Message, ex);
        }
    }

    static bool SameRecentFolder(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    static RecentRecord UpdatedRecentRecord(RecentTask entry, RecentRecord? previous)
    {
        if (previous == null) return new(entry, JsonSerializer.SerializeToElement(entry, Json.Options));
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory, new JsonWriterOptions { Encoder = Json.Options.Encoder }))
        {
            writer.WriteStartObject();
            foreach (var property in previous.Raw.EnumerateObject())
                if (!property.Name.Equals("Folder", StringComparison.OrdinalIgnoreCase) &&
                    !property.Name.Equals("Title", StringComparison.OrdinalIgnoreCase) &&
                    !property.Name.Equals("OpenedUtc", StringComparison.OrdinalIgnoreCase))
                    property.WriteTo(writer);
            writer.WriteString("Folder", entry.Folder);
            writer.WriteString("Title", entry.Title);
            writer.WriteString("OpenedUtc", entry.OpenedUtc);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(memory.ToArray());
        return new(entry, document.RootElement.Clone());
    }

    static FileStream AcquireRecentLock()
    {
        Directory.CreateDirectory(Engine.SessionsRoot);
        RejectReparsePoint(Engine.SessionsRoot, "最近任务目录");
        string path = Path.Combine(Engine.SessionsRoot, ".recent.lock");
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (File.Exists(path)) RejectReparsePoint(path, "最近任务锁文件");
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(40);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                throw new IOException("最近任务列表正被其他窗口占用，请稍后重试。", ex);
            }
        }
    }

    static RecentSnapshot ReadRecentSnapshot()
    {
        try
        {
            try
            {
                var attributes = File.GetAttributes(Engine.SessionsRoot);
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new IOException("最近任务保存位置不是文件夹。");
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("最近任务保存位置是链接，无法安全操作。");
            }
            catch (FileNotFoundException) { return new([], null); }
            catch (DirectoryNotFoundException) { return new([], null); }

            var bytes = ReadRecentBytesOrMissing();
            if (bytes == null) return new([], null);
            using var document = ParseJsonBytes(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("列表不是 JSON 数组。");
            var records = new List<RecentRecord>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("列表包含无效记录。");
                var props = RecentProperties(element);
                if (!props.TryGetValue("Folder", out var folder) || folder.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(folder.GetString()) ||
                    !props.TryGetValue("Title", out var title) || title.ValueKind != JsonValueKind.String ||
                    !props.TryGetValue("OpenedUtc", out var opened) || opened.ValueKind != JsonValueKind.String ||
                    !opened.TryGetDateTime(out _))
                    throw new InvalidDataException("列表记录缺少有效的文件夹、标题或打开时间。");
                var entry = element.Deserialize<RecentTask>(Json.Options)
                    ?? throw new InvalidDataException("列表包含空记录。");
                records.Add(new RecentRecord(entry, element.Clone()));
            }
            return new(records, bytes);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            throw new IOException("无法读取最近任务列表，原文件未改动：" + ex.Message, ex);
        }
    }

    static Dictionary<string, JsonElement> RecentProperties(JsonElement element)
    {
        var props = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            if (!props.TryAdd(property.Name, property.Value))
                throw new InvalidDataException("列表记录包含重复字段：" + property.Name);
        return props;
    }

    static byte[]? ReadRecentBytesOrMissing()
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try { RejectReparsePoint(RecentPath, "最近任务列表"); }
            catch (FileNotFoundException) { return null; }
            try
            {
                // A replacement can briefly hold the target without read sharing.
                using var stream = new FileStream(RecentPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                return ReadBounded(stream, MaxRecentBytes, "最近任务列表过大，无法安全读取");
            }
            catch (IOException ex) when ((ex is FileNotFoundException || (ex.HResult & 0xffff) is 32 or 33) && watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(10);
            }
        }
    }

    static void RejectReparsePoint(string path, string description)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(description + "是链接，无法安全操作。");
        if (description != "最近任务目录" && description != "最近任务备份目录" && (attributes & FileAttributes.Directory) != 0)
            throw new IOException(description + "不是普通文件。");
    }

    static byte[] ReadBounded(Stream stream, int limit, string error)
    {
        if (stream.CanSeek && stream.Length > limit) throw new InvalidDataException(error + "。");
        using var result = new MemoryStream();
        byte[] buffer = new byte[8192];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (result.Length + n > limit) throw new InvalidDataException(error + "。");
            result.Write(buffer, 0, n);
        }
        return result.ToArray();
    }

    static JsonDocument ParseJsonBytes(byte[] bytes)
    {
        int start = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return JsonDocument.Parse(bytes.AsMemory(start), new JsonDocumentOptions { MaxDepth = 64 });
    }

    static void PublishRecent(RecentSnapshot original, List<RecentRecord> records)
    {
        byte[] bytes;
        using (var memory = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(memory, new JsonWriterOptions { Indented = true, Encoder = Json.Options.Encoder }))
            {
                writer.WriteStartArray();
                foreach (var record in records) record.Raw.WriteTo(writer);
                writer.WriteEndArray();
            }
            if (memory.Length > MaxRecentBytes) throw new IOException("更新后的最近任务列表过大，未保存。");
            bytes = memory.ToArray();
        }

        string temp = Path.Combine(Engine.SessionsRoot, ".recent-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // A legacy executable may not use the cooperative lock. Detect changes
            // before publication; legacy writers still cannot be fully coordinated.
            var current = ReadRecentBytesOrMissing();
            if (!(original.Bytes == null ? current == null : current != null && original.Bytes.AsSpan().SequenceEqual(current)))
                throw new IOException("列表刚被其他程序修改，请刷新后重试。");
            if (original.Bytes == null) File.Move(temp, RecentPath, false);
            else
            {
                string history = Path.Combine(Engine.SessionsRoot, "recent-history");
                Directory.CreateDirectory(history);
                RejectReparsePoint(history, "最近任务备份目录");
                string backup = Path.Combine(history, "recent-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfffffff") + "-" + Guid.NewGuid().ToString("N") + ".json");
                File.Replace(temp, RecentPath, backup, ignoreMetadataErrors: true);
            }
            created = false;
        }
        finally
        {
            if (created) { try { File.Delete(temp); } catch { /* Preserve the original error. */ } }
        }
    }

    static RecentTaskView BuildRecentView(RecentTask entry, CancellationToken cancellationToken)
    {
        string subtitle = "最近打开 " + entry.OpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        try
        {
            string[] candidates = [entry.Folder, Path.Combine(entry.Folder, "review")];
            string? source = null;
            bool workbook = false;
            foreach (string folder in candidates)
            {
                string candidateSource = Path.Combine(folder, "source.json");
                bool hasSource = IsRegularFile(candidateSource);
                bool hasWorkbook = IsRegularFile(Path.Combine(folder, "图纸复核.xlsx"));
                if (hasSource && (source == null || hasWorkbook))
                {
                    source = candidateSource;
                    workbook = hasWorkbook;
                }
                if (hasSource && hasWorkbook) break;
            }
            if (source == null)
                return UnavailableRecent(entry, subtitle, "任务目录或图纸信息不存在，仍可移除这条记录。", false);

            DrawingNames drawings;
            try { drawings = ReadRecentDrawingNames(source, cancellationToken); }
            catch (RecentSourceTooLargeException)
            {
                string reason = "来源中的图纸数量或单个字段过大，列表未读取图纸名称。" + (workbook ? "可尝试打开任务。" : "复核表也不存在，无法打开任务。");
                return UnavailableRecent(entry, subtitle, reason, workbook);
            }
            var names = drawings.Names;
            if (names.Count == 0)
                return UnavailableRecent(entry, subtitle, "来源文件没有有效的图纸名称，无法打开任务；仍可移除这条记录。", false);

            var duplicateNames = names.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var labels = names.Select(n => duplicateNames.Contains(n.Name) && !string.IsNullOrWhiteSpace(n.Id)
                ? n.Name + "（ID: " + n.Id + "）" : n.Name).ToList();
            string display = labels.Count == 1 ? labels[0] : string.Join("、", labels.Take(3)) +
                (labels.Count > 3 ? " 等，共 " : "（共 ") + labels.Count + " 张" + (labels.Count > 3 ? "" : "）");
            string note = workbook ? "" : "复核表不存在，无法打开任务；仍可移除这条记录。";
            string tooltip = "图纸文件名：" + Environment.NewLine + string.Join(Environment.NewLine, labels) +
                Environment.NewLine + "任务目录：" + entry.Folder +
                (drawings.MetadataOnly ? Environment.NewLine + "任务文件较大，列表仅读取任务及图纸信息；完整内容须在打开任务时校验。" : "") +
                (note == "" ? "" : Environment.NewLine + note);
            return new RecentTaskView(entry, display, subtitle + (note == "" ? "" : " · 复核表缺失"), tooltip, workbook);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return UnavailableRecent(entry, subtitle, "来源无法读取或格式无效：" + ex.Message + "；仍可移除这条记录。", false);
        }
    }

    static RecentTaskView UnavailableRecent(RecentTask entry, string subtitle, string reason, bool canOpen) =>
        new(entry, "图纸名称暂不可用", subtitle + " · " + reason,
            reason + Environment.NewLine + "任务目录：" + entry.Folder, canOpen);

    static bool IsRegularFile(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    sealed class RecentSourceTooLargeException : IOException { }

    static DrawingNames ReadRecentDrawingNames(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > FullSourceValidationBytes) return ReadRecentDrawingNamesStreaming(stream, cancellationToken);
        byte[] bytes;
        try { bytes = ReadBounded(stream, FullSourceValidationBytes, "来源文件过大"); }
        catch (InvalidDataException)
        {
            stream.Position = 0;
            return ReadRecentDrawingNamesStreaming(stream, cancellationToken);
        }
        using var document = ParseJsonBytes(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("来源文件不是 JSON 对象。");
        var root = RecentProperties(document.RootElement);
        if (!root.TryGetValue("schema_version", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out int version) || version != 1 ||
            !root.TryGetValue("task_id", out var taskId) || taskId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(taskId.GetString()) ||
            (root.TryGetValue("items", out var items) && items.ValueKind != JsonValueKind.Array))
            throw new InvalidDataException("来源文件缺少有效的任务信息。");
        if (!root.TryGetValue("files", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("来源文件缺少图纸列表。");
        if (files.GetArrayLength() > MaxRecentSourceNames) throw new RecentSourceTooLargeException();
        var result = new List<DrawingName>();
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object) throw new InvalidDataException("来源中的图纸记录无效。");
            var fields = RecentProperties(file);
            if (!fields.TryGetValue("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameValue.GetString()))
                throw new InvalidDataException("来源中的图纸缺少文件名。");
            string name = Path.GetFileName(nameValue.GetString()!);
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxRecentSourceNameChars) throw new RecentSourceTooLargeException();
            string id = fields.TryGetValue("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() ?? "" : "";
            if (id.Length > MaxRecentSourceNameChars) throw new RecentSourceTooLargeException();
            result.Add(new DrawingName(name, id));
        }
        return new DrawingNames(result, false);
    }

    // Utf8JsonReader is resumed at token boundaries. The buffer grows only for a
    // single token, never to the size of the source file or its items array.
    static DrawingNames ReadRecentDrawingNamesStreaming(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        byte[] bom = new byte[3];
        int bomLength = stream.Read(bom, 0, bom.Length);
        if (bomLength != 3 || bom[0] != 0xef || bom[1] != 0xbb || bom[2] != 0xbf) stream.Position = 0;
        byte[] buffer = new byte[64 * 1024];
        int buffered = 0;
        bool final = false;
        var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 64 });
        var projection = new RecentSourceProjection();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!final)
            {
                if (buffered == buffer.Length)
                {
                    if (buffer.Length >= MaxRecentSourceTokenBytes) throw new RecentSourceTooLargeException();
                    Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxRecentSourceTokenBytes));
                }
                int count = stream.Read(buffer, buffered, buffer.Length - buffered);
                if (count == 0) final = true;
                else buffered += count;
            }
            var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), final, state);
            while (reader.Read())
            {
                projection.Accept(ref reader);
                if (projection.Complete) return new DrawingNames(projection.Names, true);
            }
            int consumed = checked((int)reader.BytesConsumed);
            state = reader.CurrentState;
            buffered -= consumed;
            if (buffered != 0 && consumed != 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, buffered);
            if (final) throw new InvalidDataException("来源文件缺少有效的任务信息或图纸列表。");
        }
    }

    sealed class RecentSourceProjection
    {
        readonly HashSet<string> _rootFields = new(StringComparer.OrdinalIgnoreCase);
        string? _rootProperty, _fileProperty, _name, _id;
        bool _rootStarted, _inFiles, _inFile, _schema, _task, _filesComplete, _nameSeen, _idSeen;
        public List<DrawingName> Names { get; } = [];
        public bool Complete => _schema && _task && _filesComplete;

        public void Accept(ref Utf8JsonReader reader)
        {
            var kind = reader.TokenType;
            int depth = reader.CurrentDepth;
            if (!_rootStarted)
            {
                if (kind != JsonTokenType.StartObject || depth != 0) throw new InvalidDataException("来源文件不是 JSON 对象。");
                _rootStarted = true;
                return;
            }
            if (_inFiles)
            {
                if (depth == 1 && kind == JsonTokenType.EndArray)
                {
                    _inFiles = false;
                    _filesComplete = true;
                    return;
                }
                if (depth == 2)
                {
                    if (kind == JsonTokenType.StartObject)
                    {
                        if (Names.Count >= MaxRecentSourceNames) throw new RecentSourceTooLargeException();
                        _inFile = true; _name = null; _id = ""; _nameSeen = false; _idSeen = false; _fileProperty = null;
                        return;
                    }
                    if (kind == JsonTokenType.EndObject && _inFile)
                    {
                        if (string.IsNullOrWhiteSpace(_name)) throw new InvalidDataException("来源中的图纸缺少文件名。");
                        string name = Path.GetFileName(_name);
                        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxRecentSourceNameChars || _id!.Length > MaxRecentSourceNameChars)
                            throw new RecentSourceTooLargeException();
                        Names.Add(new DrawingName(name, _id));
                        _inFile = false;
                        return;
                    }
                    if (!_inFile) throw new InvalidDataException("来源中的图纸记录无效。");
                }
                if (_inFile && depth == 3 && kind == JsonTokenType.PropertyName)
                {
                    _fileProperty = reader.GetString();
                    if (string.Equals(_fileProperty, "name", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_nameSeen) throw new InvalidDataException("来源中的文件名字段重复。");
                        _nameSeen = true;
                    }
                    if (string.Equals(_fileProperty, "id", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_idSeen) throw new InvalidDataException("来源中的文件 ID 字段重复。");
                        _idSeen = true;
                    }
                    return;
                }
                if (_inFile && depth == 3 && _fileProperty != null)
                {
                    if (string.Equals(_fileProperty, "name", StringComparison.OrdinalIgnoreCase))
                    {
                        if (kind != JsonTokenType.String) throw new InvalidDataException("来源中的文件名无效。");
                        _name = reader.GetString();
                    }
                    else if (string.Equals(_fileProperty, "id", StringComparison.OrdinalIgnoreCase))
                    {
                        if (kind != JsonTokenType.String) throw new InvalidDataException("来源中的文件 ID 无效。");
                        _id = reader.GetString() ?? "";
                    }
                    _fileProperty = null;
                }
                return;
            }
            if (depth == 1 && kind == JsonTokenType.PropertyName)
            {
                _rootProperty = reader.GetString();
                if (_rootProperty != null && (string.Equals(_rootProperty, "schema_version", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_rootProperty, "task_id", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_rootProperty, "files", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_rootProperty, "items", StringComparison.OrdinalIgnoreCase)) && !_rootFields.Add(_rootProperty))
                    throw new InvalidDataException("来源文件包含重复的任务字段。");
                return;
            }
            if (depth != 1 || _rootProperty == null) return;
            if (string.Equals(_rootProperty, "schema_version", StringComparison.OrdinalIgnoreCase))
            {
                if (kind != JsonTokenType.Number || !reader.TryGetInt32(out int schema) || schema != 1)
                    throw new InvalidDataException("来源文件的版本无效。");
                _schema = true;
            }
            else if (string.Equals(_rootProperty, "task_id", StringComparison.OrdinalIgnoreCase))
            {
                if (kind != JsonTokenType.String || string.IsNullOrWhiteSpace(reader.GetString()))
                    throw new InvalidDataException("来源文件缺少任务标识。");
                _task = true;
            }
            else if (string.Equals(_rootProperty, "files", StringComparison.OrdinalIgnoreCase))
            {
                if (kind != JsonTokenType.StartArray) throw new InvalidDataException("来源文件缺少图纸列表。");
                _inFiles = true;
            }
            else if (string.Equals(_rootProperty, "items", StringComparison.OrdinalIgnoreCase) && kind != JsonTokenType.StartArray)
                throw new InvalidDataException("来源文件的明细列表无效。");
            _rootProperty = null;
        }
    }
}
