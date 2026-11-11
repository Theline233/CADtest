#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CadReviewUI;

// These are already-confirmed values. Unit remains in the existing review record;
// the reference quotation displays only the numeric quantity.
internal sealed record ExactQuotationRow(
    string Id, string FileId, string DrawingNumber, string? Name, string Material,
    string Treatment, decimal Quantity, string Unit, string FileName);

internal static class ExactTemplateWriter
{
    private const int FirstDetailRow = 6;
    private const int TemplateDetails = 48;
    private const int TemplateLastDetail = 53;
    private const int TemplateTotal = 54;
    private const int ExcelLastRow = 1_048_576;
    private const string SheetName = "报价单样式";
    private const string WorkbookPart = "xl/workbook.xml";
    private const string WorkbookRelationshipsPart = "xl/_rels/workbook.xml.rels";
    private const string ContentTypesPart = "[Content_Types].xml";
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Only an in-memory copy is modified. The caller owns template-hash checking,
    // review/confirmation, publication and persistence of the unit and audit data.
    internal static byte[] Build(byte[] templateBytes, IReadOnlyList<ExactQuotationRow> rows)
    {
        ArgumentNullException.ThrowIfNull(templateBytes);
        ArgumentNullException.ThrowIfNull(rows);
        string caption = ValidateRows(rows);
        int detailCount = Math.Max(TemplateDetails, rows.Count);
        int lastDetail = checked(FirstDetailRow + detailCount - 1);
        int totalRow = checked(lastDetail + 1);
        if (totalRow + 1 > ExcelLastRow)
            throw new InvalidDataException("明细数量超过 Excel 工作表或打印区域的行数限制。");

        using var output = new MemoryStream();
        output.Write(templateBytes, 0, templateBytes.Length);
        output.Position = 0;
        using (var archive = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true))
        {
            if (archive.Entries.GroupBy(e => e.FullName, StringComparer.Ordinal).Any(g => g.Count() != 1))
                throw new InvalidDataException("模板含有重复的 XLSX 部件。");

            XDocument workbook = ReadXml(archive, WorkbookPart);
            XDocument relationships = ReadXml(archive, WorkbookRelationshipsPart);
            XElement sheets = workbook.Root?.Element(Main + "sheets")
                ?? throw new InvalidDataException("模板缺少工作表列表。");
            var sheetList = sheets.Elements(Main + "sheet").ToList();
            var matches = sheetList.Where(s => (string?)s.Attribute("name") == SheetName).ToList();
            if (matches.Count != 1) throw new InvalidDataException("模板报价工作表不唯一或不存在。");
            XElement sheet = matches[0];
            int sheetIndex = sheetList.IndexOf(sheet);
            string sheetRelId = (string?)sheet.Attribute(OfficeRel + "id")
                ?? throw new InvalidDataException("模板工作表缺少关系标识。");
            XElement worksheetRel = relationships.Root?.Elements(PackageRel + "Relationship")
                .SingleOrDefault(r => (string?)r.Attribute("Id") == sheetRelId &&
                    ((string?)r.Attribute("Type"))?.EndsWith("/worksheet", StringComparison.Ordinal) == true &&
                    (string?)r.Attribute("TargetMode") != "External")
                ?? throw new InvalidDataException("模板工作表关系无效。");
            string sheetPart = ResolvePart(WorkbookPart, (string?)worksheetRel.Attribute("Target"));
            XDocument worksheet = ReadXml(archive, sheetPart);

            PrepareWorksheet(worksheet, rows, caption, lastDetail, totalRow);
            if (detailCount > TemplateDetails)
            {
                UpdateDefinedName(workbook, sheetIndex, "_xlnm._FilterDatabase", "$B$5:$I$54", "$B$5:$I$" + totalRow.ToString(Invariant));
                UpdateDefinedName(workbook, sheetIndex, "_xlnm.Print_Area", "$B$1:$I$55", "$B$1:$I$" + (totalRow + 1).ToString(Invariant));
                // Once rows are inserted the old chain no longer covers every formula.
                RemoveCalculationChain(archive, relationships);
                XElement root = workbook.Root ?? throw new InvalidDataException("模板工作簿无效。");
                XElement? calc = root.Element(Main + "calcPr");
                if (calc == null)
                {
                    // calcPr precedes extLst in the workbook schema.
                    calc = new XElement(Main + "calcPr");
                    XElement? extensions = root.Element(Main + "extLst");
                    if (extensions == null) root.Add(calc); else extensions.AddBeforeSelf(calc);
                }
                calc.SetAttributeValue("calcMode", "auto");
                calc.SetAttributeValue("fullCalcOnLoad", "1");
                calc.SetAttributeValue("forceFullCalc", "1");
                WriteXml(archive, WorkbookPart, workbook);
            }

            WriteXml(archive, sheetPart, worksheet);
        }
        return output.ToArray();
    }

    private static string ValidateRows(IReadOnlyList<ExactQuotationRow> rows)
    {
        if (rows.Count == 0) throw new InvalidDataException("没有纳入的图纸明细。");
        if (rows.Count > ExcelLastRow - FirstDetailRow - 1)
            throw new InvalidDataException("明细数量超过 Excel 行数限制。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ExactQuotationRow row in rows)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Id) || !ids.Add(row.Id) ||
                string.IsNullOrWhiteSpace(row.FileId) || string.IsNullOrWhiteSpace(row.DrawingNumber) ||
                string.IsNullOrWhiteSpace(row.Material) || string.IsNullOrWhiteSpace(row.Treatment) ||
                string.IsNullOrWhiteSpace(row.Unit) || string.IsNullOrWhiteSpace(row.FileName) || row.Quantity < 0)
                throw new InvalidDataException("确认明细缺少必要字段、标识重复或数量无效。");
            CheckText(row.DrawingNumber);
            CheckText(row.Name);
            CheckText(row.Material);
            CheckText(row.Treatment);
            CheckText(row.FileName);

            double asDouble = (double)row.Quantity;
            if (!double.IsFinite(asDouble) ||
                !decimal.TryParse(asDouble.ToString("R", Invariant), NumberStyles.Float, Invariant, out decimal roundTrip) ||
                roundTrip != row.Quantity)
                throw new InvalidDataException("确认数量不能无损写入 Excel。");
            if (files.TryGetValue(row.FileId, out string? existingName))
            {
                if (!string.Equals(existingName, row.FileName, StringComparison.Ordinal))
                    throw new InvalidDataException("同一图纸标识对应了不同文件名。");
            }
            else files.Add(row.FileId, row.FileName);
        }

        if (files.Count > 1) return files.Count.ToString(Invariant) + " 份图纸";
        string fileName = files.Values.Single();
        int separator = Math.Max(fileName.LastIndexOf('/'), fileName.LastIndexOf('\\'));
        string baseName = fileName.Substring(separator + 1);
        int extension = baseName.LastIndexOf('.');
        if (extension >= 0) baseName = baseName.Substring(0, extension);
        if (string.IsNullOrWhiteSpace(baseName)) throw new InvalidDataException("图纸文件名无效。");
        CheckText(baseName);
        return baseName;
    }

    private static void CheckText(string? text)
    {
        if (text == null) return;
        if (text.Length > 32767) throw new InvalidDataException("单元格文字超过 Excel 长度限制。");
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException ex) { throw new InvalidDataException("单元格文字含无效 XML 字符。", ex); }
    }

    private static void PrepareWorksheet(XDocument document, IReadOnlyList<ExactQuotationRow> rows,
        string caption, int lastDetail, int totalRow)
    {
        XElement root = document.Root ?? throw new InvalidDataException("模板工作表无效。");
        XElement data = root.Element(Main + "sheetData") ?? throw new InvalidDataException("模板缺少工作表数据。");
        var rowMap = data.Elements(Main + "row").ToDictionary(r => (int?)r.Attribute("r")
            ?? throw new InvalidDataException("模板存在无行号的行。"));
        XElement headerCell = Cell(Row(rowMap, 2), "C", 2);
        RequireEmpty(headerCell);
        for (int r = FirstDetailRow; r <= TemplateLastDetail; r++)
        {
            XElement detail = Row(rowMap, r);
            XElement serial = Cell(detail, "B", r);
            if ((string?)serial.Element(Main + "v") != (r - FirstDetailRow + 1).ToString(Invariant) || serial.Element(Main + "f") != null)
                throw new InvalidDataException("模板的明细序号不符合原表。");
            foreach (string column in new[] { "C", "D", "E", "F", "G", "H" }) RequireEmpty(Cell(detail, column, r));
            XElement amount = Cell(detail, "I", r);
            XElement formula = amount.Element(Main + "f") ?? throw new InvalidDataException("模板缺少金额公式。");
            if ((string?)formula.Attribute("t") != "shared" || (string?)formula.Attribute("si") != "0" ||
                (r == FirstDetailRow && ((string?)formula.Attribute("ref") != "I6:I53" || formula.Value != "G6*H6")) ||
                (r != FirstDetailRow && !string.IsNullOrEmpty(formula.Value)) ||
                (string?)amount.Element(Main + "v") != "0")
                throw new InvalidDataException("模板金额公式或缓存不符合原表。");
        }
        XElement total = Row(rowMap, TemplateTotal);
        XElement totalAmount = Cell(total, "I", TemplateTotal);
        if ((string?)totalAmount.Element(Main + "f") != "SUM(I6:I53)" || (string?)totalAmount.Element(Main + "v") != "0")
            throw new InvalidDataException("模板合计公式或缓存不符合原表。");

        SetText(headerCell, caption);
        if (lastDetail > TemplateLastDetail)
        {
            XElement evenPrototype = Row(rowMap, 52);
            XElement oddPrototype = Row(rowMap, 53);
            rowMap.Remove(TemplateTotal);
            MoveRow(total, totalRow);
            rowMap.Add(totalRow, total);
            for (int r = TemplateLastDetail + 1; r <= lastDetail; r++)
            {
                XElement detail = new XElement(r % 2 == 0 ? evenPrototype : oddPrototype);
                MoveRow(detail, r);
                SetNumber(Cell(detail, "B", r), (r - FirstDetailRow + 1).ToString(Invariant));
                total.AddBeforeSelf(detail);
                rowMap.Add(r, detail);
            }
            Cell(Row(rowMap, FirstDetailRow), "I", FirstDetailRow).Element(Main + "f")!
                .SetAttributeValue("ref", "I6:I" + lastDetail.ToString(Invariant));
            XElement movedTotalAmount = Cell(total, "I", totalRow);
            movedTotalAmount.Element(Main + "f")!.Value = "SUM(I6:I" + lastDetail.ToString(Invariant) + ")";

            XElement dimension = root.Element(Main + "dimension") ?? throw new InvalidDataException("模板缺少使用区域。");
            if ((string?)dimension.Attribute("ref") != "B1:I54") throw new InvalidDataException("模板使用区域不符合原表。");
            dimension.SetAttributeValue("ref", "B1:I" + totalRow.ToString(Invariant));
            XElement filter = root.Element(Main + "autoFilter") ?? throw new InvalidDataException("模板缺少筛选区域。");
            if ((string?)filter.Attribute("ref") != "B5:I54") throw new InvalidDataException("模板筛选区域不符合原表。");
            filter.SetAttributeValue("ref", "B5:I" + totalRow.ToString(Invariant));
            XElement? merge = root.Element(Main + "mergeCells")?.Elements(Main + "mergeCell")
                .SingleOrDefault(m => (string?)m.Attribute("ref") == "B54:G54");
            if (merge == null) throw new InvalidDataException("模板缺少合计行合并区域。");
            merge.SetAttributeValue("ref", "B" + totalRow.ToString(Invariant) + ":G" + totalRow.ToString(Invariant));
        }

        for (int index = 0; index < rows.Count; index++)
        {
            int r = FirstDetailRow + index;
            XElement detail = Row(rowMap, r);
            ExactQuotationRow value = rows[index];
            SetText(Cell(detail, "C", r), value.DrawingNumber);
            if (!string.IsNullOrEmpty(value.Name)) SetText(Cell(detail, "D", r), value.Name);
            SetText(Cell(detail, "E", r), value.Material);
            SetText(Cell(detail, "F", r), value.Treatment);
            SetNumber(Cell(detail, "G", r), value.Quantity.ToString(Invariant));
        }
    }

    private static XElement Row(Dictionary<int, XElement> rows, int number) =>
        rows.TryGetValue(number, out XElement? row) ? row : throw new InvalidDataException("模板缺少必要行。");

    private static XElement Cell(XElement row, string column, int number) =>
        row.Elements(Main + "c").SingleOrDefault(c => (string?)c.Attribute("r") == column + number.ToString(Invariant))
        ?? throw new InvalidDataException("模板缺少必要单元格。");

    private static void RequireEmpty(XElement cell)
    {
        if (cell.Element(Main + "f") != null ||
            !string.IsNullOrEmpty(cell.Element(Main + "v")?.Value) ||
            cell.Element(Main + "is")?.Descendants(Main + "t").Any(t => t.Value.Length != 0) == true)
            throw new InvalidDataException("模板包含未清除的业务明细或图纸类别。");
    }

    private static void SetText(XElement cell, string value)
    {
        CheckText(value);
        ClearValue(cell);
        cell.SetAttributeValue("t", "inlineStr");
        AddCellContent(cell, new XElement(Main + "is", new XElement(Main + "t",
            new XAttribute(XNamespace.Xml + "space", "preserve"), value)));
    }

    private static void SetNumber(XElement cell, string value)
    {
        ClearValue(cell);
        cell.SetAttributeValue("t", null);
        AddCellContent(cell, new XElement(Main + "v", value));
    }

    private static void ClearValue(XElement cell)
    {
        cell.Elements().Where(e => e.Name == Main + "f" || e.Name == Main + "v" || e.Name == Main + "is").Remove();
    }

    private static void AddCellContent(XElement cell, XElement content)
    {
        XElement? extensions = cell.Element(Main + "extLst");
        if (extensions == null) cell.Add(content); else extensions.AddBeforeSelf(content);
    }

    private static void MoveRow(XElement row, int newNumber)
    {
        row.SetAttributeValue("r", newNumber.ToString(Invariant));
        foreach (XElement cell in row.Elements(Main + "c"))
        {
            string old = (string?)cell.Attribute("r") ?? throw new InvalidDataException("模板单元格缺少地址。");
            int split = 0;
            while (split < old.Length && old[split] >= 'A' && old[split] <= 'Z') split++;
            if (split == 0 || split == old.Length) throw new InvalidDataException("模板单元格地址无效。");
            cell.SetAttributeValue("r", old.Substring(0, split) + newNumber.ToString(Invariant));
        }
    }

    private static void UpdateDefinedName(XDocument workbook, int sheetIndex, string name, string oldRange, string newRange)
    {
        XElement? defined = workbook.Root?.Element(Main + "definedNames")?.Elements(Main + "definedName")
            .SingleOrDefault(n => (string?)n.Attribute("name") == name &&
                (string?)n.Attribute("localSheetId") == sheetIndex.ToString(Invariant));
        if (defined == null || !defined.Value.EndsWith("!" + oldRange, StringComparison.Ordinal))
            throw new InvalidDataException("模板的筛选或打印名称不符合原表。");
        defined.Value = defined.Value.Substring(0, defined.Value.Length - oldRange.Length) + newRange;
    }

    private static void RemoveCalculationChain(ZipArchive archive, XDocument relationships)
    {
        XElement root = relationships.Root ?? throw new InvalidDataException("模板工作簿关系无效。");
        var chainRelationships = root.Elements(PackageRel + "Relationship")
            .Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/calcChain", StringComparison.Ordinal) == true).ToList();
        var chainParts = new HashSet<string>(StringComparer.Ordinal) { "xl/calcChain.xml" };
        foreach (XElement relationship in chainRelationships)
        {
            if ((string?)relationship.Attribute("TargetMode") == "External")
                throw new InvalidDataException("模板计算链关系不能指向外部。");
            chainParts.Add(ResolvePart(WorkbookPart, (string?)relationship.Attribute("Target")));
            relationship.Remove();
        }
        foreach (string part in chainParts) archive.GetEntry(part)?.Delete();
        if (chainRelationships.Count != 0) WriteXml(archive, WorkbookRelationshipsPart, relationships);

        XDocument types = ReadXml(archive, ContentTypesPart);
        var overrides = types.Root?.Elements(ContentTypes + "Override")
            .Where(e => chainParts.Contains(((string?)e.Attribute("PartName") ?? "").TrimStart('/')) ||
                ((string?)e.Attribute("ContentType"))?.EndsWith(".calcChain+xml", StringComparison.Ordinal) == true).ToList()
            ?? throw new InvalidDataException("模板内容类型无效。");
        if (overrides.Count != 0)
        {
            foreach (XElement entry in overrides) entry.Remove();
            WriteXml(archive, ContentTypesPart, types);
        }
    }

    private static string ResolvePart(string source, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new InvalidDataException("模板部件关系缺少目标。");
        var baseUri = new Uri("http://xlsx.invalid/" + source, UriKind.Absolute);
        if (!Uri.TryCreate(baseUri, target, out Uri? result) || result.Host != baseUri.Host ||
            result.Scheme != baseUri.Scheme || !string.IsNullOrEmpty(result.Query) || !string.IsNullOrEmpty(result.Fragment))
            throw new InvalidDataException("模板部件关系目标无效。");
        return Uri.UnescapeDataString(result.AbsolutePath.TrimStart('/'));
    }

    private static XDocument ReadXml(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidDataException("模板缺少必要的 XLSX 部件。");
        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static void WriteXml(ZipArchive archive, string name, XDocument document)
    {
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidDataException("模板缺少必要的 XLSX 部件。");
        using Stream stream = entry.Open();
        stream.SetLength(0);
        using XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = document.Declaration == null,
            Indent = false,
            CloseOutput = false
        });
        document.Save(writer);
    }
}
