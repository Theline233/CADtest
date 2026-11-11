# 安装程序和便携包

此入口从干净 Git 提交复制源码，编译后端与自包含 Windows 桌面程序，组装运行时、许可证和清理过元数据的模板，再生成 ZIP 与 Inno Setup 安装程序。不会修改源代码副本或覆盖旧构建；所有临时文件留在新 build 目录。

构建机需要 PowerShell 7、Git、.NET 10 SDK、Inno Setup，以及经过核验的运行时目录。运行时输入是一个已有发行目录的 runtime，其相邻 licenses 目录须包含 Python 和 .NET 的完整许可。当前 Node 通知集固定为 24.10.0，Python 为 3.12 x64 嵌入式布局，默认后端 .NET 为 10.0.5；更换版本前应更新通知文件并重新验证。

~~~powershell
.\packaging\Build-Package.ps1 -Stamp YOUR-NEW-VERSION -RuntimeSource 'path-to-existing-package\runtime' -IsccPath 'path-to-inno\ISCC.exe' -ChineseMessages 'path-to\ChineseSimplified.isl'
~~~

- 默认输出到本仓库 outputs/packages/build-VERSION；可显式指定 OutputRoot。
- NugetCacheSource 可提供已有缓存，脚本只复制它，构建使用新的独立缓存。
- 输入源码必须已提交。PACKAGE-INFO.json 记录提交、逐文件来源哈希、运行时哈希、界面程序哈希和提取器契约。
- 打包不对业务源码做字符串补丁。界面检测包内运行时后直接调用 Python 识别器，不要求终端用户安装 PowerShell 7；后端子进程显式使用包内 .NET。
- 新包与旧包使用相同安装标识和默认目录，升级不覆盖 outputs 和 ui-inputs；卸载默认保留数据。安装程序当前没有数字签名。

构建结果初始标为 built-not-yet-accepted。分发前应核对 ZIP 与组装目录，运行包内识别、并行、取消、UI 集成及新旧任务验证，独立重新打开最终 XLSX，再检查安装/升级/卸载及数据保留。源码测试通过不能代替包体验证。

运行完整的 ZIP 验证时，测试目录必须是新的。该命令只使用新解压的副本，不安装软件；可选 DWG 参数按给定文件生成只读副本用于单图和并行检查。

~~~powershell
python -B packaging/Test-Package.py path-to-build/build-result.json path-to-new-test-directory sample1.dwg sample2.dwg
~~~

检查包含包内运行时、冻结 Python helper 的实际加载路径、界面提交/取消、栏宽和工具栏、勾选批量导出、同名文件、取消不出表、最终 XLSX 独立 XML 重开，以及初始包文件在测试后未被修改。验收报告和所有失败记录都留在测试目录，不自动清理。

## r13 修复版

2026-11-11 的 r13-01 从提交 dbe40494e745bc5e678737aafb48f057897fa716 构建，包含编辑取消、栏宽恢复/保存、窄窗口操作区和模板元数据修复。新包不再通过字符串替换旧源码来组装。

- ZIP 与组装目录 297 个文件逐一一致；包内 33 项界面/运行时检查和交互检查通过，最终样式 XLSX 独立重开。
- 两张真实小图的包内单图/并行检查通过，同一图结果一致；测试未替使用者审批实际图纸。
- 使用独立 AppId、独立目录、禁用快捷方式，完成 r12 安装、r13 升级、打开旧合成任务和静默卸载保留数据：升级保留 179 个既有测试文件，卸载保留 196 个数据文件；实际用户产品的卸载注册项未改变。
- 未验证干净电脑、可视化安装向导、交互式删除数据及实际 Excel 打印。安装程序未签名。

这些是本次固定样本和隔离环境的结果，不代表所有电脑或图纸都已验收。构建产物不提交到 Git 文件树；源码、许可证、打包与验证入口可在仓库中审查。

许可证正文位于 licenses，来源及哈希在 licenses/sources.json。项目 MIT 与第三方许可分开保留。不要把客户图纸、API 配置或开发机 outputs 放入发行目录。
