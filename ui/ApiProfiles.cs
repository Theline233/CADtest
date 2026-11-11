using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace CadReviewUI;

/// <summary>
/// Local profile storage. A null or empty newKey preserves the existing key only
/// while both protocol and base URL are unchanged; clearKey explicitly removes it.
/// Changing either endpoint component without a new key clears the old key.
/// </summary>
public sealed class ApiProfileStore
{
    private const int MaxSettingsBytes = 2 * 1024 * 1024;
    private readonly string directory;
    private readonly string settingsFile;
    private readonly string mutexName;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ApiProfileStore(string? directory = null)
    {
        this.directory = Path.GetFullPath(directory ?? DefaultDirectory());
        settingsFile = Path.Combine(this.directory, "profiles.json");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(settingsFile.ToUpperInvariant()));
        mutexName = @"Local\CADtest.ApiProfiles." + Convert.ToHexString(digest);
    }

    private static string DefaultDirectory()
    {
        return Path.Combine(Engine.OutputsRoot, "api-provider-settings", ApiPrivateStorage.UserHash());
    }

    public IReadOnlyList<ApiProfile> List() => WithLock(() =>
        (IReadOnlyList<ApiProfile>)Read().Profiles.Select(ToPublic).ToArray());

    public string? ActiveId => WithLock(() => Read().ActiveId);

    public ApiProfile Save(ApiProfile profile, string? newKey, bool clearKey, bool setActive)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (clearKey && !string.IsNullOrEmpty(newKey))
            throw new ArgumentException("不能同时替换和清除密钥。");
        if (!string.IsNullOrEmpty(newKey)) ApiSecurity.ValidateKey(newKey);

        // Copy and validate before the first write; never trust the caller's HasKey.
        string id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id;
        ApiSecurity.ValidateId(id);
        string name = ApiSecurity.ValidateName(profile.Name);
        string protocol = ApiSecurity.ValidateProtocol(profile.Protocol);
        string url = ApiSecurity.ValidateBaseUrl(profile.BaseUrl);
        string model = ApiSecurity.ValidateModel(profile.Model);

        return WithLock(() =>
        {
            var settings = Read();
            var old = settings.Profiles.SingleOrDefault(p => p.Id == id);
            if (settings.Profiles.Any(p => p.Id != id && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("配置名称已存在。");
            string? encrypted = null;
            if (!clearKey && !string.IsNullOrEmpty(newKey))
                encrypted = ApiDpapi.Protect(newKey, id, protocol, url);
            else if (!clearKey && old != null && old.Protocol == protocol && old.BaseUrl == url)
                encrypted = old.EncryptedKey;

            var entry = new StoredProfile { Id = id, Name = name, Protocol = protocol, BaseUrl = url, Model = model, EncryptedKey = encrypted };
            if (old != null) settings.Profiles[settings.Profiles.IndexOf(old)] = entry;
            else settings.Profiles.Add(entry);
            if (setActive) settings.ActiveId = id;
            Write(settings);
            if (!clearKey && !string.IsNullOrEmpty(newKey))
            {
                string cipher = encrypted!;
                ApiSecurity.RegisterKeyOrigin(id, newKey, protocol, url, settingsFile, () => IsCurrentKey(id, protocol, url, cipher));
            }
            return ToPublic(entry);
        });
    }

    public void Select(string id) => WithLock(() =>
    {
        ApiSecurity.ValidateId(id);
        var settings = Read();
        if (!settings.Profiles.Any(p => p.Id == id)) throw new KeyNotFoundException("配置不存在。");
        settings.ActiveId = id;
        Write(settings);
        return 0;
    });

    public void Delete(string id) => WithLock(() =>
    {
        ApiSecurity.ValidateId(id);
        var settings = Read();
        var item = settings.Profiles.SingleOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException("配置不存在。");
        settings.Profiles.Remove(item);
        if (settings.ActiveId == id) settings.ActiveId = null;
        Write(settings);
        return 0;
    });

    public string? GetKey(string id) => WithLock(() =>
    {
        ApiSecurity.ValidateId(id);
        var entry = Read().Profiles.SingleOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException("配置不存在。");
        if (entry.EncryptedKey == null) return null;
        string key = ApiDpapi.Unprotect(entry.EncryptedKey, entry.Id, entry.Protocol, entry.BaseUrl);
        ApiSecurity.RegisterKeyOrigin(entry.Id, key, entry.Protocol, entry.BaseUrl, settingsFile,
            () => IsCurrentKey(entry.Id, entry.Protocol, entry.BaseUrl, entry.EncryptedKey));
        return key;
    });

    private bool IsCurrentKey(string id, string protocol, string url, string? cipher)
    {
        try
        {
            return WithLock(() =>
            {
                var p = Read().Profiles.SingleOrDefault(x => x.Id == id);
                return p != null && p.Protocol == protocol && p.BaseUrl == url && p.EncryptedKey == cipher;
            });
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { return false; }
    }

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, mutexName);
        bool held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("配置正被其他进程使用，请稍后重试。");
            using var directoryLease = ApiPrivateStorage.OpenDirectory(directory);
            return action();
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    private Settings Read()
    {
        if (!File.Exists(settingsFile)) return new Settings();
        try
        {
            ApiPrivateStorage.ProtectExistingFile(settingsFile);
            using var file = new FileStream(settingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxSettingsBytes) throw new InvalidDataException("配置文件超过大小限制。");
            var data = JsonSerializer.Deserialize<Settings>(file, JsonOptions) ?? throw new InvalidDataException("配置文件为空。");
            if (data.Version != 1 || data.Profiles == null || data.Profiles.Count > 1000)
                throw new InvalidDataException("配置文件版本或数量无效。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in data.Profiles)
            {
                if (p == null) throw new InvalidDataException("配置内容无效。");
                ApiSecurity.ValidateId(p.Id);
                if (!ids.Add(p.Id) || !names.Add(ApiSecurity.ValidateName(p.Name)) ||
                    ApiSecurity.ValidateProtocol(p.Protocol) != p.Protocol ||
                    ApiSecurity.ValidateBaseUrl(p.BaseUrl) != p.BaseUrl ||
                    ApiSecurity.ValidateModel(p.Model) != p.Model ||
                    (p.EncryptedKey != null && (p.EncryptedKey.Length == 0 || p.EncryptedKey.Length > 32768)))
                    throw new InvalidDataException("配置内容无效。");
            }
            if (data.ActiveId != null && !ids.Contains(data.ActiveId)) throw new InvalidDataException("当前配置不存在。");
            return data;
        }
        catch (JsonException) { throw new InvalidDataException("配置文件无法解析。"); }
        catch (ArgumentException) { throw new InvalidDataException("配置内容无效。"); }
    }

    private void Write(Settings settings)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        if (bytes.Length > MaxSettingsBytes) throw new InvalidDataException("配置文件超过大小限制。");
        string temporary = Path.Combine(directory, ".profiles-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temporary, settingsFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static ApiProfile ToPublic(StoredProfile p) => new()
    {
        Id = p.Id, Name = p.Name, Protocol = p.Protocol, BaseUrl = p.BaseUrl,
        Model = p.Model, HasKey = p.EncryptedKey != null
    };

    private sealed class Settings
    {
        public int Version { get; set; } = 1;
        public string? ActiveId { get; set; }
        public List<StoredProfile> Profiles { get; set; } = new();
    }
    private sealed class StoredProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Protocol { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public string? EncryptedKey { get; set; }
    }
}

/// <summary>Private per-user storage beneath the project's output directory.</summary>
internal static class ApiPrivateStorage
{
    private static SecurityIdentifier CurrentUser()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("配置存储需要 Windows 用户身份。");
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("无法取得当前 Windows 用户 SID。");
    }

    public static string UserHash() => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("CADtest.ApiProfile.User.v1\0" + CurrentUser().Value))).ToLowerInvariant();

    public static string DefaultResultDirectory() => Path.Combine(Engine.OutputsRoot, "api-review-results", UserHash());

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static bool TrustedOwner(SecurityIdentifier owner, SecurityIdentifier user) =>
        owner == user || owner == SystemSid || owner == AdministratorsSid;

    private static DirectorySecurity DirectoryAcl(SecurityIdentifier user, bool setOwner = true)
    {
        var acl = new DirectorySecurity();
        if (setOwner) acl.SetOwner(user);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { user, SystemSid, AdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }

    private static FileSecurity FileAcl(SecurityIdentifier user)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { user, SystemSid, AdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return acl;
    }

    private sealed class DirectoryLease(List<SafeFileHandle> handles) : IDisposable
    {
        public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    // No FILE_SHARE_DELETE: ancestors cannot be renamed/replaced until the lease is released.
    private static SafeFileHandle HoldDirectory(string path, bool privateLeaf = false)
    {
        // Attribute-only handles do not participate in Windows sharing checks.
        const uint DirectoryListAndAttributes = 0x81, ShareRead = 0x1, ShareReadWrite = 0x3, OpenExisting = 3;
        const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        var handle = CreateFile(path, DirectoryListAndAttributes, privateLeaf ? ShareRead : ShareReadWrite, IntPtr.Zero, OpenExisting,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("无法安全锁定私有存储目录。"); }
        return handle;
    }

    public static IDisposable OpenDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("私有存储必须位于本机磁盘。");
        string root = Path.GetPathRoot(full) ?? throw new IOException("私有存储路径无效。");
        if (new DriveInfo(root).DriveType is DriveType.Network or DriveType.NoRootDirectory)
            throw new IOException("私有存储不能使用网络驱动器。");
        string[] parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new IOException("私有存储路径无效。");
        var user = CurrentUser();
        var handles = new List<SafeFileHandle>();
        try
        {
            handles.Add(HoldDirectory(root));
            string current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("私有存储路径不能包含链接或重解析点。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                var directory = new DirectoryInfo(current);
                if (!directory.Exists)
                {
                    if (i == parts.Length - 1) directory.Create(DirectoryAcl(user));
                    else directory.Create();
                }
                handles.Add(HoldDirectory(current));
                directory.Refresh();
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("私有存储路径不能包含链接或重解析点。");
                if (i != parts.Length - 1) continue;
                // Drain pre-existing directory write handles before replacing a legacy ACL.
                // The persistent handle still prevents replacement after this temporary check.
                using var exclusiveLeaf = HoldDirectory(current, privateLeaf: true);
                var existing = directory.GetAccessControl(AccessControlSections.Owner);
                var owner = existing.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                    ?? throw new IOException("无法确认私有存储目录所有者。");
                if (!TrustedOwner(owner, user)) throw new UnauthorizedAccessException("私有存储目录不属于当前用户。");
                // Such old grants can survive as already-open handles after an ACL change.
                var oldAccess = directory.GetAccessControl(AccessControlSections.Access);
                const FileSystemRights dangerous = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.DeleteSubdirectoriesAndFiles;
                foreach (FileSystemAccessRule rule in oldAccess.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                    var sid = (SecurityIdentifier)rule.IdentityReference;
                    if (!TrustedOwner(sid, user) && (rule.FileSystemRights & dangerous) != 0)
                        throw new UnauthorizedAccessException("已有私有目录授予其他用户过高权限，请先安全处理该目录。");
                }
                // Existing owner may have WRITE_DAC without WRITE_OWNER; preserve it.
                directory.SetAccessControl(DirectoryAcl(user, setOwner: false));
            }
            return new DirectoryLease(handles);
        }
        catch
        {
            for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
            throw;
        }
    }

    public static void ProtectExistingFile(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        if (!file.Exists) return;
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("私有存储文件不能是链接。");
        var user = CurrentUser();
        var existing = file.GetAccessControl(AccessControlSections.Owner);
        var owner = existing.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new IOException("无法确认私有存储文件所有者。");
        if (!TrustedOwner(owner, user)) throw new UnauthorizedAccessException("私有存储文件不属于当前用户。");
        file.SetAccessControl(FileAcl(user));
    }
}

