# 同频 / DeadlockTranslate：给 AI 执行的安装说明

## 推荐入口：GUI 安装器（0.6.8 起）

下载并运行 `DeadlockTranslate-Setup-0.7.0.exe`，首次安装和覆盖更新共用此入口；离线包已内置，无需解压、另下 ZIP 或修改 VDF。第 1 步检查证书，有效信任不重复安装；需要 Windows 确认时由用户本人操作。之后选择游戏/程序位置、Steam 账号，检查预览，再执行。更新会同时处理后台、设置页和游戏 VPK。

0.6.17 检查页显示阻塞项时，按错误码和路径处理后重新检测；不要关闭安全软件、强制结束进程或更改整目录 ACL。gameinfo 只读须明确选择临时解除，管理员重开会保留普通选择但仍需重新检查。日志导出含版本、阶段、环境和预检摘要，分享前可检查用户名路径。

完整流程、手动启动项、修复、卸载与边界见同目录 `GUI-INSTALLER.md`（源码中为 `docs/GUI-INSTALLER.md`）。以下脚本方案仍适用于手动安装和自动化；使用 GUI 时不要再重复执行脚本或直接修改 Steam VDF。AI 不得把用户取消证书信任或 UAC 当作成功。


适用：Windows x64，0.6.5 及其后的同结构运行包。供豆包等有本机文件/命令/浏览器操作能力的 AI 使用。用户只要求“阅读说明”时不要自动安装；用户明确要求安装后按本说明执行。用户当次要求优先。本说明不授予发送聊天、消耗付费 API 额度或删除其他 Mod 的权限。

## 执行原则：先证书，再安装，最后启动游戏

1. **第一步处理证书。** 此时用户应在电脑前，以便立即确认 Windows 信任弹窗；不要等装完游戏、修改启动项、用户离开电脑后才触发证书安装。
2. 第一步内只做必要准备：找到完整运行 ZIP/已解压目录，解压到固定位置并校验文件，随后立即检查证书。没有 EXE 时不能凭空安装证书。这些准备不安装 VPK、不启动后台或游戏。
3. **已就绪的证书直接跳过。** 不重复导入、不重新生成、不自动续期。证书文件存在、同名证书存在和“当前后台证书已正确受信任”是不同状态。
4. 优先使用一次命令和结构化文件验证；不用逐张截图找文件、遍历所有 Steam 账号、不断切换窗口。GUI 主要用于用户证书确认、服务配置和最后的游戏视觉验收。
5. 明确路径后直接执行已授权步骤。常规可逆安装、读文件、校验哈希不逐项询问；只有缺少必要信息、发现冲突、系统需用户确认时才停在相关步骤，其余独立检查继续。
6. 一次失败只按错误做一个针对性修正，最多两次；同一问题重复出现就报告具体原因。不要无输出时重新编写一套脚本或猜测 VDF 结构。
7. 不加开机自启、服务、计划任务或游戏监视常驻程序。不要强制结束 Steam，不改权限/ACL，不绕过证书确认，不把整个游戏目录或 addons 文件夹删除。
8. 完成后报告证书、VPK、启动项、后台连接分别是否核验；自动化和文件检查不能代替游戏 UI/FPS 实测。

## 快速执行顺序

首次安装：**完整包准备及证书检查/一次信任 → Install.cmd → 配置 Steam 启动项 → Settings.cmd 配置服务 → 从 Steam 启动并验收。**

已有安装升级：**只读核对证书是否正常（正常即跳过）→ 外置覆盖更新.cmd 自动更新 ZIP → 核对原启动路径 → 游戏验收。** 不先卸载，不从新解压目录改 Steam 路径。

两个入口不同：首次安装使用 ZIP 解压后的 `Install.cmd`；升级使用 ZIP 外面的 `覆盖更新.cmd`。升级器不处理首次安装，不自动替用户确认新证书。

## 第一步：先把证书处理到“就绪”

### 1.1 确定程序目录、状态目录和用户

找到用户下载的完整 `DeadlockTranslate-版本-win-x64.zip`，不要下载 GitHub 自动生成的 `Source code` 当运行包。源码 ZIP、VPK 单文件、EXE 单文件均不足以安装。需要下载时只用用户指定的可信发布入口；不自行找第三方重打包。

