using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace CadReviewUI;

/// <summary>A validated image snapshot used both for preview and transmission.</summary>
public sealed class ApiImageSelection
{
    private readonly byte[] bytes;
    internal ApiImageSelection(string path, byte[] bytes, string mime, ApiImageIdentity identity)
    {
        Path = path;
        this.bytes = bytes;
        Mime = mime;
        Identity = identity;
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
    public string Path { get; }
    public string Sha256 { get; }
    internal string Mime { get; }
    internal ApiImageIdentity Identity { get; }
    internal byte[] Bytes => bytes;
    public Stream OpenPreviewStream() => new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: false);
}

internal readonly record struct ApiImageIdentity(uint Volume, ulong FileId, ulong Length, ulong LastWrite);

public sealed class ApiImageChangedException : IOException
{
    public ApiImageChangedException() : base("图片已变化或无法安全读取，请重新选择。") { }
}

/// <summary>
/// Advisory calls only. The default transport does not follow redirects or use a proxy.
/// A supplied HttpClient is a trusted test/host transport: its owner must disable
/// redirects, proxying and implicit credentials before passing it here.
/// Save a changed profile before obtaining its key from ApiProfileStore.
/// </summary>
public sealed class ApiGateway
{
    public const int MaxImageBytes = 12 * 1024 * 1024;
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    private const long MaxPixels = 16_000_000;
    private const string TestPrompt = "Reply with OK.";
    private const string ReviewPrompt = "请仅根据这张图片逐项读取图号、名称、材质、数量、单位和工艺。每项保留图片上可见的原文和大致位置；看不清的内容标注不确定，没有标注的内容写图中未见。不要猜测、补全或自动计算，也不要沿用本地识别或人工复核的答案。结果仅供人工复核。";
    private static readonly HttpClient DefaultClient = CreateDefaultClient();
    private readonly HttpClient client;
    private readonly bool injected;

    public ApiGateway(HttpClient? client = null)
    {
        if (client != null) ValidateInjectedClient(client);
        injected = client != null;
        this.client = client ?? DefaultClient;
    }

