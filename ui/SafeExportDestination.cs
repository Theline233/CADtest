using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CadReviewUI;

// The dialog owns this reservation until publication or cancellation. Existing
// targets are held without write/delete sharing *before* asking to replace them.
internal sealed class SafeExportDestination : IDisposable
{
    private const uint Read = 0x80000000, Write = 0x40000000, Delete = 0x00010000;
    private const uint ReadAttributes = 0x80, Traverse = 0x20, ShareRead = 1, ShareWrite = 2, ShareDelete = 4;
    private const uint OpenExisting = 3, CreateNew = 1, BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000, WriteThrough = 0x80000000;
    private const uint DirectoryAttribute = 0x10, ReparseAttribute = 0x400, ReadOnlyAttribute = 1;
    internal const string OriginalReferenceSha256 = "BDA40BE5B0E3AD2E62A5DC020E8866910159D99664638119F24C12FBD63D05CE";
    private readonly List<SafeFileHandle> _directories = [];
    private SafeFileHandle? _original;
    private readonly Identity? _originalIdentity;
    private readonly string? _originalHash;
    private bool _published;

    internal string Path { get; }
    internal bool Replacing => _original != null;
    // Deterministic race injection, never assigned by the application.
    internal Action? BeforePublish { get; set; }
    internal Action? AfterBackup { get; set; }
    internal Action<string>? BeforeInputVerification { get; set; }
    internal Action? BeforeFinalVerification { get; set; }