internal static class ApiSecurity
{
    // Best effort only: arbitrary transformations of a secret cannot be identified reliably.
    public static string Redact(string? value, string? key)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (string.IsNullOrEmpty(key)) return value;
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
        string urlBase64 = base64.Replace('+', '-').Replace('/', '_');
        var variants = new[] { key, base64, base64.TrimEnd('='), urlBase64, urlBase64.TrimEnd('=') }
            .Where(s => s.Length != 0).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length);
        foreach (string variant in variants)
            value = value.Replace(variant, "[密钥已隐藏]", StringComparison.Ordinal);
        string escaped = Uri.EscapeDataString(key);
        if (!string.Equals(escaped, key, StringComparison.Ordinal))
            value = value.Replace(escaped, "[密钥已隐藏]", StringComparison.OrdinalIgnoreCase);
        return value;
    }

    // Associate a key released by the store with its saved endpoint even when a
    // caller later mutates the returned profile object without saving it first.
    private sealed record KeyBinding(string Origin, Func<bool> IsCurrent);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, KeyBinding>> KeyOrigins = new();
    private static readonly ConcurrentDictionary<string, byte> KnownKeys = new();
    private static string KeyFingerprint(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    private static string KeyIdentity(string id, string key) => id + ":" + KeyFingerprint(key);
    private static string Origin(string protocol, string url) => protocol + "\0" + url;
    public static void RegisterKeyOrigin(string id, string key, string protocol, string url, string storePath, Func<bool> isCurrent)
    {
        KnownKeys.TryAdd(KeyFingerprint(key), 0);
        KeyOrigins.GetOrAdd(KeyIdentity(id, key), _ => new ConcurrentDictionary<string, KeyBinding>(StringComparer.OrdinalIgnoreCase))[storePath] = new KeyBinding(Origin(protocol, url), isCurrent);
    }
    public static void ValidateKeyOrigin(string id, string key, string protocol, string url)
    {
        if (KnownKeys.ContainsKey(KeyFingerprint(key)) &&
            (!KeyOrigins.TryGetValue(KeyIdentity(id, key), out var origins) ||
             !origins.Values.Any(binding => binding.Origin == Origin(protocol, url) && binding.IsCurrent())))
            throw new InvalidOperationException("此密钥属于其他服务地址或协议；请先保存配置并明确重新输入密钥。");
    }
    public static void ValidateId(string? id)
    {
        if (id == null || id.Length is < 1 or > 128 || !Regex.IsMatch(id, @"\A[A-Za-z0-9_-]+\z"))
            throw new ArgumentException("配置 ID 无效。");
    }
    public static string ValidateName(string? name)
    {
        string value = name?.Trim() ?? "";
        if (value.Length is < 1 or > 120 || value.Any(char.IsControl)) throw new ArgumentException("请填写有效的配置名称。");
        return value;
    }
    public static string ValidateProtocol(string? protocol)
    {
        if (protocol != "responses" && protocol != "chat_completions") throw new ArgumentException("不支持此 API 协议。");
        return protocol;
    }
    public static string ValidateModel(string? model)
    {
        string value = model?.Trim() ?? "";
        if (value.Length is < 1 or > 256 || value.Any(char.IsControl)) throw new ArgumentException("请填写有效的模型名称。");
        return value;
    }
    public static void ValidateKey(string key)
    {
        if (key.Length > 8192 || !Regex.IsMatch(key, @"\A[A-Za-z0-9._~+/-]+=*\z"))
            throw new ArgumentException("密钥格式无效；请检查是否包含空格或换行。");
    }
    public static string ValidateBaseUrl(string? input)
    {
        string value = input?.Trim() ?? "";
        if (value.Length is < 1 or > 2048 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
            value.Contains('\\') || value.Contains('?') || value.Contains('#') || !value.Contains("://", StringComparison.Ordinal) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("基础地址无效：请填写不含认证信息、查询参数或片段的 HTTPS 地址。");
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(uri.Host))
            throw new ArgumentException("HTTP 仅允许本机回环地址。");
        // Examine the original path before Uri normalizes dot segments. Encoded
        // separators or percent signs could become traversal after server decoding.
        int authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        int pathStart = value.IndexOf('/', authorityStart);
        string originalPath = pathStart < 0 ? "" : value[pathStart..];
        if (Regex.IsMatch(originalPath, @"%(?![0-9A-Fa-f]{2})") ||
            Regex.IsMatch(originalPath, @"%(?:2[fF]|5[cC]|25)"))
            throw new ArgumentException("基础地址路径包含不安全的编码或分隔符。");
        foreach (string segment in originalPath.Split('/'))
        {
            string decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".." || decoded.Any(char.IsControl) || decoded.Contains('\\') || decoded.Contains('/'))
                throw new ArgumentException("基础地址路径包含不安全的目录或分隔符。");
        }
        string path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        if (path.Equals("responses", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("chat/completions", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请填写基础地址，不要包含 /responses 或 /chat/completions 接口路径。");
        return uri.GetLeftPart(UriPartial.Authority) + (path.Length == 0 ? "" : "/" + path);
    }
    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) return false;
        return IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }
}

