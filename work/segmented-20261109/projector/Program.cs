using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Encodings.Web;

// This is a bounded projection of the *frozen extractor's* actual inputs.
// Input remains authoritative; any limit, malformed input, duplicate root key,
// or changed hash fails closed, leaving the source and partial output intact.
const int InitialBuffer = 1 << 20;
const int MaxBufferedValue = 16 << 20;
const long MaxProjection = 256L << 20;
var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
try
{
    if (args.Length != 4 || (args[0] != "profile" && args[0] != "project"))
        throw new ArgumentException("Usage: profile|project INPUT PROFILE OUTPUT -- use --wait-for-go as fifth argument");
}
catch when (args.Length == 5 && args[4] == "--wait-for-go") { }

try
{
    if (args.Length == 5 && args[4] == "--wait-for-go")
    {
        if (Console.ReadLine() != "GO") throw new InvalidOperationException("invalid_gate");
    }
    else throw new ArgumentException("guard gate is required");
    string mode = args[0], input = Path.GetFullPath(args[1]), profilePath = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
    if (mode is not ("profile" or "project")) throw new ArgumentException("invalid mode");
    if (StringComparer.OrdinalIgnoreCase.Equals(input, output) || StringComparer.OrdinalIgnoreCase.Equals(input, profilePath))
        throw new ArgumentException("input cannot be an output");
    Profile? baseline = mode == "project" ? JsonSerializer.Deserialize<Profile>(File.ReadAllText(profilePath), options) : null;
    if (mode == "project" && (baseline is null || baseline.status != "passed" || baseline.anchors is null)) throw new InvalidDataException("missing_or_invalid_profile");
    var anchors = new HashSet<Anchor>();
    if (baseline is not null) foreach (var a in baseline.anchors) anchors.Add(a);
    var observedAnchors = new HashSet<Anchor>();
    var anchorLookup = BuildLookup(anchors);
    var fields = new Dictionary<string, FieldStats>(StringComparer.Ordinal);
    string? current = null, sourceFile = null, sourceSha256 = null;
    bool valueStarted = false, sawRoot = false, rootComplete = false;
    long maxTextChars = 0, textCount = 0, selectedGeometry = 0, allGeometry = 0, crossMarkCandidates = 0;
    long bytesHashed = 0, baseOffset = 0;
    string partial = output + ".partial";
    using var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    using var outputStream = mode == "project" ? new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20) : null;
    using var writer = outputStream is null ? null : new Utf8JsonWriter(outputStream, new JsonWriterOptions { Indented = false });
    writer?.WriteStartObject();
    byte[] buffer = new byte[InitialBuffer];
    int used = 0;
    bool eof = false;
    var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 128, CommentHandling = JsonCommentHandling.Disallow });
    while (true)
    {
        while (!eof && used < buffer.Length)
        {
            int n = stream.Read(buffer, used, buffer.Length - used);
            if (n == 0) { eof = true; break; }
            hash.AppendData(buffer, used, n); bytesHashed += n; used += n;
        }
        var reader = new Utf8JsonReader(buffer.AsSpan(0, used), eof, state);
        bool rollback = false;
        long rollbackBytes = 0;
        JsonReaderState rollbackState = default;
        while (true)
        {
            var checkpoint = reader.CurrentState;
            long before = reader.BytesConsumed;
            if (!reader.Read()) break;
            var token = reader.TokenType;
            int depth = reader.CurrentDepth;
            if (!sawRoot)
            {
                if (token != JsonTokenType.StartObject || depth != 0) throw new InvalidDataException("root_must_be_object");
                sawRoot = true; continue;
            }
            if (token == JsonTokenType.EndObject && depth == 0) { rootComplete = true; continue; }
            if (token == JsonTokenType.PropertyName && depth == 1)
            {
                current = reader.GetString() ?? throw new InvalidDataException("null_root_property");
                if (!fields.TryAdd(current, new FieldStats())) throw new InvalidDataException("duplicate_root_property: " + current);
                valueStarted = false; continue;
            }
            if (current is null) throw new InvalidDataException("value_without_root_property");
            var stat = fields[current];
            if (!valueStarted && depth == 1)
            {
                stat.start = baseOffset + reader.TokenStartIndex;
                stat.type = token.ToString(); valueStarted = true;
                if (current is "issues" or "notifications" or "texts" or "geometry")
                {
                    if (token != JsonTokenType.StartArray) throw new InvalidDataException(current + "_must_be_array");
                    if (writer is not null) { writer.WritePropertyName(current); writer.WriteStartArray(); }
                }
                if (current is "sourceFile" or "sourceSha256")
                {
                    if (token != JsonTokenType.String) throw new InvalidDataException(current + "_must_be_string");
                    var value = reader.GetString() ?? throw new InvalidDataException(current + "_is_null");
                    if (current == "sourceFile") sourceFile = value; else sourceSha256 = value;
                    if (writer is not null) writer.WriteString(current, value);
                }
                if (current == "success" && writer is not null)
                {
                    if (token is not (JsonTokenType.True or JsonTokenType.False))
                        throw new InvalidDataException("success_must_be_boolean");
                    writer.WriteBoolean(current, token == JsonTokenType.True);
                }
                // These two objects are used by downstream file-level
                // diagnostics. Preserve their complete JSON values.
                if (writer is not null && current is "summary" or "numericSanitization")
                {
                    if (token != JsonTokenType.StartObject) throw new InvalidDataException(current + "_must_be_object");
                    long start = reader.TokenStartIndex;
                    if (!JsonDocument.TryParseValue(ref reader, out var metadata))
                    {
                        valueStarted = false;
                        rollback = true; rollbackBytes = before; rollbackState = checkpoint; break;
                    }
                    using (metadata)
                    {
                        long size = reader.BytesConsumed - start;
                        writer.WritePropertyName(current);
                        writer.WriteRawValue(buffer.AsSpan(checked((int)start), checked((int)size)), skipInputValidation: true);
                        stat.end = baseOffset + reader.BytesConsumed;
                        stat.bytes = stat.end - stat.start;
                    }
                    continue;
                }
            }
            bool element = depth == 2 && token is JsonTokenType.StartObject or JsonTokenType.StartArray or JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null;
            if (element && stat.type == nameof(JsonTokenType.StartArray))
            {
                bool inspect = current == "texts" || (writer is not null && current is "issues" or "notifications" or "geometry");
                if (inspect)
                {
                    if (token != JsonTokenType.StartObject) throw new InvalidDataException(current + "_element_must_be_object");
                    long start = reader.TokenStartIndex;
                    if (!JsonDocument.TryParseValue(ref reader, out var doc))
                    {
                        rollback = true; rollbackBytes = before; rollbackState = checkpoint; break;
                    }
                    using (doc)
                    {
                        long size = reader.BytesConsumed - start;
                        stat.maxElementBytes = Math.Max(stat.maxElementBytes, size);
                        if (current == "texts")
                        {
                            var text = RequiredString(doc.RootElement, "text", "texts");
                            textCount++; maxTextChars = Math.Max(maxTextChars, text.Length);
                            // Be a superset of ECMAScript trim: selecting extra
                            // anchor paths only retains more geometry.
                            int left = 0, right = text.Length;
                            while (left < right && (char.IsWhiteSpace(text[left]) || text[left] == '\ufeff')) left++;
                            while (right > left && (char.IsWhiteSpace(text[right - 1]) || text[right - 1] == '\ufeff')) right--;
                            string trimmed = text[left..right];
                            if (trimmed == "PART No." || trimmed.ToUpperInvariant() == "NUMBER")
                            {
                                observedAnchors.Add(new Anchor(RequiredString(doc.RootElement, "layout", "texts"), RequiredString(doc.RootElement, "blockPath", "texts")));
                            }
                        }
                        bool retain = true;
                        if (writer is not null && current == "geometry")
                        {
                            allGeometry++;
                            var layout = RequiredString(doc.RootElement, "layout", "geometry");
                            var path = RequiredString(doc.RootElement, "blockPath", "geometry");
                            retain = Matches(anchorLookup, layout, path);
                            // Long diagonal straight segments anywhere are kept so the
                            // extractor can recognise whole-frame cancellation crosses.
                            if (IsCrossMarkCandidate(doc.RootElement)) { crossMarkCandidates++; retain = true; }
                            if (retain) selectedGeometry++;
                        }
                        if (writer is not null && retain)
                            writer.WriteRawValue(buffer.AsSpan(checked((int)start), checked((int)size)), skipInputValidation: true);
                    }
                }
                stat.items++;
            }
            if (depth == 1 && token is JsonTokenType.EndArray or JsonTokenType.EndObject or JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null)
            {
                stat.end = baseOffset + reader.BytesConsumed;
                stat.bytes = stat.end - stat.start;
                if (writer is not null && current is "issues" or "notifications" or "texts" or "geometry") writer.WriteEndArray();
            }
            if (writer is not null && writer.BytesPending >= 1 << 20)
            {
                writer.Flush();
                if (outputStream!.Length > MaxProjection) throw new InvalidDataException("projection_exceeds_256MiB; original source retained; manual review required");
            }
        }
        long consumed = rollback ? rollbackBytes : reader.BytesConsumed;
        state = rollback ? rollbackState : reader.CurrentState;
        if (consumed < 0 || consumed > used) throw new InvalidDataException("internal_reader_offset");
        int remainder = used - checked((int)consumed);
        if (remainder > 0) Buffer.BlockCopy(buffer, checked((int)consumed), buffer, 0, remainder);
        used = remainder; baseOffset += consumed;
        if (eof)
        {
            if (used != 0 || rollback || !rootComplete) throw new InvalidDataException("incomplete_or_invalid_json_at_byte_" + baseOffset);
            break;
        }
        if (used == buffer.Length)
        {
            if (buffer.Length >= MaxBufferedValue)
                throw new InvalidDataException($"single_token_or_record_exceeds_{MaxBufferedValue}_bytes; field={current}; source_byte={baseOffset}; original source retained; manual review required");
            Array.Resize(ref buffer, Math.Min(MaxBufferedValue, buffer.Length * 2));
        }
    }
    foreach (string required in new[] { "sourceFile", "sourceSha256", "success", "summary", "numericSanitization", "notifications", "issues", "texts", "geometry" })
        if (!fields.ContainsKey(required)) throw new InvalidDataException("missing_required_root_property: " + required);
    string digest = Convert.ToHexString(hash.GetHashAndReset());
    if (baseline is not null && (digest != baseline.sourceHash || bytesHashed != baseline.sourceBytes))
        throw new InvalidDataException("source_changed_since_profile; original source retained");
    if (baseline is not null && (!anchors.SetEquals(observedAnchors) || anchors.Count != baseline.anchors.Length || textCount != baseline.textCount))
        throw new InvalidDataException("profile_anchors_or_text_count_changed; original source retained");
    if (baseline is null) anchors = observedAnchors;
    if (writer is not null)
    {
        writer.WriteStartObject("projectionMetadata");
        writer.WriteString("kind", "title-anchor-geometry-subset");
        writer.WriteString("originalReportPath", input);
        writer.WriteString("originalReportSha256", digest);
        writer.WriteNumber("originalReportBytes", bytesHashed);
        writer.WriteBoolean("allTextAndIssuesRetained", true);
        writer.WriteBoolean("allNotificationsRetained", true);
        writer.WriteNumber("originalGeometryCount", allGeometry);
        writer.WriteNumber("retainedGeometryCount", selectedGeometry);
        writer.WriteString("crossMarkRule", "open_2pt_line_or_polyline_len_ge_100_diagonal_quarter_v1");
        writer.WriteNumber("crossMarkCandidateCount", crossMarkCandidates);
        writer.WriteBoolean("fullNativeReport", false);
        writer.WriteEndObject();
        writer.WriteEndObject(); writer.Flush();
    }
    if (outputStream is not null && outputStream.Length > MaxProjection) throw new InvalidDataException("projection_exceeds_256MiB; original source retained");
    var result = new Profile { status = "passed", source = input, sourceHash = digest, sourceBytes = bytesHashed,
        sourceFile = sourceFile!, sourceSha256 = sourceSha256!, fields = fields,
        anchors = anchors.OrderBy(a => a.layout, StringComparer.Ordinal).ThenBy(a => a.blockPath, StringComparer.Ordinal).ToArray(),
        textCount = textCount,
        maxTextChars = maxTextChars,
        geometryCount = mode == "project" ? allGeometry : fields["geometry"].items,
        retainedGeometry = selectedGeometry, projectionBytes = outputStream?.Length ?? 0,
        maximumBufferedValueBytes = MaxBufferedValue, maximumProjectionBytes = MaxProjection };
    if (mode == "profile") File.WriteAllText(profilePath, JsonSerializer.Serialize(result, options));
    else
    {
        writer!.Dispose();
        outputStream!.Dispose();
        File.Move(partial, output, false);
        File.WriteAllText(output + ".projection.json", JsonSerializer.Serialize(result, options));
    }
    Console.WriteLine(JsonSerializer.Serialize(new { result.status, result.sourceBytes, result.sourceHash, result.textCount, anchorPaths = result.anchors.Length, result.geometryCount, result.retainedGeometry, result.projectionBytes }));
}
catch (Exception ex)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", error = ex.ToString(), sourcePreserved = true, partialPreserved = true }));
    Environment.ExitCode = 1;
}

