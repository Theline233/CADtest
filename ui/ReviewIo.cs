using System.Globalization;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using XCell = DocumentFormat.OpenXml.Spreadsheet.Cell;
using XCellValue = DocumentFormat.OpenXml.Spreadsheet.CellValue;
using XCellValues = DocumentFormat.OpenXml.Spreadsheet.CellValues;
using XInlineString = DocumentFormat.OpenXml.Spreadsheet.InlineString;
using XRow = DocumentFormat.OpenXml.Spreadsheet.Row;
using XRun = DocumentFormat.OpenXml.Spreadsheet.Run;
using XSharedStringItem = DocumentFormat.OpenXml.Spreadsheet.SharedStringItem;
using XSheet = DocumentFormat.OpenXml.Spreadsheet.Sheet;
using XText = DocumentFormat.OpenXml.Spreadsheet.Text;

namespace CadReviewUI;

/// <summary>读写复核 Excel 的黄色确认区。只改“处理方式/确认*/复核说明”和文件页的“核查状态/说明”，
/// 其余原值列一律不动，因此仍能通过引擎对原值的逐格校验。按表头名定位列，不依赖列序。</summary>
public static class ReviewIo
{
    public const string ReviewSheet = "复核明细", FilesSheet = "文件诊断";
    const int HeaderRow = 7, FirstRow = 8;

    static Dictionary<string, int> Columns(IXLWorksheet ws)
    {
        var map = new Dictionary<string, int>();
        foreach (var c in ws.Row(HeaderRow).CellsUsed())
        {
            if (c.HasFormula) throw new InvalidDataException("表头不能包含公式。");
            var name = c.GetString().Trim();
            if (name.Length != 0 && !map.TryAdd(name, c.Address.ColumnNumber))
                throw new InvalidDataException("复核表存在重复表头：" + name);
        }
        return map;
    }

    static int Need(Dictionary<string, int> map, string name) =>
        map.TryGetValue(name, out var v) ? v : throw new InvalidDataException("复核表缺少列：" + name);

    static void SetText(IXLCell cell, string value)
    {
        if (string.IsNullOrEmpty(value)) cell.Clear(XLClearOptions.Contents);
        else cell.Value = value;
    }