internal static class ApiDpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob data, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob result);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob result);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string key, string id, string protocol, string url)
    {
        byte[] plaintext = Encoding.UTF8.GetBytes(key);
        try { return Convert.ToBase64String(Transform(plaintext, Entropy(id, protocol, url), true)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public static string Unprotect(string cipher, string id, string protocol, string url)
    {
        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(cipher); }
        catch (FormatException) { throw new CryptographicException("已保存的密钥无效。"); }
        byte[] plaintext = Transform(encrypted, Entropy(id, protocol, url), false);
        try
        {
            string key = new UTF8Encoding(false, true).GetString(plaintext);
            ApiSecurity.ValidateKey(key);
            return key;
        }
        catch (Exception e) when (e is DecoderFallbackException or ArgumentException)
        { throw new CryptographicException("已保存的密钥无效。"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    private static byte[] Entropy(string id, string protocol, string url) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("CADtest.ApiProfile.v1\0" + id + "\0" + protocol + "\0" + url));

    private static byte[] Transform(byte[] input, byte[] entropyBytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("密钥存储需要 Windows 当前用户 DPAPI。");
        Blob data = new() { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        Blob entropy = new() { Length = entropyBytes.Length, Data = Marshal.AllocHGlobal(entropyBytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(input, 0, data.Data, input.Length);
            Marshal.Copy(entropyBytes, 0, entropy.Data, entropyBytes.Length);
            bool success = protect
                ? CryptProtectData(ref data, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref data, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success || output.Length <= 0 || output.Length > 32768) throw new CryptographicException("无法使用当前 Windows 用户加密或解密密钥。");
            byte[] result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (data.Data != IntPtr.Zero) { for (int i = 0; i < data.Length; i++) Marshal.WriteByte(data.Data, i, 0); Marshal.FreeHGlobal(data.Data); }
            if (entropy.Data != IntPtr.Zero) Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero) { for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
}
