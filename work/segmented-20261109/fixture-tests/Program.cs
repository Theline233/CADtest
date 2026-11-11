using ClosedXML.Excel;

if(args.Length < 2) throw new ArgumentException("fixture INPUT OUTPUT | recalc OUTPUT");
if(args[0]=="fixture")
{
    if(args.Length!=3 || File.Exists(args[2])) throw new ArgumentException("Use a new fixture path");
    using var book=new XLWorkbook(args[1]);
    var ws=book.Worksheet("复核明细");
    if(ws.LastRowUsed()!.RowNumber()!=9) throw new InvalidDataException("Fixture requires exactly two test rows");
    ws.Cell(2,1).Value="【合成测试，禁止报价】";
    for(int r=8;r<=9;r++)
    {
        ws.Cell(r,9).Value="人工修订";
        ws.Cell(r,10).Value=$"SYNTHETIC-TEST-{r-7:D3}";
        ws.Cell(r,11).Value="模拟零件（非真实审批）";
        ws.Cell(r,12).Value="模拟材质";
        ws.Cell(r,13).Value="模拟工艺";
        ws.Cell(r,14).Value=r==8?2:0;
        ws.Cell(r,15).Value="EA";
        ws.Cell(r,16).Value="合成测试，非人工复核或客户审批";
    }
    var files=book.Worksheet("文件诊断");
    files.Cell(8,5).Value="已核查";
    files.Cell(8,6).Value="合成测试数据，仅验证文件级确认";
    Directory.CreateDirectory(Path.GetDirectoryName(args[2])!);
    book.SaveAs(args[2]);
    Console.WriteLine("synthetic_fixture_created");
}
else if(args[0]=="recalc")
{
    var path=args[1];
    using var book=new XLWorkbook(path);
    var ws=book.Worksheet("确认明细");
    if(!ws.Cell(8,8).IsEmpty() || !ws.Cell(9,8).IsEmpty()) throw new Exception("Price is not blank");
    ws.Cell(8,8).Value=10; ws.Cell(9,8).Value=20;
    book.RecalculateAllFormulas();
    if(ws.Cell(8,9).GetDouble()!=20 || ws.Cell(9,9).GetDouble()!=0) throw new Exception("Amount formula failed");
    using var reopened=new XLWorkbook(path);
    if(!reopened.Worksheet("确认明细").Cell(8,8).IsEmpty() || !reopened.Worksheet("确认明细").Cell(9,8).IsEmpty()) throw new Exception("Test prices persisted");
    Console.WriteLine("reopened_and_recalculated_in_memory_20_and_0");
}
else throw new ArgumentException("Unknown test command");