首次安装可解压到例如 `D:\DeadlockTranslate\DeadlockTranslate-0.6.3`。以实际含 EXE 的目录为准，避免解压后又多套一层导致路径写错。这个目录长期保留，不是临时目录。证书的路径不是 ZIP 所在目录，也不是源码目录。

至少确认以下配套文件齐全：`DeadlockTranslate.exe`、`dlt_translate_dir.vpk`、`pairing.key`、`data\terms.json`、`wwwroot`、`Manage-Mod.ps1`、`Install.cmd`、`Trust-Local-Https.ps1` / `.cmd`。有 `package-manifest.json` 时一次性校验其中的文件 SHA-256；有 ZIP 的 `.sha256` 文件时核对整包。配对文件不能自行重建或从其他版本借用。

使用普通用户身份，与实际启动 Steam 的同一个 Windows 用户一致。通常不需要管理员身份；不要因不确定而切换到管理员或另一个账号。后台自包含，无需安装 .NET SDK、翻译模型或 Python。

下列变量在一次 PowerShell 会话中保留；在新会话重新赋值。把示例目录换成真实目录，不要保留占位符：

```powershell
$taskRuntimeFolder = 'D:\DeadlockTranslate\DeadlockTranslate-0.6.3'
$taskStateDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DeadlockTranslate'
$taskCertificateFile = Join-Path $taskStateDirectory 'localhost-certificate.cer'
$taskCertificateSecret = Join-Path $taskStateDirectory 'localhost-certificate.dpapi'
```

先确认 Deadlock 已退出。证书首次信任后需要 Steam 重新读取信任状态：如果 Steam 现在打开，**先读出并记住当前 Steam 用户 ID**，再正常退出 Steam。不要强制结束进程。可用的只读信息：

```powershell
$taskSteamPath = (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath
$taskSteamUserId = (Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess').ActiveUser
[pscustomobject]@{ SteamPath=$taskSteamPath; SteamUserId=$taskSteamUserId }
```

`ActiveUser` 为 0 不代表 userdata 没有账号；Steam 退出后可能变成 0。沿用退出前已确认的 ID。没有记录时，必要时让用户打开并登录 Steam 一次或确认目标账号；不要猜别的目录、按最大编号或最近修改时间选账号。此时可先继续证书和游戏目录检查。

### 1.2 只读检查：满足全部条件即跳过安装

检查以下四项：

- 当前状态目录有 `.cer` 公钥文件和 `.dpapi` 加密私钥文件。
- 公钥身份为 `CN=DeadlockTranslate localhost`，签发者与主体相同，当前时间在有效期内。
- **同一张证书的指纹**存在于当前用户的 `Root` 信任库；不能只看是否有同名证书。
- 若旧后台已经运行，其 `/health` 返回的 `certificateThumbprint` 与此文件一致；不一致时先确认实际数据目录，停止，不盲目再导入。

只读检查示例，不改变信任库：

```powershell
if ((Test-Path -LiteralPath $taskCertificateFile) -and (Test-Path -LiteralPath $taskCertificateSecret)) {
    $taskCertificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($taskCertificateFile)
    try {
        $taskNow = [DateTime]::UtcNow
        $taskTrusted = @(Get-ChildItem Cert:\CurrentUser\Root | Where-Object Thumbprint -eq $taskCertificate.Thumbprint).Count -gt 0
        [pscustomobject]@{
            IdentityOK = ($taskCertificate.Subject -eq 'CN=DeadlockTranslate localhost' -and $taskCertificate.Subject -eq $taskCertificate.Issuer)
            ValidNow = ($taskCertificate.NotBefore.ToUniversalTime() -le $taskNow -and $taskCertificate.NotAfter.ToUniversalTime() -gt $taskNow)
            Trusted = $taskTrusted
            Thumbprint = $taskCertificate.Thumbprint
        }
    } finally { $taskCertificate.Dispose() }
} else {
    Write-Output 'Local certificate files are incomplete'
}
```

