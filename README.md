# CADtest

本地 CAD 图纸识别与复核工具：读取 DWG，提取图号、名称、材质、工艺、数量和单位，在桌面界面核对后导出 Excel。也可通过命令行使用后端。

**包含 WPF 桌面前端和后端，支持 Windows 64 位。** 没有 Web API 或内嵌 CAD 看图功能，需要看原图时自行使用 AutoCAD。识别结果需要人工核对，目前没有经过人工真值集验证的总体准确率承诺。

## 快速开始

需要 PowerShell 7、.NET 10 SDK、Python 3.11+、Node.js 22+。Python 和 Node 应能从 PATH 找到，也可以显式传入路径。不需要 Codex，也不需要安装 AutoCAD 或 Excel 才能执行后端识别和导出。

1. 克隆仓库到任意普通目录，可以含中文和空格；不要覆盖已有工作区。
2. 在仓库根目录执行构建。
3. 将自己的 DWG 副本放入仓库内的 inputs 目录，使用新的输出任务名运行。

~~~powershell
.\Build.ps1
.\Start.ps1
~~~

构建默认包含桌面前端，日常操作见 [桌面使用说明](ui/README.md)。仅构建后端可添加 -BackendOnly。命令行使用方式：

~~~powershell
New-Item -ItemType Directory -Path .\inputs -Force
# 将需要处理的图纸复制为 inputs\sample.dwg 后运行：
.\Run.ps1 -Mode Recognize -Drawing inputs\sample.dwg -Output outputs\first-run
~~~

输出为 outputs\first-run\review\图纸复核.xlsx。对照原图填写复核区域并另存为 reviewed.xlsx 后：

~~~powershell
.\Run.ps1 -Mode Validate -Manifest outputs\first-run\review\source.json -Review outputs\first-run\review\reviewed.xlsx
.\Run.ps1 -Mode Export -Manifest outputs\first-run\review\source.json -Review outputs\first-run\review\reviewed.xlsx -Output outputs\confirmed-first-run -Reviewer '实际复核人'
~~~

输出目录必须位于本工作区 outputs 下且尚不存在。来源文件、原值和冻结程序仍须匹配；缺项或未核查内容会阻止导出。不能直接搬动旧任务并声称来源仍有效，因为任务内部记录了识别时的绝对路径和哈希。

## 路径与依赖

默认从引擎脚本所在位置确定仓库根目录，不依赖当前终端目录或固定盘符。相对输入和输出路径均相对于该根目录。根入口也可以从其他目录按绝对路径调用。

| 设置 | 用途 |
| --- | --- |
| -Python 或 CADTEST_PYTHON | 指定 Python；否则尝试仓库自带 runtime/python，再从 PATH 查找 |
| -Node 或 CADTEST_NODE | 指定 Node.js；否则尝试 runtime/node/node.exe，再从 PATH 查找 |
| CADTEST_ROOT | 可选的绝对工作区根目录，必须包含当前引擎；一般无需设置 |
| CADTEST_POWERSHELL | 桌面程序可选的 PowerShell 7 路径，默认从 PATH 查找 |
| NUGET_PACKAGES | 可选 NuGet 缓存位置；仓库不强制本机缓存路径 |

~~~powershell
.\Run.ps1 -Mode Recognize -Drawing inputs\sample.dwg -Output outputs\second-run -Python python.exe -Node node.exe
~~~

源码使用 .NET 10，global.json 允许同一 10.0 版本内的稳定 SDK feature band 更新。NuGet 使用标准配置和锁定依赖。构建产物在本地生成，不把开发机 exe/dll 当发行包上传。

## 能力和边界

- 本机读取 DWG，不需要模型 API。
- 原始读取、分段投影、字段关联及预测均保存来源证据。
- 支持多张独立任务并行，受内存、CPU、磁盘和共享锁约束。
- 同图号兼容数量默认累加；整框打 X 的候选默认排除。冲突、缺单位、工艺范围和父子计价范围仍须核对。
- 后端导出通用确认明细；桌面端可导出中韩文报价样式，单价留空，保留复核和来源记录。
- 单张默认上限 64 MiB，每批最多 20 张、总计 256 MiB；大图是否能处理还取决于图内结构和可用资源。
- Windows Job Object、GO 握手、超时和冻结哈希检查继续启用。目前不支持在 Linux/macOS 上运行完整流程。

并行、取消及来源规则见 [后端使用说明](work/segmented-20261109/README.md)。

## 测试

~~~powershell
.\Build.ps1 -IncludeTestTools
node .\work\segmented-20261109\field-safety-tests.mjs
python -B .\tests\test_portability.py
~~~

可公开运行的检查使用合成数据，覆盖带中文及空格目录、独立进程锁竞争、冻结程序的根目录传递、来源篡改拦截、复核和导出、XLSX 独立重开与公式计算。测试证据保留在新的 outputs/portability-* 目录中，不改用户实际任务。

历史脚本依赖开发机私有图纸及未上传结果，已从当前使用目录移出；原本地成果和早期 Git 历史保留。当前测试不能代替对不同客户图纸的准确性评估。

## 目录

| 路径 | 内容 |
| --- | --- |
| Build.ps1 / Run.ps1 | 构建与运行入口 |
| Start.ps1 / ui | 桌面启动入口、界面和经过元数据清理的样式模板 |
| work/segmented-20261109/native-source | DWG 读取器源码 |
| work/segmented-20261109/projector | 分段投影器源码 |
| work/segmented-20261109/batch-fix-20261106 | 数量与预测规则 |
| work/segmented-20261109/polish-20261106 | 工艺提取 |
| work/segmented-20261109/review-source | 复核和通用 Excel 导出 |
| tests | 不依赖客户图纸的迁移测试 |

引擎目录名沿用当前结构。桌面与后端使用同一工作区和运行时配置；源码构建不等于已制作可分发安装程序。

## 数据和公开范围

图纸、客户表格、运行结果、复核决定、API 密钥和本机环境文件不应提交。首次上传是内部后端快照；当前目录整理不会自动删除旧 Git 历史中的路径和样本名称。正式改为公开前仍应检查完整历史。

## 许可证

本项目自有代码采用 [MIT License](LICENSE)，Copyright (c) 2026 Theline233。依赖按各自包内许可使用，详见 [第三方依赖说明](THIRD_PARTY_NOTICES.md)；本项目许可证不代表第三方素材或客户图纸的使用授权。
