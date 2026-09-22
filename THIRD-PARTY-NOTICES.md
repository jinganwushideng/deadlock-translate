# 第三方来源与授权边界

整理日期：2026-09-22。本文件区分技术参考、运行依赖、构建工具和游戏资源；不能将“参考实现”描述成“整个上游代码已复制”，也不能由未发现相同代码推导出完全无衍生关系。

## 实现参考

| 来源 | 涉及范围 | 说明 |
| --- | --- | --- |
| [c1375rick/BabelTower](https://github.com/c1375rick/BabelTower) | `mod/panorama/scripts/dlt_chat.js`、设置窗口桥接、后台游戏桥接接口 | 参考聊天扫描、隐藏 HTML 标题桥接、原生提交与焦点释放。核对过的提交为 `078819cfa20dcaea52606a973980b3d772906635`。上游 LICENSE 为 GPL v3；本地参考文件另标有 Thirt927 署名，保留于 NOTICE。本项目另行维护，没有作为 npm/NuGet 包引入。 |
| [Thirt927/BabelTower](https://github.com/Thirt927/BabelTower) | 上述参考文件的贡献者来源 | 不重复计算为独立运行依赖。 |
| [qjimbo/SteamWrap](https://github.com/qjimbo/SteamWrap) | `src/Bridge/SteamGameLaunch.cs`、Steam 启动说明 | `%command%` 包装启动思路参考；上游 Apache-2.0。没有附带 SteamWrap 二进制。 |
| 本机 QOL LOCK 等已有 Mod | `dlt_menu.xml`、菜单样式与兼容性排查 | 观察入口布局、反编译检查冲突；未把第三方整包加入源码。未确定这些本地包的完整上游身份或许可，不重新发布其资源。 |
| [Deadlock 社区词汇说明](https://www.deadlock.io/en/articles/mechanics/glossary) | 术语类别交叉核对 | 非整站词库导入；正式名字以本地游戏资源为准。 |

DLCT、poker chat 属于在 BabelTower 资料中接触的间接参考，未确认为本项目直接代码来源。上游致谢中的其他项目不自动成为本项目的直接依赖。

本项目新增/维护翻译提供者、队列与版本控制、并发限制、发送排序、设置页、DPAPI 保存及安装脚本。上述说明不抹去参考实现的贡献，也不对来源审计作法律上的穷尽保证。

## 依赖与工具

| 名称 | 用途 | 许可及处理 |
| --- | --- | --- |
| [.NET / ASP.NET Core](https://github.com/dotnet/runtime) | 自包含后台运行时 | MIT；运行时另有第三方声明，构建时收集原始 LICENSE/ThirdPartyNotices。 |
| [System.Security.Cryptography.ProtectedData](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/10.0.0) 10.0.0 | Windows DPAPI | .NET 项目 MIT；包内原始许可随构建收集。Windows 本身不是本项目重新授权的部分。 |
| [TypeScript](https://github.com/microsoft/TypeScript) 5.9.3 | 设置页编译 | Apache-2.0，仅开发依赖；锁文件固定版本。 |
| [Playwright](https://github.com/microsoft/playwright) 1.55.1 | 浏览器测试 | Apache-2.0，仅测试依赖。 |
| [ValveResourceFormat / Source 2 Viewer](https://github.com/ValveResourceFormat/ValveResourceFormat) 20.0 | 反编译与验证 | MIT，Copyright (c) 2015 ValveResourceFormat Contributors；工具本体不上传。 |
| [VPKEdit](https://github.com/craftablescience/VPKEdit) v5.0.0.4 | VPK 打包与校验 | MIT，Copyright (c) 2022 Laura Lewis；工具本体不上传。 |
| Valve Source 2 resourcecompiler | Panorama 编译 | Valve 工具；从合法安装的 CS2 Workshop Tools 获取，不随源码发布。参考过 [CSDK 12 工具说明](https://deadlockmodding.pages.dev/modding-tools/csdk-12)，实际使用 CS2 编译器。 |

`LICENSES/` 保留核对时取得的许可正文。GPL 正文位于根目录 LICENSE。依赖自己的许可不会被根目录 GPL 标签替换。

## Valve 资源

`mod/panorama/layout/chat.xml` 是从原版 `panorama/layout/chat.vxml_c` 重建后注入脚本和桥接面板的生成物；`src/Bridge/data/terms.json` 包含游戏本地化生成数据。二者从公开 Git 源码及源码归档排除，由构建者自己的游戏安装重新生成。项目提供生成脚本和自己维护的词条适配逻辑。其他 XML 引用游戏原生样式和图标 URI，不附带图像文件。

本项目 GPL 授权不授予 Valve 原始资源的再分发权。含生成布局和词库的二进制 VPK/运行包与公开源码包是不同发布物；发布者需分别核对游戏资源分发条件，不能用本文件代替授权。

## 云端服务

Bing 网页翻译、Azure Translator、Chat Completions 兼容服务通过 HTTP 调用，不将服务端代码或模型权重包含在项目中。服务条款、费用及数据处理规则由服务提供方决定。

## 展示截图

`docs/images/` 两张 PNG 由项目作者提供并授权作为效果展示，包含 Valve 游戏画面和其他外观/UI Mod。仅用于说明使用效果，不表示拥有画面中所有第三方素材的权利；截图中的第三方素材不因本仓库 GPL 许可证而重新授权。原始游戏资源与工具二进制仍不包含在源码中。
