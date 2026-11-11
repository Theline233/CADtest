# CAD 识别：分段处理的独立版

这是 `D:\CADtest` 内的本机命令行开发副本，不替换旧版、网站或原始资料。完整测试与失败记录见 `D:\CADtest\outputs\segmented-20261109\RESULTS.md`。此版不是安装包；没有使用外部模型 API，也没有用参考 Excel 反填 CAD。

## 识别与复核

使用 PowerShell 指定项目内的 DWG 和 `outputs` 下一个尚不存在的目录：

```powershell
$cadRuntime = 'C:\Program Files\WindowsApps\OpenAI.CodexPrimaryRuntime.v26-1110-778-0_26.1110.778.0_x64__3k8sg7r9htsxt\dependencies'
$cadPython = Join-Path $cadRuntime 'python\python.exe'
$cadNode = Join-Path $cadRuntime 'node\bin\node.exe'
& 'D:\CADtest\work\segmented-20261109\RunAccuracy.ps1' -Mode Recognize `
  -Drawing @('D:\CADtest\图纸与要求\11.18 TX26320-261117-PPC1-C3855-1 261104.dwg') `
  -Output 'D:\CADtest\outputs\my-cad-run' -Python $cadPython -Node $cadNode
```

`-Drawing` 可传多个绝对路径，逐份串行处理。识别全部完成后生成 `review\图纸复核.xlsx` 和 `review\source.json`。先查看“文件诊断”，然后在“复核明细”黄色区域逐条填写并另存；文件级诊断须人工核查并填写说明。父图与子件同时纳入须说明计价范围。数量、单位、材质或工艺缺失时不可自动通过；数量 0 是有效值。不要把候选数当作应报价件数。

新识别任务中，同一图纸内的重复图号保留每处证据并标记，正常图框的兼容数量默认累加；可在界面改为按一处计。整框打 X 不计入。数量缺失、单位或范围冲突时不自动累加；各处数量齐全但单位全部缺失时先显示合计，单位留空待核，补齐前不能导出。旧任务继续使用自身冻结的程序和原数量。详见 `outputs\duplicate-sum-20261111\RESULTS.md`。

## 独立任务并行识别

界面多张导入会自动调用并行协调器；`RunAccuracy.ps1` 的多张参数仍是串行的一个复核任务。若需命令行并行，先在工作区内准备 UTF-8 JSON 清单，分别指定图纸和 `outputs` 下尚不存在的独立任务目录，例如：

```json
{
  "tasks": [
    {"id": "D001", "drawing": "D:\\CADtest\\图纸与要求\\第一张.dwg", "output": "D:\\CADtest\\outputs\\parallel-example-D001"},
    {"id": "D002", "drawing": "D:\\CADtest\\图纸与要求\\第二张.dwg", "output": "D:\\CADtest\\outputs\\parallel-example-D002"}
  ]
}
```

清单及图纸必须在项目内，路径用绝对路径；上例图纸名仅用于说明。批次目录和全部任务目录均必须是 `outputs` 下此前不存在的不同目录。将上面的运行时变量设为当前实际路径后：

```powershell
& $cadPython -B -X utf8 'D:\CADtest\work\segmented-20261109\runner.py' `
  --output 'D:\CADtest\outputs\parallel-example-batch' `
  --parallel-batch 'D:\CADtest\outputs\parallel-example-manifest.json' `
  --node $cadNode --memory-mib 3072 --timeout 300 --cpu-percent 75 --max-parallel 0
```

`--max-parallel 0` 为自动，1–6 为手动上限。示例的 75% 适用于至少 3 个逻辑核的自动预算；1–2 核界面使用 100%，显式 CPU 预算低于一个逻辑核份额时会拒绝启动。状态和每次准入记录在批次目录的 `batch-status.json`、`admission.json`；每个成功任务各有 `recognition/` 和 `review/`。取消时在已创建的批次目录内新建 `cancel.request`，等待协调器完成收尾；成功任务可继续使用。接口和验证见 `outputs\parallel-recognition-20261111\RESULTS.md`。

```powershell
& 'D:\CADtest\work\segmented-20261109\RunAccuracy.ps1' -Mode Validate `
  -Manifest 'D:\CADtest\outputs\my-cad-run\review\source.json' `
  -Review 'D:\CADtest\outputs\my-cad-run\review\已复核.xlsx' -Python $cadPython -Node $cadNode

& 'D:\CADtest\work\segmented-20261109\RunAccuracy.ps1' -Mode Export `
  -Manifest 'D:\CADtest\outputs\my-cad-run\review\source.json' `
  -Review 'D:\CADtest\outputs\my-cad-run\review\已复核.xlsx' `
  -Output 'D:\CADtest\outputs\my-cad-confirmed' -Reviewer '实际复核人' -Python $cadPython -Node $cadNode
```