    private static void ValidateInjectedClient(HttpClient client)
    {
        // HttpClient has no public handler accessor. Fail closed if the runtime
        // cannot expose it; known transports must be configured safely. A custom
        // terminal mock/handler is trusted code controlled by the caller.
        var field = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(client) is not HttpMessageHandler handler)
            throw new InvalidOperationException("无法验证注入的 HttpClient，请提供禁用重定向和代理的可信客户端。");
        var visited = new HashSet<HttpMessageHandler>(ReferenceEqualityComparer.Instance);
        while (true)
        {
            if (!visited.Add(handler)) throw new InvalidOperationException("注入的 HttpClient 处理链无效。");
            if (handler is DelegatingHandler chain)
            {
                handler = chain.InnerHandler ?? throw new InvalidOperationException("注入的 HttpClient 处理链不完整。");
                continue;
            }
            if (handler is HttpClientHandler standard &&
                (standard.AllowAutoRedirect || standard.UseProxy || standard.UseCookies || standard.Credentials != null || standard.UseDefaultCredentials || standard.ServerCertificateCustomValidationCallback != null))
                throw new InvalidOperationException("注入的 HttpClient 必须禁用重定向、代理、Cookie、默认认证和自定义证书验证。");
            if (handler is SocketsHttpHandler sockets &&
                (sockets.AllowAutoRedirect || sockets.UseProxy || sockets.UseCookies || sockets.Credentials != null || sockets.SslOptions.RemoteCertificateValidationCallback != null))
                throw new InvalidOperationException("注入的 HttpClient 必须禁用重定向、代理、Cookie、默认认证和自定义证书验证。");
            return;
        }
    }

    public Task<ApiCallResult> TestAsync(ApiProfile profile, string? apiKey, CancellationToken ct) =>
        CallAsync(profile, apiKey, null, ct);

    // Only the settings editor uses this path for a key explicitly typed for this test.
    internal Task<ApiCallResult> TestWithExplicitKeyAsync(ApiProfile profile, string apiKey, CancellationToken ct) =>
        CallAsync(profile, apiKey, null, ct, explicitlyEnteredKey: true);

    public async Task<ApiCallResult> ReviewAsync(ApiProfile profile, string? apiKey, string imagePath, CancellationToken ct)
    {
        var selected = await SelectImageAsync(imagePath ?? throw new ArgumentNullException(nameof(imagePath)), ct).ConfigureAwait(false);
        return await ReviewSelectedAsync(profile, apiKey, selected, ct).ConfigureAwait(false);
    }

    public Task<ApiCallResult> ReviewSelectedAsync(ApiProfile profile, string? apiKey, ApiImageSelection selected, CancellationToken ct) =>
        CallAsync(profile, apiKey, selected ?? throw new ArgumentNullException(nameof(selected)), ct);

    private async Task<ApiCallResult> CallAsync(ApiProfile profile, string? apiKey, ApiImageSelection? selected, CancellationToken ct, bool explicitlyEnteredKey = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string protocol = ApiSecurity.ValidateProtocol(profile.Protocol);
        string baseUrl = ApiSecurity.ValidateBaseUrl(profile.BaseUrl);
        string model = ApiSecurity.ValidateModel(profile.Model);
        if (!string.IsNullOrEmpty(apiKey))
        {
            ApiSecurity.ValidateId(profile.Id);
            ApiSecurity.ValidateKey(apiKey);
            if (!explicitlyEnteredKey) ApiSecurity.ValidateKeyOrigin(profile.Id, apiKey, protocol, baseUrl);
        }
        if (injected) ValidateInjectedClient(client);
        if (client.DefaultRequestHeaders.Any())
            throw new InvalidOperationException("注入的 HttpClient 不允许预设请求头；认证由网关按当前配置设置。");
        var endpoint = new Uri(baseUrl + (protocol == "responses" ? "/responses" : "/chat/completions"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(selected == null ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(120));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            // Keep a read-only handle open through the request, while sending the exact preview snapshot.
            await using FileStream? imageHandle = selected == null ? null : OpenSelectedForVerification(selected);
            if (imageHandle != null) await VerifySelectionAsync(imageHandle, selected!, deadline.Token).ConfigureAwait(false);
            ImageData? image = selected == null ? null : new ImageData("data:" + selected.Mime + ";base64," + Convert.ToBase64String(selected.Bytes));
            byte[] requestJson = BuildRequest(protocol, model, image);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            // Avoid reused-connection replay and HTTP/2 stream retry behavior.
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.ConnectionClose = true;
            if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new ByteArrayContent(requestJson);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            HttpResponseMessage received;
            try { received = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is not OutOfMemoryException)
            { throw new InvalidOperationException("网络请求失败，请检查连接和服务地址。未自动重试。"); }
            using var response = received;
            if (response.RequestMessage?.RequestUri != null && response.RequestMessage.RequestUri != endpoint)
                throw new InvalidOperationException("检测到请求目标改变；请检查注入的 HttpClient 是否禁用重定向。");
            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
                throw new InvalidOperationException($"服务端返回 HTTP {status} 重定向，已停止请求。");
            if (!response.IsSuccessStatusCode)
            {
                if (status is 400 or 422)
                {
                    string limitParameter = protocol == "responses" ? "max_output_tokens" : "max_tokens";
                    throw new InvalidOperationException($"服务端拒绝请求（HTTP {status}）；请检查模型、输入及接口是否支持 {limitParameter} 参数。");
                }
                throw new InvalidOperationException($"服务端请求失败（HTTP {status}）。");
            }
            byte[] body = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            return ParseResponse(body, protocol, apiKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { throw new OperationCanceledException("请求已取消。", ct); }
        catch (OperationCanceledException)
        { throw new TimeoutException("请求超时，未自动重试。服务端可能仍在处理该请求。"); }
    }

    private static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // Never resolve 'localhost' through a configurable name server or hosts entry.
            ConnectCallback = async (context, cancellationToken) =>
            {
                Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    EndPoint destination = string.Equals(context.DnsEndPoint.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                        ? new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port)
                        : context.DnsEndPoint;
                    await socket.ConnectAsync(destination, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static byte[] BuildRequest(string protocol, string model, ImageData? image)
    {
        object payload;
        if (protocol == "responses")
        {
            object[] content = image == null
                ? new object[] { new { type = "input_text", text = TestPrompt } }
                : new object[] { new { type = "input_text", text = ReviewPrompt }, new { type = "input_image", image_url = image.DataUrl } };
            payload = new { model, store = false, max_output_tokens = image == null ? 256 : 2048, input = new[] { new { role = "user", content } } };
        }
        else
        {
            object[] content = image == null
                ? new object[] { new { type = "text", text = TestPrompt } }
                : new object[] { new { type = "text", text = ReviewPrompt }, new { type = "image_url", image_url = new { url = image.DataUrl } } };
            payload = new { model, max_tokens = image == null ? 256 : 2048, messages = new[] { new { role = "user", content } } };
        }
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("服务端响应超过大小限制。");
        try
        {
            await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaxResponseBytes) throw new InvalidDataException("服务端响应超过大小限制。");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new InvalidOperationException("读取服务端响应失败。未自动重试。"); }
    }

    private static ApiCallResult ParseResponse(byte[] data, string protocol, string? key)
    {
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("服务端响应格式无效。");
            string? returnedModel = GetString(root, "model");
            var pieces = new List<string>();
            bool truncated = false;
            if (protocol == "responses")
            {
                string? state = GetString(root, "status");
                if (state != null && state != "completed") throw new InvalidDataException("模型响应未完成或失败。");
                if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                    foreach (var item in output.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("content", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
                        foreach (var part in parts.EnumerateArray())
                            if (part.ValueKind == JsonValueKind.Object && GetString(part, "type") == "output_text" && GetString(part, "text") is string text) pieces.Add(text);
                    }
                if (pieces.Count == 0 && GetString(root, "output_text") is string aggregate) pieces.Add(aggregate);
            }
            else if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                string? finish = GetString(first, "finish_reason");
                if (finish == "content_filter") throw new InvalidDataException("模型输出被服务端过滤。");
                truncated = finish == "length";
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String) pieces.Add(content.GetString() ?? "");
                    else if (content.ValueKind == JsonValueKind.Array)
                        foreach (var part in content.EnumerateArray())
                            if (part.ValueKind == JsonValueKind.Object && GetString(part, "type") == "text" && GetString(part, "text") is string text) pieces.Add(text);
                }
            }
            string combined = string.Concat(pieces);
            if (string.IsNullOrWhiteSpace(combined)) throw new InvalidDataException("服务端没有返回可供人工审查的文本。");
            if (truncated) combined = "[模型输出达到长度限制，以下内容可能不完整]\n" + combined;
            JsonElement usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
            return new ApiCallResult
            {
                Text = Redact(combined, key), ReturnedModel = returnedModel == null ? null : Redact(returnedModel, key),
                InputTokens = GetLong(usage, protocol == "responses" ? "input_tokens" : "prompt_tokens"),
                OutputTokens = GetLong(usage, protocol == "responses" ? "output_tokens" : "completion_tokens"),
                Note = truncated ? "输出达到长度限制，内容可能不完整；结果仅供人工审查。" : "结果仅供人工审查，不会修改本地复核或导出。"
            };
        }
        catch (JsonException) { throw new InvalidDataException("服务端响应不是有效的 JSON。"); }
    }

    private static string Redact(string value, string? key) => ApiSecurity.Redact(value, key);
    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? GetLong(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) && n >= 0 ? n : null;

    private sealed record ImageData(string DataUrl);

    public static async Task<ApiImageSelection> SelectImageAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var file = OpenLocalImage(path);
            var identity = GetImageIdentity(file.SafeFileHandle);
            byte[] bytes = await ReadImageBytesAsync(file, ct).ConfigureAwait(false);
            if (identity != GetImageIdentity(file.SafeFileHandle)) throw new InvalidDataException("读取期间图片发生变化，请重新选择。");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string mime;
            if (ext == ".png" && bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            { ValidatePng(bytes, ct); ValidateWic(bytes, "png", ct); mime = "image/png"; }
            else if ((ext == ".jpg" || ext == ".jpeg") && bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff }))
            { ValidateWic(bytes, "jpeg", ct); mime = "image/jpeg"; }
            else if (ext == ".webp")
            { ValidateWebp(bytes); ValidateWic(bytes, "webp", ct); mime = "image/webp"; }
            else throw new InvalidDataException("图片扩展名与实际内容不匹配。");
            return new ApiImageSelection(Path.GetFullPath(path), bytes, mime, identity);
        }
        catch (InvalidDataException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw new InvalidDataException("无法读取或验证所选本机图片。"); }
    }

    private static FileStream OpenLocalImage(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
            throw new InvalidDataException("请选择本机绝对路径的图片。");
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? throw new InvalidDataException("图片路径无效。");
        if (new DriveInfo(root).DriveType is DriveType.Network or DriveType.NoRootDirectory)
            throw new InvalidDataException("仅允许本机图片，不允许网络驱动器。");
        string ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp")) throw new InvalidDataException("仅支持 PNG、JPEG 和 WebP 图片。");
        string current = root;
        foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("图片路径不能包含链接或重解析点，请选择本机原文件。");
        }
        var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            EnsureLocalHandle(file.SafeFileHandle);
            if (file.Length <= 0 || file.Length > MaxImageBytes) throw new InvalidDataException("图片为空或超过 12 MiB 限制。");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static async Task<byte[]> ReadImageBytesAsync(FileStream file, CancellationToken ct)
    {
        byte[] bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        if (file.ReadByte() != -1) throw new InvalidDataException("读取期间图片大小发生变化。");
        return bytes;
    }

    private static FileStream OpenSelectedForVerification(ApiImageSelection selected)
    {
        try { return OpenLocalImage(selected.Path); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw new ApiImageChangedException(); }
    }

    private static async Task VerifySelectionAsync(FileStream file, ApiImageSelection selected, CancellationToken ct)
    {
        try
        {
            if (GetImageIdentity(file.SafeFileHandle) != selected.Identity) throw new ApiImageChangedException();
            byte[] bytes = await ReadImageBytesAsync(file, ct).ConfigureAwait(false);
            if (GetImageIdentity(file.SafeFileHandle) != selected.Identity || !bytes.AsSpan().SequenceEqual(selected.Bytes))
                throw new ApiImageChangedException();
        }
        catch (OperationCanceledException) { throw; }
        catch (ApiImageChangedException) { throw; }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw new ApiImageChangedException(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public NativeFileTime CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerial, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    private static ApiImageIdentity GetImageIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("无法确认图片身份。");
        return new ApiImageIdentity(info.VolumeSerial, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow, ((ulong)info.LastWriteTime.High << 32) | info.LastWriteTime.Low);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    private static void EnsureLocalHandle(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本机图片验证需要 Windows。");
        var result = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, result, (uint)result.Capacity, 0);
        if (length == 0 || length >= result.Capacity) throw new InvalidDataException("无法确认图片来源。");
        string finalPath = result.ToString();
        if (!finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) || finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("仅允许本机图片，不允许网络路径。");
        string? root = Path.GetPathRoot(finalPath.Substring(4));
        if (root == null || new DriveInfo(root).DriveType == DriveType.Network) throw new InvalidDataException("仅允许本机图片，不允许网络驱动器。");
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height > MaxPixels)
            throw new InvalidDataException("图片尺寸超过限制（最多 1600 万像素）。");
    }

    // WIC can expose only the first APNG frame; reject animation chunks before preview or sending.
    private static void ValidatePng(byte[] data, CancellationToken ct)
    {
        int offset = 8, count = 0;
        bool ended = false;
        while (offset < data.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 65536 || data.Length - offset < 12) throw new InvalidDataException("PNG 分块无效或过多。");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            long end = (long)offset + 12 + length;
            if (end > data.Length) throw new InvalidDataException("PNG 分块长度无效。");
            var type = data.AsSpan(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8))
                throw new InvalidDataException("不支持动画 PNG，请选择静态截图。");
            if (count == 1 && (!type.SequenceEqual("IHDR"u8) || length != 13))
                throw new InvalidDataException("PNG 文件头无效。");
            if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || end != data.Length) throw new InvalidDataException("PNG 结尾无效。");
                ended = true;
                break;
            }
            offset = checked((int)end);
        }
        if (!ended) throw new InvalidDataException("PNG 缺少结束分块。");
    }

    private static void ValidateWic(byte[] data, string expected, CancellationToken ct)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
            if ((expected == "png" && decoder is not PngBitmapDecoder) || (expected == "jpeg" && decoder is not JpegBitmapDecoder) || decoder.Frames.Count != 1)
                throw new InvalidDataException("图片格式与内容不匹配。");
            var frame = decoder.Frames[0];
            ValidateDimensions(frame.PixelWidth, frame.PixelHeight);
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int stride = checked(bitmap.PixelWidth * 4);
            byte[] strip = new byte[checked(stride * Math.Min(64, bitmap.PixelHeight))];
            for (int y = 0; y < bitmap.PixelHeight; y += 64)
            {
                ct.ThrowIfCancellationRequested();
                bitmap.CopyPixels(new Int32Rect(0, y, bitmap.PixelWidth, Math.Min(64, bitmap.PixelHeight - y)), strip, stride, 0);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException) { throw; }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException or OverflowException or FormatException or COMException)
        { throw new InvalidDataException("图片内容损坏或无法解码。"); }
    }

    // Validate RIFF framing, VP8/VP8L keyframe headers and dimensions first;
    // ValidateWic then decodes the image and fails closed if no codec is installed.
    private static void ValidateWebp(byte[] data)
    {
        if (data.Length < 20 || !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !data.AsSpan(8, 4).SequenceEqual("WEBP"u8) || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) != data.Length - 8)
            throw new InvalidDataException("WebP 文件头或长度无效。");
        int offset = 12, count = 0, width = 0, height = 0, canvasW = 0, canvasH = 0, pictures = 0;
        bool extended = false;
        while (offset < data.Length)
        {
            if (++count > 1024 || data.Length - offset < 8) throw new InvalidDataException("WebP 分块无效。");
            var type = data.AsSpan(offset, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            long end = (long)offset + 8 + size + (size & 1);
            if (end > data.Length) throw new InvalidDataException("WebP 分块长度无效。");
            int start = offset + 8;
            if (type.SequenceEqual("VP8X"u8))
            {
                if (extended || count != 1 || size != 10 || (data[start] & 0xc3) != 0 || data[start + 1] != 0 || data[start + 2] != 0 || data[start + 3] != 0)
                    throw new InvalidDataException("不支持动画或无效的 WebP。");
                extended = true;
                canvasW = 1 + data[start + 4] + (data[start + 5] << 8) + (data[start + 6] << 16);
                canvasH = 1 + data[start + 7] + (data[start + 8] << 8) + (data[start + 9] << 16);
                ValidateDimensions(canvasW, canvasH);
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                if (++pictures != 1 || size < 11 || (data[start] & 1) != 0 || data[start + 3] != 0x9d || data[start + 4] != 0x01 || data[start + 5] != 0x2a)
                    throw new InvalidDataException("WebP 图像内容无效。");
                uint tag = (uint)(data[start] | data[start + 1] << 8 | data[start + 2] << 16);
                if ((tag >> 5) > size - 10) throw new InvalidDataException("WebP 图像数据不完整。");
                width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start + 6, 2)) & 0x3fff;
                height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start + 8, 2)) & 0x3fff;
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                if (++pictures != 1 || size < 6 || data[start] != 0x2f) throw new InvalidDataException("WebP 图像内容无效。");
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start + 1, 4));
                if ((bits >> 29) != 0) throw new InvalidDataException("WebP 图像版本无效。");
                width = (int)(bits & 0x3fff) + 1; height = (int)((bits >> 14) & 0x3fff) + 1;
            }
            else if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8)) throw new InvalidDataException("不支持动画 WebP。");
            offset = (int)end;
        }
        if (offset != data.Length || pictures != 1 || (extended && (canvasW != width || canvasH != height))) throw new InvalidDataException("WebP 图像结构无效。");
        ValidateDimensions(width, height);
    }
}