上面不能单独证明加密私钥可读取；后续后台健康检查会核验私钥/HTTPS 是否可用。若这几项都正常，**不要运行 Trust-Local-Https.cmd / Renew-Local-Https.cmd，也不删除证书文件。** 从未改过信任且已有 Steam 正常使用记录时，不为“证书已存在”再重启 Steam。

### 1.3 证书缺失、不受信任或损坏时的分支

| 状态 | 处理 |
| --- | --- |
| 文件齐全、有效、指纹已受信任 | 跳过证书安装。后面以后台健康检查确认私钥可读。 |
| `.dpapi` 存在，`.cer` 缺失 | 在同一用户/状态目录运行 EXE 的 `--prepare-https`，恢复公钥文件；它复用原加密证书。重查指纹，已经受信任就跳过导入。不要先删私钥。 |
| 文件有效，但精确指纹未受信任 | 运行一次 `Trust-Local-Https.cmd`，复用本机证书并请求信任。 |
| 两个文件都不存在 | 运行一次 `Trust-Local-Https.cmd`，在本机生成证书再请求信任。不要导入朋友电脑的证书。 |
| 只有 `.cer`，没有可用私钥 | 不能仅导入公钥后宣称成功。说明需生成本机私钥；使用包内工具生成本机证书并由用户确认信任。 |
| 过期或加密私钥读取失败 | 确认 Windows 用户和实际状态目录；停止游戏/后台。确需修复时才使用 `Renew-Local-Https.cmd`，它会备份旧证书并需要重新确认信任。不要自动续期一张正常证书。 |
| 距到期小于 30 天但尚有效 | 提醒安排续期即可，不为了安装视频擅自更换。 |
| 同名已受信任证书与本机文件指纹不同 | 不能当作已安装。找实际后台使用的目录/指纹，不能自动删所有同名证书。 |

公钥恢复的程序调用示例，仅在需要时使用，不导入信任：

```powershell
$taskPrepare = Start-Process -FilePath (Join-Path $taskRuntimeFolder 'DeadlockTranslate.exe') -ArgumentList @('--prepare-https','--data-dir',('"'+$taskStateDirectory+'"')) -WindowStyle Hidden -PassThru -Wait
if ($taskPrepare.ExitCode -ne 0) { throw 'Certificate preparation failed; stop and inspect the error' }
```

需要首次信任时，让用户现在处理确认，不要留到最后：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$taskRuntimeFolder\Trust-Local-Https.ps1" -StateDirectory "$taskStateDirectory"
```

该脚本先打印指纹及有效期，再要求输入 `YES`；Windows 还可能弹出“根证书存储”等安全确认。说明这是信任当前用户、本机 localhost 的证书，要求用户核对 `DeadlockTranslate localhost` 并手动确认。不要承诺能越过系统/AI 工具的安全确认规则。

若用户已明确同意信任这一张本机证书，可以加 `-ConfirmTrust` 跳过脚本的 `YES` 问答，**它不会跳过 Windows 自身确认**。没有明确同意就不要加。安全弹窗不能由当前 AI 工具代点时，让用户手动完成，其他独立文件检查可以继续。

卡在“Certificate SHA-1 thumbprint / Expires”后没输出，首先考虑未确认的 Windows 弹窗。只触发一次，让用户找任务栏或其他窗口后面的“根证书存储”；不要并行打开多个导入进程。用户不在电脑前时，明确停在“待用户确认”，不能以超时当作同意。

确认后回读精确指纹是否出现在 `Cert:\CurrentUser\Root`，并等脚本正常完成。退出码 0 / 弹窗关闭不足以单独证明成功。第一次信任或续期完成后完全退出并重新启动 Steam；如果 Steam 已退出且一直未开，稍后首次打开即可。只关闭游戏、关 Steam 窗口到托盘或重新载入网页都不能代替完整重启。

### 1.4 AI 的数据目录隔离问题

AI 应用/沙盒可能把 LocalAppData 映射到应用自己的 LocalCache，而双击启动的后台使用真实 Windows 用户目录。不要从一个环境生成证书，再在另一个环境导入一张不同的证书。

优先让用户通过包内证书入口执行，随后比较文件指纹、Windows 信任指纹和实际后台 `/health` 的指纹。三者不一致时停止并记录差异，找真实目录；不能每次失败都重新生成证书“碰运气”。

## 第二步：安装 VPK，保留其他 Mod

确认 Deadlock 已完全退出。Steam 是否开启不影响 VPK 安装，但证书和启动项步骤通常会要求它退出。

**状态目录存在不等于已经安装 Mod。** 第一步生成证书会创建这个目录；应看 `install-manifest.json`、记录中的 VPK 是否存在及哈希是否匹配。

- 没有安装记录且属于首次安装：在实际运行目录执行 `Install.cmd`，或直接调用 `Manage-Mod.ps1 -Action Install`，避免等待 CMD 的 `pause`。
- 已有安装记录且 VPK 匹配：版本已相同可跳过，版本旧时使用外置覆盖更新器；不要再次 Install 导致冲突，不先卸载，不混装配对文件。
- 有安装记录但 VPK 缺失/哈希不同：记录具体路径、实际哈希和错误。不要改安装记录的哈希来绕过，也不要把不明 VPK 当作自己的删掉。

命令示例，后一个游戏目录也要换成实际位置：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$taskRuntimeFolder\Manage-Mod.ps1" -Action Install -GamePath 'E:\SteamLibrary\steamapps\common\Deadlock'
```

