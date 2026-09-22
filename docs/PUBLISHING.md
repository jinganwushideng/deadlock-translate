# 发布到 GitHub

## 应上传哪个目录

从项目根目录运行 `python scripts/export_source.py`，命令会输出新建的干净目录与源码 ZIP。上传这个目录里的文件，不要直接上传整个开发工作目录，不要把 ZIP 当成唯一的仓库源码文件。

导出使用明确的文件清单，包含源码、依赖锁文件、许可证、公开文档与 CI；不包含私人配置、API Key、配对密钥、聊天日志、安装备份、工具二进制、原版聊天布局及生成游戏词库。生成资源由构建者自行获取。

## 首次上传

1. 在 GitHub 新建空仓库，例如 `deadlock-translate`，选择 Public。不要让 GitHub 再生成 README、许可证或 gitignore。
2. 在导出的目录打开终端，执行以下命令（用你自己的仓库地址）：

```powershell
git init -b main
git add .
git diff --cached --stat
git diff --cached --check
git commit -m "Initial public source release"
git remote add origin https://github.com/YOUR_ACCOUNT/deadlock-translate.git
git push -u origin main
```

3. 查看 Actions 的 CI 结果。设置仓库简介、主题、启用私密漏洞报告。
4. README 的相对链接可直接工作；不要添加个人服务端点、密钥或私人联系方式。

## 发布版本

同一版本同时保留对应源码提交/标签、许可证和构建说明。GitHub 自动生成的标签源码档案可用于取得对应源码。

公开源码不包含 Valve 生成资源；旧运行 ZIP 也尚未因本次整理而自动变成可直接公开发布的 Release。发布 VPK/自包含 EXE 前，另行核对游戏资源分发条件、收集运行时第三方声明，并在 Release 明确游戏实测与未验证项。不应只发二进制而不给对应源码。

不要运行 `git add -f` 强行上传被忽略的配置、生成游戏资源或工具。发现密钥泄漏应撤销密钥并处理 Git 历史。