    private SafeExportDestination(string path, IEnumerable<string> protectedFiles, string protectedProjectRoot, string referenceSha256)
    {
        string full = ValidatePath(path);
        string parent = System.IO.Path.GetDirectoryName(full) ?? throw new IOException("保存目录无效。");
        try
        {
            LockDirectoryChain(parent);
            string physicalParent = FinalName(_directories[^1]);
            ValidateLocal(physicalParent);
            LockDirectoryChain(physicalParent);
            string logicalRoot = System.IO.Path.GetFullPath(protectedProjectRoot);
            string physicalRoot = ResolveExistingDirectory(logicalRoot);
            Path = System.IO.Path.Combine(physicalParent, System.IO.Path.GetFileName(full));

            foreach (string restricted in new[] { "图纸与要求", "1.一次发货", "2.二次发货" })
            {
                string restrictedPath = System.IO.Path.Combine(logicalRoot, restricted);
                if (IsWithin(full, restrictedPath) || (Directory.Exists(restrictedPath) &&
                    IsWithin(Path, ResolveExistingDirectory(restrictedPath))))
                    throw new IOException("该目录保存原始资料，不能在此导出。");
            }

            if ((IsWithin(full, logicalRoot) || IsWithin(Path, physicalRoot)) &&
                (File.Exists(Path) || Directory.Exists(Path)))
                throw new IOException("项目内的已有文件和成果受到保护，请选择新文件名。");

            _original = TryOpenOriginal(Path);
            if (_original == null) return;
            var info = Info(_original);
            if ((info.FileAttributes & (DirectoryAttribute | ReparseAttribute | ReadOnlyAttribute)) != 0)
                throw new IOException("目标是目录、链接或只读文件，不能覆盖。");
            if (info.NumberOfLinks != 1)
                throw new IOException("目标文件存在硬链接，不能安全覆盖，请选择其他名称。");
            string actual = FinalName(_original);
            if (IsWithin(full, logicalRoot) || IsWithin(actual, physicalRoot))
                throw new IOException("项目内的已有文件和成果受到保护，请选择新文件名。");
            _originalIdentity = IdentityOf(info);
            foreach (string protectedFile in protectedFiles.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                using var protectedHandle = TryOpenForIdentity(protectedFile);
                if (protectedHandle != null && IdentityOf(Info(protectedHandle)) == _originalIdentity)
                    throw new IOException("目标是本任务的原图或复核输入，不能覆盖。");
            }
            _originalHash = Hash(_original);
            if (string.Equals(_originalHash, referenceSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("该文件内容与原始报价模板一致，受到保护；请更换文件名。");
        }
        catch { Dispose(); throw; }
    }

    internal static SafeExportDestination Acquire(string path, IEnumerable<string> protectedFiles, string protectedProjectRoot) =>
        new(path, protectedFiles, protectedProjectRoot, OriginalReferenceSha256);

    // Allows a synthetic file to exercise the same content-protection branch.
    internal static SafeExportDestination AcquireForTesting(string path, IEnumerable<string> protectedFiles, string protectedProjectRoot, string referenceSha256)
    {
        if (referenceSha256.Length != 64 || !referenceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("测试参考哈希无效。", nameof(referenceSha256));
        return new(path, protectedFiles, protectedProjectRoot, referenceSha256);
    }

    internal static string DefaultName(TaskSession task, IReadOnlyDictionary<string, int> included)
    {
        if (included.Count != 1) return "多张图纸_确认明细.xlsx";
        string id = included.Keys.Single();
        var file = task.Files.Single(f => f.Id == id);
        string name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
        if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileNameWithoutExtension(file.Path);
        if (string.IsNullOrWhiteSpace(name)) name = "图纸确认明细";
        name = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd(' ', '.');
        return (name.Length == 0 ? "图纸确认明细" : name) + ".xlsx";
    }

    internal string? Publish(string archiveFolder, string expectedSourceHash, string expectedReviewHash, string expectedTaskId,
        string expectedFormattedHash, string expectedAssociationHash)
    {
        if (_published) throw new InvalidOperationException("该保存选择已经使用。");
        BeforeInputVerification?.Invoke(archiveFolder);
        string source = System.IO.Path.Combine(archiveFolder, "确认明细.xlsx");
        string link = System.IO.Path.Combine(archiveFolder, "报价样式关联.json");
        string records = System.IO.Path.Combine(archiveFolder, "复核记录");
        LockDirectoryChain(archiveFolder);
        LockDirectoryChain(records);
        using var sourceStream = OpenInput(source);
        using var linkStream = OpenInput(link);
        string associationHash = StreamHash(linkStream);
        if (expectedAssociationHash.Length != 64 || !expectedAssociationHash.All(Uri.IsHexDigit) ||
            !string.Equals(associationHash, expectedAssociationHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("关联记录与已独立校验的哈希不一致，未复制。");
        linkStream.Position = 0;
        using var engineStream = OpenInput(System.IO.Path.Combine(records, "确认明细.xlsx"));
        using var auditStream = OpenInput(System.IO.Path.Combine(records, "确认结果.json"));
        using var validationStream = OpenInput(System.IO.Path.Combine(records, "validation.json"));
        string engineHash = StreamHash(engineStream), auditHash = StreamHash(auditStream), validationHash = StreamHash(validationStream);
        using var json = JsonDocument.Parse(linkStream);
        auditStream.Position = 0;
        validationStream.Position = 0;
        using var audit = JsonDocument.Parse(auditStream);
        using var validation = JsonDocument.Parse(validationStream);
        string sourceHash = StreamHash(sourceStream);
        RequireHash(json.RootElement, "formatted_workbook_sha256", sourceHash);
        if (expectedFormattedHash.Length != 64 || !expectedFormattedHash.All(Uri.IsHexDigit) ||
            !string.Equals(sourceHash, expectedFormattedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("格式化工作簿与已独立校验的哈希不一致，未复制。");
        RequireHash(json.RootElement, "engine_workbook_sha256", engineHash);
        RequireHash(json.RootElement, "confirmation_record_sha256", auditHash);
        RequireHash(json.RootElement, "validation_record_sha256", validationHash);
        RequireHash(json.RootElement, "source_sha256", expectedSourceHash);
        RequireHash(json.RootElement, "review_sha256", expectedReviewHash);
        RequireHash(audit.RootElement, "source_sha256", expectedSourceHash);
        RequireHash(audit.RootElement, "review_sha256", expectedReviewHash);
        RequireHash(audit.RootElement, "output_sha256", engineHash);
        RequireHash(validation.RootElement, "manifest_sha256", expectedSourceHash);
        RequireHash(validation.RootElement, "review_sha256", expectedReviewHash);
        if (json.RootElement.GetProperty("kind").GetString() != "quotation_presentation" ||
            audit.RootElement.GetProperty("task_id").GetString() != expectedTaskId ||
            validation.RootElement.GetProperty("task_id").GetString() != expectedTaskId ||
            validation.RootElement.GetProperty("status").GetString() != "ready" ||
            validation.RootElement.GetProperty("pending").GetInt32() != 0)
            throw new InvalidDataException("任务或审计状态不一致，未复制。");
        if (_originalIdentity == IdentityOf(Info(sourceStream.SafeFileHandle)))
            throw new IOException("保存目标与引擎格式化文件相同，不能覆盖。");

        string stagePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, ".cadreview-stage-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var stageHandle = Open(stagePath, Read | Write | Delete, ShareRead, CreateNew, WriteThrough);
        bool renamed = false;
        string? backup = null;
        try
        {
            using (var stage = new FileStream(Duplicate(stageHandle), FileAccess.ReadWrite, 81920, false))
            {
                sourceStream.Position = 0;
                sourceStream.CopyTo(stage);
                stage.Flush(true);
                stage.Position = 0;
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(stage)), sourceHash, StringComparison.Ordinal))
                    throw new IOException("复制后的文件哈希不一致。");

                BeforePublish?.Invoke();
                if (_original != null)
                {
                    using var current = TryOpenForIdentity(Path) ?? throw new IOException("所选文件已消失，未覆盖。");
                    if (IdentityOf(Info(current)) != _originalIdentity || Hash(_original) != _originalHash ||
                        !string.Equals(FinalName(_original), Path, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("所选文件发生变化，未覆盖。");
                    backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!,
                        ".cadreview-backup-" + Guid.NewGuid().ToString("N") + ".bak");
                    Rename(_original, backup);
                    try
                    {
                        // Recheck the held file after moving it out of the target
                        // name as well; any detected change restores it and stops.
                        if (Hash(_original) != _originalHash)
                            throw new IOException("所选原文件发生变化，未覆盖。");
                        AfterBackup?.Invoke();
                        if (Hash(_original) != _originalHash)
                            throw new IOException("所选原文件发生变化，未覆盖。");
                        Rename(stageHandle, Path);
                    }
                    catch (Exception publishError)
                    {
                        try { Rename(_original, Path); }
                        catch (Exception restoreError)
                        {
                            throw new IOException("目标出现并发变化，未覆盖新文件。原文件保存在：" + backup +
                                "；请手动恢复。" , new AggregateException(publishError, restoreError));
                        }
                        throw new IOException("发布失败，原文件已恢复。", publishError);
                    }
                }
                else Rename(stageHandle, Path); // no-replace, including the absent-to-present race
                renamed = true;
                try
                {
                    BeforeFinalVerification?.Invoke();
                    using var finalHandle = Open(Path, Read, ShareRead | ShareWrite | ShareDelete, OpenExisting, OpenReparsePoint);
                    if (IdentityOf(Info(finalHandle)) != IdentityOf(Info(stageHandle)) || Hash(finalHandle) != sourceHash)
                        throw new IOException("文件身份或哈希不一致。");
                }
                catch (Exception ex)
                {
                    throw new IOException("文件已发布，但重新打开或校验失败；请检查所选路径及保留的引擎输出。" +
                        (backup == null ? "" : " 原文件备份：" + backup) + " " + ex.Message, ex);
                }
                _published = true;
                return backup;
            }
        }
        finally
        {
            // Delete only the file held by this handle, never a path that another
            // process could have replaced. The original handle stays open here.
            if (!renamed && !stageHandle.IsClosed && !stageHandle.IsInvalid)
                try { MarkForDeletion(stageHandle); }
                catch (Exception ex) { throw new IOException("无法清除暂存文件：" + stagePath, ex); }
        }
    }

    internal void WriteReceipt(string archiveFolder, string formattedHash, string? backup)
    {
        if (!_published) throw new InvalidOperationException("所选文件尚未通过发布校验。");
        if (formattedHash.Length != 64 || !formattedHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("保存记录的初次哈希无效。");
        string target = System.IO.Path.Combine(archiveFolder, "用户保存记录.json");
        string stage = System.IO.Path.Combine(archiveFolder, ".cadreview-receipt-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var handle = Open(stage, Read | Write | Delete, ShareRead, CreateNew, WriteThrough);
        bool renamed = false;
        try
        {
            using (var stream = new FileStream(Duplicate(handle), FileAccess.ReadWrite, 81920, false))
            {
                JsonSerializer.Serialize(stream, new
                {
                    schema_version = 1,
                    saved_utc = DateTimeOffset.UtcNow,
                    selected_path = Path,
                    initial_formatted_workbook_sha256 = formattedHash.ToUpperInvariant(),
                    previous_file_backup = backup
                });
                stream.Flush(true);
                Rename(handle, target);
                renamed = true;
            }
        }
        finally
        {
            if (!renamed && !handle.IsClosed && !handle.IsInvalid)
                try { MarkForDeletion(handle); }
                catch (Exception ex) { throw new IOException("无法清除保存记录暂存文件：" + stage, ex); }
        }
    }

    private static FileStream OpenInput(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("引擎输出或审计记录是链接，未复制。");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static string StreamHash(FileStream stream)
    {
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void RequireHash(JsonElement item, string property, string expected)
    {
        if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count(p => p.NameEquals(property)) != 1 ||
            !item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not string actual || actual.Length != 64 || !actual.All(Uri.IsHexDigit) ||
            expected.Length != 64 || !expected.All(Uri.IsHexDigit) || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("导出文件、来源或审计哈希不一致，未复制。");
    }

    public void Dispose()
    {
        _original?.Dispose();
        _original = null;
        for (int i = _directories.Count - 1; i >= 0; --i) _directories[i].Dispose();
        _directories.Clear();
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || !System.IO.Path.IsPathFullyQualified(path))
            throw new IOException("请选择本机磁盘上的完整路径；不支持网络共享或映射网络盘。");
        string full = System.IO.Path.GetFullPath(path);
        ValidateLocal(full);
        string name = System.IO.Path.GetFileName(full);
        if (name.Length == 0 || name.EndsWith(' ') || name.EndsWith('.') || name.Contains(':') ||
            name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(System.IO.Path.GetExtension(name), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new IOException("文件名无效，请选择 .xlsx 文件。");
        string stem = name.Split('.')[0];
        if (System.Text.RegularExpressions.Regex.IsMatch(stem, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new IOException("文件名是 Windows 保留名称。");
        return full;
    }

    private static void ValidateLocal(string path)
    {
        string? root = System.IO.Path.GetPathRoot(path);
        if (root == null || root.StartsWith(@"\\", StringComparison.Ordinal) || root.Length != 3 || root[1] != ':')
            throw new IOException("只支持本机磁盘；不支持网络共享或映射网络盘。");
        var type = new DriveInfo(root).DriveType;
        if (type is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("只支持本机磁盘；不支持网络共享或映射网络盘。");
    }

    private void LockDirectoryChain(string path)
    {
        var chain = new Stack<string>();
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent) chain.Push(dir.FullName);
        while (chain.Count != 0)
        {
            string entry = chain.Pop();
            // Hold both the directory entry itself (including a junction) and
            // its resolved directory, so an alias cannot be swapped underneath us.
            _directories.Add(Open(entry, ReadAttributes | Traverse, ShareRead | ShareWrite, OpenExisting, BackupSemantics | OpenReparsePoint));
            _directories.Add(Open(entry, ReadAttributes | Traverse, ShareRead | ShareWrite, OpenExisting, BackupSemantics));
        }
    }

    private static string ResolveExistingDirectory(string path)
    {
        using var handle = Open(path, ReadAttributes, ShareRead | ShareWrite | ShareDelete, OpenExisting, BackupSemantics);
        return FinalName(handle);
    }

    private static bool IsWithin(string path, string root) =>
        string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static SafeFileHandle? TryOpenOriginal(string path)
    {
        var handle = CreateFile(path, Read | Delete | ReadAttributes, ShareRead, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error == 2) return null;
        throw new IOException("不能安全打开保存目标：" + new Win32Exception(error).Message);
    }

    private static SafeFileHandle? TryOpenForIdentity(string path)
    {
        var handle = CreateFile(path, ReadAttributes, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting,
            BackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is 2 or 3) return null;
        throw new IOException("不能确认文件身份：" + new Win32Exception(error).Message);
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint mode, uint flags)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, mode, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("文件操作失败：" + new Win32Exception(error).Message);
    }

    private static string Hash(SafeFileHandle handle)
    {
        // Duplicate the handle so closing the stream cannot close the reservation.
        using var stream = new FileStream(Duplicate(handle), FileAccess.Read, 81920, false);
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static SafeFileHandle Duplicate(SafeFileHandle handle)
    {
        if (!DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out var duplicate, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return duplicate;
    }

    private static FileInfoNative Info(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }
    private readonly record struct Identity(uint Volume, uint High, uint Low);
    private static Identity IdentityOf(FileInfoNative info) => new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);

    private static string FinalName(SafeFileHandle handle)
    {
        var buffer = new System.Text.StringBuilder(32768);
        uint count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        string name = buffer.ToString();
        if (!name.StartsWith(@"\\?\", StringComparison.Ordinal) || name.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("无法确认本机路径；不支持网络共享或映射网络盘。");
        string local = name[4..];
        return local.Length == 3 && local[1] == ':' ? local : local.TrimEnd('\\');
    }

    private static void Rename(SafeFileHandle handle, string target)
    {
        byte[] name = System.Text.Encoding.Unicode.GetBytes(target);
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + 4;
        // SetFileInformationByHandle's Win32 wrapper expects a terminated name
        // even though FileNameLength itself excludes the terminator.
        IntPtr data = Marshal.AllocHGlobal(nameOffset + name.Length + 2);
        try
        {
            for (int i = 0; i < nameOffset; ++i) Marshal.WriteByte(data, i, 0);
            Marshal.WriteInt32(data, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(data, nameOffset), name.Length);
            Marshal.WriteInt16(data, nameOffset + name.Length, 0);
            if (!SetFileInformationByHandle(handle, 3, data, (uint)(nameOffset + name.Length + 2)))
                throw new IOException("目标已存在或无法发布：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    private static void MarkForDeletion(SafeFileHandle handle)
    {
        IntPtr value = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(value, 1);
            if (!SetFileInformationByHandle(handle, 4, value, 1))
                throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        finally { Marshal.FreeHGlobal(value); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoNative
    {
        internal uint FileAttributes, CreationTimeLow, CreationTimeHigh, LastAccessLow, LastAccessHigh,
            LastWriteLow, LastWriteHigh, VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, System.Text.StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr info, uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess,
        out SafeFileHandle target, uint access, bool inherit, uint options);
}