校验未通过返回 2；导出时会保存 `validation.json`，不会生成正式确认明细。所有记录被明确排除时保存排除记录，不生成空报价。通过后输出通用 `确认明细.xlsx`、`确认结果.json`：单价为空，金额为公式，复核人姓名为自报信息。它不是任意客户模板报价单，也不是电子签名。不要移动或修改源图、识别目录、冻结程序及复核源文件；导出会重新校验来源哈希。操作日志另存于 `outputs\segmented-review-operations\`。

## 分段、来源和资源边界

- 每张图按顺序执行原生读取、原报告分段统计、投影、投影统计、独立逐字节核验、字段和预测，再准备复核表。旧入口下各图串行；并行入口由协调器持有同一共享锁，允许不同图的阶段并行。各阶段使用 Windows Job，并在安装保护后通过 GO 门控。某张失败只中断该图下游阶段，日志、中间文件和失败记录保留；不能覆盖现有输出目录。
- `cad-read-report.json` 是完整原生报告；`cad-read-projection.json` 是字段所需投影，不替代原报告。投影保留全部文字、问题与读取器通知，仅筛选标题路径相关几何。投影器第二次扫描时重新收集标题路径并与索引比较；独立校验逐对象比较保留内容的原始 JSON 字节和来源元数据。任何校验不通过均不进入字段提取。
- `recognition\protocol.json` 记录程序快照、环境与参数哈希；`samples\*\run.json` 记录各阶段输入前后和输出哈希、耗时及内存。复核源包含原 DWG、完整报告、投影、阶段结果和冻结程序的哈希。哈希是可追溯检查，不是数字签名，也不能排除核验间隙被修改又恢复的情况。
- 默认最多 20 张图、单 DWG 64 MiB、合计 256 MiB。原 PowerShell 入口每阶段 Job 内存上限 3,072 MiB、整机 CPU 25%、超时 300 秒，图纸串行。并行入口默认每任务预留 3,072 MiB、物理内存保留 3,072 MiB，并扣除活动任务的完整预留；每次启动前重新检查可用物理内存、提交量和磁盘。并发上限为逻辑核数一半、6 和手动设置的较小值，单核时最多 1 张；CPU 总预算在活动任务间分配。磁盘每张按 DWG 大小 50 倍且不少于 1 GiB 预留；无活动任务且 15 秒仍不足时明确失败。可检测到的内存超限在其他首轮任务结束后以可用内存/提交量 80%、最高 8 GiB 且保留 3 GiB 物理内存单独重试一次，首轮失败保留。Windows 峰值计数在拒绝分配附近可能略高于设定值；阶段外的文件哈希和启动协调也需要时间。
- 单个 JSON token/相关记录最多 16 MiB，投影最多 256 MiB；独立校验的 profile 和锚点文字各最多 64 MiB，锚点最多 100 万。超限、非法 JSON、重复根字段或来源变化会明确失败而不截断。复核模块另限制最多 10,000 条候选，输入 XLSX 包和单个部件各不超过 32 MiB。这些限制意味着并非所有合法大图都可完成。
- 项目使用本机 .NET 10、固定版本 ClosedXML 和脚本中指定的 Python/Node；升级环境时可用 `-Python`、`-Node` 指定新运行时并重新验证。投影契约绑定当前字段提取器的 SHA-256；修改提取逻辑后必须审查几何保留规则、更新契约并完成差分测试。

## 必须人工核查的局限

图框范围按标题路径及子路径几何推断。独立合成测试发现远处子块几何可能扩大边界并误关联相邻文字；`ATTRIBUTE` 标题路径与块内几何路径也可能不一致。此版要求对照图纸核查每份文件的图框与字段归属，但尚未修复这些原有识别局限。未确定图框、非有限数字、读取器通知、文字格式/编码警告和父子范围应结合原报告核对。若源字符串包含孤立 UTF-16 代理项，现有原生 JSON 序列化可能替换为 U+FFFD。

复核、字段覆盖和 Excel 一致情况均不能替代人工图面真值；资料版本或业务范围差异不能自动判作识别错误。

## 重新编译

已还原依赖的本机可使用：

```powershell
$env:DOTNET_CLI_HOME = 'D:\CADtest\work\cli-home'
$env:NUGET_PACKAGES = 'D:\CADtest\work\nuget'
dotnet build 'D:\CADtest\work\segmented-20261109\projector\Projector.csproj' -c Release --no-restore
dotnet build 'D:\CADtest\work\segmented-20261109\review-source\CadReviewWorkflow.csproj' -c Release --no-restore -o 'D:\CADtest\work\segmented-20261109\review-native'
```

修改代码或依赖后应重新执行适用的边界、资源、实图回归和 XLSX 重开核验。不要清理历史输出及 `work` 中的现有流程。
