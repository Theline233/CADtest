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

许可证正文位于 licenses，来源及哈希在 licenses/sources.json。项目 MIT 与第三方许可分开保留。不要把客户图纸、API 配置或开发机 outputs 放入发行目录。