`-GamePath` 指向 Deadlock 顶层目录，下面有 `game\citadel\gameinfo.gi`，不是指向 `citadel` 或 `addons`。先用 Steam 的 `libraryfolders.vdf`/现有安装记录快速定位。0.6.5 快速失败后支持手动选文件夹、输入路径和用户选择的限时深层扫描，不默认全盘扫描。

安装器自己找未占用的 `pak90` 到 `pak199` 槽位，所以**不要假定所有人都装成 pak90**。成功以 `Installed:`、退出码和安装记录对应的文件为准，回读 VPK 哈希及加载路径；不要把 CMD 窗口消失当作成功。

出现精确资源冲突时，列出具体 VPK 和重叠资源，不批量删除其他 Mod。相关聊天/气泡 Mod 提示只有在用户明确同意共存时才用 `-AllowRelated`；不要为了省点击默认跳过。QOL 共存也要最终看游戏菜单，不用纯文件测试宣称 UI 已通过。

## 第三步：Steam 启动项——用固定方法，不反复猜 VDF

### 3.1 正确文本

Steam 库 → Deadlock → 右键属性 → 通用 → 启动选项。内容是 **实际 EXE 绝对路径**：

```text
"D:\DeadlockTranslate\DeadlockTranslate-0.6.3\DeadlockTranslate.exe" --steam-game %command%
```

有普通参数时放在后面，例如：

```text
"D:\DeadlockTranslate\DeadlockTranslate-0.6.3\DeadlockTranslate.exe" --steam-game %command% -novid -dev
```

- 引号只包住 EXE 路径，使用英文双引号；不能把整条命令都放进引号。
- `%command%` 原样保留，不换成游戏名、路径或 `%%command%%`，不要漏掉 `--steam-game`。
- Steam UI 中路径使用单个反斜杠；VDF 文件中的字符串使用转义。不要把带 `\\` / `\"` 的 VDF 内部文本贴进 UI。
- 一个 `%command%` 和一个翻译启动前缀即可，不把已有翻译前缀再包一层。
- 已有其他软件的 `%command%` 包装器时，停止自动覆盖，先说明冲突。普通游戏参数保留。
- 不用 `--settings`、`--launch` 或后台 EXE 独立启动形式替代这里的 `--steam-game %command%`。
- 路径是运行目录，不是 ZIP、VPK、源码、临时解压目录或作者电脑的路径；文件夹套了一层时包含实际那一层。
- 升级器更新原目录，所以升级后不用把启动项改成新 ZIP 的解压目录；目录名仍为旧版本号是正常的。

### 3.2 一次完成的命令方式（AI 推荐）

配套 `Configure-Steam.ps1` 使用 Windows 自带 PowerShell 5.1，不需要 Python。脚本在新版包内；本说明单独发给 AI 时，可以把脚本放在旁边，并使用它的真实路径。

