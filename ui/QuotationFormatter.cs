using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ClosedXML.Excel;

namespace CadReviewUI;

/// <summary>
/// Produces a presentation copy only after the guarded engine export has succeeded.
/// The engine's workbook and audit records remain the authority for the decisions.
/// </summary>
public static class QuotationFormatter
{
    private const int FirstRow = 6;
    private const int TemplateDetailRows = 48;
    private const int TemplateTotalRow = 54;
    private const int MaxItems = 10000;
    private const long MaxInputBytes = 128L * 1024 * 1024;
    private const string OutputName = "确认明细.xlsx";
    private const string LinkName = "报价样式关联.json";
    private const string EngineSheetName = "确认明细";
    private const string SheetName = "报价单样式";
    private const string ExpectedTemplateSha256 = "14323DB85ADB0F4C2AB80653248D76471D85247F7A9EC3812D1D8D2287C17F46";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] EngineHeaders =
        ["样本", "图号", "部品名", "材质", "表面处理", "数量", "单位", "单价", "金额", "复核说明", "原图文件", "记录ID"];

    private sealed record ConfirmedRow(string Id, string FileId, string DrawingNumber, string? Name,
        string Material, string Treatment, decimal Quantity, string Unit, string Reason, string FileName);

    /// <summary>
    /// Called while TaskSession still holds its review/archive locks, after the guarded
    /// engine returns ready. The two final files must not already exist.
    /// </summary>
    public static string Write(string engineOutputDir, string destinationDir, ValidationDto result, Dataset dataset) =>
        WriteVerified(engineOutputDir, destinationDir, result, dataset).Path;

    // Return the independently validated hash in memory while the TaskSession
    // locks are still held; the later copy must not trust the sidecar alone.
    internal static (string Path, string Sha256, string AssociationSha256) WriteVerified(string engineOutputDir, string destinationDir, ValidationDto result, Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(dataset);
        string destination = Path.GetFullPath(destinationDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string engine = Path.GetFullPath(engineOutputDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(destination) || !Directory.Exists(engine) ||
            !string.Equals(engine, Path.Combine(destination, "复核记录"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("导出目录或复核记录目录无效。");
        RejectReparseDirectories(destination);
        RejectReparseDirectories(engine);

        string templatePath = Path.Combine(AppContext.BaseDirectory, "templates", "ExactQuotationTemplate-native-korean-header.xlsx");
        string originalPath = Path.Combine(engine, OutputName);
        string auditPath = Path.Combine(engine, "确认结果.json");
        string validationPath = Path.Combine(engine, "validation.json");
        string finalPath = Path.Combine(destination, OutputName);
        string linkPath = Path.Combine(destination, LinkName);
        string lockPath = Path.Combine(destination, ".quotation-format.lock");
        if (File.Exists(finalPath) || Directory.Exists(finalPath) || File.Exists(linkPath) || Directory.Exists(linkPath))
            throw new IOException("目标文件已存在，不能覆盖。");

        // CreateNew is both a concurrency guard and a refusal to overwrite a stale lock.
        using var publishLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        using var original = HoldInput(originalPath);
        using var audit = HoldInput(auditPath);
        using var validation = HoldInput(validationPath);
        using var template = HoldInput(templatePath);
        byte[] originalBytes = ReadSnapshot(original);
        byte[] auditBytes = ReadSnapshot(audit);
        byte[] validationBytes = ReadSnapshot(validation);
        byte[] templateBytes = ReadSnapshot(template);
        string originalHash = Hash(originalBytes);
        string auditHash = Hash(auditBytes);
        string validationHash = Hash(validationBytes);
        string templateHash = Hash(templateBytes);
        if (templateHash != ExpectedTemplateSha256)
            throw new InvalidDataException("样式模板哈希不符合已确认版本，已停止导出。");

        using var auditDoc = ParseJson(auditBytes);
        using var validationDoc = ParseJson(validationBytes);
        var rows = CheckAudit(auditDoc.RootElement, validationDoc.RootElement, result, dataset, originalHash);
        CheckEngineWorkbook(originalBytes, rows);

        string nonce = Guid.NewGuid().ToString("N");
        string stagedWorkbook = Path.Combine(destination, ".quotation-" + nonce + ".tmp.xlsx");
        string stagedLink = Path.Combine(destination, ".quotation-" + nonce + ".tmp.json");
        var writerRows = rows.Select(r => new ExactQuotationRow(r.Id, r.FileId, r.DrawingNumber, r.Name,
            r.Material, r.Treatment, r.Quantity, r.Unit, r.FileName)).ToArray();
        byte[] builtBytes = ExactTemplateWriter.Build(templateBytes, writerRows);
        if (builtBytes == null || builtBytes.Length == 0 || builtBytes.LongLength > MaxInputBytes)
            throw new InvalidDataException("格式化结果大小不在允许范围内。");
        using (var file = new FileStream(stagedWorkbook, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(builtBytes);
            file.Flush(true);
        }

        byte[] outputBytes;
        using (var file = HoldInput(stagedWorkbook)) outputBytes = ReadSnapshot(file);
        CheckPublishedWorkbook(outputBytes, templateBytes, rows);
        string outputHash = Hash(outputBytes);
        var association = new
        {
            schema_version = 1,
            kind = "quotation_presentation",
            created_utc = DateTimeOffset.UtcNow,
            engine_workbook = "复核记录/确认明细.xlsx",
            engine_workbook_sha256 = originalHash,
            confirmation_record_sha256 = auditHash,
            validation_record_sha256 = validationHash,
            template_sha256 = templateHash,
            formatted_workbook = OutputName,
            formatted_workbook_sha256 = outputHash,
            source_sha256 = RequiredHash(auditDoc.RootElement, "source_sha256"),
            review_sha256 = RequiredHash(auditDoc.RootElement, "review_sha256"),
            accepted_count = rows.Count,
            first_detail_row = FirstRow,
            row_order = "same as accepted in confirmation record"
        };
        byte[] linkBytes = JsonSerializer.SerializeToUtf8Bytes(association, Json.Options);
        using (var file = new FileStream(stagedLink, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(linkBytes);
            file.Flush(true);
        }

        // Recheck the locked input handles immediately before publication. The JSON
        // is published first; the final XLSX name is the completion marker.
        if (Hash(ReadSnapshot(original)) != originalHash || Hash(ReadSnapshot(audit)) != auditHash ||
            Hash(ReadSnapshot(validation)) != validationHash || Hash(ReadSnapshot(template)) != templateHash)
            throw new InvalidDataException("导出来源或模板在格式化期间发生变化。");
        File.Move(stagedLink, linkPath, false);
        File.Move(stagedWorkbook, finalPath, false);
        using (var final = HoldInput(finalPath))
        {
            byte[] finalBytes = ReadSnapshot(final);
            if (Hash(finalBytes) != outputHash) throw new InvalidDataException("发布后的文件哈希不一致。");
            CheckPublishedWorkbook(finalBytes, templateBytes, rows);
        }
        string associationHash;
        using (var link = HoldInput(linkPath))
        {
            associationHash = Hash(ReadSnapshot(link));
            if (associationHash != Hash(linkBytes)) throw new InvalidDataException("关联记录哈希不一致。");
        }
        return (finalPath, outputHash, associationHash);
    }

    private static void RejectReparseDirectories(string path)
    {
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("导出路径不能包含目录链接或重解析点。");
    }

    private static FileStream HoldInput(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("输入文件不能是链接或重解析点。");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static byte[] ReadSnapshot(FileStream stream)
    {
        if (stream.Length < 1 || stream.Length > MaxInputBytes)
            throw new InvalidDataException("输入文件大小不在允许范围内。");
        stream.Position = 0;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (buffer.Length != stream.Length) throw new IOException("读取输入文件时长度发生变化。");
        return buffer.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static JsonDocument ParseJson(byte[] bytes)
    {
        // Engine JSON can be UTF-8 with or without a BOM.
        var data = bytes.AsMemory();
        if (data.Length >= 3 && data.Span[0] == 0xEF && data.Span[1] == 0xBB && data.Span[2] == 0xBF)
            data = data[3..];
        var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 128 });
        try { RejectDuplicateProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!seen.Add(p.Name)) throw new InvalidDataException("审计 JSON 包含重复字段。");
                RejectDuplicateProperties(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static JsonElement Property(JsonElement obj, string name, JsonValueKind kind)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new InvalidDataException("审计记录缺少必需字段或类型无效。");
        return value;
    }

    private static string Text(JsonElement obj, string name) => Property(obj, name, JsonValueKind.String).GetString()!;
    private static string? NullableText(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new InvalidDataException("审计记录的文本字段无效。");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }
    private static decimal? NullableNumber(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
            throw new InvalidDataException("审计记录的数量字段缺失。");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
            throw new InvalidDataException("审计记录的数量不是有效数字。");
        return number;
    }
    private static string RequiredHash(JsonElement obj, string name)
    {
        string hash = Text(obj, name);
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("审计哈希无效。");
        return hash.ToUpperInvariant();
    }
    private static void SameHash(string left, string? right)
    {
        if (right == null || !string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("复核结果或来源哈希不一致。");
    }
    private static void SameArray(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != JsonValueKind.Array || right.ValueKind != JsonValueKind.Array || !JsonElement.DeepEquals(left, right))
            throw new InvalidDataException("复核记录与校验结果不一致。");
    }
    private static void SameList(JsonElement array, List<JsonElement> list)
    {
        if (array.ValueKind != JsonValueKind.Array || list == null || array.GetArrayLength() != list.Count)
            throw new InvalidDataException("引擎返回的记录数量不一致。");
        int i = 0;
        foreach (var entry in array.EnumerateArray())
        {
            RejectDuplicateProperties(list[i]);
            if (!JsonElement.DeepEquals(entry, list[i++])) throw new InvalidDataException("引擎返回的记录与审计不一致。");
        }
    }

    private static List<ConfirmedRow> CheckAudit(JsonElement record, JsonElement validation, ValidationDto result,
        Dataset dataset, string workbookHash)
    {
        if (result.Status != "ready" || result.Pending != 0 || result.Errors == null || result.Errors.Count != 0 ||
            dataset.SchemaVersion != 1 || string.IsNullOrWhiteSpace(dataset.TaskId))
            throw new InvalidDataException("引擎结果不是可导出的已复核状态。");
        if (Property(record, "schema_version", JsonValueKind.Number).GetInt32() != 1 ||
            Text(validation, "status") != "ready" || Property(validation, "pending", JsonValueKind.Number).GetInt32() != 0 ||
            Property(validation, "errors", JsonValueKind.Array).GetArrayLength() != 0 ||
            Text(record, "task_id") != dataset.TaskId || Text(validation, "task_id") != dataset.TaskId || result.TaskId != dataset.TaskId)
            throw new InvalidDataException("任务或复核状态不一致。");
        SameHash(RequiredHash(record, "output_sha256"), workbookHash);
        SameHash(RequiredHash(record, "source_sha256"), RequiredHash(validation, "manifest_sha256"));
        SameHash(RequiredHash(record, "review_sha256"), RequiredHash(validation, "review_sha256"));
        SameHash(RequiredHash(record, "review_sha256"), result.ReviewSha256);
        var accepted = Property(record, "accepted", JsonValueKind.Array);
        var excluded = Property(record, "excluded", JsonValueKind.Array);
        SameArray(accepted, Property(validation, "accepted", JsonValueKind.Array));
        SameArray(excluded, Property(validation, "excluded", JsonValueKind.Array));
        SameArray(Property(record, "files", JsonValueKind.Array), Property(validation, "files", JsonValueKind.Array));
        SameList(accepted, result.Accepted);
        SameList(excluded, result.Excluded);
        if (accepted.GetArrayLength() < 1 || accepted.GetArrayLength() > MaxItems ||
            dataset.Items == null || dataset.Files == null || dataset.Items.Count > MaxItems)
            throw new InvalidDataException("复核明细数量超出允许范围。");
        var items = new Dictionary<string, DsItem>(StringComparer.Ordinal);
        var files = new Dictionary<string, DsFile>(StringComparer.Ordinal);
        foreach (var item in dataset.Items)
            if (item == null || string.IsNullOrEmpty(item.Id) || !items.TryAdd(item.Id, item))
                throw new InvalidDataException("来源数据存在缺失或重复的明细标识。");
        foreach (var file in dataset.Files)
            if (file == null || string.IsNullOrEmpty(file.Id) || !files.TryAdd(file.Id, file))
                throw new InvalidDataException("来源数据存在缺失或重复的文件标识。");

        var covered = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ConfirmedRow>(accepted.GetArrayLength());
        foreach (var entry in accepted.EnumerateArray())
        {
            string id = Text(entry, "id"), fileId = Text(entry, "file_id"), mode = Text(entry, "mode");
            if (!covered.Add(id) || !items.TryGetValue(id, out var item) || item.FileId != fileId ||
                !files.TryGetValue(fileId, out var file) || mode is not ("采用图纸" or "人工修订") || item.Cad == null)
                throw new InvalidDataException("纳入明细与来源数据不一致。");
            var original = Property(entry, "original", JsonValueKind.Object);
            if (Text(original, "drawing_number") != item.Cad.DrawingNumber || NullableText(original, "name") != item.Cad.Name ||
                NullableText(original, "material") != item.Cad.Material || NullableText(original, "treatment") != item.Cad.Treatment ||
                NullableNumber(original, "quantity") != item.Cad.Quantity || NullableText(original, "unit") != item.Cad.Unit)
                throw new InvalidDataException("纳入明细的原始字段与来源不一致。");
            var parents = Property(entry, "parents", JsonValueKind.Array);
            if (item.Parents == null || parents.GetArrayLength() != item.Parents.Length ||
                !parents.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).SequenceEqual(item.Parents))
                throw new InvalidDataException("纳入明细的范围来源不一致。");
            var final = Property(entry, "final", JsonValueKind.Object);
            string number = Text(final, "drawing_number"), material = Text(final, "material"), treatment = Text(final, "treatment");
            string unit = Text(final, "unit"); decimal quantity = NullableNumber(final, "quantity") ?? throw new InvalidDataException("确认数量为空。");
            if (!Rules.Meaningful(number) || !Rules.Meaningful(material) || !Rules.Meaningful(treatment) ||
                !Rules.Meaningful(unit) || quantity < 0)
                throw new InvalidDataException("确认值不完整，或单位不能安全显示。");
            _ = QuantityFormat(quantity, unit); // Retain the existing unit-safety guard for the audit record.
            // Do not silently round a decimal while converting it to an Excel number.
            double asDouble = (double)quantity;
            if (!double.IsFinite(asDouble) || !decimal.TryParse(asDouble.ToString("R", Invariant), NumberStyles.Float, Invariant, out var roundTrip) || roundTrip != quantity)
                throw new InvalidDataException("确认数量不能无损写入 Excel。");
            rows.Add(new ConfirmedRow(id, fileId, number, NullableText(final, "name"), material, treatment,
                quantity, unit, Text(entry, "reason"), file.Name));
        }
        foreach (var entry in excluded.EnumerateArray())
        {
            string id = Text(entry, "id"), fileId = Text(entry, "file_id");
            if (!covered.Add(id) || !items.TryGetValue(id, out var item) || item.FileId != fileId || !files.ContainsKey(fileId))
                throw new InvalidDataException("排除明细与来源数据不一致。");
        }
        if (covered.Count != items.Count) throw new InvalidDataException("审计记录未覆盖全部来源明细。");
        return rows;
    }

    private static void CheckEngineWorkbook(byte[] bytes, IReadOnlyList<ConfirmedRow> rows)
    {
        using var stream = new MemoryStream(bytes, false);
        using var book = new XLWorkbook(stream, new ClosedXML.Excel.LoadOptions { RecalculateAllFormulas = false });
        if (book.Worksheets.Count != 1 || book.Worksheet(1).Name != EngineSheetName)
            throw new InvalidDataException("引擎工作簿结构无效。");
        var sheet = book.Worksheet(1);
        if (sheet.LastRowUsed()?.RowNumber() != rows.Count + 7)
            throw new InvalidDataException("引擎工作簿行数与审计不一致。");
        for (int c = 1; c <= EngineHeaders.Length; c++) SameText(sheet.Cell(7, c), EngineHeaders[c - 1]);
        for (int i = 0; i < rows.Count; i++)
        {
            int n = i + 8; var r = rows[i];
            SameText(sheet.Cell(n, 1), r.FileId); SameText(sheet.Cell(n, 2), r.DrawingNumber);
            SameText(sheet.Cell(n, 3), r.Name); SameText(sheet.Cell(n, 4), r.Material);
            SameText(sheet.Cell(n, 5), r.Treatment); SameNumber(sheet.Cell(n, 6), r.Quantity);
            SameText(sheet.Cell(n, 7), r.Unit);
            if (!sheet.Cell(n, 8).IsEmpty() || sheet.Cell(n, 8).HasFormula ||
                sheet.Cell(n, 9).FormulaA1 != $"IF(H{n}=\"\",\"\",F{n}*H{n})")
                throw new InvalidDataException("引擎单价或金额公式不符合确认记录。");
            SameText(sheet.Cell(n, 10), r.Reason); SameText(sheet.Cell(n, 11), r.FileName); SameText(sheet.Cell(n, 12), r.Id);
        }
    }

    private static void SameText(IXLCell cell, string? expected)
    {
        if (cell.HasFormula || (string.IsNullOrEmpty(expected) ? !cell.IsEmpty() :
            cell.DataType != XLDataType.Text || cell.GetString() != expected))
            throw new InvalidDataException("工作簿文本与确认记录不一致。");
    }
    private static void SameNumber(IXLCell cell, decimal expected)
    {
        if (cell.HasFormula || cell.DataType != XLDataType.Number ||
            !decimal.TryParse(cell.GetDouble().ToString("R", Invariant), NumberStyles.Float, Invariant, out var value) || value != expected)
            throw new InvalidDataException("工作簿数量与确认记录不一致。");
    }

    private static string QuantityText(decimal value) => value.ToString("0.############################", Invariant);
    private static string DrawingCaption(IReadOnlyList<ConfirmedRow> rows)
    {
        var byFile = rows.GroupBy(x => x.FileId, StringComparer.Ordinal).ToArray();
        if (byFile.Length == 0) throw new InvalidDataException("没有可导出的图纸。");
        if (byFile.Length > 1) return byFile.Length.ToString(Invariant) + " 份图纸";
        string name = Path.GetFileNameWithoutExtension(byFile[0].First().FileName);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("纳入图纸的文件名无效。");
        return name;
    }

    private static string QuantityFormat(decimal quantity, string unit)
    {
        string number = QuantityText(quantity);
        int point = number.IndexOf('.');
        int decimals = point < 0 ? 0 : number.Length - point - 1;
        var format = new StringBuilder("0" + (decimals == 0 ? "" : "." + new string('#', decimals)) + " \"");
        for (int i = 0; i < unit.Length; i++)
        {
            char ch = unit[i];
            if (char.IsControl(ch) || ch is '\uFFFE' or '\uFFFF')
                throw new InvalidDataException("单位包含无法安全显示的控制字符。");
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= unit.Length || !char.IsLowSurrogate(unit[i + 1]))
                    throw new InvalidDataException("单位包含无效的 Unicode 字符。");
                format.Append(ch).Append(unit[++i]);
            }
            else if (char.IsLowSurrogate(ch))
                throw new InvalidDataException("单位包含无效的 Unicode 字符。");
            else if (ch is '"' or '\\')
            {
                // End the quoted literal, escape this one character, and resume it.
                format.Append('"').Append('\\').Append(ch).Append('"');
            }
            else format.Append(ch);
        }
        format.Append('"');
        if (format.Length > 255) throw new InvalidDataException("单位过长，超出 Excel 数字格式长度限制。");
        return format.ToString();
    }
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypeNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    // Reopen the staged file, compare its contents to the confirmed records, and
    // compare every fixed part of the native template before making it visible.
    private static void CheckPublishedWorkbook(byte[] bytes, byte[] templateBytes, IReadOnlyList<ConfirmedRow> rows)
    {
        int total = FirstRow + Math.Max(TemplateDetailRows, rows.Count);
        CheckNativeParts(templateBytes, bytes, total);
        using var templateStream = new MemoryStream(templateBytes, false);
        using var outputStream = new MemoryStream(bytes, false);
        using var template = new XLWorkbook(templateStream, new ClosedXML.Excel.LoadOptions { RecalculateAllFormulas = false });
        using var output = new XLWorkbook(outputStream, new ClosedXML.Excel.LoadOptions { RecalculateAllFormulas = false });
        if (template.Worksheets.Count != 1 || output.Worksheets.Count != 1 ||
            template.Worksheet(1).Name != SheetName || output.Worksheet(1).Name != SheetName)
            throw new InvalidDataException("原模板或导出结果的工作表不正确。");
        var src = template.Worksheet(1);
        var dst = output.Worksheet(1);

        // C2 is the selected drawing caption; every other title and header cell,
        // including intentionally fixed text, must match the native template.
        for (int row = 1; row <= 5; row++)
        {
            for (int col = 2; col <= 9; col++)
            {
                var expected = src.Cell(row, col);
                var actual = dst.Cell(row, col);
                if (!expected.Style.Equals(actual.Style))
                    throw new InvalidDataException("标题或表头样式与原模板不一致。");
                if (row == 2 && col == 3) SameText(actual, DrawingCaption(rows));
                else
                {
                    if (expected.HasFormula || (!expected.IsEmpty() && expected.DataType != XLDataType.Text))
                        throw new InvalidDataException("原模板标题或表头类型无效。");
                    SameText(actual, expected.IsEmpty() ? null : expected.GetString());
                }
            }
        }

        for (int i = 0; i < total - FirstRow; i++)
        {
            int row = FirstRow + i;
            int sourceRow = row < TemplateTotalRow ? row : (row % 2 == 0 ? 52 : 53);
            for (int col = 2; col <= 9; col++)
                if (!src.Cell(sourceRow, col).Style.Equals(dst.Cell(row, col).Style))
                    throw new InvalidDataException("明细样式或金额会计格式与原模板不一致。");
            SameNumber(dst.Cell(row, 2), i + 1);
            if (i < rows.Count)
            {
                var confirmed = rows[i];
                SameText(dst.Cell(row, 3), confirmed.DrawingNumber);
                SameText(dst.Cell(row, 4), confirmed.Name);
                SameText(dst.Cell(row, 5), confirmed.Material);
                SameText(dst.Cell(row, 6), confirmed.Treatment);
                SameNumber(dst.Cell(row, 7), confirmed.Quantity);
            }
            else
            {
                for (int col = 3; col <= 7; col++)
                    if (!dst.Cell(row, col).IsEmpty() || dst.Cell(row, col).HasFormula)
                        throw new InvalidDataException("空白明细行包含未确认的数据。");
            }
            if (!dst.Cell(row, 8).IsEmpty() || dst.Cell(row, 8).HasFormula ||
                dst.Cell(row, 9).FormulaA1 != $"G{row}*H{row}")
                throw new InvalidDataException("单价不为空，或金额公式与原表不一致。");
        }
        for (int col = 2; col <= 9; col++)
            if (!src.Cell(TemplateTotalRow, col).Style.Equals(dst.Cell(total, col).Style))
                throw new InvalidDataException("合计行样式与原模板不一致。");
        SameText(dst.Cell(total, 2), src.Cell(TemplateTotalRow, 2).GetString());
        for (int col = 3; col <= 8; col++)
            if (!dst.Cell(total, col).IsEmpty() || dst.Cell(total, col).HasFormula)
                throw new InvalidDataException("合计行包含未确认的数据。");
        if (dst.Cell(total, 9).FormulaA1 != $"SUM(I{FirstRow}:I{total - 1})" ||
            dst.CellsUsed().Any(cell => cell.Address.ColumnNumber > 9 || cell.Address.RowNumber > total))
            throw new InvalidDataException("合计公式或数据范围与原表不一致。");
    }

    private static void CheckNativeParts(byte[] templateBytes, byte[] outputBytes, int total)
    {
        using var sourceStream = new MemoryStream(templateBytes, false);
        using var resultStream = new MemoryStream(outputBytes, false);
        using var source = new ZipArchive(sourceStream, ZipArchiveMode.Read);
        using var result = new ZipArchive(resultStream, ZipArchiveMode.Read);
        var sourceNames = PartNames(source);
        var resultNames = PartNames(result);
        // The calculation chain may be removed when formulas are extended.
        if (!resultNames.Contains("xl/calcChain.xml")) sourceNames.Remove("xl/calcChain.xml");
        if (!sourceNames.SetEquals(resultNames) || (total > TemplateTotalRow && resultNames.Contains("xl/calcChain.xml")))
            throw new InvalidDataException("结果的原生模板部件不完整或含有额外部件。");
        var changed = new HashSet<string>(StringComparer.Ordinal)
        {
            "[Content_Types].xml", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/worksheets/sheet1.xml"
        };
        foreach (string part in sourceNames)
            if (!changed.Contains(part) && !PartBytes(source, part).SequenceEqual(PartBytes(result, part)))
                throw new InvalidDataException("结果未保留原模板的样式、绘图、打印或其他固定部件。");
        if (!sourceNames.Any(n => n.StartsWith("xl/drawings/", StringComparison.Ordinal)) ||
            !sourceNames.Contains("xl/worksheets/_rels/sheet1.xml.rels"))
            throw new InvalidDataException("原模板缺少必需的绘图及其关系。");

        // Only the removed calculation chain may change the package relationships
        // and content types; this also keeps drawing and printer relationships intact.
        XElement sourceRels = ReadPart(source, "xl/_rels/workbook.xml.rels").Root!;
        XElement resultRels = ReadPart(result, "xl/_rels/workbook.xml.rels").Root!;
        XElement sourceTypes = ReadPart(source, "[Content_Types].xml").Root!;
        XElement resultTypes = ReadPart(result, "[Content_Types].xml").Root!;
        bool hasChain = resultNames.Contains("xl/calcChain.xml");
        int chainRelationships = resultRels.Elements(PackageRelNs + "Relationship")
            .Count(e => ((string?)e.Attribute("Type"))?.EndsWith("/calcChain", StringComparison.Ordinal) == true);
        int chainTypes = resultTypes.Elements(ContentTypeNs + "Override")
            .Count(e => ((string?)e.Attribute("ContentType"))?.EndsWith(".calcChain+xml", StringComparison.Ordinal) == true);
        if (chainRelationships != (hasChain ? 1 : 0) || chainTypes != (hasChain ? 1 : 0) ||
            (hasChain && (!SameXml(sourceRels, resultRels) || !SameXml(sourceTypes, resultTypes))))
            throw new InvalidDataException("结果的计算链部件或关系不一致。");
        foreach (var element in sourceRels.Elements(PackageRelNs + "Relationship")
            .Where(e => ((string?)e.Attribute("Type"))?.EndsWith("/calcChain", StringComparison.Ordinal) == true).ToArray())
            element.Remove();
        foreach (var element in resultRels.Elements(PackageRelNs + "Relationship")
            .Where(e => ((string?)e.Attribute("Type"))?.EndsWith("/calcChain", StringComparison.Ordinal) == true).ToArray())
            element.Remove();
        if (!SameXml(sourceRels, resultRels)) throw new InvalidDataException("工作簿关系与原模板不一致。");
        foreach (var element in sourceTypes.Elements(ContentTypeNs + "Override")
            .Where(e => ((string?)e.Attribute("ContentType"))?.EndsWith(".calcChain+xml", StringComparison.Ordinal) == true).ToArray())
            element.Remove();
        foreach (var element in resultTypes.Elements(ContentTypeNs + "Override")
            .Where(e => ((string?)e.Attribute("ContentType"))?.EndsWith(".calcChain+xml", StringComparison.Ordinal) == true).ToArray())
            element.Remove();
        if (!SameXml(sourceTypes, resultTypes)) throw new InvalidDataException("工作簿部件类型与原模板不一致。");

        var sourceBook = ReadPart(source, "xl/workbook.xml");
        var resultBook = ReadPart(result, "xl/workbook.xml");
        CheckDefinedNames(sourceBook, resultBook, total);
        var sourceBookFixed = new XElement(sourceBook.Root ?? throw new InvalidDataException("原模板工作簿无效。"));
        var resultBookFixed = new XElement(resultBook.Root ?? throw new InvalidDataException("结果工作簿无效。"));
        sourceBookFixed.Element(SpreadsheetNs + "definedNames")?.Remove();
        resultBookFixed.Element(SpreadsheetNs + "definedNames")?.Remove();
        sourceBookFixed.Element(SpreadsheetNs + "calcPr")?.Remove();
        resultBookFixed.Element(SpreadsheetNs + "calcPr")?.Remove();
        if (!SameXml(sourceBookFixed, resultBookFixed)) throw new InvalidDataException("结果工作簿的固定内容与原模板不一致。");
        XElement? calc = resultBook.Root?.Element(SpreadsheetNs + "calcPr");
        var expectedCalc = new XElement(sourceBook.Root?.Element(SpreadsheetNs + "calcPr")
            ?? throw new InvalidDataException("原模板缺少计算设置。"));
        if (total > TemplateTotalRow)
        {
            expectedCalc.SetAttributeValue("calcMode", "auto");
            expectedCalc.SetAttributeValue("fullCalcOnLoad", "1");
            expectedCalc.SetAttributeValue("forceFullCalc", "1");
        }
        if (!SameXml(expectedCalc, calc)) throw new InvalidDataException("结果的计算设置与原模板不一致。");

        var sourceSheet = ReadPart(source, "xl/worksheets/sheet1.xml");
        var resultSheet = ReadPart(result, "xl/worksheets/sheet1.xml");
        XElement src = sourceSheet.Root ?? throw new InvalidDataException("原模板工作表无效。");
        XElement dst = resultSheet.Root ?? throw new InvalidDataException("结果工作表无效。");
        var sourceSheetFixed = new XElement(src);
        var resultSheetFixed = new XElement(dst);
        foreach (string name in new[] { "dimension", "sheetData", "autoFilter", "mergeCells" })
        {
            sourceSheetFixed.Element(SpreadsheetNs + name)?.Remove();
            resultSheetFixed.Element(SpreadsheetNs + name)?.Remove();
        }
        if (!SameXml(sourceSheetFixed, resultSheetFixed))
            throw new InvalidDataException("结果的列宽、打印设置或绘图与原模板不一致。");

        var filter = new XElement(src.Element(SpreadsheetNs + "autoFilter") ?? throw new InvalidDataException("原模板缺少筛选。"));
        filter.SetAttributeValue("ref", $"B5:I{total}");
        if (!SameXml(filter, dst.Element(SpreadsheetNs + "autoFilter")))
            throw new InvalidDataException("结果筛选范围与原模板不一致。");
        var merges = new XElement(src.Element(SpreadsheetNs + "mergeCells") ?? throw new InvalidDataException("原模板缺少合并区域。"));
        var totalMerge = merges.Elements(SpreadsheetNs + "mergeCell").SingleOrDefault(e => (string?)e.Attribute("ref") == "B54:G54")
            ?? throw new InvalidDataException("原模板缺少合计行合并区域。");
        totalMerge.SetAttributeValue("ref", $"B{total}:G{total}");
        if (!SameXml(merges, dst.Element(SpreadsheetNs + "mergeCells")))
            throw new InvalidDataException("结果的合并区域与原模板不一致。");
        var dimension = new XElement(src.Element(SpreadsheetNs + "dimension") ?? throw new InvalidDataException("原模板缺少使用区域。"));
        dimension.SetAttributeValue("ref", $"B1:I{total}");
        if (!SameXml(dimension, dst.Element(SpreadsheetNs + "dimension")))
            throw new InvalidDataException("结果的使用区域不正确。");

        var srcRows = XmlRows(src);
        var dstRows = XmlRows(dst);
        for (int row = 1; row <= total; row++)
        {
            int originalRow = row == total ? TemplateTotalRow :
                row < TemplateTotalRow ? row : (row % 2 == 0 ? 52 : 53);
            if (!srcRows.TryGetValue(originalRow, out var original) || !dstRows.TryGetValue(row, out var actual))
                throw new InvalidDataException("结果缺少原模板行。");
            var originalAttrs = new XElement(original.Name, original.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.LocalName != "r"));
            var actualAttrs = new XElement(actual.Name, actual.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.LocalName != "r"));
            if (!SameXml(originalAttrs, actualAttrs)) throw new InvalidDataException("结果的行高或行属性与原模板不一致。");
            if (row >= FirstRow)
            {
                var amount = actual.Elements(SpreadsheetNs + "c").SingleOrDefault(c => (string?)c.Attribute("r") == $"I{row}");
                string? cached = (string?)amount?.Element(SpreadsheetNs + "v");
                string? type = (string?)amount?.Attribute("t");
                if (amount?.Element(SpreadsheetNs + "f") == null || (type != null && type != "n") ||
                    !decimal.TryParse(cached, NumberStyles.Float, Invariant, out decimal value) || value != 0)
                    throw new InvalidDataException("空单价时金额或合计的缓存结果不为零。");
            }
        }
        if (dstRows.Keys.Any(r => r > total)) throw new InvalidDataException("结果包含额外工作表行。");
    }

    private static void CheckDefinedNames(XDocument source, XDocument result, int total)
    {
        var sourceNames = source.Root?.Element(SpreadsheetNs + "definedNames")?.Elements(SpreadsheetNs + "definedName").ToArray()
            ?? throw new InvalidDataException("原模板缺少打印及筛选定义。");
        var resultNames = result.Root?.Element(SpreadsheetNs + "definedNames")?.Elements(SpreadsheetNs + "definedName").ToArray()
            ?? throw new InvalidDataException("结果缺少打印及筛选定义。");
        if (sourceNames.Length != resultNames.Length) throw new InvalidDataException("结果的工作簿名称定义不一致。");
        for (int i = 0; i < sourceNames.Length; i++)
        {
            var expected = new XElement(sourceNames[i]);
            string? name = (string?)expected.Attribute("name");
            if (name == "_xlnm.Print_Area") expected.Value = expected.Value.Replace("$I$55", "$I$" + (total + 1), StringComparison.Ordinal);
            if (name == "_xlnm._FilterDatabase") expected.Value = expected.Value.Replace("$I$54", "$I$" + total, StringComparison.Ordinal);
            if (!SameXml(expected, resultNames[i])) throw new InvalidDataException("结果的打印区域、标题行或筛选名称不正确。");
        }
    }

    private static Dictionary<int, XElement> XmlRows(XElement sheet) =>
        (sheet.Element(SpreadsheetNs + "sheetData") ?? throw new InvalidDataException("工作表缺少数据。"))
        .Elements(SpreadsheetNs + "row").ToDictionary(row => (int?)row.Attribute("r") ?? throw new InvalidDataException("工作表行号无效。"));

    private static HashSet<string> PartNames(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
            if (!names.Add(entry.FullName) || !folded.Add(entry.FullName)) throw new InvalidDataException("工作簿包含重复部件。");
        }
        return names;
    }

    private static byte[] PartBytes(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException("工作簿缺少原模板部件。");
        if (entry.Length > MaxInputBytes) throw new InvalidDataException("工作簿部件过大。");
        using var source = entry.Open();
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static XDocument ReadPart(ZipArchive archive, string name)
    {
        using var bytes = new MemoryStream(PartBytes(archive, name), false);
        using var reader = System.Xml.XmlReader.Create(bytes, new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null
        });
        return XDocument.Load(reader);
    }

    private static bool SameXml(XElement? left, XElement? right)
    {
        if (left == null || right == null) return left == null && right == null;
        if (left.Name != right.Name) return false;
        var la = left.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToArray();
        var ra = right.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToArray();
        if (la.Length != ra.Length || la.Where((a, i) => a.Name != ra[i].Name || a.Value != ra[i].Value).Any()) return false;
        var le = left.Elements().ToArray();
        var re = right.Elements().ToArray();
        if (le.Length != re.Length) return false;
        string lt = string.Concat(left.Nodes().OfType<XText>().Select(t => t.Value));
        string rt = string.Concat(right.Nodes().OfType<XText>().Select(t => t.Value));
        if (le.Length != 0 && string.IsNullOrWhiteSpace(lt) && string.IsNullOrWhiteSpace(rt)) { lt = ""; rt = ""; }
        if (lt != rt) return false;
        for (int i = 0; i < le.Length; i++) if (!SameXml(le[i], re[i])) return false;
        return true;
    }
}