    // Compare decimal values without rounding: significant digits plus base-10 exponent.
    // The caller first checks syntax with decimal.TryParse using the invariant culture.
    static bool TryCanonicalQuantity(string text, out string digits, out long exponent)
    {
        digits = "";
        exponent = 0;
        int e = text.IndexOfAny(['e', 'E']);
        var mantissa = e < 0 ? text : text[..e];
        if (e >= 0 && !long.TryParse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent)) return false;
        if (mantissa.StartsWith('+') || mantissa.StartsWith('-')) mantissa = mantissa[1..];
        int point = mantissa.IndexOf('.');
        int fractional = point < 0 ? 0 : mantissa.Length - point - 1;
        digits = mantissa.Replace(".", "").TrimStart('0');
        if (digits.Length == 0) { digits = "0"; exponent = 0; return true; }
        int end = digits.Length;
        while (end > 0 && digits[end - 1] == '0') end--;
        try { exponent = checked(exponent - fractional + (digits.Length - end)); }
        catch (OverflowException) { return false; }
        digits = digits[..end];
        return true;
    }

    static bool TryEncodeQuantity(string text, out decimal value, out double encoded)
    {
        value = 0;
        encoded = 0;
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < 0 ||
            !TryCanonicalQuantity(text, out var inputDigits, out var inputExponent) ||
            !TryCanonicalQuantity(value.ToString("G29", CultureInfo.InvariantCulture), out var decimalDigits, out var decimalExponent) ||
            inputDigits != decimalDigits || inputExponent != decimalExponent ||
            decimalDigits.Length > 15) return false;

        // Parsing the original text directly avoids an additional decimal-to-double
        // rounding step (for example, 1e-28 has a shorter exact round-trip this way).
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out encoded) ||
            !double.IsFinite(encoded) || encoded < 0) return false;
        return TryCanonicalQuantity(encoded.ToString("R", CultureInfo.InvariantCulture), out var excelDigits, out var excelExponent) &&
            excelDigits == inputDigits && excelExponent == inputExponent;
    }

    public static void Write(string sourceXlsx, string targetXlsx,
        IReadOnlyDictionary<string, ItemDecision> items, IReadOnlyDictionary<string, FileDecision> files,
        List<string> problems)
    {
        using var book = new XLWorkbook(sourceXlsx);
        var expectedQuantities = new List<(int Row, string Id, decimal? Value)>();
        var ws = book.Worksheet(ReviewSheet);
        var cols = Columns(ws);
        int id = Need(cols, "记录ID"), mode = Need(cols, "处理方式"), num = Need(cols, "确认图号"), name = Need(cols, "确认名称"),
            mat = Need(cols, "确认材质"), trt = Need(cols, "确认工艺"), qty = Need(cols, "确认数量"),
            unit = Need(cols, "确认单位"), note = Need(cols, "复核说明");
        int last = ws.LastRowUsed()?.RowNumber() ?? FirstRow - 1;
        for (int r = FirstRow; r <= last; r++)
        {
            var rid = ws.Cell(r, id).GetString().Trim();
            if (rid == "" || !items.TryGetValue(rid, out var d)) continue;
            SetText(ws.Cell(r, mode), d.Mode);
            SetText(ws.Cell(r, num), d.DrawingNumber.Trim());
            SetText(ws.Cell(r, name), d.Name.Trim());
            SetText(ws.Cell(r, mat), d.Material.Trim());
            SetText(ws.Cell(r, trt), d.Treatment.Trim());
            SetText(ws.Cell(r, unit), d.Unit.Trim());
            SetText(ws.Cell(r, note), d.Note.Trim());
            var q = d.Quantity.Trim();
            if (q == "")
            {
                ws.Cell(r, qty).Clear(XLClearOptions.Contents);
                expectedQuantities.Add((r, rid, null));
            }
            else if (TryEncodeQuantity(q, out var qv, out var encoded))
            {
                ws.Cell(r, qty).Value = encoded;
                expectedQuantities.Add((r, rid, qv));
            }
            else
            {
                problems.Add($"{rid}: 确认数量“{q}”无效或无法按 Excel 精度无损保存");
            }
        }

        var fs = book.Worksheet(FilesSheet);
        var fcols = Columns(fs);
        int fid = Need(fcols, "文件ID"), state = Need(fcols, "核查状态"), fnote = Need(fcols, "文件核查说明"), pc = Need(fcols, "父子同时纳入说明");
        int flast = fs.LastRowUsed()?.RowNumber() ?? FirstRow - 1;
        for (int r = FirstRow; r <= flast; r++)
        {
            var key = fs.Cell(r, fid).GetString().Trim();
            if (key == "" || !files.TryGetValue(key, out var f)) continue;
            SetText(fs.Cell(r, state), f.State);
            SetText(fs.Cell(r, fnote), f.Note.Trim());
            SetText(fs.Cell(r, pc), f.ParentChildNote.Trim());
        }
        // An invalid in-memory workbook must never become an importable file.
        if (problems.Count != 0) return;
        var target = Path.GetFullPath(targetXlsx);
        var temp = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp.xlsx");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                book.SaveAs(stream, new SaveOptions { ValidatePackage = true, EvaluateFormulasBeforeSaving = true });
                stream.Flush(true);
            }
            // Verify what the workbook will actually yield to the import and export validators.
            using (var saved = new XLWorkbook(temp, new LoadOptions { RecalculateAllFormulas = false }))
            {
                var savedSheet = saved.Worksheet(ReviewSheet);
                foreach (var expected in expectedQuantities)
                {
                    try
                    {
                        if (Number(savedSheet.Cell(expected.Row, qty)) != expected.Value)
                            problems.Add($"{expected.Id}: 保存后的确认数量发生变化，未发布复核表");
                    }
                    catch (InvalidDataException)
                    {
                        problems.Add($"{expected.Id}: 保存后的确认数量无法无损读取，未发布复核表");
                    }
                }
            }
            if (problems.Count != 0) return;
            // The temporary file is a sibling, so replacement stays on the same volume.
            File.Move(temp, target, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    const string MetaSheet = "绑定信息", WorkerArg = "--cad-review-import-worker-v1";
    const long MaxWorkbookBytes = 32L * 1024 * 1024, MaxArchiveBytes = 128L * 1024 * 1024;
    const long MaxWorkerMemory = 3072L * 1024 * 1024;
    static readonly string[] ItemHeaders = ["样本", "图号", "部品名", "原材质", "原工艺", "原数量", "原单位",
        "需核对事项", "处理方式", "确认图号", "确认名称", "确认材质", "确认工艺", "确认数量", "确认单位", "复核说明",
        "来源类别", "父图号", "数量原文", "工艺原文", "原图文件", "记录ID", "参考表信息（仅对照）"];
    static readonly string[] FileHeaders = ["文件ID", "原图文件", "诊断摘要", "是否需确认",
        "核查状态", "文件核查说明", "父子同时纳入说明", "原图SHA256"];

    sealed class ImportRequest
    {
        public string Workbook { get; set; } = "";
        public string Original { get; set; } = "";
        public string Manifest { get; set; } = "";
        public string Hash { get; set; } = "";
        public string TaskId { get; set; } = "";
        public int Schema { get; set; }
    }
    sealed class ImportResult
    {
        public string? Error { get; set; }
        public Dictionary<string, ItemDecision> Items { get; set; } = [];
        public Dictionary<string, FileDecision> Files { get; set; } = [];
    }
    sealed class Originals
    {
        public Dictionary<string, Dictionary<string, object?>> Items { get; } = [];
        public Dictionary<string, Dictionary<string, object?>> Files { get; } = [];
    }

    // The application executable doubles as a gated worker, without changing the UI entry point.
    // It does not open any input until the parent has installed the Windows Job memory limits.
    [ModuleInitializer]
    internal static void ImportWorkerEntry()
    {
        if (!Environment.GetCommandLineArgs().Contains(WorkerArg, StringComparer.Ordinal)) return;
        try
        {
            if (Console.In.ReadLine() != "GO") Environment.Exit(2);
            var request = JsonSerializer.Deserialize<ImportRequest>(Console.In.ReadLine() ?? "")
                ?? throw new InvalidDataException("缺少导入参数。");
            var result = ReadBoundInWorker(request);
            JsonSerializer.Serialize(Console.OpenStandardOutput(), result);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            try
            {
                var message = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                JsonSerializer.Serialize(Console.OpenStandardOutput(), new ImportResult { Error = message });
            }
            catch { }
            Environment.Exit(1);
        }
    }

    public static (Dictionary<string, ItemDecision> Items, Dictionary<string, FileDecision> Files) Read(
        string xlsx, string original, string manifest, string hash, string taskId, int schema)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法启动导入校验进程。");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("无法定位导入校验程序。"));
        psi.ArgumentList.Add(WorkerArg);
        using var job = new ImportJob();
        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new InvalidOperationException("无法启动导入校验进程。");
        try
        {
            job.Assign(process);
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, MaxArchiveBytes, process);
            var errors = ReadBoundedAsync(process.StandardError.BaseStream, 128 * 1024, process);
            var request = new ImportRequest { Workbook = xlsx, Original = original, Manifest = manifest,
                Hash = hash, TaskId = taskId, Schema = schema };
            process.StandardInput.WriteLine("GO");
            process.StandardInput.WriteLine(JsonSerializer.Serialize(request));
            process.StandardInput.Close();
            if (!process.WaitForExit(300_000))
            {
                process.Kill(true);
                throw new InvalidDataException("导入校验超时，当前任务未更改。");
            }
            var bytes = output.GetAwaiter().GetResult();
            _ = errors.GetAwaiter().GetResult();
            ImportResult? result;
            try { result = JsonSerializer.Deserialize<ImportResult>(bytes); }
            catch (JsonException) { throw new InvalidDataException("导入校验进程返回了无效结果。"); }
            if (process.ExitCode != 0 || result?.Error != null || result == null)
                throw new InvalidDataException("导入校验失败：" + (result?.Error ?? "进程异常结束或超过资源限制。"));
            return (result.Items, result.Files);
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch { } }
        }
    }

    static async Task<byte[]> ReadBoundedAsync(Stream stream, long limit, Process process)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer)) != 0)
        {
            if (output.Length + count > limit)
            {
                try { process.Kill(true); } catch { }
                throw new InvalidDataException("导入校验结果超过大小上限。");
            }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    static ImportResult ReadBoundInWorker(ImportRequest request)
    {
        using var manifest = new FileStream(request.Manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(manifest));
        if (!string.Equals(hash, request.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("source.json 已变化，请重新打开任务。");
        manifest.Position = 0;
        var data = JsonSerializer.Deserialize<Dataset>(manifest, Json.Options) ?? throw new InvalidDataException("数据集为空。");
        if (data.SchemaVersion != 1 || data.SchemaVersion != request.Schema || data.TaskId != request.TaskId ||
            string.IsNullOrWhiteSpace(data.TaskId) || data.Items == null || data.Items.Count > 10000 ||
            data.Files == null || data.Files.Count == 0)
            throw new InvalidDataException("数据集的 schema、任务或规模无效。");
        if (data.Files.Any(f => string.IsNullOrWhiteSpace(f.Id)) ||
            data.Files.Select(f => f.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Files.Count ||
            data.Items.Any(i => string.IsNullOrWhiteSpace(i.Id) || i.Cad == null) ||
            data.Items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != data.Items.Count)
            throw new InvalidDataException("数据集的文件 ID 或记录 ID 缺失或重复。");
        var fileIds = data.Files.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        if (data.Items.Any(i => !fileIds.Contains(i.FileId)))
            throw new InvalidDataException("数据集中存在未知文件 ID。");

        var originals = new Originals();
        using (var original = OpenWorkbook(request.Original))
        {
            CheckRawQuantities(original);
            original.Position = 0;
            using var book = new XLWorkbook(original, new LoadOptions { RecalculateAllFormulas = false });
            ValidateBook(book, data, hash, originals, reference: null);
        }
        using var input = OpenWorkbook(request.Workbook);
        CheckRawQuantities(input);
        input.Position = 0;
        using var candidate = new XLWorkbook(input, new LoadOptions { RecalculateAllFormulas = false });
        return ValidateBook(candidate, data, hash, new Originals(), originals);
    }

    static FileStream OpenWorkbook(string path)
    {
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("复核文件必须是 XLSX。");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length > MaxWorkbookBytes) throw new InvalidDataException("复核文件超过 32 MiB。");
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (zip.Entries.Count > 5000) throw new InvalidDataException("工作簿的 ZIP 部件过多。");
                long total = 0;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    if (!names.Add(entry.FullName) || entry.Length > MaxWorkbookBytes ||
                        entry.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("工作簿 ZIP 部件重复、过大或包含宏。");
                    total = checked(total + entry.Length);
                    if (total > MaxArchiveBytes) throw new InvalidDataException("工作簿解压总量超过 128 MiB。");
                }
            }
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    // Inspect both workbooks before ClosedXML can convert a raw number to double.
    // The already-open file handle keeps the checked bytes stable for the later load.
    static void CheckRawQuantities(FileStream stream)
    {
        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false,
            new OpenSettings { MaxCharactersInPart = MaxWorkbookBytes });
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("工作簿缺少工作簿部件。");
        var matches = workbook.Workbook.Sheets?.Elements<XSheet>()
            .Where(s => string.Equals(s.Name?.Value, ReviewSheet, StringComparison.Ordinal)).ToList() ?? [];
        if (matches.Count != 1 || string.IsNullOrEmpty(matches[0].Id?.Value))
            throw new InvalidDataException("工作簿缺少唯一的复核明细工作表。");
        if (workbook.GetPartById(matches[0].Id!.Value!) is not WorksheetPart sheet)
            throw new InvalidDataException("复核明细关系不是工作表。");

        var headers = new Dictionary<int, XCell>();
        foreach (var (row, column, cell) in ReadRawCells(sheet))
            if (row == HeaderRow && !headers.TryAdd(column, cell))
                throw new InvalidDataException("复核明细存在重复表头单元格。");
        var sharedIndices = new HashSet<int>();
        foreach (var cell in headers.Values)
        {
            if (cell.CellFormula != null) throw new InvalidDataException("表头不能包含公式。");
            if (cell.DataType?.Value == XCellValues.SharedString)
            {
                if (!int.TryParse(cell.CellValue?.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
                    throw new InvalidDataException("复核表的共享表头无效。");
                sharedIndices.Add(index);
            }
        }
        var strings = ReadSharedHeaders(workbook, sharedIndices);
        int originalColumn = 0, confirmedColumn = 0;
        foreach (var (column, cell) in headers)
        {
            var name = RawHeader(cell, strings).Trim();
            if (name == "原数量")
            {
                if (originalColumn != 0) throw new InvalidDataException("复核表存在重复表头：原数量");
                originalColumn = column;
            }
            else if (name == "确认数量")
            {
                if (confirmedColumn != 0) throw new InvalidDataException("复核表存在重复表头：确认数量");
                confirmedColumn = column;
            }
        }
        if (originalColumn == 0 || confirmedColumn == 0)
            throw new InvalidDataException("复核表缺少原数量或确认数量列。");

        var seen = new HashSet<(int Row, int Column)>();
        foreach (var (row, column, cell) in ReadRawCells(sheet))
        {
            if (row < FirstRow || (column != originalColumn && column != confirmedColumn)) continue;
            // Formatting-only cells and empty rows do not consume the quantity limit.
            if (cell.CellFormula == null && cell.CellValue == null && cell.InlineString == null) continue;
            if (!seen.Add((row, column)) || seen.Count > 20000)
                throw new InvalidDataException("复核表数量单元格重复或超过规模限制。");
            if (cell.CellFormula != null) throw new InvalidDataException("数量不允许公式。");
            if (cell.DataType != null && cell.DataType.Value != XCellValues.Number) continue;
            if (cell.Elements<XCellValue>().Skip(1).Any() || cell.InlineString != null)
                throw new InvalidDataException("数量单元格包含不明确的数值。");
            var raw = cell.CellValue?.Text;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!TryEncodeQuantity(raw.Trim(), out _, out _))
                throw new InvalidDataException($"复核明细第 {row} 行的原数量或确认数量无法无损读取。");
        }
    }

    static IEnumerable<(int Row, int Column, XCell Cell)> ReadRawCells(WorksheetPart sheet)
    {
        using var reader = OpenXmlReader.Create(sheet);
        bool inRow = false;
        int previousRow = 0, previousColumn = 0;
        int? currentRow = null;
        while (reader.Read())
        {
            if (reader.ElementType == typeof(XRow))
            {
                if (reader.IsStartElement)
                {
                    if (inRow) throw new InvalidDataException("复核表的行结构无效。");
                    inRow = true;
                    previousColumn = 0;
                    currentRow = null;
                    var rawRow = reader.Attributes.FirstOrDefault(a => a.LocalName == "r" && a.NamespaceUri == "").Value;
                    if (rawRow != null)
                    {
                        if (!int.TryParse(rawRow, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
                            parsed < 1 || parsed > 1048576) throw new InvalidDataException("复核表的行号无效。");
                        currentRow = parsed;
                    }
                }
                else if (reader.IsEndElement && inRow)
                {
                    previousRow = currentRow ?? checked(previousRow + 1);
                    if (previousRow > 1048576) throw new InvalidDataException("复核表的行号超出范围。");
                    inRow = false;
                }
            }
            else if (reader.ElementType == typeof(XCell) && reader.IsStartElement)
            {
                if (!inRow) throw new InvalidDataException("复核表的单元格不在行中。");
                var cell = reader.LoadCurrentElement() as XCell
                    ?? throw new InvalidDataException("无法读取复核表单元格。");
                int column;
                var reference = cell.CellReference?.Value;
                if (reference != null)
                {
                    if (!TryRawAddress(reference, out var referencedRow, out column) ||
                        (currentRow.HasValue && currentRow.Value != referencedRow))
                        throw new InvalidDataException("复核表的单元格位置无效。");
                    currentRow ??= referencedRow;
                }
                else
                {
                    currentRow ??= checked(previousRow + 1);
                    column = checked(previousColumn + 1);
                    if (currentRow > 1048576 || column > 16384)
                        throw new InvalidDataException("复核表的单元格位置超出范围。");
                }
                previousColumn = column;
                yield return (currentRow.Value, column, cell);
            }
        }
    }

    static bool TryRawAddress(string text, out int row, out int column)
    {
        row = 0;
        column = 0;
        int i = 0;
        while (i < text.Length && ((text[i] >= 'A' && text[i] <= 'Z') || (text[i] >= 'a' && text[i] <= 'z')))
        {
            column = column * 26 + (char.ToUpperInvariant(text[i]) - 'A' + 1);
            if (column > 16384) return false;
            i++;
        }
        return i > 0 && i < text.Length && int.TryParse(text.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out row) &&
            row >= 1 && row <= 1048576;
    }

    static Dictionary<int, string> ReadSharedHeaders(WorkbookPart workbook, HashSet<int> needed)
    {
        var found = new Dictionary<int, string>();
        if (needed.Count == 0) return found;
        var part = workbook.SharedStringTablePart ?? throw new InvalidDataException("复核表缺少共享字符串。");
        using var reader = OpenXmlReader.Create(part);
        int index = 0;
        while (reader.Read())
        {
            if (reader.ElementType != typeof(XSharedStringItem) || !reader.IsStartElement) continue;
            if (needed.Contains(index))
            {
                var item = reader.LoadCurrentElement() as XSharedStringItem
                    ?? throw new InvalidDataException("无法读取复核表共享字符串。");
                found.Add(index, DecodeExcelText(HeaderText(item)));
                if (found.Count == needed.Count) break;
            }
            index++;
        }
        if (found.Count != needed.Count) throw new InvalidDataException("复核表的共享表头索引无效。");
        return found;
    }

    static string RawHeader(XCell cell, Dictionary<int, string> shared)
    {
        var type = cell.DataType?.Value;
        if (type == XCellValues.SharedString)
            return shared[int.Parse(cell.CellValue!.Text, CultureInfo.InvariantCulture)];
        if (type == XCellValues.InlineString)
            return cell.InlineString is XInlineString inline ? DecodeExcelText(HeaderText(inline)) : "";
        return DecodeExcelText(cell.CellValue?.Text ?? "");
    }

    static string HeaderText(OpenXmlCompositeElement element)
    {
        var text = new System.Text.StringBuilder();
        foreach (var child in element.ChildElements)
        {
            if (child is XText direct) text.Append(direct.Text);
            else if (child is XRun run)
                foreach (var piece in run.Elements<XText>()) text.Append(piece.Text);
        }
        return text.ToString();
    }

    static string DecodeExcelText(string input)
    {
        var text = new System.Text.StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            if (i + 6 < input.Length && input[i] == '_' && (input[i + 1] == 'x' || input[i + 1] == 'X') &&
                input[i + 6] == '_' && ushort.TryParse(input.AsSpan(i + 2, 4), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var code))
            {
                text.Append((char)code);
                i += 6;
            }
            else text.Append(input[i]);
        }
        return text.ToString();
    }

    static ImportResult ValidateBook(XLWorkbook book, Dataset data, string hash, Originals capture, Originals? reference)
    {
        foreach (var sheet in book.Worksheets)
            foreach (var cell in sheet.CellsUsed())
                if (cell.HasFormula) throw new InvalidDataException($"工作簿不允许公式：{sheet.Name}!{cell.Address}");
        var meta = book.Worksheet(MetaSheet);
        if (Literal(meta.Cell(1, 1)) != "schema" || Literal(meta.Cell(2, 1)) != "task_id" ||
            Literal(meta.Cell(3, 1)) != "source_sha256" || Number(meta.Cell(1, 2)) != data.SchemaVersion ||
            Literal(meta.Cell(2, 2)) != data.TaskId || !Literal(meta.Cell(3, 2)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("工作簿的 schema、任务 ID 或 source.json 哈希不匹配。");

        var result = new ImportResult();
        var filesById = data.Files.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var fs = book.Worksheet(FilesSheet);
        var fc = RequiredColumns(fs, FileHeaders);
        foreach (var row in fs.RowsUsed(XLCellsUsedOptions.Contents).Where(r => r.RowNumber() >= FirstRow))
        {
            int n = row.RowNumber();
            CheckExtra(row, fc);
            var id = Literal(fs.Cell(n, fc["文件ID"]));
            if (!filesById.TryGetValue(id, out var file) || result.Files.ContainsKey(id))
                throw new InvalidDataException($"文件诊断第 {n} 行的文件 ID 缺失、未知或重复。");
            var expected = new Dictionary<string, object?> {
                ["文件ID"] = file.Id, ["原图文件"] = file.Name, ["诊断摘要"] = file.Diagnostics,
                ["是否需确认"] = file.RequiresAcknowledgement ? "需确认" : "仅信息", ["原图SHA256"] = file.Sha256 };
            CompareOriginals(fs, n, fc, expected, reference?.Files.GetValueOrDefault(id), capture.Files, id);
            var state = Literal(fs.Cell(n, fc["核查状态"]));
            if (state == "") state = "待核查";
            if (!Rules.FileStates.Contains(state, StringComparer.Ordinal)) throw new InvalidDataException($"文件诊断第 {n} 行的核查状态无效。");
            result.Files.Add(id, new FileDecision { State = state, Note = Literal(fs.Cell(n, fc["文件核查说明"])),
                ParentChildNote = Literal(fs.Cell(n, fc["父子同时纳入说明"])) });
        }
        if (result.Files.Count != data.Files.Count) throw new InvalidDataException("文件诊断行不完整。");

        var byId = data.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var ws = book.Worksheet(ReviewSheet);
        var cols = RequiredColumns(ws, ItemHeaders);
        foreach (var row in ws.RowsUsed(XLCellsUsedOptions.Contents).Where(r => r.RowNumber() >= FirstRow))
        {
            int n = row.RowNumber();
            CheckExtra(row, cols);
            var id = Literal(ws.Cell(n, cols["记录ID"]));
            if (!byId.TryGetValue(id, out var item) || result.Items.ContainsKey(id))
                throw new InvalidDataException($"复核明细第 {n} 行的记录 ID 缺失、未知或重复。");
            var cad = item.Cad;
            var expected = new Dictionary<string, object?> {
                ["样本"] = item.FileId, ["图号"] = cad.DrawingNumber, ["部品名"] = cad.Name,
                ["原材质"] = cad.Material, ["原工艺"] = cad.Treatment, ["原数量"] = cad.Quantity,
                ["原单位"] = cad.Unit, ["需核对事项"] = item.Warnings, ["来源类别"] = item.Kind,
                ["父图号"] = string.Join("；", item.Parents), ["数量原文"] = item.QuantityEvidence,
                ["工艺原文"] = item.TreatmentEvidence, ["原图文件"] = filesById[item.FileId].Name,
                ["记录ID"] = item.Id, ["参考表信息（仅对照）"] = item.ReferenceInfo };
            CompareOriginals(ws, n, cols, expected, reference?.Items.GetValueOrDefault(id), capture.Items, id);
            var mode = Literal(ws.Cell(n, cols["处理方式"]));
            if (mode == "") mode = Rules.Pending;
            if (!Rules.Modes.Contains(mode, StringComparer.Ordinal)) throw new InvalidDataException($"复核明细第 {n} 行的处理方式无效。");
            var quantity = Number(ws.Cell(n, cols["确认数量"]));
            var d = new ItemDecision { Mode = mode, DrawingNumber = Literal(ws.Cell(n, cols["确认图号"])),
                Name = Literal(ws.Cell(n, cols["确认名称"])), Material = Literal(ws.Cell(n, cols["确认材质"])),
                Treatment = Literal(ws.Cell(n, cols["确认工艺"])), Quantity = quantity?.ToString("G29", CultureInfo.InvariantCulture) ?? "",
                Unit = Literal(ws.Cell(n, cols["确认单位"])), Note = Literal(ws.Cell(n, cols["复核说明"])) };
            if (mode == Rules.Adopt && (d.DrawingNumber != "" || d.Name != "" || d.Material != "" ||
                d.Treatment != "" || d.Quantity != "" || d.Unit != ""))
                throw new InvalidDataException($"复核明细第 {n} 行采用图纸时不能混入确认值。");
            result.Items.Add(id, d);
        }
        if (result.Items.Count != data.Items.Count) throw new InvalidDataException("复核明细行不完整。");
        return result;
    }

    static Dictionary<string, int> RequiredColumns(IXLWorksheet sheet, string[] headers)
    {
        var cols = Columns(sheet);
        foreach (var name in headers) Need(cols, name);
        return cols;
    }

    static void CheckExtra(IXLRow row, Dictionary<string, int> cols)
    {
        var allowed = cols.Values.ToHashSet();
        if (row.CellsUsed(XLCellsUsedOptions.Contents).Any(c => !allowed.Contains(c.Address.ColumnNumber)))
            throw new InvalidDataException($"第 {row.RowNumber()} 行存在没有表头的内容。");
    }

    static void CompareOriginals(IXLWorksheet sheet, int row, Dictionary<string, int> cols,
        Dictionary<string, object?> expected, Dictionary<string, object?>? reference,
        Dictionary<string, Dictionary<string, object?>> capture, string id)
    {
        var actual = new Dictionary<string, object?>();
        foreach (var (name, value) in expected)
        {
            var cell = sheet.Cell(row, cols[name]);
            object? read = name == "原数量" && value is decimal ? Number(cell) : Raw(cell);
            if (value is decimal number ? read is not decimal actualNumber || actualNumber != number
                : !string.Equals(((string)read!).Trim(), (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Trim(), StringComparison.Ordinal))
                throw new InvalidDataException($"原始数据与数据集不一致：{sheet.Name} 第 {row} 行 {name}。");
            if (reference != null && (!reference.TryGetValue(name, out var prior) || !Equals(prior, read)))
                throw new InvalidDataException($"原始数据与原复核表不一致：{sheet.Name} 第 {row} 行 {name}。");
            actual.Add(name, read);
        }
        capture.Add(id, actual);
    }

    static string Raw(IXLCell cell)
    {
        if (cell.HasFormula) throw new InvalidDataException("工作簿不允许公式。");
        if (cell.DataType == XLDataType.Error) throw new InvalidDataException("工作簿包含错误值。");
        return cell.IsEmpty() ? "" : cell.GetString();
    }
    static string Literal(IXLCell cell) => Raw(cell).Trim();
    static decimal? Number(IXLCell cell)
    {
        if (cell.HasFormula) throw new InvalidDataException("数量不允许公式。");
        if (cell.IsEmpty()) return null;
        var text = cell.DataType == XLDataType.Number ? cell.GetDouble().ToString("R", CultureInfo.InvariantCulture) : Literal(cell);
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new InvalidDataException("数量必须是有效的非负数字：" + cell.Address);
        var decimalText = value.ToString("G29", CultureInfo.InvariantCulture);
        if (!TryCanonicalQuantity(text, out var inputDigits, out var inputExponent) ||
            !TryCanonicalQuantity(decimalText, out var resultDigits, out var resultExponent) ||
            inputDigits != resultDigits || inputExponent != resultExponent)
            throw new InvalidDataException("数量无法无损解析：" + cell.Address);
        var digits = decimalText.Split('E')[0].Replace(".", "").Trim('0');
        if (digits.Length > 15) throw new InvalidDataException("数量超过 Excel 的可靠精度：" + cell.Address);
        return value;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    sealed class ImportJob : IDisposable
    {
        readonly IntPtr handle;
        public ImportJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("无法创建导入资源保护。");
            var limits = new ExtendedLimits { BasicLimitInformation = new BasicLimits { LimitFlags = 0x100 | 0x200 | 0x2000 },
                ProcessMemoryLimit = (nuint)MaxWorkerMemory, JobMemoryLimit = (nuint)MaxWorkerMemory };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
                !QueryInformationJobObject(handle, 9, out var actual, (uint)Marshal.SizeOf<ExtendedLimits>(), IntPtr.Zero) ||
                (actual.BasicLimitInformation.LimitFlags & 0x2300) != 0x2300 ||
                actual.ProcessMemoryLimit != (nuint)MaxWorkerMemory || actual.JobMemoryLimit != (nuint)MaxWorkerMemory)
            {
                CloseHandle(handle);
                throw new InvalidOperationException("无法设置或核实导入进程的 3 GiB 内存保护。");
            }
        }
        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
                throw new InvalidOperationException("无法将导入进程纳入内存保护。");
        }
        public void Dispose() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out ExtendedLimits info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);

    // Kept for the existing internal round-trip self-test; importing must use the bound overload.
    internal static (Dictionary<string, ItemDecision> Items, Dictionary<string, FileDecision> Files) Read(string xlsx)
    {
        using var book = new XLWorkbook(xlsx);
        var items = new Dictionary<string, ItemDecision>();
        var files = new Dictionary<string, FileDecision>();
        var ws = book.Worksheet(ReviewSheet);
        var cols = Columns(ws);
        int id = Need(cols, "记录ID"), mode = Need(cols, "处理方式"), num = Need(cols, "确认图号"), name = Need(cols, "确认名称"),
            mat = Need(cols, "确认材质"), trt = Need(cols, "确认工艺"), qty = Need(cols, "确认数量"),
            unit = Need(cols, "确认单位"), note = Need(cols, "复核说明");
        int last = ws.LastRowUsed()?.RowNumber() ?? FirstRow - 1;
        for (int r = FirstRow; r <= last; r++)
        {
            var rid = ws.Cell(r, id).GetString().Trim();
            if (rid == "") continue;
            var qc = ws.Cell(r, qty);
            string q = qc.IsEmpty() ? "" : qc.DataType == XLDataType.Number
                ? qc.GetDouble().ToString("R", CultureInfo.InvariantCulture) : qc.GetString().Trim();
            var m = ws.Cell(r, mode).GetString().Trim();
            items[rid] = new ItemDecision
            {
                Mode = m == "" ? Rules.Pending : m,
                DrawingNumber = ws.Cell(r, num).GetString().Trim(),
                Name = ws.Cell(r, name).GetString().Trim(),
                Material = ws.Cell(r, mat).GetString().Trim(),
                Treatment = ws.Cell(r, trt).GetString().Trim(),
                Quantity = q,
                Unit = ws.Cell(r, unit).GetString().Trim(),
                Note = ws.Cell(r, note).GetString().Trim(),
            };
        }
        var fs = book.Worksheet(FilesSheet);
        var fcols = Columns(fs);
        int fid = Need(fcols, "文件ID"), state = Need(fcols, "核查状态"), fnote = Need(fcols, "文件核查说明"), pc = Need(fcols, "父子同时纳入说明");
        int flast = fs.LastRowUsed()?.RowNumber() ?? FirstRow - 1;
        for (int r = FirstRow; r <= flast; r++)
        {
            var key = fs.Cell(r, fid).GetString().Trim();
            if (key == "") continue;
            files[key] = new FileDecision
            {
                State = fs.Cell(r, state).GetString().Trim() is { Length: > 0 } s ? s : "待核查",
                Note = fs.Cell(r, fnote).GetString().Trim(),
                ParentChildNote = fs.Cell(r, pc).GetString().Trim(),
            };
        }
        return (items, files);
    }
}