先在 Steam 仍登录时读并记住 `ActiveUser`。如果 Steam 已关闭、值变为 0，使用已经确认的 ID，而不是重新遍历所有账号。下面 `123456789` 只是示例，要换成当前账号 ID：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\Configure-Steam.ps1' -RuntimeFolder "$taskRuntimeFolder" -UserId 123456789 -Apply -StopSteam -RestartSteam -ResultFile 'D:\steam-launch-result.json'
```

如果脚本在运行包目录中，`-File` 改成 `$taskRuntimeFolder\Configure-Steam.ps1`。省略 `-UserId` 时脚本尝试读取当前活跃账号；值为 0 就明确要求提供退出前已记录的 ID，不会猜测账号。`-SteamPath` 可指定实际 Steam 根目录，默认从注册表读取。

默认不加 `-Apply` 只预览，不修改文件。`-StopSteam` 用 Steam 自己的 `-shutdown` 正常退出，等待完成；25 秒内退出失败就停止，不 `Stop-Process -Force`。`-RestartSteam` 只在该脚本确实正常停止过 Steam 时请求重新打开。已经退出的 Steam 可在下一步由用户正常打开。

这个脚本会：

1. 精确定位 **`UserLocalConfigStore / Software / Valve / Steam / apps / 1422450`**，键名匹配不区分大小写，不把其他分支或其他游戏当作 Deadlock。
2. `LaunchOptions` 缺失时插入，空值时替换，已正确配置时不重复写、不重复关 Steam。
3. 修正旧翻译 EXE 路径并保留普通参数，拒绝覆盖其他包装器。
4. Steam 正常退出后重新读取文件，避免丢掉退出时刷回磁盘的内容。
5. 备份原始 VDF，保留 UTF-8/BOM 和其他字段，原子替换，再解析回读。不会重写整个 Steam 配置树。
6. 将完整结果写到 `-ResultFile`，包括 `actual`、`userId`、`applied`、`changed`、`alreadyConfigured`、`backup`。这只包含相关配置与状态，不包含登录令牌或 API Key；文件仍是本机资料，不发布到网上。

读取一次结果文件即可确认当前值。脚本重启 Steam 时返回 `steamRestartRequested` 只代表启动请求；等客户端正常加载后再回读一次 `LaunchOptions` 是否保持，不反复逐行读整份 VDF。

### 3.3 针对豆包反复卡住的情况

**不要因为看到某个 userdata 目录没有 Apps 区块，就重查整个 Steam 存储结构。** 不活跃账号或未运行游戏的账号可能没有记录。以已确认的当前账号 ID 为准，不以昵称、账号文件夹修改时间或随便第一个目录为准。

`LaunchOptions` 缺失/空值是正常的首次设置状态，不需要找别人的非空示例。使用配套脚本插入/替换即可。`apps` 实际位于 `Software\Valve\Steam` 下，不是 VDF 顶层。

如果已确认正确账号，却没有 Deadlock `1422450` 记录，不编造整个 Apps 树。核对这个账号的 Steam 库里是否已安装 Deadlock，必要时由库中属性 GUI 设置一次，或正常启动一次游戏后退出以创建记录，然后只重试一次。

工具显示第 N 行截断，不等于文件被截断。看结果 JSON 的 `actual` 和解析回读，或只输出目标字段的完整字符串和长度。不要把整个 `localconfig.vdf` / `loginusers.vdf` 发到聊天里，也不要为了“输出不稳定”反复写同一个配置。

PowerShell 5.1 不支持把普通 `if { ... } else { ... }` 语句直接接到管道。应先赋值再写出：

```powershell
$taskSteamStatus = if (Get-Process -Name steam -ErrorAction SilentlyContinue) { 'running' } else { 'not-running' }
$taskSteamStatus | Set-Content -LiteralPath 'D:\steam-status.txt' -Encoding UTF8
```

找不到 Steam 主窗口不代表 Steam 没运行。以进程和脚本状态为准，不为了改启动项反复点桌面、Alt+Tab、放大托盘。命令方式已经具备正常退出、备份、定点修改及回读能力。

## 第四步：配置服务与验收

用 `Settings.cmd` 打开浏览器设置。浏览器地址带临时会话凭据，属于本机资料，不公开完整链接。不要直接打开磁盘上的 `wwwroot\index.html`，也不要把匿名 `/api/config` 返回 401 当作后台失败。

保留已有设置、密钥和词库，不为新版本重置。默认接收简体中文、发送英语，可选独立语言；不擅自改用户选项。API Key 由用户填写，模型字段严格使用服务商给出的完整模型 ID，不能凭展示名或别的平台缩写猜测。

Bing 网页快译可能不稳定。出现服务错误不代表安装失败；不静默切换到付费 Azure/LLM。用户要求接入精翻时再配置接口和完整模型 ID。连接测试/试译可能花费额度，先有用户授权；固定词库如 GG 能返回译文不能证明云端联网成功。安装验收的健康/证书/桥接检查不消耗翻译 API 额度。

启动 `Settings.cmd` 后可做不含密钥的健康检查：

```powershell
Invoke-RestMethod 'http://localhost:17865/health' -TimeoutSec 3 | Select-Object app,version,httpsReady,certificateThumbprint
```

核对 `app=DeadlockTranslate`、版本正确、`httpsReady=true`，以及指纹与刚检查的证书一致。再使用正常 TLS 验证请求 `https://localhost:17866/health`；不使用 `-SkipCertificateCheck`、忽略 HTTPS 警告或浏览器禁用校验参数。HTTP 是本机管理入口，游戏桥必须用 HTTPS。

