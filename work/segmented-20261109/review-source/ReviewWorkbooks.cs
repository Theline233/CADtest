using ClosedXML.Excel;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

public static class ReviewWorkbooks
{
    const int First=8;
    const string ReviewSheet="复核明细",FilesSheet="文件诊断",MetaSheet="绑定信息";
    static readonly string[] Headers=["样本","图号","部品名","原材质","原工艺","原数量","原单位","需核对事项","处理方式","确认图号","确认名称","确认材质","确认工艺","确认数量","确认单位","复核说明","来源类别","父图号","数量原文","工艺原文","原图文件","记录ID","参考表信息（仅对照）"];
    static readonly string[] FileHeaders=["文件ID","原图文件","诊断摘要","是否需确认","核查状态","文件核查说明","父子同时纳入说明","原图SHA256"];
    static readonly string[] Unknown=["待核","待确认","待定","未知","未确定","未指定","按图","见图","?","？","N/A"];
    public static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    static string SafePath(string value)
    {
        var full=Path.GetFullPath(value);
        if(!full.StartsWith(@"D:\CADtest\",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Path must remain inside D:/CADtest");
        return full;
    }
    static string NewOutput(string value,string? stagedInput=null)
    {
        var full=SafePath(value);
        if(!full.StartsWith(@"D:\CADtest\outputs\",StringComparison.OrdinalIgnoreCase)||File.Exists(full))throw new InvalidDataException("Use a new directory below outputs");
        if(Directory.Exists(full)){
            var entries=Directory.GetFileSystemEntries(full);
            if(stagedInput is null||entries.Length!=1||Path.GetFullPath(entries[0])!=Path.GetFullPath(stagedInput)||Path.GetFileName(stagedInput)!="input-dataset.json")throw new InvalidDataException("Existing results are never overwritten");
        }else Directory.CreateDirectory(full);
        using(var claim=new FileStream(Path.Combine(full,"operation.claim"),FileMode.CreateNew,FileAccess.Write)){}
        return full;
    }
    static void VerifyDataset(ReviewDataset data)
    {
        if(data.SchemaVersion!=1||string.IsNullOrWhiteSpace(data.TaskId)||data.Items is null||data.Items.Length>10000)throw new InvalidDataException("Invalid dataset schema/size");
        if(data.Files is null||data.Files.Length==0||data.Sources is null||data.Files.Select(f=>f.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=data.Files.Length||data.Items.Select(r=>r.Id).Distinct().Count()!=data.Items.Length)throw new InvalidDataException("Missing or repeated IDs");
        foreach(var f in data.Files){
            var path=SafePath(f.Path);
            if(f.Sha256.Length!=64||!f.Sha256.All(Uri.IsHexDigit)||!data.Sources.Any(s=>SafePath(s.Path).Equals(path,StringComparison.OrdinalIgnoreCase)&&s.Sha256.Equals(f.Sha256,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Original drawing is not bound to Sources: "+f.Id);
        }
        var files=data.Files.Select(f=>f.Id).ToHashSet();
        foreach(var row in data.Items)if(!files.Contains(row.FileId)||string.IsNullOrWhiteSpace(row.Id)||string.IsNullOrWhiteSpace(row.Cad.DrawingNumber))throw new InvalidDataException("Item does not have a valid source file/number");
        foreach(var row in data.Items)if(row.DefaultDecision is not null&&(row.DefaultDecision.Mode!="不纳入"||string.IsNullOrWhiteSpace(row.DefaultDecision.Note)))throw new InvalidDataException("Unsupported default decision: "+row.Id);
    }
    static void VerifySources(ReviewDataset data)
    {
        foreach(var source in data.Sources){var path=SafePath(source.Path);if(!File.Exists(path)||!Hash(path).Equals(source.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Source changed or missing: "+path);}
        foreach(var f in data.Files){var path=SafePath(f.Path);if(!File.Exists(path)||!Hash(path).Equals(f.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Drawing changed or missing: "+f.Id);}
    }
    static object?[] Original(ReviewItem r,ReviewDataset data)=>[r.FileId,r.Cad.DrawingNumber,r.Cad.Name,r.Cad.Material,r.Cad.Treatment,r.Cad.Quantity,r.Cad.Unit,r.Warnings,
        r.Kind,string.Join("；",r.Parents),r.QuantityEvidence,r.TreatmentEvidence,data.Files.Single(f=>f.Id==r.FileId).Name,r.Id,r.ReferenceInfo];
    static object?[] OriginalFile(ReviewFile f)=>[f.Id,f.Name,f.Diagnostics,f.RequiresAcknowledgement?"需确认":"仅信息",f.Sha256];
    static void Put(IXLCell cell,object? value){if(value is null)return;if(value is decimal d)cell.Value=(double)d;else if(value is int n)cell.Value=n;else cell.Value=Convert.ToString(value,CultureInfo.InvariantCulture)??"";}
    static void BaseStyle(IXLWorksheet sheet,int last,int cols,string title,string note,double[] widths)
    {
        sheet.ShowGridLines=false;sheet.Style.Font.FontName="Microsoft YaHei";sheet.Style.Font.FontSize=10;
        sheet.Cell(2,1).Value=title;sheet.Cell(2,1).Style.Font.FontSize=16;sheet.Cell(2,1).Style.Font.Bold=true;
        sheet.Cell(3,1).Value=note;sheet.Row(1).Height=10;sheet.Row(2).Height=27;
        sheet.Range(2,1,2,Math.Min(8,cols)).Merge();sheet.Range(3,1,3,Math.Min(8,cols)).Merge();
        sheet.Cell(3,1).Style.Alignment.WrapText=true;sheet.Row(3).Height=30;
        for(int c=1;c<=cols;c++)sheet.Column(c).Width=widths[c-1];
        sheet.Range(7,1,7,cols).Style.Fill.BackgroundColor=XLColor.FromHtml("#304B63");
        sheet.Range(7,1,7,cols).Style.Font.FontColor=XLColor.White;sheet.Range(7,1,7,cols).Style.Font.Bold=true;
        sheet.Range(7,1,last,cols).Style.Alignment.Vertical=XLAlignmentVerticalValues.Center;
        sheet.Range(7,1,last,cols).Style.Alignment.WrapText=true;sheet.Row(7).Height=34;
        sheet.Range(7,1,last,cols).SetAutoFilter();sheet.SheetView.FreezeRows(7);sheet.SheetView.FreezeColumns(2);
        for(int r=8;r<=last;r++){
            int lines=1;
            for(int c=1;c<=cols;c++){
                // Excel does not auto-fit wrapped rows when opening a generated file.
                // Count CJK characters at double width and keep the source text visible.
                var text=sheet.Cell(r,c).GetString();
                int cellLines=text.Split('\n').Sum(part=>Math.Max(1,(int)Math.Ceiling(part.Sum(ch=>ch>=0x2E80?2d:1d)/Math.Max(4,widths[c-1]*0.88-2))));
                lines=Math.Max(lines,cellLines);
            }
            sheet.Row(r).Height=Math.Min(409,Math.Max(38,lines*14+10));
        }
    }
    static SaveOptions SaveOptions()=>new(){ValidatePackage=true,EvaluateFormulasBeforeSaving=true};
    public static void Prepare(string input,string output)
    {
        input=SafePath(input);var data=JsonData.Read<ReviewDataset>(input);VerifyDataset(data);VerifySources(data);
        var sourceHash=Hash(input);var folder=NewOutput(output,input);var manifest=Path.Combine(folder,"source.json");File.Copy(input,manifest);
        using var book=new XLWorkbook();var ws=book.AddWorksheet(ReviewSheet);var files=book.AddWorksheet(FilesSheet);var meta=book.AddWorksheet(MetaSheet);
        for(int c=0;c<Headers.Length;c++)ws.Cell(7,c+1).Value=Headers[c];
        int n=First;
        foreach(var row in data.Items)
        {
            var original=Original(row,data);
            for(int c=0;c<8;c++)Put(ws.Cell(n,c+1),original[c]);
            for(int c=8;c<15;c++)Put(ws.Cell(n,c+9),original[c]);
            ws.Cell(n,9).Value="待复核";
            if(row.DefaultDecision is not null){ws.Cell(n,9).Value=row.DefaultDecision.Mode;ws.Cell(n,16).Value=row.DefaultDecision.Note;}
            ws.Range(n,9,n,16).Style.Fill.BackgroundColor=XLColor.FromHtml("#FFF6D9");
            if(row.RequiresReview){ws.Cell(n,8).Style.Fill.BackgroundColor=XLColor.FromHtml("#FDE9E7");ws.Cell(n,8).Style.Font.FontColor=XLColor.DarkRed;}
            n++;
        }
        BaseStyle(ws,n-1,23,data.Title,"只填写黄色确认区。采用图纸无需重复抄写已有值；缺值/冲突须人工修订。正式导出由程序重新校验。",[10,36,24,16,22,12,12,55,16,36,24,18,24,12,12,44,20,36,65,65,65,26,70]);
        ws.Range(5,1,5,8).Merge();ws.Cell(5,1).Value=$"共 {data.Files.Length} 份图纸、{data.Items.Length} 条候选明细。先核对文件范围，再填写逐项确认。";
        if(n>First){ws.Range(First,9,n-1,9).CreateDataValidation().List("\"待复核,采用图纸,人工修订,不纳入\"",true);
            ws.Range(First,6,n-1,6).Style.NumberFormat.Format="General";ws.Range(First,14,n-1,14).Style.NumberFormat.Format="General";}
        for(int c=0;c<FileHeaders.Length;c++)files.Cell(7,c+1).Value=FileHeaders[c];
        n=First;
        foreach(var f in data.Files){var values=OriginalFile(f);for(int c=0;c<4;c++)Put(files.Cell(n,c+1),values[c]);Put(files.Cell(n,8),values[4]);files.Cell(n,5).Value=f.RequiresAcknowledgement||!data.Items.Any(r=>r.FileId==f.Id)?"待核查":"无需确认";files.Range(n,5,n,7).Style.Fill.BackgroundColor=XLColor.FromHtml("#FFF6D9");n++;}
        BaseStyle(files,n-1,8,"文件诊断与范围确认","零明细/未关联图框/异常数值需核查。选择不纳入本次并填写原因，可整份文件排除；父子均纳入必须说明。",[12,65,85,14,16,70,70,70]);
        files.Range(First,5,n-1,5).CreateDataValidation().List("\"待核查,已核查,无需确认,不纳入本次\"",true);
        meta.Cell(1,1).Value="schema";meta.Cell(1,2).Value=1;meta.Cell(2,1).Value="task_id";meta.Cell(2,2).Value=data.TaskId;meta.Cell(3,1).Value="source_sha256";meta.Cell(3,2).Value=sourceHash;
        meta.Visibility=XLWorksheetVisibility.VeryHidden;
        var path=Path.Combine(folder,"图纸复核.xlsx");book.SaveAs(path,SaveOptions());
        using(var reopened=new XLWorkbook(path)){if(reopened.Worksheet(ReviewSheet).LastRowUsed()!.RowNumber()!=data.Items.Length+7)throw new InvalidDataException("Review export row count changed");}
        VerifySources(data);if(Hash(input)!=sourceHash||Hash(manifest)!=sourceHash)throw new InvalidDataException("Dataset changed during prepare");
        File.WriteAllText(Path.Combine(folder,"prepare-result.json"),JsonData.Text(new {status="prepared",sourceHash,review=path,rows=data.Items.Length,taskId=data.TaskId}));
        Console.WriteLine(JsonData.Text(new {status="prepared",folder,rows=data.Items.Length}));
    }
    static void CheckArchive(string path)
    {
        if(Path.GetExtension(path).ToLowerInvariant()!=".xlsx"||new FileInfo(path).Length>32L*1024*1024)throw new InvalidDataException("Review workbook must be an XLSX under 32 MiB");
        using var zip=ZipFile.OpenRead(path);
        if(zip.Entries.Count>5000||zip.Entries.Sum(e=>e.Length)>128L*1024*1024||zip.Entries.Any(e=>e.Length>32L*1024*1024||e.FullName.Contains("vbaProject",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Workbook package limits exceeded or macros present");
    }
    static string Text(IXLCell cell){if(cell.HasFormula)throw new InvalidDataException($"Formula not allowed in source/confirmation cell {cell.Address}");return cell.IsEmpty()?"":cell.GetString().Trim();}
    static decimal? Number(IXLCell cell)
    {
        if(cell.HasFormula)throw new InvalidDataException("Quantity must be a literal number: "+cell.Address);
        if(cell.IsEmpty())return null;
        var text=cell.DataType==XLDataType.Number?cell.GetDouble().ToString("R",CultureInfo.InvariantCulture):cell.GetString().Trim();
        if(!decimal.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||value<0)throw new InvalidDataException("Invalid nonnegative quantity: "+cell.Address);
        var digits=value.ToString("G29",CultureInfo.InvariantCulture).Split('E')[0].Replace(".","").Trim('0');
        if(digits.Length>15)throw new InvalidDataException("Quantity exceeds reliable Excel precision");
        return value;
    }
    static void Same(IXLCell cell,object? value)
    {
        if(value is decimal number){if(Number(cell)!=number)throw new InvalidDataException("Source quantity changed at "+cell.Address);}
        else if(Text(cell)!=(Convert.ToString(value,CultureInfo.InvariantCulture)??"").Trim())throw new InvalidDataException("Source value changed at "+cell.Address);
    }
    static bool Meaningful(string? s)=>!string.IsNullOrWhiteSpace(s)&&!Unknown.Contains(s.Trim(),StringComparer.OrdinalIgnoreCase);
    static bool Complete(CadValues c)=>Meaningful(c.DrawingNumber)&&c.Quantity.HasValue&&c.Quantity>=0&&Meaningful(c.Unit)&&Meaningful(c.Material)&&Meaningful(c.Treatment);
    public static ValidationResult Validate(string manifest,string review)
    {
        manifest=SafePath(manifest);review=SafePath(review);CheckArchive(review);
        var result=new ValidationResult();var data=JsonData.Read<ReviewDataset>(manifest);VerifyDataset(data);result.TaskId=data.TaskId;
        var sourceHash=Hash(manifest);var reviewHash=Hash(review);result.ManifestSha256=sourceHash;result.ReviewSha256=reviewHash;
        try{VerifySources(data);}catch(Exception ex){result.Errors.Add(ex.Message);return result;}
        using var book=new XLWorkbook(review,new LoadOptions{RecalculateAllFormulas=false});
        var meta=book.Worksheet(MetaSheet);
        if(Number(meta.Cell(1,2))!=1||Text(meta.Cell(2,2))!=data.TaskId||Text(meta.Cell(3,2))!=sourceHash)throw new InvalidDataException("Workbook does not belong to this source/version");
        var ws=book.Worksheet(ReviewSheet);var files=book.Worksheet(FilesSheet);
        for(int c=0;c<Headers.Length;c++)if(Text(ws.Cell(7,c+1))!=Headers[c])throw new InvalidDataException("Review header changed");
        for(int c=0;c<FileHeaders.Length;c++)if(Text(files.Cell(7,c+1))!=FileHeaders[c])throw new InvalidDataException("File header changed");
        var fileIds=new HashSet<string>();
        foreach(var row in files.RowsUsed().Where(r=>r.RowNumber()>=First))
        {
            int n=row.RowNumber();var id=Text(files.Cell(n,1));var f=data.Files.SingleOrDefault(f=>f.Id==id);
            if(f is null||!fileIds.Add(id))throw new InvalidDataException("Unknown/repeated file ID");
            var original=OriginalFile(f);for(int c=0;c<4;c++)Same(files.Cell(n,c+1),original[c]);Same(files.Cell(n,8),original[4]);
            var state=Text(files.Cell(n,5));var note=Text(files.Cell(n,6));var parentChild=Text(files.Cell(n,7));
            if(state is not ("待核查" or "已核查" or "无需确认" or "不纳入本次"))result.Errors.Add(id+": 未知文件核查状态");
            if(state=="不纳入本次"&&!Meaningful(note))result.Errors.Add(id+": 整份排除必须说明原因");
            result.Files.Add(new(id,state,note,parentChild));
        }
        if(fileIds.Count!=data.Files.Length)throw new InvalidDataException("File rows deleted");
        var byId=data.Items.ToDictionary(r=>r.Id);var seen=new HashSet<string>();
        foreach(var record in ws.RowsUsed().Where(r=>r.RowNumber()>=First))
        {
            int row=record.RowNumber();var id=Text(ws.Cell(row,22));
            if(!byId.TryGetValue(id,out var original)||!seen.Add(id))throw new InvalidDataException("Missing/unknown/repeated row ID at row "+row);
            var source=Original(original,data);for(int c=0;c<8;c++)Same(ws.Cell(row,c+1),source[c]);for(int c=8;c<15;c++)Same(ws.Cell(row,c+9),source[c]);
            var fileDecision=result.Files.Single(f=>f.FileId==original.FileId);
            if(fileDecision.State=="不纳入本次") {result.Excluded.Add(new(id,original.FileId,original.Cad.DrawingNumber,"文件级排除："+fileDecision.Note));continue;}
            string mode=Text(ws.Cell(row,9)),note=Text(ws.Cell(row,16));
            if(mode=="待复核"){result.Pending++;result.Errors.Add(id+": 尚未复核");continue;}
            if(mode=="不纳入"){if(!Meaningful(note)){result.Errors.Add(id+": 不纳入须说明原因");continue;}result.Excluded.Add(new(id,original.FileId,original.Cad.DrawingNumber,note));continue;}
            if(mode is not ("采用图纸" or "人工修订")){result.Errors.Add(id+": 未知处理方式");continue;}
            var fields=Enumerable.Range(10,6).Select(c=>c==14?"":Text(ws.Cell(row,c))).ToArray();
            var quantity=Number(ws.Cell(row,14));
            bool hasOverride=fields.Any(v=>v!="")||quantity.HasValue;
            if(mode=="采用图纸"&&hasOverride){result.Errors.Add(id+": 采用图纸时不可混入修订值，请选择人工修订");continue;}
            string? Override(string value,string? sourceValue)=>value==""?sourceValue:value;
            var final=mode=="采用图纸"?original.Cad:new CadValues(Override(fields[0],original.Cad.DrawingNumber)!,Override(fields[1],original.Cad.Name),Override(fields[2],original.Cad.Material),Override(fields[3],original.Cad.Treatment),quantity??original.Cad.Quantity,Override(fields[5],original.Cad.Unit));
            if(!Complete(final)){result.Errors.Add(id+": 图号/数量/单位/材质/工艺仍有缺值或未确定内容");continue;}
            if((original.RequiresReview||mode=="人工修订")&&!Meaningful(note)){result.Errors.Add(id+": 异议或修订必须填写复核说明");continue;}
            if(original.DefaultDecision is not null&&note==original.DefaultDecision.Note.Trim()){result.Errors.Add(id+": 图面打X的明细改为纳入时，须改写复核说明，写明纳入原因");continue;}
            if(mode=="人工修订"&&!hasOverride){result.Errors.Add(id+": 人工修订未填写修订值");continue;}
            result.Accepted.Add(new(id,original.FileId,mode,original.Cad,final,note,original.Parents));
        }
        if(seen.Count!=data.Items.Length)throw new InvalidDataException("Rows deleted from review workbook");
        foreach(var f in data.Files)
        {
            var decision=result.Files.Single(d=>d.FileId==f.Id);
            if(decision.State=="不纳入本次")continue;
            bool empty=!data.Items.Any(r=>r.FileId==f.Id),included=result.Accepted.Any(r=>r.FileId==f.Id);
            if(included||empty){
                if(decision.State=="待核查")result.Errors.Add(f.Id+": 文件仍被标记为待核查");
                else if((f.RequiresAcknowledgement||empty)&&(decision.State!="已核查"||!Meaningful(decision.Note)))result.Errors.Add(f.Id+": 文件诊断/零明细需核查并说明");
            }
        }
        string NumberKey(string value)=>string.Concat(value.Normalize(NormalizationForm.FormKC).Where(c=>!char.IsWhiteSpace(c))).ToUpperInvariant();
        foreach(var group in result.Accepted.GroupBy(r=>(r.FileId,NumberKey(r.Final.DrawingNumber))))if(group.Count()>1)result.Errors.Add(group.Key+": 同文件最终图号重复，不自动合并");
        foreach(var child in result.Accepted)if(child.Parents.Any(p=>result.Accepted.Any(parent=>parent.FileId==child.FileId&&parent.Original.DrawingNumber==p)))
            if(!Meaningful(result.Files.Single(f=>f.FileId==child.FileId).ParentChildNote))result.Errors.Add(child.FileId+": 父图和子件同时纳入，须说明计价范围");
        VerifySources(data);if(Hash(review)!=reviewHash||Hash(manifest)!=sourceHash)throw new InvalidDataException("Review/source changed during validation");
        result.Status=result.Errors.Count>0?"blocked":result.Accepted.Count>0?"ready":"nothing_to_export";return result;
    }
    public static ValidationResult Export(string manifest,string review,string output,string reviewer)
    {
        if(!Meaningful(reviewer))throw new InvalidDataException("Reviewer name is required");
        // Recompute at the write boundary; callers and cell colors cannot supply approval.
        var fresh=Validate(manifest,review);var folder=NewOutput(output);
        File.WriteAllText(Path.Combine(folder,"validation.json"),JsonData.Text(fresh));
        if(fresh.Status=="nothing_to_export"){
            File.WriteAllText(Path.Combine(folder,"确认结果.json"),JsonData.Text(new {schemaVersion=1,engineVersion="review-20261106.1",status="nothing_to_export",taskId=fresh.TaskId,reviewer,confirmedUtc=DateTimeOffset.UtcNow,sourceSha256=fresh.ManifestSha256,reviewSha256=fresh.ReviewSha256,accepted=fresh.Accepted,excluded=fresh.Excluded,files=fresh.Files}));
            return fresh;
        }
        if(fresh.Status!="ready")return fresh;
        using var book=new XLWorkbook();var ws=book.AddWorksheet("确认明细");
        string[] headers=["样本","图号","部品名","材质","表面处理","数量","单位","单价","金额","复核说明","原图文件","记录ID"];
        for(int c=0;c<headers.Length;c++)ws.Cell(7,c+1).Value=headers[c];
        var data=JsonData.Read<ReviewDataset>(manifest);if(Hash(manifest)!=fresh.ManifestSha256||Hash(review)!=fresh.ReviewSha256)throw new InvalidDataException("Source/review changed after validation");int n=First;
        foreach(var r in fresh.Accepted){object?[] values=[r.FileId,r.Final.DrawingNumber,r.Final.Name,r.Final.Material,r.Final.Treatment,r.Final.Quantity,r.Final.Unit,null,null,r.Reason,data.Files.Single(f=>f.Id==r.FileId).Name,r.Id];for(int c=0;c<values.Length;c++)Put(ws.Cell(n,c+1),values[c]);ws.Cell(n,9).FormulaA1=$"IF(H{n}=\"\",\"\",F{n}*H{n})";n++;}
        BaseStyle(ws,n-1,headers.Length,"人工复核后的确认明细","仅包含已确认且纳入的记录。单位分别保留，不合计跨单位数量。单价留空，金额公式保留。",[12,36,24,16,25,12,12,14,18,65,65,26]);
        ws.Range(First,8,n-1,8).Style.Fill.BackgroundColor=XLColor.FromHtml("#FFF6D9");ws.Range(First,8,n-1,9).Style.NumberFormat.Format="0.00";
        var temp=Path.Combine(folder,"确认明细.tmp.xlsx");var final=Path.Combine(folder,"确认明细.xlsx");book.SaveAs(temp,SaveOptions());
        using(var saved=new XLWorkbook(temp)){if(saved.Worksheet("确认明细").LastRowUsed()!.RowNumber()!=fresh.Accepted.Count+7)throw new InvalidDataException("Final row count changed");foreach(var cell in saved.Worksheet("确认明细").Range(First,8,n-1,8).Cells())if(!cell.IsEmpty())throw new InvalidDataException("Unit price must remain empty");}
        VerifySources(data);if(Hash(manifest)!=fresh.ManifestSha256||Hash(review)!=fresh.ReviewSha256)throw new InvalidDataException("Source/review changed before publishing");
        // Publish the audit record before the final workbook name becomes visible.
        File.WriteAllText(Path.Combine(folder,"确认结果.json"),JsonData.Text(new {schemaVersion=1,engineVersion="review-20261106.1",taskId=data.TaskId,reviewer,confirmedUtc=DateTimeOffset.UtcNow,sourceSha256=fresh.ManifestSha256,reviewSha256=fresh.ReviewSha256,outputSha256=Hash(temp),accepted=fresh.Accepted,excluded=fresh.Excluded,files=fresh.Files}));
        File.Move(temp,final);
        return fresh;
    }
}
