# 第三方依赖说明

CADtest 自有代码采用根目录 [MIT License](LICENSE)。以下依赖保留各自的许可证；项目的 MIT 许可不替代依赖许可，也不授权客户图纸、表格或外部字体素材。

本表依据当前构建所用 NuGet 包的 nuspec 元数据整理，版本与项目依赖锁文件对应。当前源码树不分发这些依赖的二进制；构建时通过 NuGet 还原。以后制作发行包时，应同时保留所打包依赖要求的许可证及通知文件。

| NuGet 包 | 版本 | 包声明的许可证 |
| --- | --- | --- |
| [ACadSharp](https://www.nuget.org/packages/ACadSharp/3.8.35) | 3.8.35 | MIT |
| [ClosedXML](https://www.nuget.org/packages/ClosedXML/0.105.1) | 0.105.1 | MIT |
| [ClosedXML.Parser](https://www.nuget.org/packages/ClosedXML.Parser/2.0.0) | 2.0.0 | MIT |
| [DocumentFormat.OpenXml](https://www.nuget.org/packages/DocumentFormat.OpenXml/3.1.1) | 3.1.1 | MIT |
| [DocumentFormat.OpenXml.Framework](https://www.nuget.org/packages/DocumentFormat.OpenXml.Framework/3.1.1) | 3.1.1 | MIT |
| [ExcelNumberFormat](https://www.nuget.org/packages/ExcelNumberFormat/1.1.0) | 1.1.0 | MIT |
| [RBush.Signed](https://www.nuget.org/packages/RBush.Signed/4.0.0) | 4.0.0 | MIT |
| [SixLabors.Fonts](https://www.nuget.org/packages/SixLabors.Fonts/1.0.0) | 1.0.0 | Apache-2.0 |
| [System.IO.Packaging](https://www.nuget.org/packages/System.IO.Packaging/8.0.1) | 8.0.1 | MIT |

此处 SixLabors.Fonts 1.0.0 是 Excel 处理链的依赖，不能据此推断其他版本或独立图纸预览组件的许可。当前仓库没有接入 ACadSharp.Image 图纸预览模块。

.NET SDK/运行时、Python、Node.js 和 PowerShell 是使用者安装的构建或运行环境，当前源码树不包含其发行文件。