从 Steam 库启动 Deadlock。按原启动项联动后台，无需额外监视进程。进入游戏后由用户完成以下实际验证：

- Esc 菜单有“翻译设置”入口，打开后能正常点击和保存；与 QOL 并装时看实际布局。
- 聊天记录及顶部气泡追加译文，原文/输入/滚动/淡出正常。
- 游戏内左 Alt＋空格按当前配置发送到所选频道；不未经授权向真实玩家发送测试消息。
- 常用分辨率与实际帧率可接受。文件或浏览器模拟通过不能代替这一步。

浏览器设置页“连接自检/重新检查”不消耗翻译额度。后台版本正确不代表当前游戏已经载入新 VPK；游戏运行期间替换包后要完整退出重开。设置页单独启动的后台可以一直运行，关闭浏览器不等于停止后台；下一次 Steam 游戏启动会通过同包配对入口接入联动。

## 常见故障：各查各的，不循环重装

| 表现 | 第一项检查 | 不要做 |
| --- | --- | --- |
| 只有原生“设置”、没有“翻译设置” | 安装记录/VPK 哈希、是否重启游戏、是否载入对应聊天适配脚本及其他资源冲突；必要时收集 clientVersion/gamePost 与菜单错误 | 证书不会创建 Panorama 按钮，不要反复导入证书；Settings.cmd 只是临时设置入口，不代表游戏入口已修好 |
| 游戏设置黑屏 | HTTPS 健康、精确指纹信任、首次信任后是否完整重启 Steam、后台/VPK/配对文件是否成套 | 不改成 HTTP 代替游戏桥，不关闭 TLS 校验，不反复重新生成正常证书 |
| 设置页提示未连接 | 先 `/health`；再用设置页连接自检区分“后台可用”与“游戏心跳未到” | 不把看到选项就当作整个游戏链路已连接 |
| 浏览器 `/api/*` 返回 401 | 通过 Settings.cmd 获得本机登录会话 | 不删密钥、不绕过鉴权、不硬编码会话令牌 |
| Steam 启动游戏时后台没起 | 目标账号 LaunchOptions 解码后的值、真实 EXE 路径、`--steam-game %command%` | 不加常驻守护程序补漏，不把“创建了一个 EXE 进程”当成功 |
| API 错误或快译失败 | 服务地址、完整模型 ID、套餐 Key、所选服务状态 | 不把云端服务故障当作证书/VPK 问题，未经同意不切换付费服务 |
| 有同名后台进程但无端口/路径不可读 | 读端口与权限错误；本机确有残留且拒绝终止时报告并建议用户重启 | 不只凭进程名强杀所有实例，不调整系统权限或禁用防护 |

## 升级与卸载的边界

升级只把新版 ZIP 和 ZIP 外的 `覆盖更新.cmd` 放一起，退出游戏后双击更新器。它自动定位原目录并同步 EXE、网页、词库、配对文件和已安装 VPK；临时解压目录清理，回退备份保留。已有效受信任的本机证书、密钥和 Steam 原目录启动项继续使用，不重新信任证书。

