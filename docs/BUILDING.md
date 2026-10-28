# 从源码构建

## 环境

Windows x64、.NET 10 SDK、Node.js 22 或更新 LTS、Python 3.10+、Git。安装自己的 Deadlock 与 CS2 Workshop Tools，确保可以读取游戏资源。不要把游戏原始资源上传到仓库。

下载以下工具，保留其原始许可证；不要只复制 EXE 而遗漏随附 DLL：

- [Source 2 Viewer CLI](https://github.com/ValveResourceFormat/ValveResourceFormat/releases)：完整解压到 `tools/vrf/`，入口 `Source2Viewer-CLI.exe`。本项目验证过 20.0。
- [VPKEdit](https://github.com/craftablescience/VPKEdit/releases)：完整解压到 `tools/vpkedit/`，入口 `vpkeditcli.exe`。验证过 v5.0.0.4。
- Valve 编译器取自自己的 CS2 Workshop Tools 安装，不随源码提供。

## 完整构建

在仓库根目录执行，以下 D 盘路径都是示例，必须替换为实际路径：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 `
  -GamePath 'D:\SteamLibrary\steamapps\common\Deadlock' `
  -Compiler 'D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\resourcecompiler.exe' `
  -CorePath 'D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\core' `
  -ModBinPath 'D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo\bin'
```

步骤会从本机游戏生成术语数据、使用 Source 2 Viewer 重新提取原版聊天 XML、注入本项目控件、生成新的配对密钥、编译 Panorama、编译网页、发布自包含后台、收集依赖许可并打包 VPK。若游戏布局锚点变化则停止，不静默使用旧缓存。

结果在 `dist/DeadlockTranslate-<version>/`，版本取自 `version.json`。构建不会安装 Mod 或启动游戏。需要安装时参照 USER-GUIDE.md。

生成的 `pairing.key` 必须与同次构建的 VPK 配套，不能把旧 VPK 和新密钥混用。它不是云端 API Key。`artifacts/` 内保留本地资源哈希与冲突报告，不上传到 Git。

完整构建发布自包含运行时时，会从实际还原的 NuGet 包中收集运行时和 ProtectedData 的第三方声明；缺失时停止。单独使用 `-ModOnly` 不生成新的后台或其运行时许可，不可把这种结果当成完整发行包。

## 只开发后台和网页

无需游戏即可编译：

```powershell
Push-Location src/Settings
npm ci --ignore-scripts
npm run build
Pop-Location
dotnet build src/Bridge -c Release
```

此时没有自动生成正式游戏词库，不能把空词库的开发结果当成完整发行版。

## 自动测试

公开 CI 不包含 Valve 游戏原始资源或生成词库。风格、缓存等单元检查使用独立样例；SendTermTests、NamePerformanceTests、GlossaryTests、LegacyNameTests 需要先从自己的游戏生成完整词库，不在没有该词库的公共运行器上执行。正式包的本机词库专项与公共 CI 范围分别记录。

以下测试不访问云端翻译，不启动游戏：

```powershell
New-Item -ItemType Directory -Force artifacts
python tests/source-export.test.py
dotnet run --project tests/ConcurrencyTests
dotnet run --project tests/OptimizationTests
node tests/panorama.test.cjs
node tests/outgoing-ui.test.cjs
node tests/game-window.test.cjs
python tests/terms-merge.test.py
python tests/steam-options.test.py
python tests/configure-steam.test.py
python tests/update.test.py
python tests/update-archive.test.py
```

完整生成词库后可运行 `dotnet run --project tests/CoreTests`。浏览器测试需 `tests/` 中 `npm ci --ignore-scripts`、Edge 和隔离测试后台；其固定端口、状态目录和模拟服务前置条件需先阅读对应测试文件。不要把测试连接到个人正在使用的后台。

中英俄外号、中文连续句、歧义词和模拟云端接收/发送链路检查：先通过 `scripts/build_terms.py` 从游戏生成完整词库，再运行 `dotnet run --project tests/GlossaryTests`。该测试不调用真实云端服务；词库处理耗时只测后台，不能作为游戏 FPS 验收。

0.6.13 新旧译名、显示选择、旧配置/词库、缓存及双向模拟链路：生成词库后运行 `dotnet run --project tests/LegacyNameTests`。`node tests/legacy-name-ui.test.cjs` 使用独立模拟 HTTP 服务验证网页选项、预览、编辑与导入/导出，需要 `tests/` 的 Playwright 和本机 Edge，无需真实后台。`python tests/update.test.py` 包含旧名编辑保留与非法字段的隔离更新检查。

0.6.14 录制：`dotnet run --project tests/RecordingTests` 校验 Windows 原始键码、窗口/游戏状态、超时、取消、释放配对和发送隔离。`node tests/native-recording-ui.test.cjs` 使用模拟原生端点验证游戏/浏览器页面、错误 DOM 键码不会影响结果及下拉选择。都不发送真实键盘输入；真实 Windows 钩子、浏览器系统菜单和局内键盘仍需人工验收。

`live-corpus.cjs`、`final-smoke.cjs`、`providers.test.cjs`、`outgoing-http.test.cjs` 可能调用实际配置的服务，必须设置 `DLT_ALLOW_REAL_API=1` 后才允许运行，不放入默认 CI。

## 导出公开源码

```powershell
python scripts/export_source.py
```

输出新的源码目录、ZIP、逐文件 SHA-256 清单。它不会清理或覆盖现有发行包。此导出不是游戏运行包，详见 PUBLISHING.md。

完整构建并更新验证文档后，可运行 `python scripts/package_share.py` 生成带相同版本源码的完整运行 ZIP。默认所有正式交付集中在工作区 `release/版本号/`：运行 ZIP、ZIP 外面的独立 `覆盖更新.cmd`、安装说明、Steam 配置脚本、校验文件和已解压运行目录。更新器不放在运行 ZIP 内。`--output-folder` 和 `--archive-dir` 仅用于明确要求的特殊导出。遇到同名运行目录或 ZIP 会拒绝覆盖，避免替换已发布版本。个人设置、API 密钥、证书和安装记录不进入运行包；不要把 `artifacts/` 的临时目录当作交付目录。

完整构建后首次游戏内使用需运行 Trust-Local-Https.cmd，并完整重启 Steam。常规测试不能自动安装证书信任。optimization-ui.test.cjs 使用模拟 HTTP；expired-certificate.test.cjs 使用 OptimizationTests 生成的隔离过期证书验证 HTTP 设置降级，不更改 Windows 信任库。


## 构建 GUI 离线安装器

先完整构建后台/设置页/VPK，再运行 `python scripts/package_share.py`，最后运行 `python scripts/build_installer.py`。不要在打包后继续改源文件，否则需重新打包以保持内置源码一致。

GUI 项目在 `src/Installer`，Windows Forms / .NET 10，win-x64 自包含单文件。构建脚本嵌入相同版本运行 ZIP、可信安装脚本和实际恢复版本的运行库许可证，输出 `release/<version>/DeadlockTranslate-Setup-<version>.exe`、SHA-256 和 `Installer-LICENSES.txt`。GUI 不嵌入到它自己的运行 ZIP，避免递归；运行 ZIP 的 source 文件夹包含可重建 GUI 的代码。

开发构建 `dotnet build src/Installer -c Release` 不含运行 ZIP，适合界面预览和单元测试，不是可发布安装器。`--preview <PNG>` 输出不执行安装的界面样例；正式 EXE 的 `--verify-package <JSON>` 只解包校验并输出结果，不安装、改 Steam 或证书。

补充隔离测试：

```powershell
dotnet run --project tests/InstallerTests -c Release
python tests/installer-workflow.test.py
python tests/configure-steam.test.py
python tests/update.test.py
```

工作流测试需先存在当前版本 dist/VPK。证书门禁的成功安装路径使用明确的模拟函数，不能声称完成真实 Windows 信任验证。


`--startup-check <目录>` 在窗口消息循环中执行与普通启动相同的准备/检测，保存截图和结果 JSON 后退出。只读检测会使用当前用户实际状态，但不会安装、配置 Steam 或证书；截图可能有路径和证书指纹，只保存在内部 artifacts 中。


运行异常提示回归：`dotnet run --project tests/RuntimeAlertTests -c Release` 与 `node tests/runtime-alert-ui.test.cjs`。后者自行启动隔离模拟服务、使用 Edge，需先编译 src/Settings；不连接个人后台、不请求真实 UAC/翻译。


Bing 跳转回归：`dotnet run --project tests/BingRedirectTests -c Release` 使用内存 HTTP 模拟。可选 `-- --public-bing` 仅用合成文本调用免费 Bing，不使用个人密钥或付费 API；默认 CI 不启用这项真实网络检查。


API 配置诊断测试：`dotnet run --project tests/ApiConfigurationTests -c Release` 与 `node tests/api-config-ui.test.cjs`（先编译设置页）。测试使用内存 HTTP / 本地模拟页面，不调用真实付费 API。0.6.12 补完沿用版本号，发布前必须备份旧同名目录，重新生成完整 ZIP/Setup/哈希，不能只覆盖网页。

专名性能测试：`dotnet run --project tests/NamePerformanceTests -c Release`，使用实际生成词库、合成聊天和内存 HTTP，覆盖密集专名请求、逐次歧义、纯名字列表边界、5000 条快照、单次占位符还原、词库版本与并发/耗时日志。输出 `artifacts/name-performance-after.json`，只测后台预处理耗时和请求字符数，不能当作真实云端延迟或 FPS。真实服务的速度与语义质量需另外验收。

发送词库与思考预算测试：`dotnet run --project tests/SendTermTests -c Release`，默认只使用内存 HTTP，验证装备简称语境、输出上限、缓存与草稿、思考保留、输出错误分类和隐私日志。可显式添加 `-- --live --defaults --after` 用当前 Windows 用户已有服务测试固定合成句；会消耗少量接口额度，不修改个人配置，也不导出接口/密钥/原始响应或思考正文。CI 不启用真实网络测试。

0.6.16 流式/上下文专项：`dotnet run --project tests/StreamingTests -c Release` 默认使用内存 HTTP。界面回归运行 `node tests/llm-progress-ui.test.cjs`（先编译设置页）。可显式为 StreamingTests 添加 `-- --live` 或 `-- --live --off`，用现有配置测试合成句及两条合成上下文，分别验证思考开启/关闭；会消耗少量接口额度，不修改个人配置，只输出脱敏的阶段和用量。思考正文和翻译正文不写入报告。CI 不启用真实网络。


## 安装器预检与回退（0.6.17）

共享脚本 `scripts/Install-Safety.ps1` 必须随命令行工具、运行包和 GUI 嵌入资源一起发布；外置覆盖更新器将它直接内嵌，无需下载者另找依赖。`build_installer.py` 先按 win-x64 还原许可证依赖，再一次发布内置准确版本运行 ZIP 的 EXE，避免重复生成大文件。

离线验证：`python tests/installer-workflow.test.py`、`python tests/update.test.py`、`python tests/update-archive.test.py`、`python tests/configure-steam.test.py`，以及 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/install-safety.test.ps1`、`dotnet run --project tests/InstallerTests -c Release`。工作流测试无发行 VPK 时使用合成九资源 VPK，CI 不依赖游戏或编译工具。证书与启动命令使用测试替身，不修改信任库，不启动游戏。

安装日志及恢复选择文件仅在用户状态目录，禁止打入发行包或公开源码。管理员重开恢复文件只含白名单选择，不执行安装指令。更新可能同时触及多个文件，原子替换指单个文件；正常异常按备份回退，不能把它当作断电后的全文件事务保证。


## 气泡生命周期（0.6.18）

运行 `node tests/bubble-lifecycle.test.cjs` 验证独立容器重建、原生父子树替换、标签删除/清空、过期气泡、晚到结果、面板复用与并发气泡。测试替身模拟最新版 MessageWrapper 层级，统计树访问，不能证明真实 Panorama 样式/淡出或 FPS。使用 Source 2 Viewer 检查本机当前气泡资源，不把解编译的游戏原始资源纳入公开源码/VPK。VPK 仍仅九个翻译资源。


0.6.18 发送样式与统计补完的离线测试：`dotnet run --project tests/StyleMetricsTests -c Release`，以及设置页编译后 `node tests/outgoing-style-ui.test.cjs`。没有真实网络或游戏输入；语气数据不应进入 LogFields/导出摘要。英文大小写应始终在名称校验后处理，缓存保存规范译文，队列配置按入队快照。


0.6.18 气泡挂载补完还检查“旧内容容器仍保留在原消息内”的情况，以及自有译文标签 visible/collapse/opacity 被隐藏后的恢复。hudHostRepairs 与 hudHiddenRepairs 只含数字，区分修复原因；不把写入计数当作屏幕可见证明。不要为了恢复译文修改原生父面板的可见性或取消 Expired 淡出。


## 接收与气泡链路复审（0.6.18）

运行 `node tests/chat-pipeline-audit.test.cjs` 和 `dotnet run --project tests/ChatPipelineTests -c Release`。测试专用环境变量 DLT_CHAT_TEST_SOURCE 可切换到另存的旧适配器，用同一套场景对照；不能把失败个数直接当独立漏洞个数。常规发现每次最多 64 条边，最多 8 个根，轮询最多 48 个 ID，单次回调最多显示 2 个任务。完整修复范围和运行限制见 CHAT-PIPELINE-AUDIT-0.6.18.md。


## 0.6.20 接收显示

`node tests/display-reliability.test.cjs` 使用模拟 Panorama；`dotnet run --project tests/DisplaySettingsTests -c Release` 校验显示配置、定型原因和导出；`node tests/display-settings-ui.test.cjs` 使用模拟 HTTP 和隔离 Edge。接收调度测试 `dotnet run --project tests/ReceiveBurstTests -c Release` 现检查两条门槛，`node tests/receive-burst-ui.test.cjs` 对应状态 UI。这些检查不调用真实模型、不发送游戏输入、不修改证书信任。详见 DISPLAY-RELIABILITY-0.6.20.md。


## 0.6.20 显示自定义补完

完整构建额外运行 scripts/build_hero_labels.py，只读取本机英雄资源 DATA 区及本地译名，生成临时映射供 Panorama 编译，不分发游戏原始 vdata。无法解码时停止，不使用陈旧映射。运行 `node tests/display-customization.test.cjs`、`node tests/display-customization-ui.test.cjs` 及 `dotnet run --project tests/DisplaySettingsTests -c Release`；测试样本不调用翻译或发送聊天。实际画面边界见 DISPLAY-OPTIONS-0.6.20.md。


## 安装器更新日志维护

0.6.22 接收风格和独立栏修复：`dotnet run --project tests/IncomingStyleTests -c Release`、`node tests/incoming-feed.test.cjs`、设置页编译后的 `node tests/incoming-style-ui.test.cjs`。均为内存 HTTP / 模拟控件，不调用付费 API、不发送真实输入；旧样本可通过 DLT_CHAT_TEST_SOURCE 只读对照。运行 .NET 项目测试与 publish 时应顺序执行，避免共享输出被重建覆盖。

简要记录位于 docs/UPDATE-HISTORY.json，按版本从新到旧排列；同版本多次补完合并到同一条目。新版本追加条目，不删旧记录，并同步 CHANGELOG.md 与 version.json。运行 python scripts/release_history.py 或 python tests/release-history.test.py 检查；完整构建和安装器构建都会执行校验。该 JSON 作为 installer-update-history.json 内置到 EXE，不从网络加载。


0.6.23 起，手动开发编译前运行 `python scripts/build_identity.py`，验证时用 `python scripts/build_identity.py --check`。完整 `build.ps1` 会自动生成统一编号；源码变更后须后台和 VPK 一起构建，不能通过 `-ModOnly` 混入旧后台。正式打包会检查编号与构建文件哈希。
