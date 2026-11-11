using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (args.Contains("--wait-for-go") && Console.ReadLine() != "GO") throw new InvalidOperationException("Resource guard handshake not received.");
var recognitionMode = args.Contains("--recognition-mode");
var input = args.Length > 0 ? Path.GetFullPath(args[0]) : @"D:\CADtest\inputs\27.21 TX26320-261113-DZN1-A(1).dwg";
var output = args.Length > 1 ? Path.GetFullPath(args[1]) : @"D:\CADtest\outputs";
Directory.CreateDirectory(output);
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var numericAudit = new NumericSanitization();
json.Converters.Add(new FiniteDoubleConverter(numericAudit));
var messages = new List<ReadMessage>();
var issues = new List<ReadMessage>();
var texts = new List<TextRecord>();
var dimensions = new List<DimensionRecord>();
var geometry = new List<GeometryRecord>();
var instanceCounts = new Dictionary<string, int>();
var insertInstances = new List<object>();
var tables = new List<object>();
var header = new byte[6];
using (var stream = File.OpenRead(input)) stream.ReadExactly(header);
var watch = Stopwatch.StartNew();
CadDocument doc;
try
{
    using var reader = new DwgReader(input);
    reader.Configuration.Failsafe = false;
    reader.OnNotification += (_, n) => messages.Add(new(n.NotificationType.ToString(), n.Message, n.Exception?.GetType().Name));
    doc = reader.Read();
}
catch (Exception ex)
{
    File.WriteAllText(Path.Combine(output, "read-failure.json"), JsonSerializer.Serialize(new { success = false, input, messages, error = ex.ToString() }, json));
    throw;
}
watch.Stop();
var readMilliseconds = watch.Elapsed.TotalMilliseconds;

// Known coordinates verify nested transform order before processing the drawing.
var fixtureInner = new Insert(new BlockRecord("fixture-inner")) { InsertPoint = new XYZ(10, 20, 0), XScale = 2, YScale = 3 };
var fixtureOuter = new Insert(new BlockRecord("fixture-outer")) { InsertPoint = new XYZ(100, 200, 0) };
var fixturePoint = World(new XYZ(2, 3, 0), [CadPlacement.FromInsert(fixtureOuter), CadPlacement.FromInsert(fixtureInner)]);
if (Math.Abs(fixturePoint.X - 114) > 1e-9 || Math.Abs(fixturePoint.Y - 229) > 1e-9)
    throw new InvalidOperationException("Nested-transform self-check failed.");

var visitedRootBlocks = new HashSet<ulong>();
var layoutRecords = new List<object>();
foreach (var layout in doc.Layouts.OrderBy(l => l.TabOrder))
{
    var block = layout.AssociatedBlock;
    if (block is null) { issues.Add(new("MISSING_LAYOUT_BLOCK", layout.Name)); continue; }
    layoutRecords.Add(new { layout.Name, layout.TabOrder, block = block.Name, topLevelEntityCount = block.Entities.Count });
    if (!visitedRootBlocks.Add(block.Handle)) continue;
    foreach (var entity in block.Entities)
        Walk(entity, layout.Name, block.Name, [], new HashSet<ulong> { block.Handle }, false);
}
if (visitedRootBlocks.Add(doc.ModelSpace.Handle))
    foreach (var entity in doc.ModelSpace.Entities)
        Walk(entity, "Model", doc.ModelSpace.Name, [], new HashSet<ulong> { doc.ModelSpace.Handle }, false);
