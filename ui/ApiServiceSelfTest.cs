using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;

namespace CadReviewUI;

/// <summary>Synthetic local tests only. Return 0 on success, 1 on failure.</summary>
public static class ApiServiceSelfTest
{
    private const string Key = "dummy-test-key-83Q4km5";
    private const string Replacement = "dummy-replacement-key-42jZ";
    private const string WireKey = "dummy-wire-key-67tY";
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAIAAAASFvFNAAAAFElEQVR4nGNUjlrFAAZMEIqBgQEAFHMBK94VxV4AAAAASUVORK5CYII=";
    // Synthetic 2x2 APNG with two fcTL frames; independently decoded as two frames.
    private const string PngTwoFrames = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAACGFjVEwAAAACAAAAAPONk3AAAAAaZmNUTAAAAAAAAAACAAAAAgAAAAAAAAAAAAEACgAA6FTcAAAAAAFzUkdCAK7OHOkAAAAEZ0FNQQAAsY8L/GEFAAAACXBIWXMAAA7DAAAOwwHHb6hkAAAAFElEQVQYV2P8z8Dwn4GBgYGJAQoAHxcCAh1+kfcAAAAaZmNUTAAAAAEAAAACAAAAAgAAAAAAAAAAAAEACgAAcyc21AAAABhmZEFUAAAAAhhXY/zPwPCfgYGBgYkBCgAfFwICwMRh4QAAAABJRU5ErkJggg==";
    private const string Webp = "UklGRh4AAABXRUJQVlA4TBEAAAAvAkAAAAdQrY5Utf+BiOh/AAA=";
    private const string WebpLossy = "UklGRjgAAABXRUJQVlA4ICwAAADwAQCdASoDAAIAAUAmJaACdLoB+AAF9AAA/vFNr/z0zXmD/Jz/5BaVy3dgAA==";

    public static async Task<int> RunAsync(string outputDirectory, CancellationToken ct = default)
    {
        string root = Path.GetFullPath(Engine.OutputsRoot) + Path.DirectorySeparatorChar;
        string dir = Path.GetFullPath(outputDirectory);
        if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Directory.Exists(dir) || File.Exists(dir))
            throw new ArgumentException("自测输出必须是 outputs 下尚不存在的新目录。");
        Directory.CreateDirectory(dir);
        var results = new List<TestResult>();
        async Task Check(string name, Func<Task> action)
        {
            ct.ThrowIfCancellationRequested();
            try { await action().ConfigureAwait(false); results.Add(new(name, true, null)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                string detail = ApiSecurity.Redact(ApiSecurity.Redact(ApiSecurity.Redact(e.Message, Key), Replacement), WireKey);
                results.Add(new(name, false, e.GetType().Name + ": " + detail));
            }
        }
        string png = Path.Combine(dir, "ApiServiceSynthetic.png"), jpeg = Path.Combine(dir, "ApiServiceSynthetic.jpg");
        string webp = Path.Combine(dir, "ApiServiceSynthetic.webp"), lossy = Path.Combine(dir, "ApiServiceSyntheticLossy.webp");
        await File.WriteAllBytesAsync(png, Convert.FromBase64String(Png), ct);
        await File.WriteAllBytesAsync(webp, Convert.FromBase64String(Webp), ct);
        await File.WriteAllBytesAsync(lossy, Convert.FromBase64String(WebpLossy), ct);
        using (var source = new MemoryStream(Convert.FromBase64String(Png)))
        using (var output = new MemoryStream())
        {
            var frame = BitmapDecoder.Create(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame)); encoder.Save(output);
            await File.WriteAllBytesAsync(jpeg, output.ToArray(), ct);
        }