static string RequiredString(JsonElement value, string property, string context)
{
    if (!value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String)
        throw new InvalidDataException(context + "_missing_string_" + property);
    return item.GetString() ?? throw new InvalidDataException(context + "_null_" + property);
}
static Dictionary<string, HashSet<string>> BuildLookup(IEnumerable<Anchor> anchors)
{
    var lookup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    foreach (var a in anchors)
    {
        if (!lookup.TryGetValue(a.layout, out var paths)) lookup.Add(a.layout, paths = new(StringComparer.Ordinal));
        paths.Add(a.blockPath);
    }
    return lookup;
}
// Must stay identical to independent_projection_audit.py and the extractor's
// crossMarkSegment filter: only exact IEEE products/comparisons, no trig.
static bool IsCrossMarkCandidate(JsonElement g)
{
    if (g.TryGetProperty("dimensionGraphics", out var dim) && dim.ValueKind != JsonValueKind.False) return false;
    if (g.TryGetProperty("closed", out var closed) && closed.ValueKind != JsonValueKind.False) return false;
    if (!g.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return false;
    var k = kind.GetString() ?? "";
    if (k != "Line" && !k.Contains("Polyline", StringComparison.Ordinal)) return false;
    if (!g.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array || points.GetArrayLength() != 2) return false;
    var xy = new double[4];
    int i = 0;
    foreach (var point in points.EnumerateArray())
    {
        if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2) return false;
        int j = 0;
        foreach (var v in point.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || !double.IsFinite(d)) return false;
            if (j < 2) xy[i * 2 + j] = d;
            j++;
        }
        i++;
    }
    double dx = Math.Abs(xy[2] - xy[0]), dy = Math.Abs(xy[3] - xy[1]);
    return dx * dx + dy * dy >= 10000 && dy >= 0.25 * dx && dx >= 0.25 * dy;
}
static bool Matches(Dictionary<string, HashSet<string>> lookup, string layout, string path)
{
    if (!lookup.TryGetValue(layout, out var anchors)) return false;
    if (anchors.Contains(path)) return true;
    for (int i = path.IndexOf('/'); i >= 0; i = path.IndexOf('/', i + 1))
        if (anchors.Contains(path[..i])) return true;
    return false;
}
public record Anchor(string layout, string blockPath);
public class FieldStats { public long start { get; set; } public long end { get; set; } public long bytes { get; set; } public long items { get; set; } public long maxElementBytes { get; set; } public string type { get; set; } = ""; }
public class Profile
{
    public string status { get; set; } = ""; public string source { get; set; } = ""; public string sourceHash { get; set; } = "";
    public long sourceBytes { get; set; } public string sourceFile { get; set; } = ""; public string sourceSha256 { get; set; } = "";
    public Dictionary<string, FieldStats> fields { get; set; } = new(); public Anchor[] anchors { get; set; } = [];
    public long textCount { get; set; } public long maxTextChars { get; set; } public long geometryCount { get; set; }
    public long retainedGeometry { get; set; } public long projectionBytes { get; set; }
    public long maximumBufferedValueBytes { get; set; } public long maximumProjectionBytes { get; set; }
}