普通卸载：退出游戏，正常停止后台，运行 `Uninstall.cmd`，移除自己的 Steam 翻译前缀、保留其他普通参数；个人配置/证书默认保留。不因为“卸载”擅自做全盘清理。

只有用户明确要求“从零教学/完整重置”时，才额外备份并移出原程序、个人状态与缓存，撤销**本程序对应指纹**的当前用户证书信任。Windows 证书删除可能也有确认弹窗，由用户手动完成。不能删除全部证书、其他 Mod、Steam 整份配置、源码或用户明确要保留的下载 ZIP。遇到权限/审批拒绝就说明动作与原因，改用能保留原件的安全归档方式；不要绕过。

## AI 完成报告模板

```text
安装结果：完成 / 部分完成（具体阻塞原因）。
实际运行目录：……
实际游戏 VPK：……；文件哈希已匹配 / 未匹配。
证书：已有就绪并跳过 / 已由用户确认并回读 / 待用户确认。
Steam：目标账号 ID ……；LaunchOptions 实际值 ……；是否完整重启 ……。
后台 HTTP / 游戏 HTTPS：已验证 / 未验证；游戏心跳 ……。
已有配置、密钥及其他 Mod：保留 / 具体改动。
游戏内设置/聊天/发送/FPS：用户实测结果 / 尚未验证。
备份与诊断文件：……（不附密钥、完整会话 URL 或整份 Steam 配置）。
```

安装说明编写时依据本项目脚本、后台实现和本机实际排查（2026-10-09）。说明和配套脚本本身不会自动安装 Mod、变更证书或消费云端额度。

## 0.6.5 新增排查入口

安装器找不到游戏时，先选择手动路径；游戏根目录、citadel 子目录和游戏 EXE 可自动归一化。需要扫描时才选择深层模式，60 秒/5 万目录到限额会提示不完整，多份安装须选择。命令方式：`Manage-Mod.ps1 -Action Install -LocateMode Deep -ScanRoots 'E:\SteamLibrary'`；自动化非交互方式应加 `-NonInteractive`，或直接给 `-GamePath`。不能通过伪造 marker 文件来通过检查。

用户快捷键失灵时，不代发真实消息。先在设置页保存键位、做 15 秒检测，再查看注册错误、触发计数、游戏确认、权限差异和发送任务阶段。默认左 Alt 与右 Alt不同，AltGr/输入法/其他软件或模拟宏也可能影响。刚开聊天框立刻按可能早于回传，不能在聊天未确认时吞掉游戏按键；不能为解决问题擅自提权或改系统保护。

让用户点击“一键导出日志”并提供 ZIP；后台打不开可用 `Export-Diagnostics.cmd`。日志包不含聊天正文、译文、API Key、URL、模型名、原始配置和私钥；不要转而要求整份用户状态目录或 Steam 配置。游戏桥完全断开时游戏端事件可能还未同步，后台日志和有限 `[DLT]` 控制台输出用于区分路径，不宣称已拿到全部游戏记录。

## AI 操作管理员权限的补充规则（0.6.5）