        await Check("default directory isolated by Windows SID", () =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            string sid = identity.User?.Value ?? throw new Exception("SID unavailable");
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("CADtest.ApiProfile.User.v1\0" + sid))).ToLowerInvariant();
            string? actual = typeof(ApiProfileStore).GetField("directory", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(new ApiProfileStore()) as string;
            Require(actual == Path.Combine(Engine.OutputsRoot, "api-provider-settings", hash), "SID directory mismatch");
            return Task.CompletedTask;
        });
        await Check("DPAPI, same-user reopen, no-key profile and selection", () =>
        {
            string storage = Path.Combine(dir, "ApiServiceProfiles"); var store = new ApiProfileStore(storage);
            var a = store.Save(Profile("Alpha", "http://127.0.0.1:12341/v1"), Key, false, true);
            var b = store.Save(Profile("Beta", "http://127.0.0.1:12342/v1"), null, false, false);
            Require(a.HasKey && !b.HasKey && store.ActiveId == a.Id && store.GetKey(b.Id) == null, "profile state incorrect");
            Require(!File.ReadAllText(Path.Combine(storage, "profiles.json")).Contains(Key), "plaintext in settings");
            var reopened = new ApiProfileStore(storage);
            Require(reopened.List().Count == 2 && reopened.GetKey(a.Id) == Key, "same-user reopen failed");
            reopened.Select(b.Id); Require(store.ActiveId == b.Id, "selection not saved");
            reopened.Delete(b.Id); Require(reopened.ActiveId == null && reopened.List().Count == 1, "delete failed");
            return Task.CompletedTask;
        });
        await Check("profile storage ACL permits only current user and administrators", () =>
        {
            string storage = Path.Combine(dir, "ApiServicePrivateAcl");
            var store = new ApiProfileStore(storage);
            store.Save(Profile("acl", "http://127.0.0.1:12343/v1"), Key, false, false);
            RequirePrivateAcl(storage, directory: true);
            RequirePrivateAcl(Path.Combine(storage, "profiles.json"), directory: false);
            return Task.CompletedTask;
        });
        await Check("key preserve, replace, clear and conflicting action", () =>
        {
            var store = new ApiProfileStore(Path.Combine(dir, "ApiServiceKeyActions"));
            var p = store.Save(Profile("actions", "https://example.com/v1"), Key, false, true);
            p.Model = "other-model"; p = store.Save(p, null, false, false);
            Require(store.GetKey(p.Id) == Key && p.HasKey, "null did not preserve key");
            p = store.Save(p, "", false, false); Require(store.GetKey(p.Id) == Key, "empty did not preserve key");
            p = store.Save(p, Replacement, false, false); Require(store.GetKey(p.Id) == Replacement, "replacement failed");
            Expect<ArgumentException>(() => store.Save(p, Key, true, false));
            p = store.Save(p, null, true, false); Require(!p.HasKey && store.GetKey(p.Id) == null, "clear failed");
            return Task.CompletedTask;
        });
        await Check("normalized scheme, host, port, path and protocol bind the key", () =>
        {
            var store = new ApiProfileStore(Path.Combine(dir, "ApiServiceOrigins"));
            var same = store.Save(Profile("canonical", "https://EXAMPLE.com:443/v1/"), Key, false, false);
            same.BaseUrl = "https://example.COM/v1"; same = store.Save(same, null, false, false);
            Require(same.HasKey && store.GetKey(same.Id) == Key, "equivalent URL did not preserve key");
            var changes = new (string Old, string New)[] {
                ("https://127.0.0.1:9876/v1", "http://127.0.0.1:9876/v1"),
                ("https://example.com/v1", "https://example.org/v1"),
                ("https://example.com:8443/v1", "https://example.com:8444/v1"),
                ("https://example.com/a/v1", "https://example.com/b/v1") };
            int n = 0;
            foreach (var change in changes)
            {
                var p = store.Save(Profile("change" + n++, change.Old), Key, false, false);
                p.BaseUrl = change.New; p = store.Save(p, null, false, false);
                Require(!p.HasKey && store.GetKey(p.Id) == null, "changed URL retained key");
                p = store.Save(p, Replacement, false, false); Require(store.GetKey(p.Id) == Replacement, "explicit new key failed");
            }
            var q = store.Save(Profile("protocol", "https://example.com/v1"), Key, false, false);
            q.Protocol = "chat_completions"; Require(!store.Save(q, null, false, false).HasKey, "changed protocol retained key");
            return Task.CompletedTask;
        });
        await Check("mutated profile and another store instance cannot reuse old key", async () =>
        {
            string storage = Path.Combine(dir, "ApiServiceHeldKey"); var store = new ApiProfileStore(storage);
            var p = store.Save(Profile("held", "http://127.0.0.1:11221/v1"), Key, false, true);
            string old = store.GetKey(p.Id)!; int sent = 0;
            using var http = new HttpClient(new MockHandler((_, _) => { sent++; return Task.FromResult(ResponsesOk()); }));
            var gateway = new ApiGateway(http); p.BaseUrl = "http://127.0.0.1:11222/v1";
            await ExpectAsync<InvalidOperationException>(() => gateway.TestAsync(p, old, ct));
            var oldProfile = store.List().Single(); new ApiProfileStore(storage).Save(p, null, false, false);
            await ExpectAsync<InvalidOperationException>(() => gateway.TestAsync(oldProfile, old, ct));
            Require(sent == 0, "old key was sent");
            oldProfile.Id = "different-profile-id"; oldProfile.BaseUrl = "http://127.0.0.1:11222/v1";
            await ExpectAsync<InvalidOperationException>(() => gateway.TestAsync(oldProfile, old, ct));
            Require(sent == 0, "stored key followed a changed profile ID");
            Require((await gateway.TestWithExplicitKeyAsync(oldProfile, old, ct)).Text == "OK" && sent == 1,
                "explicitly retyped key was rejected");
        });
        await Check("DPAPI entropy rejects tampered endpoint", () =>
        {
            string storage = Path.Combine(dir, "ApiServiceTamper"); var store = new ApiProfileStore(storage);
            var p = store.Save(Profile("tamper", "http://127.0.0.1:12221/v1"), Key, false, false);
            string file = Path.Combine(storage, "profiles.json"); var json = JsonNode.Parse(File.ReadAllText(file))!;
            json["Profiles"]![0]!["BaseUrl"] = "http://127.0.0.1:12222/v1"; File.WriteAllText(file, json.ToJsonString());
            Expect<CryptographicException>(() => store.GetKey(p.Id)); return Task.CompletedTask;
        });
        await Check("URL validation: credentials, query, fragment, path and loopback", () =>
        {
            string[] invalid = { "http://example.com/v1", "http://127.0.0.1.example.com/v1", "https://user:password@example.com/v1", "https://example.com/v1?key=secret", "https://example.com/v1#part", "https://example.com/a/../v1", "https://example.com/a/%2e%2e/v1", "https://example.com/a%2fb/v1", "https://example.com/%252e/v1", "file:///C:/v1", "https://example.com/v1/responses", "https://example.com\\@evil/v1" };
            foreach (string url in invalid) Expect<ArgumentException>(() => ApiSecurity.ValidateBaseUrl(url));
            Require(ApiSecurity.ValidateBaseUrl("http://localhost:8080/v1/") == "http://localhost:8080/v1", "localhost rejected");
            Require(ApiSecurity.ValidateBaseUrl("http://[::1]:8080/v1").Contains("[::1]"), "IPv6 loopback rejected");
            Require(ApiSecurity.ValidateBaseUrl("https://example.com/") == "https://example.com", "root path rejected");
            Require(ApiSecurity.ValidateBaseUrl("https://example.com/v1beta/openai/") == "https://example.com/v1beta/openai", "custom path rejected");
            Require(ApiSecurity.ValidateBaseUrl("https://example.com/compatible-mode/v1") == "https://example.com/compatible-mode/v1", "compatible path rejected");
            try { ApiSecurity.ValidateBaseUrl("https://example.com/v1/chat/completions"); throw new Exception("full endpoint accepted"); }
            catch (ArgumentException e) { Require(e.Message.Contains("/chat/completions"), "full endpoint guidance missing"); }
            return Task.CompletedTask;
        });
        await Check("custom base paths append the selected endpoint", async () =>
        {
            foreach (string path in new[] { "", "/v1beta/openai", "/compatible-mode/v1" })
                foreach (string protocol in new[] { "responses", "chat_completions" })
                {
                    using var http = new HttpClient(new MockHandler((request, _) =>
                    {
                        string suffix = protocol == "responses" ? "/responses" : "/chat/completions";
                        Require(request.RequestUri!.AbsolutePath == path + suffix, "endpoint appended incorrectly");
                        return Task.FromResult(protocol == "responses" ? ResponsesOk() : JsonResponse(new { choices = new[] { new { message = new { content = "OK" } } } }));
                    }));
                    var p = Profile("custom", "http://127.0.0.1:16660" + path); p.Protocol = protocol;
                    Require((await new ApiGateway(http).TestAsync(p, null, ct)).Text == "OK", "custom path request failed");
                }
        });
        await Check("Responses test sends minimal text and no empty Authorization", async () =>
        {
            int count = 0;
            using var http = new HttpClient(new MockHandler(async (request, token) =>
            {
                count++; Require(request.RequestUri!.AbsolutePath == "/v1/responses" && request.Headers.Authorization == null, "endpoint/header incorrect");
                using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
                var r = json.RootElement; var parts = r.GetProperty("input")[0].GetProperty("content");
                Require(r.GetProperty("model").GetString() == "mock-model" && !r.GetProperty("store").GetBoolean() && r.GetProperty("max_output_tokens").GetInt32() == 256 && parts.GetArrayLength() == 1 && parts[0].GetProperty("type").GetString() == "input_text" && parts[0].GetProperty("text").GetString() == "Reply with OK.", "test sent extra content");
                return ResponsesOk();
            }));
            var result = await new ApiGateway(http).TestAsync(Profile("mock", "http://127.0.0.1:16661/v1"), null, ct);
            Require(count == 1 && result.Text == "OK" && result.ReturnedModel == "mock-return" && result.InputTokens == 3 && result.OutputTokens == 1, "Responses result incorrect");
        });
        await Check("Responses review sends selected PNG and redacts echoed key", async () =>
        {
            int count = 0; byte[] image = await File.ReadAllBytesAsync(png, ct);
            using var http = new HttpClient(new MockHandler(async (request, token) =>
            {
                count++; Require(request.Headers.Authorization?.Parameter == WireKey, "key header incorrect");
                byte[] raw = await request.Content!.ReadAsByteArrayAsync(token); string wire = Encoding.UTF8.GetString(raw);
                Require(!wire.Contains(png, StringComparison.OrdinalIgnoreCase) && !wire.Contains("local-results-never-send"), "local information leaked");
                using var json = JsonDocument.Parse(raw); var parts = json.RootElement.GetProperty("input")[0].GetProperty("content");
                string data = parts[1].GetProperty("image_url").GetString()!;
                string prompt = parts[0].GetProperty("text").GetString()!;
                Require(json.RootElement.GetProperty("max_output_tokens").GetInt32() == 2048 && parts.GetArrayLength() == 2 && parts[0].GetProperty("type").GetString() == "input_text" && parts[1].GetProperty("type").GetString() == "input_image" && data.StartsWith("data:image/png;base64,") && Convert.FromBase64String(data.Split(',')[1]).SequenceEqual(image), "wrong PNG payload or limit");
                Require(new[] { "图号", "名称", "材质", "数量", "单位", "工艺", "原文", "位置", "不确定", "不要猜测", "自动计算" }.All(prompt.Contains) && !prompt.Contains("LocalContext") && !prompt.Contains(WireKey), "review prompt incorrect or leaked context");
                string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(WireKey));
                return JsonResponse(new { status = "completed", model = "echo-" + WireKey, output = new[] { new { content = new[] { new { type = "output_text", text = WireKey[..8] }, new { type = "output_text", text = WireKey[8..] + " " + encoded + " " + encoded.TrimEnd('=') } } } } });
            }));
            var result = await new ApiGateway(http).ReviewAsync(Profile("mock", "http://127.0.0.1:16662/v1"), WireKey, png, ct);
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(WireKey));
            Require(count == 1 && !result.Text.Contains(WireKey) && !result.Text.Contains(encoded) &&
                !result.Text.Contains(encoded.TrimEnd('=')) && result.Text.Contains("隐藏") && !result.ReturnedModel!.Contains(WireKey), "redaction failed");
        });
        await Check("changed selected image is rejected without sending", async () =>
        {
            string path = Path.Combine(dir, "ApiServiceSelected.png");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(Png), ct);
            var selected = await ApiGateway.SelectImageAsync(path, ct);
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(Png)[..24], ct);
            int sent = 0;
            using var http = new HttpClient(new MockHandler((_, _) => { sent++; return Task.FromResult(ResponsesOk()); }));
            await ExpectAsync<ApiImageChangedException>(() => new ApiGateway(http).ReviewSelectedAsync(Profile("changed", "http://127.0.0.1:16673/v1"), null, selected, ct));
            Require(sent == 0, "changed selection was sent");
        });
        await Check("Chat Completions supports JPEG and both WebP variants", async () =>
        {
            foreach (var (path, mime) in new[] { (jpeg, "image/jpeg"), (webp, "image/webp"), (lossy, "image/webp") })
            {
                byte[] image = await File.ReadAllBytesAsync(path, ct);
                using var http = new HttpClient(new MockHandler(async (request, token) =>
                {
                    Require(request.RequestUri!.AbsolutePath == "/v1/chat/completions" && request.Headers.Authorization == null, "chat endpoint/header incorrect");
                    using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
                    var parts = json.RootElement.GetProperty("messages")[0].GetProperty("content");
                    string data = parts[1].GetProperty("image_url").GetProperty("url").GetString()!;
                    string prompt = parts[0].GetProperty("text").GetString()!;
                    Require(json.RootElement.GetProperty("max_tokens").GetInt32() == 2048 && parts.GetArrayLength() == 2 && parts[0].GetProperty("type").GetString() == "text" && parts[1].GetProperty("type").GetString() == "image_url" && data.StartsWith("data:" + mime + ";base64,") && Convert.FromBase64String(data.Split(',')[1]).SequenceEqual(image) && !prompt.Contains("LocalContext") && !prompt.Contains(Key), "chat payload or limit incorrect");
                    return JsonResponse(new { model = "chat-return", choices = new[] { new { message = new { content = "人工核对" } } }, usage = new { prompt_tokens = 7, completion_tokens = 2 } });
                }));
                var p = Profile("mock", "http://127.0.0.1:16663/v1"); p.Protocol = "chat_completions";
                var result = await new ApiGateway(http).ReviewAsync(p, "", path, ct);
                Require(result.Text == "人工核对" && result.InputTokens == 7 && result.OutputTokens == 2, "chat result incorrect");
            }
        });
        await Check("HTTP failure ignores body; network errors are redacted and never retried", async () =>
        {
            int count = 0; var p = Profile("mock", "http://127.0.0.1:16664/v1");
            using (var http = new HttpClient(new MockHandler((_, _) => { count++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new NeverReadContent() }); })))
            {
                Exception error = await CaptureAsync(() => new ApiGateway(http).TestAsync(p, WireKey, ct));
                Require(error.Message.Contains("401") && !error.ToString().Contains(WireKey) && count == 1, "HTTP error incorrect");
            }
            count = 0;
            using var failing = new HttpClient(new MockHandler((_, _) => { count++; throw new HttpRequestException(WireKey); }));
            Exception network = await CaptureAsync(() => new ApiGateway(failing).TestAsync(p, WireKey, ct));
            Require(!network.ToString().Contains(WireKey) && count == 1, "network error leaked or retried");
            using var otherFailure = new HttpClient(new MockHandler((_, _) => throw new InvalidOperationException(WireKey)));
            Exception other = await CaptureAsync(() => new ApiGateway(otherFailure).TestAsync(p, WireKey, ct));
            Require(!other.ToString().Contains(WireKey), "transport exception leaked key");
            using var badStream = new HttpClient(new MockHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new LeakyContent() })));
            Exception read = await CaptureAsync(() => new ApiGateway(badStream).TestAsync(p, WireKey, ct));
            Require(!read.ToString().Contains(Key), "response stream exception leaked key");
        });
        await Check("Chat test limit and incompatible parameter guidance", async () =>
        {
            using var http = new HttpClient(new MockHandler(async (request, token) =>
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
                Require(json.RootElement.GetProperty("max_tokens").GetInt32() == 256, "chat test output limit incorrect");
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new NeverReadContent() };
            }));
            var p = Profile("mock", "http://127.0.0.1:16668/v1"); p.Protocol = "chat_completions";
            Exception error = await CaptureAsync(() => new ApiGateway(http).TestAsync(p, WireKey, ct));
            Require(error.Message.Contains("400") && error.Message.Contains("max_tokens") && !error.ToString().Contains(WireKey), "compatibility guidance missing");
        });
        await Check("truncated chat output is labelled; incomplete Responses fail", async () =>
        {
            var p = Profile("truncated", "http://127.0.0.1:16670"); p.Protocol = "chat_completions";
            using (var http = new HttpClient(new MockHandler((_, _) => Task.FromResult(JsonResponse(new { choices = new[] { new { finish_reason = "length", message = new { content = "partial" } } } })))))
            {
                var result = await new ApiGateway(http).TestAsync(p, null, ct);
                Require(result.Text.Contains("partial") && result.Text.Contains("不完整") && result.Note!.Contains("不完整"), "truncation not labelled");
            }
            p.Protocol = "responses";
            using var incomplete = new HttpClient(new MockHandler((_, _) => Task.FromResult(JsonResponse(new { status = "incomplete", output_text = "partial" }))));
            await ExpectAsync<InvalidDataException>(() => new ApiGateway(incomplete).TestAsync(p, null, ct));
        });
        await Check("cancel before and during request without retry", async () =>
        {
            int count = 0;
            using var http = new HttpClient(new MockHandler(async (_, token) => { count++; await Task.Delay(Timeout.InfiniteTimeSpan, token); return ResponsesOk(); }));
            var gateway = new ApiGateway(http); var p = Profile("mock", "http://127.0.0.1:16665/v1");
            using (var before = new CancellationTokenSource()) { before.Cancel(); await ExpectAsync<OperationCanceledException>(() => gateway.TestAsync(p, null, before.Token)); Require(count == 0, "cancelled request sent"); }
            using var during = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await ExpectAsync<OperationCanceledException>(() => gateway.TestAsync(p, null, during.Token)); Require(count == 1, "cancelled request retried");
        });
        await Check("timeout is explicit and does not retry", async () =>
        {
            int count = 0;
            using var http = new HttpClient(new MockHandler(async (_, token) => { count++; await Task.Delay(Timeout.InfiniteTimeSpan, token); return ResponsesOk(); })) { Timeout = TimeSpan.FromMilliseconds(100) };
            var error = await CaptureAsync(() => new ApiGateway(http).TestAsync(Profile("timeout", "http://127.0.0.1:16669/v1"), null, ct));
            Require(error is TimeoutException && count == 1, "timeout did not surface or retried");
        });
        await Check("injected standard clients cannot follow redirects or use proxies", async () =>
        {
            using (var unsafeClient = new HttpClient()) Expect<InvalidOperationException>(() => new ApiGateway(unsafeClient));
            using (var unsafeProxy = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = true, UseCookies = false }))
                Expect<InvalidOperationException>(() => new ApiGateway(unsafeProxy));
            using var safe = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false });
            _ = new ApiGateway(safe);
            using var changedHandler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
            using var changed = new HttpClient(changedHandler);
            var gateway = new ApiGateway(changed); changedHandler.AllowAutoRedirect = true;
            await ExpectAsync<InvalidOperationException>(() => gateway.TestAsync(Profile("mock", "http://127.0.0.1:16671"), null, CancellationToken.None));
        });
        await Check("response limit applies to declared and streamed content", async () =>
        {
            var p = Profile("mock", "http://127.0.0.1:16666/v1");
            using (var http = new HttpClient(new MockHandler((_, _) => { var b = new StringContent("x"); b.Headers.ContentLength = ApiGateway.MaxResponseBytes + 1; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = b }); })))
                await ExpectAsync<InvalidDataException>(() => new ApiGateway(http).TestAsync(p, null, ct));
            using var streamed = new HttpClient(new MockHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new byte[ApiGateway.MaxResponseBytes + 1])) })));
            await ExpectAsync<InvalidDataException>(() => new ApiGateway(streamed).TestAsync(p, null, ct));
        });
        await Check("malformed, oversized and nonlocal images rejected before sending", async () =>
        {
            string invalidPng = Path.Combine(dir, "ApiServiceInvalid.png"), corruptPng = Path.Combine(dir, "ApiServiceCorrupt.png"), invalidWebp = Path.Combine(dir, "ApiServiceInvalid.webp"), corruptWebp = Path.Combine(dir, "ApiServiceCorrupt.webp"), oversized = Path.Combine(dir, "ApiServiceOversized.png"), dimensions = Path.Combine(dir, "ApiServiceDimensions.webp");
            await File.WriteAllBytesAsync(invalidPng, Encoding.ASCII.GetBytes("not an image"), ct);
            await File.WriteAllBytesAsync(corruptPng, Convert.FromBase64String(Png)[..24], ct);
            await File.WriteAllBytesAsync(invalidWebp, Convert.FromBase64String(Webp)[..20], ct);
            byte[] corrupt = Convert.FromBase64String(Webp); Array.Clear(corrupt, 25, corrupt.Length - 25);
            await File.WriteAllBytesAsync(corruptWebp, corrupt, ct);
            await using (var file = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write)) file.SetLength(ApiGateway.MaxImageBytes + 1L);
            byte[] huge = Convert.FromBase64String(Webp); BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(21, 4), (8192u - 1) | ((2049u - 1) << 14));
            await File.WriteAllBytesAsync(dimensions, huge, ct); int count = 0;
            using var http = new HttpClient(new MockHandler((_, _) => { count++; return Task.FromResult(ResponsesOk()); }));
            var gateway = new ApiGateway(http); var p = Profile("mock", "http://127.0.0.1:16667/v1");
            foreach (string path in new[] { invalidPng, corruptPng, invalidWebp, corruptWebp, oversized, dimensions, @"\\server\share\fake.png" }) await ExpectAsync<InvalidDataException>(() => gateway.ReviewAsync(p, null, path, ct));
            Require(count == 0, "invalid image was sent");
        });
        await Check("two-frame APNG is rejected before sending", async () =>
        {
            string path = Path.Combine(dir, "ApiServiceSyntheticTwoFrame.png");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(PngTwoFrames), ct);
            int count = 0;
            using var http = new HttpClient(new MockHandler((_, _) => { count++; return Task.FromResult(ResponsesOk()); }));
            var error = await CaptureAsync(() => new ApiGateway(http).ReviewAsync(Profile("apng", "http://127.0.0.1:16672/v1"), null, path, ct));
            Require(error is InvalidDataException && error.Message.Contains("不支持动画 PNG") && count == 0, "animated PNG was not rejected before sending");
        });
        await Check("default transport does not forward key and image on real loopback redirect", async () =>
        {
            using var target = new TcpListener(IPAddress.Loopback, 0); using var source = new TcpListener(IPAddress.Loopback, 0);
            target.Start(); source.Start(); int targetPort = ((IPEndPoint)target.LocalEndpoint).Port, sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var server = Task.Run(async () =>
            {
                using var socket = await source.AcceptTcpClientAsync(timeout.Token); await using var stream = socket.GetStream();
                byte[] buffer = new byte[8192]; Require(await stream.ReadAsync(buffer, timeout.Token) > 0, "loopback source received nothing");
                string response = $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{targetPort}/v1/responses\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
            }, timeout.Token);
            Exception error = await CaptureAsync(() => new ApiGateway().ReviewAsync(Profile("loopback", $"http://127.0.0.1:{sourcePort}/v1"), WireKey, png, timeout.Token));
            await server; await Task.Delay(200, timeout.Token);
            Require(error is InvalidOperationException && error.Message.Contains("307") && !target.Pending(), "redirect forwarded or incorrect error");
        });
        int failed = results.Count(x => !x.Passed);
        var report = new { status = failed == 0 ? "passed" : "failed", passed = results.Count - failed, failed, tests = results };
        await File.WriteAllTextAsync(Path.Combine(dir, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
        return failed == 0 ? 0 : 1;
    }
    private sealed record TestResult(string Name, bool Passed, string? Detail);
    private static void RequirePrivateAcl(string path, bool directory)
    {
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        string user = identity.User?.Value ?? throw new InvalidOperationException("User SID unavailable.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { user, "S-1-5-18", "S-1-5-32-544" };
        bool extra = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow && !allowed.Contains(r.IdentityReference.Value));
        Require(!extra && (!directory || acl.AreAccessRulesProtected), "private ACL grants another principal access");
    }
    private static ApiProfile Profile(string name, string url) => new() { Name = name, BaseUrl = url, Protocol = "responses", Model = "mock-model" };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<Exception> CaptureAsync(Func<Task> action) { try { await action(); } catch (Exception e) { return e; } throw new InvalidOperationException("Expected a failure"); }
    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage ResponsesOk() => JsonResponse(new { status = "completed", model = "mock-return", output = new[] { new { content = new[] { new { type = "output_text", text = "OK" } } } }, usage = new { input_tokens = 3, output_tokens = 1 } });
    private sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class NeverReadContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("HTTP error body was read");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class LeakyContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException(Key);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