var definitions = doc.BlockRecords.Select(b => new { b.Name, handle = b.Handle.ToString("X"), entityCount = b.Entities.Count }).ToArray();
var report = new
{
    success = true, sourceFile = Path.GetFileName(input),
    sourceSha256 = InputHash(input),
    headerVersion = Encoding.ASCII.GetString(header),
    headerMetadata = new { version = doc.Header.VersionString.ToString(), insertionUnits = doc.Header.InsUnits.ToString(), measurementUnits = doc.Header.MeasurementUnits.ToString() },
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    assemblyVersion = typeof(CadDocument).Assembly.GetName().Version?.ToString(),
    strictRead = true, readStatus = "READ_SUCCEEDED_WITH_WARNINGS", recognitionMode, readMilliseconds, selfChecks = new { nestedTransformKnownPoint = true },
    summary = new
    {
        layoutCount = layoutRecords.Count, blockDefinitionCount = definitions.Length,
        topLevelModelEntityCount = doc.ModelSpace.Entities.Count,
        topLevelModelTypes = doc.ModelSpace.Entities.GroupBy(e => e.GetType().Name).ToDictionary(g => g.Key, g => g.Count()),
        instantiatedTypes = instanceCounts,
        sourceTextCount = texts.Count(t => t.Kind != "DIMENSION_GRAPHICS"),
        dimensionGraphicsTextCount = texts.Count(t => t.Kind == "DIMENSION_GRAPHICS"),
        attributeCount = texts.Count(t => t.Kind == "ATTRIBUTE"),
        dimensionCount = dimensions.Count, nativeTableCount = tables.Count,
        readNotificationCount = messages.Count,
        dimensionDisplayApiErrorCount = issues.Count(i => i.Kind == "DIMENSION_FORMAT_API_ERROR"),
        previewOnlyIssueCount = issues.Count(i => i.Kind.StartsWith("PREVIEW_", StringComparison.Ordinal)),
        extractionIssueCount = issues.Count(i => !i.Kind.StartsWith("PREVIEW_", StringComparison.Ordinal))
    },
    limitations = new[] {
        "Windows/.NET 10 sample parsing only; Linux is not tested.",
        "No native CAD viewer baseline was supplied; content completeness and field accuracy are not independently certified.",
        "Saved dimension-block text is retained when the formatting helper fails; compare against the source CAD.",
        "Diagnostic geometry reconstructs extracted primitives; it is not a certified CAD rendering.",
        "Recognition mode omits dimension-display formatting, dimension graphics, and arc/circle tessellation; source dimension values remain available.",
        "Unrecognized or malformed MTEXT formatting is retained raw with an explicit review flag; non-finite numbers serialize as null."
    },
    layouts = layoutRecords, blockDefinitions = definitions, inserts = insertInstances,
    notifications = messages, issues, texts, dimensions, tables, geometry,
    numericSanitization = numericAudit
};
using (var reportStream = File.Create(Path.Combine(output, "cad-read-report.json"))) JsonSerializer.Serialize(reportStream, report, json);
Console.WriteLine(JsonSerializer.Serialize(new { report.success, report.headerVersion, report.runtime, report.assemblyVersion, report.readMilliseconds, report.summary }, json));

string InputHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
XYZ World(XYZ p, IReadOnlyList<CadPlacement> chain)
{
    for (var i = chain.Count - 1; i >= 0; i--) p = chain[i].Apply(p);
    return p;
}
double[] Point(XYZ p) => [p.X, p.Y, p.Z];
string EvidenceId(string layout, string path, Entity e) => $"{layout}/{path}#{e.Handle:X}";
string Plain(string value) => SafeCadText.Symbols(value);
CadTextResult PlainEncoded(string value, string id)
{
    var parsed=SafeCadText.ReadPlain(value);
    if(parsed.RequiresReview) issues.Add(new("TEXT_ENCODING_REVIEW",id+" | "+parsed.Reason));
    return parsed;
}
CadTextResult MTextPlain(MText text, string id)
{
    var parsed = SafeCadText.Parse(text.Value);
    if (parsed.RequiresReview) issues.Add(new("MTEXT_FORMAT_REVIEW", id + " | " + parsed.Reason));
    return parsed;
}

void AddText(Entity e, string raw, string plain, XYZ p, double height, double rotation, string kind, string? tag,
    string layout, string path, IReadOnlyList<CadPlacement> chain, bool requiresReview = false)
{
    if (string.IsNullOrWhiteSpace(raw) && string.IsNullOrWhiteSpace(plain)) return;
    var world = World(p, chain);
    var xDirection = World(p + new XYZ(Math.Cos(rotation) * height, Math.Sin(rotation) * height, 0), chain) - world;
    var yDirection = World(p + new XYZ(-Math.Sin(rotation) * height, Math.Cos(rotation) * height, 0), chain) - world;
    if (!CadPlacement.Finite(world) || !CadPlacement.Finite(xDirection) || !CadPlacement.Finite(yDirection))
    { issues.Add(new("NON_FINITE_TEXT_POSITION", EvidenceId(layout, path, e))); return; }
    texts.Add(new(EvidenceId(layout, path, e), kind, e.Handle.ToString("X"), layout, path, e.Layer?.Name ?? "", raw, plain, tag,
        Point(world), Math.Sqrt(yDirection.X * yDirection.X + yDirection.Y * yDirection.Y + yDirection.Z * yDirection.Z),
        Math.Atan2(xDirection.Y, xDirection.X), e.IsInvisible, Point(xDirection), Point(yDirection), requiresReview));
}

