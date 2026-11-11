# CAD 识别后端

在仓库根目录使用 Build.ps1 构建，使用 Run.ps1 进行识别、复核校验和导出，详见 [快速开始](../../README.md)。输入必须在工作区内，输出必须使用 outputs 下的新目录。

## 单图和串行多图

~~~powershell
.\Run.ps1 -Mode Recognize -Drawing inputs\sample.dwg -Output outputs\run-01
~~~

PowerShell 数组可指定多张图纸，形成一个串行批次。需要每张图独立的任务和复核表时，使用下方并行入口。

## 并行识别

在仓库根目录写入 UTF-8 清单 batch.json，例如：

~~~json
{
  "tasks": [
    {"id": "D001", "drawing": "inputs/first.dwg", "output": "outputs/task-first"},
    {"id": "D002", "drawing": "inputs/second.dwg", "output": "outputs/task-second"}
  ]
}
~~~

~~~powershell
python -B -X utf8 .\work\segmented-20261109\runner.py --parallel-batch batch.json --output outputs\batch-01 --max-parallel 0 --cpu-percent 75
~~~

相对路径按工作区根目录解析；所有任务输出和批次输出都必须是新目录。75% CPU 预算示例适用于至少 3 个逻辑核；1–2 核应使用 100%，显式预算不足一个逻辑核份额时会拒绝。--max-parallel 0 为自动，1–6 为手动上限。

协调器记录 batch-status.json。取消可以在该批次目录新建 cancel.request 文件，等待 Job 收尾；已经完成的任务保留。任务使用共享锁，并沿用每阶段内存、CPU、超时和 GO 握手保护。

## 复核与导出

识别生成 review/source.json 和 review/图纸复核.xlsx。原值区域保留，只修改人工确认区；另存复核表后调用根目录 Run.ps1 的 Validate 或 Export 模式。参数示例见根 README。

导出时重新检查原图、识别输出、冻结程序和复核表绑定。来源变化、缺项或待核会阻止导出；退出码 2 表示校验或准入未通过。数量 0 有效，单位分别保留，不能自动把缺单位当 EA，也不能把父子件都当成最终报价行。

## 路径和冻结程序

workspace_paths.py 从引擎位置确定工作区；CADTEST_ROOT 可显式指定包含当前引擎的绝对根目录。Python 父进程向所有冻结子进程传入同一根目录，C# 复核器使用该值限制输入和输出。不要在不同工作区任务之间混用冻结程序或手工改来源哈希。

代码仓库可以换目录后重新构建和识别；已有任务绑定的是原绝对路径，不能据此推断存档支持任意搬家。输入及输出仍有路径范围检查，链接或 junction 不能用于绕出工作区。

## 资源和局限

默认每阶段 3 GiB 内存、300 秒，单图 PowerShell 默认 25% 整机 CPU。并行按可用物理内存、提交量、磁盘、核数和配置准入；默认最多 6 个任务，系统保留 3 GiB。明确内存不足时允许一次单独重试，其他失败保留原因并隔离。

完整原报告和用于字段关联的投影均保留。提取器 SHA 与投影契约绑定，规则改动需要重新审查和差分验证。未确定图框、未知文字编码、缺少字段、数量范围及工艺范围仍需人工核对。完成运行、导出和文件结构验证，不代表图纸理解全部正确。
