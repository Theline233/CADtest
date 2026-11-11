using System.Text;
using ClosedXML.Excel;

namespace CadReviewUI;

/// <summary>无界面自测：用已有的识别结果走完 载入→保存→写入 Excel→引擎校验→导出→重开导出文件。
/// 全部产物写到指定的新目录（outputs 下），真实复核存档和原识别结果不会被改动。</summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string reviewFolder, string outDir)
    {
        var log = new StringBuilder();
        int failures = 0, checks = 0;
        void Check(string name, bool ok, string detail = "")
        {
            checks++;
            if (!ok) failures++;
            log.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }
        void Info(string text) => log.AppendLine("       " + text);

        try
        {
            Directory.CreateDirectory(outDir);
            Engine.SessionsRoot = Path.Combine(outDir, "sessions");

            var notReady = Engine.CheckReady();
            Check("引擎文件就绪", notReady == null, notReady ?? "");
            if (notReady != null) return Finish(log, outDir, failures, checks);

            var task = TaskSession.Open(reviewFolder);
            Check("载入任务", task.Items.Count > 0 && task.Files.Count > 0, $"{task.Files.Count} 份图纸 / {task.Items.Count} 条候选");
            Info($"校验/导出引擎：{task.Kind}");
            var c0 = task.Counts();
            // 图面整框打X的明细由识别结果预设为“不纳入”，其余初始为待复核。
            int defaults = task.Items.Count(i => i.Src.DefaultDecision is { Mode: Rules.Exclude });
            Check("初始待复核/默认不纳入", c0.Pending == task.Items.Count - defaults && c0.Exclude == defaults && c0.Adopt + c0.Revise == 0,
                $"待复核 {c0.Pending}，默认不纳入 {defaults}");
            Check("默认不纳入带原因", task.Items.Where(i => i.Src.DefaultDecision != null).All(i => i.Mode == Rules.Exclude && i.Note == i.Src.DefaultDecision!.Note));

            // 1) 空决定写出再校验：不能因格式变动被引擎判成“原值被改”
            var problems = task.WriteReviewed();
            Check("写入已复核.xlsx（空决定）", problems.Count == 0 && File.Exists(task.ReviewedPath), string.Join("；", problems));
            var v0 = await Engine.ValidateAsync(task.Kind, task.Manifest, task.ReviewedPath);
            Check("引擎接受往返后的工作簿", v0.Status == "blocked" && v0.Pending == task.Items.Count - defaults,
                $"status={v0.Status}, pending={v0.Pending}, error={v0.Error}");

            // 2) 往返读取
            var (readItems, readFiles) = ReviewIo.Read(task.ReviewedPath);
            Check("从 Excel 读回的条数一致", readItems.Count == task.Items.Count && readFiles.Count == task.Files.Count);

            // 3) 构造一个可导出的小场景：只保留一份文件
            var completeByFile = task.Items.Where(i => i.IsCompleteOriginal).GroupBy(i => i.FileId)
                .Where(g => g.Count() >= 2).Select(g => (Id: g.Key, Total: task.Items.Count(i => i.FileId == g.Key)))
                .OrderBy(x => x.Total).ToList();
            Check("找到可用于自测的文件", completeByFile.Count > 0);
            var keep = completeByFile[0].Id;
            Info($"自测文件 {keep}，共 {completeByFile[0].Total} 条候选");

            foreach (var f in task.Files)
            {
                if (f.Id == keep) { f.State = "已核查"; f.Note = "自测：已核查，仅验证流程"; f.ParentChildNote = "自测：父子均纳入仅验证流程"; }
                else { f.State = "不纳入本次"; f.Note = "自测：整份排除"; }
            }
            var seen = new HashSet<string>();
            ItemRow? revised = null;
            foreach (var i in task.Items.Where(i => i.FileId == keep))
            {
                if (i.Src.DefaultDecision != null) continue; // 保留默认不纳入，验证其导出排除
                var key = i.OrigDrawing.Normalize(NormalizationForm.FormKC).Replace(" ", "").ToUpperInvariant();
                if (!seen.Add(key)) { i.Mode = Rules.Exclude; i.Note = "自测：同文件重复图号，不重复纳入"; continue; }
                if (i.IsCompleteOriginal) { i.Mode = Rules.Adopt; i.Note = "自测：采用图纸识别值"; }
                else if (revised == null)
                {
                    revised = i;
                    foreach (var m in Rules.MissingFields(i.Src.Cad))
                    {
                        switch (m)
                        {
                            case "数量": i.ConfQuantity = "1"; break;
                            case "单位": i.ConfUnit = "EA"; break;
                            case "材质": i.ConfMaterial = "自测材质"; break;
                            case "工艺": i.ConfTreatment = "无"; break;
                        }
                    }
                    i.Note = "自测：缺项由人工补全";
                }
                else { i.Mode = Rules.Exclude; i.Note = "自测：原值缺项，本次不纳入"; }
            }
            if (revised != null)
                Check("填写确认值时自动切换为人工修订", revised.Mode == Rules.Revise, "当前处理方式=" + revised.Mode);

            // 4) 反例：采用图纸却没有复核说明 → 引擎必须拦住，并能映射回具体行
            // 人工修订、以及带核对提示的行，必须写复核说明；挑这样的行来测
            var victim = revised ?? task.Items.FirstOrDefault(i => i.FileId == keep && i.Mode == Rules.Adopt && i.Src.RequiresReview);
            if (victim == null) Info("跳过：该数据集里没有必须写说明的采用项，无法构造“缺说明”反例");
            else
            {
                var savedNote = victim.Note;
                victim.Note = "";
                task.WriteReviewed();
                var vBad = await Engine.ValidateAsync(task.Kind, task.Manifest, task.ReviewedPath);
                var mapped = task.MapErrors(vBad.Errors);
                Check("缺复核说明被拦住", vBad.Status == "blocked" && vBad.Errors.Any(e => e.StartsWith(victim.Id)), $"status={vBad.Status}, errors={vBad.Errors.Count}");
                Check("错误能映射到具体行", victim.HasError && mapped.Any(e => e.ItemId == victim.Id), victim.ErrorText);
                Check("引擎中文提示未乱码", victim.ErrorText.Contains("复核说明"), victim.ErrorText);
                victim.Note = savedNote;
            }

            // 5) 正例：校验通过
            var pr = task.WriteReviewed();
            Check("写入已复核.xlsx（完整决定）", pr.Count == 0, string.Join("；", pr));
            var vOk = await Engine.ValidateAsync(task.Kind, task.Manifest, task.ReviewedPath);
            task.MapErrors(vOk.Errors);
            Check("校验通过", vOk.Status == "ready", $"status={vOk.Status}, errors={string.Join(" | ", vOk.Errors.Take(5))}");
            var expectedAccepted = task.Items.Count(i => i.FileId == keep && (i.Mode == Rules.Adopt || i.Mode == Rules.Revise));
            Check("采纳条数与界面一致", vOk.Accepted.Count == expectedAccepted, $"engine={vOk.Accepted.Count}, ui={expectedAccepted}");
            Check("已清除行错误", task.Items.All(i => !i.HasError));

            // 6) 保存/重新载入
            task.Save();
            var again = TaskSession.Open(reviewFolder);
            Check("重新打开后保留复核内容", again.Counts() == task.Counts() && again.Files.Single(f => f.Id == keep).State == "已核查",
                $"{again.Counts()} vs {task.Counts()}");
            Check("修订值保留", revised == null || again.Items.Single(i => i.Id == revised.Id).ConfUnit == revised.ConfUnit);

            // 7) 导出并重新打开最终文件
            var outFolder = Path.Combine(outDir, "confirmed");
            var vExp = await Engine.ExportAsync(task.Kind, task.Manifest, task.ReviewedPath, outFolder, "界面自测");
            Check("导出成功", vExp.Status == "ready" && File.Exists(Path.Combine(outFolder, "确认明细.xlsx")), $"status={vExp.Status}, {vExp.Error}");
            if (File.Exists(Path.Combine(outFolder, "确认明细.xlsx")))
            {
                using var book = new XLWorkbook(Path.Combine(outFolder, "确认明细.xlsx"));
                var ws = book.Worksheet("确认明细");
                int rows = (ws.LastRowUsed()?.RowNumber() ?? 7) - 7;
                var ids = new HashSet<string>();
                bool priceEmpty = true;
                for (int r = 8; r < 8 + rows; r++)
                {
                    ids.Add(ws.Cell(r, 12).GetString());
                    if (!ws.Cell(r, 8).IsEmpty()) priceEmpty = false;
                }
                Check("导出明细行数 = 采纳条数", rows == expectedAccepted, $"rows={rows}");
                Check("导出的记录ID都来自采纳项", ids.SetEquals(task.Items.Where(i => i.FileId == keep && (i.Mode == Rules.Adopt || i.Mode == Rules.Revise)).Select(i => i.Id)));
                Check("单价列保持空白", priceEmpty);
                if (revised != null)
                {
                    var rowOfRevised = Enumerable.Range(8, rows).FirstOrDefault(r => ws.Cell(r, 12).GetString() == revised.Id);
                    Check("人工修订值写进导出", rowOfRevised != 0 && ws.Cell(rowOfRevised, 7).GetString() == revised.ConfUnit
                        || revised.ConfUnit == "", $"行 {rowOfRevised}");
                }
            }
            Check("审计记录已生成", File.Exists(Path.Combine(outFolder, "确认结果.json")) && File.Exists(Path.Combine(outFolder, "validation.json")));
        }
        catch (Exception ex)
        {
            failures++;
            log.AppendLine("[FAIL] 自测异常：" + ex);
        }
        return Finish(log, outDir, failures, checks);
    }

    static int Finish(StringBuilder log, string outDir, int failures, int checks)
    {
        log.AppendLine();
        log.AppendLine(failures == 0 ? $"全部通过：{checks} 项检查" : $"有 {failures} 项失败（共 {checks} 项检查）");
        File.WriteAllText(Path.Combine(outDir, "selftest.log"), log.ToString(), new UTF8Encoding(true));
        return failures == 0 ? 0 : 1;
    }
}
