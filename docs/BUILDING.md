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

以下测试不访问云端翻译，不启动游戏：

```powershell
New-Item -ItemType Directory -Force artifacts
python tests/source-export.test.py
dotnet run --project tests/ConcurrencyTests
node tests/panorama.test.cjs
node tests/outgoing-ui.test.cjs
node tests/game-window.test.cjs
python tests/terms-merge.test.py
python tests/steam-options.test.py
```

完整生成词库后可运行 `dotnet run --project tests/CoreTests`。浏览器测试需 `tests/` 中 `npm ci --ignore-scripts`、Edge 和隔离测试后台；其固定端口、状态目录和模拟服务前置条件需先阅读对应测试文件。不要把测试连接到个人正在使用的后台。

`live-corpus.cjs`、`final-smoke.cjs`、`providers.test.cjs`、`outgoing-http.test.cjs` 可能调用实际配置的服务，必须设置 `DLT_ALLOW_REAL_API=1` 后才允许运行，不放入默认 CI。

## 导出公开源码

```powershell
python scripts/export_source.py
```

输出新的源码目录、ZIP、逐文件 SHA-256 清单。它不会清理或覆盖现有发行包。此导出不是游戏运行包，详见 PUBLISHING.md。