- 保持文档第一步证书安装的流程；已有同频证书有效且受信任时跳过。连接失败不等于证书失效，不要重新生成证书、清空配置或把其他账户 DPAPI 文件拷来拷去。
- 默认生成普通启动项。仅用户明确选择管理员兼容模式时，在 `--steam-game` 前加 `--admin-compatible`，或使用 `Launch-Admin.cmd`。示例：`"D:\DeadlockTranslate\DeadlockTranslate.exe" --admin-compatible --steam-game %command% -novid`。AI 不自行将 Steam 或所有程序设为管理员。
- UAC 必须由用户在 Windows 安全界面确认。不要点击/绕过安全弹窗，不能使用另一个管理员账户凭据；需使用原安装用户。没有可用的同用户管理员权限时，先让用户选择以普通权限运行游戏。程序不会循环请求 UAC。
- 若使用 Configure-Steam：普通首次安装不带权限参数；明确兼容模式才加 `-AdministratorCompatibility`；明确回退才加 `-NormalPermissions`。该脚本在普通更新时保留旧管理员选择。继续遵守当前登录 Steam 用户、正常退出、备份、结构化 VDF 和回读规则，绝不在运行中的 Steam 配置里直接写入。
- 先对齐版本/安装目录/配对文件。缺配对文件时不能随机生成替代品，因为 VPK 中的桥接值必须一致；使用成套覆盖更新。不匹配旧后台/另一用户或目录占用端口会明确报错，不强杀其他程序，也不重复安装证书。
- 同用户、同数据及程序目录的已运行普通后台，可在用户主动选择管理员兼容重启后正常退出并由新后台接管；旧待发送任务取消。先完成或复制待发任务，再进行兼容重启。
- 验收分别检查 HTTPS 连接、游戏聊天识别、快捷键触发/确认计数。只看到后台具有管理员权限不能声称真实聊天发送已通过；需要用户在管理员游戏中验证，随后一键导出诊断。`localhost` 与 `127.0.0.1` 重试使用 HTTPS，禁止退回 HTTP、忽略证书验证或降低安全策略。

## Unable to read default keybinding configuration（0.6.7）

这是游戏启动时读取默认按键配置失败。已发现旧安装器的一处兼容缺陷：当 `Game citadel/addons` 被放到首位、但没有显式 `Mod`/`Write` 路径时，游戏可能把默认 MOD/写入目录推导为 addons，无法从原来的 citadel/cfg 读取默认配置。已有 Mod 管理器补齐这些路径的机器可能不会遇到。

**已安装用户：退出 Deadlock，下载 0.6.7 完整 ZIP 和同目录的新版“覆盖更新.cmd”，放在一起运行新版更新器。旧更新器不包含这次修复逻辑，也可能拒绝新组件，因此两个文件一起更新。** 后台、VPK 和必要搜索路径会一起处理，Steam 可以保持开启。

若只需修复搜索路径，可解压新包，双击其中 `Repair-GameConfig.cmd`；也可在 PowerShell 中运行 `Manage-Mod.ps1 -Action Repair -GamePath "自己的 Deadlock 根目录"`。无需先启动游戏或重复安装证书。脚本定位真实 `GameInfo/FileSystem/SearchPaths` 区块，只补缺项：

```text
Mod   citadel
Write citadel
Mod   core
Write core
```

保留原来的 Game、语言、其他 Mod 和性能配置；已有正确路径时不重复修改。单独修复会在 gameinfo.gi 旁保存 `.dlt-before-repair-*.bak` 备份，覆盖更新的备份位于个人状态目录 `backups/update-*`。不重置或删除 cfg 下的个人按键配置。

如果路径已经正确、修复后仍报错，可能是默认配置文件缺失/损坏或其他启动路径问题。先备份自己的额外游戏配置，再通过 Steam → Deadlock 属性 → 已安装文件 → 验证游戏文件完整性。校验后重新运行修复入口以恢复需要的 Mod 挂载；不要删除整个 cfg 文件夹。可提供报错括号中的完整文件名和 SearchPaths 区块继续判断。

参考： [Grimoire 1.30.1 同类问题修复说明](https://github.com/Slush97/grimoire/releases/tag/v1.30.1)、[Steam 官方文件校验说明](https://help.steampowered.com/en/faqs/view/0C48-FCBD-DA71-93EB)。本项目未引入 Grimoire 的代码或运行依赖。


## 手动删除后重新安装（0.6.18 补完）

旧 VPK、后台文件或安装记录被手动删除时，直接使用完整安装器选择“安装 / 覆盖更新”或“修复已有安装”。无需先卸载、强行恢复记录或编辑 install-manifest.json。检查页会显示已验证目录的原地重装，或保留不明残留并改用新的空目录。已有有效证书沿用；设置、API 密钥和自定义词库只要仍存在就保留，已删除数据需自行从备份恢复。

程序目录改变后，Steam 启动项也必须指向新目录；自动配置使用最终路径，跳过或失败时复制完成页启动项。不要继续反复调用要求旧记录的外置覆盖更新器，不要清空整个 addons 或删除其他 Mod。导出安装日志检查 record-inspection 和 reinstall-plan，必要时提供日志，不要公开完整个人配置。