void Walk(Entity e, string layout, string path, IReadOnlyList<CadPlacement> chain, HashSet<ulong> ancestors, bool dimensionGraphics)
{
    if (chain.Count > 32) { issues.Add(new("BLOCK_DEPTH_LIMIT", EvidenceId(layout, path, e))); return; }
    if (!dimensionGraphics) instanceCounts[e.GetType().Name] = instanceCounts.GetValueOrDefault(e.GetType().Name) + 1;
    var id = EvidenceId(layout, path, e);
    switch (e)
    {
        case TableEntity table:
            var cells = new List<object>();
            for (var r = 0; r < table.Rows.Count; r++)
                for (var c = 0; c < table.Rows[r].Cells.Count; c++)
                    foreach (var content in table.Rows[r].Cells[c].Contents)
                        cells.Add(new { row = r, column = c, contentType = content.ContentType.ToString(),
                            formattedValue = content.CadValue.FormattedValue,
                            value = Convert.ToString(content.CadValue.Value, System.Globalization.CultureInfo.InvariantCulture),
                            valueType = content.CadValue.ValueType.ToString() });
            tables.Add(new { id, layout, path, rowCount = table.Rows.Count, columnCount = table.Columns.Count, cells });
            break;
        case Insert insert:
            insertInstances.Add(new { id, layout, path, block = insert.Block?.Name,
                position = Point(World(insert.InsertPoint, chain)), insert.XScale, insert.YScale, insert.ZScale, insert.Rotation,
                insert.RowCount, insert.ColumnCount, attributeCount = insert.Attributes.Count });
            foreach (var attribute in insert.Attributes)
            {
                var parsedAttribute = attribute.MText is null ? PlainEncoded(attribute.Value,id) : MTextPlain(attribute.MText, id);
                AddText(attribute, attribute.MText?.Value ?? attribute.Value, parsedAttribute.Text,
                    attribute.InsertPoint, attribute.Height, attribute.Rotation, "ATTRIBUTE", attribute.Tag,
                    layout, path + $"/INSERT:{insert.Handle:X}", chain, parsedAttribute.RequiresReview);
            }
            if (insert.IsMultiple) issues.Add(new("ARRAY_INSERT_NOT_EXPANDED", id));
            if (insert.Block is null) { issues.Add(new("MISSING_BLOCK", id)); break; }
            if (!ancestors.Add(insert.Block.Handle)) { issues.Add(new("BLOCK_CYCLE", id)); break; }
            CadPlacement placement;
            try { placement = CadPlacement.FromInsert(insert); }
            catch (InvalidDataException ex) { issues.Add(new("INVALID_BLOCK_TRANSFORM", id + " | " + ex.Message)); ancestors.Remove(insert.Block.Handle); break; }
            var nested = chain.Concat(new[] { placement }).ToArray();
            foreach (var child in insert.Block.Entities)
                Walk(child, layout, path + $"/INSERT:{insert.Handle:X}:{insert.Block.Name}", nested, ancestors, dimensionGraphics);
            ancestors.Remove(insert.Block.Handle);
            break;
        case Dimension dimension:
            var cachedTexts = dimension.Block?.Entities.Select(x => x switch { MText m => MTextPlain(m, id + $"/CACHE:{m.Handle:X}").Text, TextEntity t => Plain(t.Value), _ => null })
                .OfType<string>().Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? [];
            string? formatted = null;
            string? formatError = null;
            try { if (!recognitionMode) formatted = dimension.GetMeasurementText(); }
            catch (Exception ex)
            {
                formatError = ex.GetType().Name + ": " + ex.Message;
                issues.Add(new("DIMENSION_FORMAT_API_ERROR", id + " | " + ex.Message, ex.GetType().Name));
            }
            dimensions.Add(new(id, dimension.Handle.ToString("X"), layout, path, dimension.GetType().Name,
                dimension.Measurement, dimension.Text, formatted, cachedTexts, dimension.HasStyleOverride, formatError,
                Point(World(dimension.TextMiddlePoint, chain)), dimension.Style?.Name, dimension.Style?.LinearScaleFactor));
            if (!recognitionMode && dimension.Block is not null && ancestors.Add(dimension.Block.Handle))
            {
                foreach (var child in dimension.Block.Entities)
                    Walk(child, layout, path + $"/DIMENSION:{dimension.Handle:X}:{dimension.Block.Name}", chain, ancestors, true);
                ancestors.Remove(dimension.Block.Handle);
            }
            break;
        case AttributeDefinition:
            break; // Definitions are template fields, not actual instance values.
        case MText text:
            var parsedText = MTextPlain(text, id);
            AddText(e, text.Value, parsedText.Text, text.InsertPoint, text.Height, text.Rotation,
                dimensionGraphics ? "DIMENSION_GRAPHICS" : "MTEXT", null, layout, path, chain, parsedText.RequiresReview);
            break;
        case TextEntity text:
            var plainText=PlainEncoded(text.Value,id);
            AddText(e, text.Value, plainText.Text, text.InsertPoint, text.Height, text.Rotation,
                dimensionGraphics ? "DIMENSION_GRAPHICS" : "TEXT", null, layout, path, chain,plainText.RequiresReview);
            break;
        case Line line:
            geometry.Add(new(id, layout, path, e.GetType().Name, e.Layer?.Name ?? "",
                [Point(World(line.StartPoint, chain)), Point(World(line.EndPoint, chain))], false, dimensionGraphics));
            break;
        case Arc arc:
            if (!recognitionMode) AddArc(arc.Center, arc.Radius, arc.StartAngle, arc.EndAngle, arc.Normal, e, false);
            break;
        case Circle circle:
            if (!recognitionMode) AddArc(circle.Center, circle.Radius, 0, 2 * Math.PI, circle.Normal, e, true);
            break;
        case LwPolyline polyline:
            if (polyline.Vertices.Any(v => Math.Abs(v.Bulge) > 1e-10)) issues.Add(new("PREVIEW_POLYLINE_BULGE_APPROXIMATION", id));
            geometry.Add(new(id, layout, path, e.GetType().Name, e.Layer?.Name ?? "",
                polyline.Vertices.Select(v => Point(World(new XYZ(v.Location.X, v.Location.Y, polyline.Elevation), chain))).ToArray(),
                polyline.IsClosed, dimensionGraphics));
            break;
        default:
            issues.Add(new("PREVIEW_ENTITY_NOT_DRAWN", id + " | " + e.GetType().Name));
            break;
    }
    void AddArc(XYZ center, double radius, double start, double end, XYZ normal, Entity entity, bool closed)
    {
        if (Math.Abs(normal.X) > 1e-8 || Math.Abs(normal.Y) > 1e-8 || normal.Z < 0)
        { issues.Add(new("PREVIEW_NON_XY_ARC", id)); return; }
        if (end <= start) end += 2 * Math.PI;
        var n = Math.Max(8, (int)Math.Ceiling((end - start) / (Math.PI / 60)));
        var points = Enumerable.Range(0, n + 1).Select(i => start + (end - start) * i / n)
            .Select(a => Point(World(center + new XYZ(radius * Math.Cos(a), radius * Math.Sin(a), 0), chain))).ToArray();
        geometry.Add(new(id, layout, path, entity.GetType().Name, entity.Layer?.Name ?? "", points, closed, dimensionGraphics));
    }
}

record ReadMessage(string Kind, string Message, string? ExceptionType = null);
record TextRecord(string Id, string Kind, string Handle, string Layout, string BlockPath, string Layer, string RawText, string Text, string? Tag,
    double[] Position, double Height, double Rotation, bool Invisible, double[] AxisX, double[] AxisY, bool TextRequiresReview);
record DimensionRecord(string Id, string Handle, string Layout, string BlockPath, string Kind, double Measurement, string? OverrideText,
    string? ApiFormattedText, string[] CachedGraphicsText, bool HasStyleOverride, string? FormattingError, double[] Position, string? BaseStyle, double? BaseLinearScaleFactor);
record GeometryRecord(string Id, string Layout, string BlockPath, string Kind, string Layer, double[][] Points, bool Closed, bool DimensionGraphics);
