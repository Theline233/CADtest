# CADtest 后端

本仓库先保存本地 CAD 工具当前使用的后端：读取 DWG、提取图框字段、解析数量与工艺、协调多图并行、准备人工复核数据，以及校验和导出 Excel。桌面前端待更新完成后再上传；当前没有 HTTP 服务或网页前端。

2026-11-11 首次上传基于本地源码提交 `016ef087f075c87802350cefbcf13be74c87a093` 提取。`BACKEND-SNAPSHOT.json` 记录每个原始文件的 Git blob、SHA-256 和大小；后端文件保持该提交内容，没有为上传修改业务逻辑。这个提交来自原本地仓库，不是此上传仓库的父提交。

## 代码入口

| 路径 | 用途 |
| --- | --- |
| `work/segmented-20261109/RunAccuracy.ps1` | 识别、校验、导出的 PowerShell 入口 |
| `work/segmented-20261109/runner.py` | 受限识别阶段、来源校验和共享锁 |
| `work/segmented-20261109/parallel_runner.py` | 独立任务并行、资源准入、失败隔离与取消 |
| `work/segmented-20261109/native-source/` | ACadSharp 原生 DWG 读取器源码 |
| `work/segmented-20261109/projector/` | 大报告分段投影及统计 |
| `work/segmented-20261109/extract-requirement-frames.mjs` | 图框与字段关联 |
| `work/segmented-20261109/batch-fix-20261106/` | 预测及数量规则，包含重复累加处理 |
| `work/segmented-20261109/polish-20261106/` | 工艺提取与相关检查 |
| `work/segmented-20261109/prepare_review.py` | 生成带原始证据的复核数据 |
| `work/segmented-20261109/review-source/` | 复核校验及通用 Excel 导出源码 |
| `work/segmented-20261109/review_guard.py` | 冻结来源绑定与受限复核操作 |

`native/`、`review-native/` 和 `projector/bin/Release/net10.0/` 保留现行流程调用的程序文件；源码及依赖锁文件也一并保存。`work/batch-validation-20261106/.gitkeep` 只保留共享锁的父目录，没有上传运行中的锁文件。

## 当前运行限制

- 当前仍是 Windows 本机开发快照，根路径绑定 `D:\CADtest`；不是任意目录可运行的发行包。不要把此仓库克隆到已存在的 `D:\CADtest` 上覆盖用户工作区。
- 开发环境使用 .NET SDK 10.0.201、.NET 10 运行时、Python 3.12 或兼容版本、Node.js。依赖版本以各项目及 `packages.lock.json` 为准。
- PowerShell 脚本里的历史 Python/Node 默认路径可能已失效，运行时显式传入实际 `-Python` 和 `-Node` 路径。直接执行 Python 协调器时同样明确指定 `--node`。
- 原始 DWG、报价模板、客户 Excel、运行输出、人工复核记录、API 配置和密钥均未上传。需要用户自行提供输入，输出目录必须使用新的 `D:\CADtest\outputs\...` 路径。
- 部分历史实验和回归脚本引用未上传的本机样本、旧版基线或 `outputs/`，不能视为克隆后全部可独立运行。当前运行链路和可独立运行的检查分开说明。
- 复核和导出需要原图、识别结果及任务冻结文件一致。当前后端导出通用确认明细；桌面端的客户报价样式转换不在本次上传中。

## 基本运行

在合适的 Windows 开发环境中，准备输入与空输出父目录，替换以下占位路径：

```powershell
& 'D:\CADtest\work\segmented-20261109\RunAccuracy.ps1' `
  -Mode Recognize `
  -Drawing 'D:\CADtest\inputs\sample.dwg' `
  -Output 'D:\CADtest\outputs\new-run' `
  -Python 'C:\path\to\python.exe' `
  -Node 'C:\path\to\node.exe'
```

并行清单、复核校验及导出参数见 [引擎说明](work/segmented-20261109/README.md)。其中历史样本及结果链接是本机开发记录，不包含在此仓库。数量、单位、父子件范围和工艺仍须人工核对，软件测试通过不代表识别准确率已经达到 90%。本地查看图纸由使用者自行使用 AutoCAD，本次不接入预览渲染库。

## 本次上传检查

- 逐文件核对快照与来源提交；上传内容不包含桌面 UI、WebCAD、客户输入或历史 Git 中的前端文件。
- 检查常见凭据格式和上传文件大小；该检查不等同于完整安全审计。
- 对快照 Python 脚本做语法解析，对 JavaScript 模块执行 `node --check`。
- 在快照上运行现有的合成字段检查：

```powershell
node .\work\segmented-20261109\field-safety-tests.mjs
```

本次仅发布代码快照，没有改动识别逻辑，也没有重新进行整批实图准确率测试或新安装包验收。

## 后续前端上传

Claude 的前端更新保留在原本地工作区，没有被本次暂存或提交。后续应以本仓库远端已有提交为基础追加确认后的前端及必要配套文件，更新 `.gitignore` 中本阶段的 `/ui/` 排除项，并做集成验证。

原 `D:\CADtest` 的本地 Git 历史与此次后端上传历史不同；不要直接强制推送原 `main` 覆盖远端。先核对当前远端和新前端版本，再决定同步内容与保留的开发历史。原图、客户资料、配置密钥和运行结果仍不上传。
