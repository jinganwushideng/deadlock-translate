param([Parameter(Mandatory=$true)][string]$RequestFile,[Parameter(Mandatory=$true)][string]$ResultFile)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'Find-Deadlock.ps1')
. (Join-Path $PSScriptRoot 'Game-SearchPaths.ps1')
. (Join-Path $PSScriptRoot 'Install-Safety.ps1')
. (Join-Path $PSScriptRoot 'Vpk-Recovery.ps1')
$q=Get-Content -LiteralPath $RequestFile -Raw -Encoding UTF8|ConvertFrom-Json
$state=[IO.Path]::GetFullPath($q.state);$payload=[IO.Path]::GetFullPath($q.payload)
$script:DltTraceFile=Join-Path ([IO.Path]::GetDirectoryName($ResultFile)) 'operations.jsonl'
function Full([string]$p){[IO.Path]::GetFullPath($p).TrimEnd('\','/')}
function NoLinks([string]$p){for($v=[IO.Path]::GetFullPath($p);$v;$v=[IO.Path]::GetDirectoryName($v)){if((Test-Path -LiteralPath $v) -and ((Get-Item -LiteralPath $v -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '路径包含链接，请选择普通目录。'}}}
function Hash([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()}
function Emit([string]$s){Send-DltJournalProgress $s;Write-Output ($s|ConvertTo-Json -Compress)}
function Run([string]$script,[string[]]$arguments){
    $failureFile=Join-Path ([IO.Path]::GetDirectoryName($ResultFile)) ('failure-'+[Guid]::NewGuid().ToString('N')+'.json')
    if($script -in @('Manage-Mod.ps1','Update-Mod.ps1','Configure-Steam.ps1')){$arguments+=@('-ErrorResultFile',$failureFile);if($script -in @('Manage-Mod.ps1','Update-Mod.ps1')){$arguments+=@('-TraceFile',$script:DltTraceFile)}}
    if($script -in @('Manage-Mod.ps1','Update-Mod.ps1') -and $q.allowReadOnlyGameinfo){$arguments+='-AllowReadOnlyGameinfo'}
    $previousPreference=$ErrorActionPreference
    $childExit=-1
    $oldChild=$env:DLT_INSTALL_CHILD;$env:DLT_INSTALL_CHILD='1'
    try{$ErrorActionPreference='Continue';$out=& (Join-Path $PSHOME 'powershell.exe') -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $script) @arguments 2>&1;$childExit=$LASTEXITCODE}finally{$ErrorActionPreference=$previousPreference;$env:DLT_INSTALL_CHILD=$oldChild}
    if($childExit -ne 0){
        if(Test-Path -LiteralPath $failureFile){
            $fault=Get-Content -LiteralPath $failureFile -Raw -Encoding UTF8|ConvertFrom-Json
            $ex=[IO.IOException]::new($fault.message);$ex.Data['dltFault']=$fault;throw $ex
        }
        # Never publish Configure-Steam's JSON, launch arguments or config contents to logs.
        $category='child-no-result';$text=($out|Out-String)
        if($text -match 'ParserError|ParseException'){$category='child-parser'}elseif($text -match 'PSSecurityException|UnauthorizedAccess|AccessDenied'){$category='child-access'}elseif($text -match 'ModuleNotFound|Modules_ModuleNotFound'){$category='child-module'}elseif($text -match 'CommandNotFoundException'){$category='child-command'}
        Write-DltInstallTrace 'child-process' $script ($category+':exit='+$childExit)
        Throw-DltFault $category ('script-'+$script.Replace('.ps1','')) '' ($script+' 未完成，退出码 '+$childExit+'；错误类别 '+$category+'。') '请重试本步骤或导出日志；已保留脚本阶段和退出码，不需要删除配置或证书。'
    }
}
function Readiness([string]$game,[string]$runtime,$install,[switch]$Apply){
    NoLinks $state
    $gi=Join-Path $game 'game\citadel\gameinfo.gi';$checks=@()
    try{$change=if($q.mode -eq 'uninstall'){$install -and $install.gameinfoChanged -and (Test-Path -LiteralPath $gi) -and (Hash $gi) -eq $install.gameinfoAfterHash.ToLowerInvariant()}else{(Get-DltSearchPathsPlan ([IO.File]::ReadAllBytes($gi)) -EnsureAddons).Changed}}catch{$ex=[IO.IOException]::new('无法读取游戏配置。',$_.Exception);$ex.Data['dltFault']=Get-DltFault $_ 'gameinfo-preflight' $gi;throw $ex}
    if($change){$checks+=Get-DltFileCheck $gi 'gameinfo' -AllowReadOnly}else{$checks+=@{path=$gi;stage='gameinfo';code='preserved';message='配置已满足要求，保留原文件';blocking=$false;requiresReadOnlyConsent=$false}}
    $checks+=Get-DltFileCheck (Join-Path $state 'install-manifest.json') 'installation-record'
    if($install){$taskVpkState=Get-DltGameVpkState $install (Join-Path $game 'game\citadel\addons') $runtime
        if($taskVpkState.state -in @('ready','renamed')){$checks+=Get-DltFileCheck $taskVpkState.target 'game-vpk'}
        if($taskVpkState.blocked){$checks+=@{path=$taskVpkState.recordedTarget;stage='game-vpk';code='vpk-ambiguous';message=$taskVpkState.message;blocking=$true;requiresReadOnlyConsent=$false;remedy='导出安装日志并核对列出的文件。'}}
    }
    if($q.mode -in @('install','repair')){
        $meta=Get-Content -LiteralPath (Join-Path $payload 'package-manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
        foreach($file in $meta.files){$checks+=Get-DltFileCheck (Join-Path $runtime $file.file) 'runtime-files' -DeferLock}
    }
    foreach($folder in @((Join-Path $game 'game\citadel\addons'),$state,$runtime)|Select-Object -Unique){
        try{Assert-DltNoLinks $folder;if($Apply -and ($q.mode -in @('install','repair') -or $folder -ne $runtime) -and ($q.mode -ne 'uninstall' -or (Test-Path -LiteralPath $folder))){Assert-DltWritableDirectory $folder 'directory-preflight'}}catch{
            $fault=Get-DltFault $_ 'directory-preflight' $folder;$checks+=@{path=$folder;stage=$fault.stage;code=$fault.code;message=$fault.message;remedy=$fault.remedy;blocking=$true;requiresReadOnlyConsent=$false;nativeError=$fault.nativeError;hresult=$fault.hresult;attributes=$fault.attributes;rollback=@()}
        }
    }
    if($q.configureSteam -and $q.mode -ne 'uninstall'){
        try{
            $steamPlans=@(Get-SteamBatchPreview $runtime)
            $script:DltSteamPlans=$steamPlans
            $script:DltSteamAccounts=@(ConvertTo-SteamPreviewSummary $steamPlans)
            $failed=@($steamPlans|Where-Object {$_.error})
            if($failed.Count){throw ('账号 '+$failed[0].userId+'：'+$failed[0].error)}
            $eligible=@($steamPlans|Where-Object {-not $_.skipped})
            if($eligible.Count -eq 1){$script:DltSteamLaunch=$eligible[0].after}
            foreach($steamPlan in $eligible){
                if($steamPlan.before -cne $steamPlan.after){
                    $steamCheck=Get-DltFileCheck $steamPlan.file 'steam-preflight'
                    if($steamCheck.blocking){Throw-DltCheck $steamCheck};$checks+=$steamCheck
                }else{$checks+=@{path=$steamPlan.file;stage='steam-preflight';code='ready';message=('账号 '+$steamPlan.userId+' 的启动项已匹配');blocking=$false;requiresReadOnlyConsent=$false}}
            }
        }catch{
            $fault=Get-DltFault $_ 'steam-preflight';$script:DltSteamUnavailable=$fault;$checks+=@{path=$fault.path;stage='steam-preflight';code='steam-manual-fallback';message='自动配置 Steam 暂不可用，核心安装可以继续；完成页会提供完整启动项。';remedy=$fault.message;blocking=$false;requiresReadOnlyConsent=$false;nativeError=$fault.nativeError;hresult=$fault.hresult;attributes=$fault.attributes;rollback=@()}
        }
    }
    if($Apply){foreach($check in $checks){
        if($check.blocking){Throw-DltCheck $check}
        if($check.requiresReadOnlyConsent -and -not $q.allowReadOnlyGameinfo){Throw-DltFault 'read-only' $check.stage $check.path '游戏配置为只读；尚未复制程序或停用冲突包。' $check.remedy}
    }}
    return @($checks)
}
function ReadInstall { $f=Join-Path $state 'install-manifest.json';if(Test-Path -LiteralPath $f){$record=Get-Content -LiteralPath $f -Raw -Encoding UTF8|ConvertFrom-Json;if(-not $record.target -or $record.vpkHash -notmatch '^[a-fA-F0-9]{64}$'){throw '安装记录缺失关键字段，将按重新安装流程备份恢复。'};$null=Get-DltRecordedGame $record;return $record} }
function Convert-DltRuntimePath([string]$Path){
    if($Path -notmatch '^(?:[a-zA-Z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+)'){Throw-DltFault 'runtime-path-invalid' 'runtime-preflight' $Path '程序目录需要完整的磁盘路径。' '请选择程序文件夹，或留空使用建议目录。'}
    try{return Full $Path}catch{Throw-DltFault 'runtime-path-invalid' 'runtime-preflight' $Path '程序目录格式无效。' '请用“浏览”选择文件夹，或留空使用建议目录。'}
}
function Assert-DltRuntimeLocation([string]$game,[string]$runtime){
    NoLinks $runtime
    $protectedLocations=@($game,$state,[IO.Path]::GetTempPath())
    # The backend inspects its own installed folder; only installer write operations must keep it separate from their payload.
    if(-not ($q.backendOwned -and $q.action -in @('health','steam-accounts','steam-review'))){$protectedLocations+=$payload}
    foreach($protected in $protectedLocations){
        if(-not $protected){continue};$p=Full $protected
        if($runtime -ieq $p -or $runtime.StartsWith($p+'\',[StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($runtime+'\',[StringComparison]::OrdinalIgnoreCase)){throw '程序目录不能与游戏、个人状态、临时目录或安装包重叠。'}
    }
    $root=[IO.Path]::GetPathRoot($runtime+'\')
    if($runtime -ieq $root.TrimEnd('\')){throw '不能使用磁盘根目录作为程序目录。'}
    if(-not [IO.Directory]::Exists($root)){throw '程序目录所在磁盘不可用。'}
    if($runtime.Length -gt 160){throw '程序目录过长，请选择较短的目录。'}
    if([IO.File]::Exists($runtime)){throw '程序目录指向了一个文件，请选择文件夹。'}
}
function Get-DltRecordedRuntime($Record,[string]$Game){
    if(-not $Record -or [string]::IsNullOrWhiteSpace($Record.runtimeFolder)){return ''}
    try{
        $old=Convert-DltRuntimePath $Record.runtimeFolder
        Assert-DltRuntimeLocation $Game $old
        Assert-DltRuntimeLocation (Get-DltRecordedGame $Record) $old
        return $old
    }catch{return ''}
}
function Test-DltEmptyRuntime([string]$Path){
    return (-not (Test-Path -LiteralPath $Path)) -or ((Test-Path -LiteralPath $Path -PathType Container) -and @(Get-ChildItem -LiteralPath $Path -Force).Count -eq 0)
}
function Get-DltSuggestedRuntime($Record,[string]$Game){
    $old=Get-DltRecordedRuntime $Record $Game
    if($old){return $old}
    $parent=Join-Path $env:LOCALAPPDATA 'Programs'
    foreach($n in 0..100){
        $name=if($n -eq 0){'DeadlockTranslate'}else{'DeadlockTranslate-Reinstall-'+$n}
        $candidate=Full (Join-Path $parent $name)
        try{Assert-DltRuntimeLocation $Game $candidate;if(((-not $Record) -and $n -eq 0) -or (Test-DltEmptyRuntime $candidate)){return $candidate}}catch{}
    }
    Throw-DltFault 'runtime-path-unavailable' 'runtime-preflight' $parent '未找到可用的默认程序目录。' '请用“浏览”选择一个空文件夹。'
}
function ResolveInstall([string]$game,[string]$runtime){
    $record=$null;$issue='missing';$originalHash='';$file=Join-Path $state 'install-manifest.json';NoLinks $file
    if(Test-Path -LiteralPath $file -PathType Leaf){$originalHash=Hash $file;try{$record=ReadInstall;$issue='ready'}catch{$issue='invalid'}}
    $oldRuntime=Get-DltRecordedRuntime $record $game
    $defaulted=[string]::IsNullOrWhiteSpace($runtime)
    if($defaulted){$runtime=Get-DltSuggestedRuntime $record $game}else{$runtime=Convert-DltRuntimePath $runtime}
    Assert-DltRuntimeLocation $game $runtime
    $runtimeMove=$null;$runtimeChoice=$null
    if($record -and $q.mode -in @('install','repair')){
        $oldGame=Get-DltRecordedGame $record
        Assert-DltRuntimeLocation $oldGame $runtime
        if($oldRuntime -and $runtime -ine $oldRuntime){
            $separate=-not ($runtime.StartsWith($oldRuntime+'\',[StringComparison]::OrdinalIgnoreCase) -or $oldRuntime.StartsWith($runtime+'\',[StringComparison]::OrdinalIgnoreCase))
            $canUseCurrent=$separate -and (Test-DltEmptyRuntime $runtime)
            $hasApproval=$q.runtimeChoice -eq 'current' -and $q.runtimeChoicePath -ieq $runtime -and $q.runtimeChoiceRecordHash -ceq $originalHash
            if($hasApproval -and $canUseCurrent){$runtimeMove=@{oldRuntime=$oldRuntime;runtime=$runtime;kind='move'}}
            else{$runtimeChoice=@{previous=$oldRuntime;current=$runtime;canUseCurrent=[bool]$canUseCurrent;recordHash=$originalHash;reason=if($canUseCurrent){''}elseif(-not $separate){'当前目录和上次目录互相包含，请选择独立的空文件夹。'}else{'当前目录已有文件。请选择空文件夹，或沿用上次目录。'}}}
        }elseif(-not $oldRuntime -and (Test-DltEmptyRuntime $runtime)){
            $runtimeMove=@{oldRuntime='';runtime=$runtime;kind='recover-missing-path'}
        }
    }
    $requestedRuntime=$runtime
    $reinstall=$null
    if(-not $record -and $q.mode -ne 'uninstall'){$reinstall=Get-DltReinstallPlan $game $runtime $q.mode;$runtime=$reinstall.runtime;$record=$reinstall.record}
    Write-DltInstallTrace 'record-inspection' $file $issue $originalHash
    if($reinstall){Write-DltInstallTrace 'reinstall-plan' $runtime $reinstall.kind}
    $migration=$null
    if($record -and $q.mode -in @('install','repair')){
        $migration=Get-DltGameMigrationPlan $record $game $runtime
        if($migration){$migration.oldRuntime=$oldRuntime;$record=$migration.record}
    }
    return @{install=$record;runtime=$runtime;requestedRuntime=$requestedRuntime;reinstall=$reinstall;migration=$migration;recordState=$issue;recordHash=$originalHash;runtimeChoice=$runtimeChoice;runtimeMove=$runtimeMove;defaulted=$defaulted}
}
function ResolveGame {
    $script:DltGameCorrection=$null
    if($q.mode -eq 'uninstall'){$record=$null;try{$record=ReadInstall}catch{};if($record){$recorded=Get-DltRecordedGame $record;NoLinks $recorded;if([string]::IsNullOrWhiteSpace($q.game) -or (Full $q.game) -ine $recorded){$script:DltGameCorrection=@{requested=$q.game;selected=$recorded;message='卸载将按原安装记录处理旧位置；不会删除当前新游戏的文件。'}};return $recorded}}
    $game=Normalize-DeadlockPath $q.game
    if($game){return $game}
    $found=@(Find-DeadlockQuick -Libraries @($q.steam))
    if($found.Count -eq 1){$script:DltGameCorrection=@{requested=$q.game;selected=$found[0];message='原选择已失效，已定位到唯一完整游戏目录。'};return $found[0]}
    Throw-DltFault 'game-not-found' 'game-location' $q.game '尚未找到唯一的完整 Deadlock 安装。' '点击快速检测或深层扫描；也可在 Steam 管理菜单浏览本地文件后选择该目录。未写入任何游戏文件。'
}
function ValidateTargets([string]$game,[string]$runtime,$install,[switch]$NewRuntime){
    NoLinks $game;Assert-DltRuntimeLocation $game $runtime
    if($q.mode -in @('install','repair')){
        $meta=Get-Content -LiteralPath (Join-Path $payload 'package-manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
        $bytes=[long](($meta.files|Measure-Object -Property bytes -Sum).Sum)+33554432
        foreach($volume in @([IO.Path]::GetPathRoot($runtime),[IO.Path]::GetPathRoot($state))|Select-Object -Unique){
            if($volume -match '^[A-Za-z]:\\$'){$drive=New-Object IO.DriveInfo($volume);if($drive.AvailableFreeSpace -lt $bytes*2){Throw-DltFault 'disk-full' 'space-preflight' $volume '程序或备份磁盘剩余空间不足。' '请至少预留两份完整程序与备份的空间后重试。'}}
        }
    }
    if($install){
        if((Full (Split-Path $install.target -Parent)) -ine (Full (Join-Path $game 'game\citadel\addons'))){throw '所选游戏与原安装记录不一致。迁移前请先卸载旧游戏中的本 Mod。'}
        $validRuntime=(Test-Path -LiteralPath (Join-Path $runtime 'dlt_translate_dir.vpk')) -and (Hash (Join-Path $runtime 'dlt_translate_dir.vpk')) -eq $install.vpkHash.ToLowerInvariant()
        $recorded=$runtime -ieq (Get-DltRecordedRuntime $install $game)
        if(-not $validRuntime -and -not $recorded -and -not($NewRuntime -and (Test-DltEmptyRuntime $runtime))){throw '无法确认程序目录。请选择原目录或独立的空目录。'}
        $meta=Get-Content -LiteralPath (Join-Path $payload 'package-manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
        if($q.mode -in @('install','repair') -and $install.version -and [version]$install.version -gt [version]$meta.version){throw '已有版本比本安装器更新，请下载新版安装器；不会降级覆盖。'}
    }elseif($q.mode -eq 'install' -and (Test-Path -LiteralPath $runtime) -and @(Get-ChildItem -LiteralPath $runtime -Force).Count){throw '首次安装目录必须为空；已有程序请先恢复安装记录。'}
}
function CertStatus {
    $file=Join-Path $state 'localhost-certificate.cer';$private=Test-Path -LiteralPath (Join-Path $state 'localhost-certificate.dpapi')
    if(-not(Test-Path -LiteralPath $file)){return @{ready=$false;state='missing';message='尚未准备本机 HTTPS 证书。'}}
    try{
        $c=New-Object Security.Cryptography.X509Certificates.X509Certificate2($file)
        try{
            if($c.Subject -ne 'CN=DeadlockTranslate localhost' -or $c.Issuer -ne $c.Subject){throw '证书身份不符合本程序。'}
            $store=New-Object Security.Cryptography.X509Certificates.X509Store('Root','CurrentUser');$store.Open('ReadOnly')
            try{$trusted=$store.Certificates.Find('FindByThumbprint',$c.Thumbprint,$false).Count -gt 0}finally{$store.Close()}
            $valid=$c.NotAfter.ToUniversalTime() -gt [DateTime]::UtcNow -and $c.NotBefore.ToUniversalTime() -le [DateTime]::UtcNow
            $ready=$valid -and $trusted -and $private
            return @{ready=$ready;state=if(-not $valid){'expired'}elseif(-not $private){'private-missing'}elseif(-not $trusted){'untrusted'}else{'ready'};thumbprint=$c.Thumbprint;expires=$c.NotAfter.ToString('yyyy-MM-dd');message=if($ready){'已有有效且受信任的证书；无需重复安装。'}elseif(-not $valid){'证书已过期或尚未生效，需要续期。'}elseif(-not $private){'证书私钥文件缺失，需要重新准备并确认信任。'}else{'证书已生成，尚未获得当前用户信任。'}}
        }finally{$c.Dispose()}
    }catch{return @{ready=$false;state='invalid';message='无法读取本程序证书，请使用原安装用户或导出诊断。'}}
}
function VpkPaths([string]$file){
    $r=[IO.BinaryReader]::new([IO.File]::OpenRead($file))
    try{
        if($r.ReadUInt32() -ne 0x55AA1234){throw 'VPK 文件头无效'};$ver=$r.ReadUInt32();$size=$r.ReadUInt32()
        if($ver -eq 2){$null=$r.BaseStream.Seek(16,1)}elseif($ver -ne 1){throw 'VPK 版本不支持'}
        $end=$r.BaseStream.Position+$size;if($size -gt 134217728 -or $end -gt $r.BaseStream.Length){throw 'VPK 目录无效'}
        function Str { $b=[Collections.Generic.List[byte]]::new();while($r.BaseStream.Position -lt $end){$v=$r.ReadByte();if($v -eq 0){return [Text.Encoding]::UTF8.GetString($b.ToArray())};if($b.Count -gt 4096){throw 'VPK 名称过长'};$b.Add($v)};throw 'VPK 目录截断' }
        while(($ext=Str) -ne ''){while(($dir=Str) -ne ''){while(($name=Str) -ne ''){$null=$r.ReadUInt32();$pre=$r.ReadUInt16();$null=$r.BaseStream.Seek(12+$pre,1);if($r.BaseStream.Position -gt $end){throw 'VPK 目录越界'};"$dir/$name.$ext"}}}
    }finally{$r.Dispose()}
}
function Inspect {
    $recordError='';$install=$null;try{$install=ReadInstall}catch{$recordError='安装记录损坏：选择安装或修复即可重新检测残留目录并恢复，原记录会先备份。'};$games=@(Find-DeadlockQuick -Libraries @($q.steam));$steam=$q.steam
    if(-not $steam){$steam=(Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath}
    $active=0;try{$active=[long](Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction Stop).ActiveUser;if($active -lt 0){$active+=4294967296}}catch{}
    $names=@{}
    $users=@()
    if([IO.Directory]::Exists($steam)){
        $loginFile=[IO.Path]::Combine($steam,'config\loginusers.vdf')
        if([IO.File]::Exists($loginFile)){try{$logins=[IO.File]::ReadAllText($loginFile,[Text.Encoding]::UTF8);foreach($match in [regex]::Matches($logins,'"(\d{17})"\s*\{([^{}]*)\}')){$id=[uint64]$match.Groups[1].Value-[uint64]76561197960265728;if($id -le [uint32]::MaxValue){$nick=[regex]::Match($match.Groups[2].Value,'"PersonaName"\s+"((?:\\.|[^"\\])*)"',[Text.RegularExpressions.RegexOptions]::IgnoreCase);if($nick.Success){$names[[string]$id]=[regex]::Replace($nick.Groups[1].Value,'\\([\\"])',{param($m)$m.Groups[1].Value})}}}}catch{}}
        $userData=[IO.Path]::Combine($steam,'userdata')
        if([IO.Directory]::Exists($userData)){
            foreach($d in Get-ChildItem -LiteralPath $userData -Directory -ErrorAction SilentlyContinue){
                if($d.Name -match '^\d+$' -and [long]$d.Name -gt 0 -and [long]$d.Name -le [uint32]::MaxValue -and [IO.File]::Exists([IO.Path]::Combine($d.FullName,'config\localconfig.vdf'))){
                    $users+=@{id=$d.Name;name=$names[$d.Name];active=([string]$active -eq $d.Name)}
                }
            }
        }
    }
    $runtime=Get-DltSuggestedRuntime $install $q.game
    $game=if($install){Split-Path (Split-Path (Split-Path (Split-Path $install.target -Parent) -Parent) -Parent) -Parent}elseif($games.Count -eq 1){$games[0]}else{''}
    if($q.mode -ne 'uninstall' -and -not (Normalize-DeadlockPath $game) -and $games.Count -eq 1){$game=$games[0]}
    @{games=@($games);game=$game;runtime=$runtime;recordError=$recordError;installedVersion=if($install){$install.version}else{''};installed=[bool]$install;steam=$steam;users=@($users);activeUser=[string]$active;certificate=(CertStatus);gameRunning=[bool](Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue);steamRunning=[bool](Get-Process -Name steam -ErrorAction SilentlyContinue)}
}
function Get-ReviewedFingerprint([string]$Game,[string]$Runtime,[string]$Target){
    $paths=@((Join-Path $Game 'game\citadel\gameinfo.gi'),(Join-Path $state 'install-manifest.json'),(Join-Path $Runtime 'package-manifest.json'))
    if($Target){$paths+=$Target}
    if($q.configureSteam -and $q.steam){
        foreach($id in @(Get-SelectedSteamUsers|Sort-Object)){
            $paths+=[IO.Path]::Combine((Full $q.steam),('userdata\'+$id+'\config\localconfig.vdf'))
        }
    }
    $facts=@($paths|ForEach-Object {
        try{(Full $_)+'|'+(Get-DltJournalHash $_)}catch{(Full $_)+'|unavailable'}
    })
    return Get-DltBytesHash ([Text.Encoding]::UTF8.GetBytes(($facts -join [char]10)))
}
function Get-MaintenanceHealth {
    $view=Inspect;$items=[Collections.Generic.List[object]]::new()
    function Check([string]$Id,[string]$Status,[string]$Message,[string]$Action=''){
        $items.Add(@{id=$Id;status=$Status;message=$Message;action=$Action})
    }
    $record=$null;try{$record=ReadInstall}catch{}
    $game=if($q.game){Normalize-DeadlockPath $q.game}else{Normalize-DeadlockPath $view.game}
    $runtime=if($record){Get-DltSuggestedRuntime $record $game}elseif($q.runtime){$q.runtime}else{$view.runtime}
    if($game){Check 'game' 'ok' ('游戏目录：'+$game)}else{Check 'game' 'attention' '未确认唯一的完整游戏目录，请在安装器中选择当前游戏。' 'choose-game'}
    if($record -and (Get-DltRecordedRuntime $record $game)){Check 'record' 'ok' '安装记录可读取。'}
    elseif($record){Check 'record' 'repair' '安装记录的程序目录不可用，可在预览中选择新位置。' 'repair-core'}
    else{Check 'record' 'repair' '安装记录缺失或损坏。' 'repair-core'}
    if($record -and $game){
        try{
            $integrity=Get-DltRuntimeIntegrity $runtime $record
            if($integrity.requiresRepair){Check 'runtime' 'repair' '后台、网页、配对或构建文件缺失或不一致。' 'repair-core'}
            else{Check 'runtime' 'ok' '已检查程序文件和配对。'}
        }catch{Check 'runtime' 'attention' '程序文件无法读取，请检查目录权限。'}
        try{
            if((Get-DltRecordedGame $record) -ine $game){Check 'vpk' 'repair' '安装记录指向旧游戏位置。' 'repair-core'}
            else{
                $vpk=Get-DltGameVpkState $record (Join-Path $game 'game\citadel\addons') $runtime
                if($vpk.blocked){Check 'vpk' 'attention' $vpk.message 'repair-core'}
                elseif($vpk.requiresRepair){Check 'vpk' 'repair' $vpk.message 'repair-core'}
                else{Check 'vpk' 'ok' '游戏 VPK 与安装记录匹配。'}
            }
        }catch{Check 'vpk' 'attention' '游戏 VPK 无法读取。'}
    }
    if($game){
        try{
            $gi=Join-Path $game 'game\citadel\gameinfo.gi';NoLinks $gi
            if((Get-DltSearchPathsPlan ([IO.File]::ReadAllBytes($gi)) -EnsureAddons).Changed){Check 'game-paths' 'repair' '游戏搜索路径需要修复。' 'repair-core'}
            else{Check 'game-paths' 'ok' '游戏搜索路径正常。'}
        }catch{Check 'game-paths' 'attention' '无法解析游戏搜索路径，请先验证游戏文件。'}
    }
    if($view.certificate.ready){Check 'certificate' 'ok' ('本机证书可用，到期 '+$view.certificate.expires)}
    else{Check 'certificate' 'attention' $view.certificate.message 'certificate'}
    $localPort=17865;if($q.port -ge 1 -and $q.port -le 65534){$localPort=[int]$q.port}
    try{
        $ports=@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()|ForEach-Object {$_.Port})
        if($q.backendOwned){
            if($ports -contains ($localPort+1)){Check 'backend' 'ok' '当前后台可访问，已检测到本地 HTTPS 监听。'}
            else{Check 'backend' 'attention' '当前后台可访问，但 HTTPS 端口未监听。' 'certificate'}
        }elseif($ports -contains $localPort){
            try{
                $processes=@(Get-CimInstance Win32_Process -Filter "Name='DeadlockTranslate.exe'" -ErrorAction Stop)
                $owned=@($processes|Where-Object {$_.ExecutablePath -ieq (Join-Path $runtime 'DeadlockTranslate.exe')})
                if(-not $owned.Count){Check 'backend' 'attention' '端口正在使用，无法确认属于当前安装。'}
                else{
                    $request=[Net.HttpWebRequest]::Create("http://localhost:$localPort/health");$request.Proxy=$null;$request.AllowAutoRedirect=$false;$request.Timeout=2000;$request.ReadWriteTimeout=2000
                    $response=$request.GetResponse()
                    try{
                        $reader=[IO.StreamReader]::new($response.GetResponseStream());$buf=New-Object char[] 8192;$count=$reader.ReadBlock($buf,0,8192)
                        if($count -eq 0 -or $count -eq 8192){throw 'Invalid health response'}
                        $health=(-join $buf[0..($count-1)])|ConvertFrom-Json
                        $localBuild=Read-DltJournalJson (Join-Path $runtime 'build-info.json')
                        if($health.app -eq 'DeadlockTranslate' -and $health.buildId -eq $localBuild.buildId){Check 'backend' 'ok' '当前程序后台与本机连接可用。'}
                        else{Check 'backend' 'attention' '端口响应与当前安装构建不一致。'}
                    }finally{$response.Dispose()}
                }
            }catch{Check 'backend' 'attention' '本机端口已占用，后台身份或连接未通过检查。'}
        }elseif($ports -contains ($localPort+1)){Check 'backend' 'attention' '后台尚未连接，本地 HTTPS 端口已被占用。'}
        else{Check 'backend' 'verify' '后台尚未启动，按 Steam 原方式启动后验证连接。'}
    }catch{Check 'backend' 'attention' '无法检查本机端口。'}
    $steam=$view.steam
    $selected=if($q.userId){[string]$q.userId}else{[string]$view.activeUser}
    if($steam -and -not [IO.Directory]::Exists($steam)){
        Check 'steam' 'attention' '所选 Steam 目录当前不可用，请重新选择 Steam 目录。' 'steam-retry'
    }elseif($steam -and $selected -match '^[1-9]\d*$'){
        try{
            $steamFile=Join-Path ([IO.Path]::GetDirectoryName($ResultFile)) ('health-steam-'+[Guid]::NewGuid().ToString('N')+'.json')
            try{
                Run 'Configure-Steam.ps1' @('-SteamPath',$steam,'-UserId',$selected,'-RuntimeFolder',$runtime,'-PreviewRuntime','-ResultFile',$steamFile)
                $steamPlan=Read-DltJournalJson $steamFile
                if($steamPlan.alreadyConfigured){Check 'steam' 'ok' '所选 Steam 账号的启动项匹配。'}
                else{Check 'steam' 'repair' '所选 Steam 账号的启动项需要配置。' 'steam-retry'}
            }finally{if([IO.File]::Exists($steamFile)){[IO.File]::Delete($steamFile)}}
        }catch{
            if((Get-DltFault $_).code -eq 'steam-no-game-record'){Check 'steam' 'verify' '当前账号还没有 Deadlock 本地记录，已跳过启动项配置。'}
            else{Check 'steam' 'attention' '无法确认当前账号的启动项，请在安装器中核对。' 'steam-retry'}
        }
    }else{Check 'steam' 'attention' '尚未确认当前 Steam 账号。' 'steam-retry'}
    $pending=@(Get-DltPendingTransactions $state)
    if($pending.Count){Check 'interrupted' 'repair' '有未完成的安装或启动项配置。' 'recover-transaction'}
    else{Check 'interrupted' 'ok' '没有未完成的安装记录。'}
    Check 'game-connect' 'verify' '实际游戏内接收、发送和显示需要启动 Deadlock 验证。'
    return @{checks=$items.ToArray();pending=$pending;game=$game;runtime=$runtime;gameRunning=$view.gameRunning;steam=$steam;userId=$selected}
}
function Conflicts([string]$game,$KnownInstall=$null){
    $own=@(VpkPaths (Join-Path $payload 'dlt_translate_dir.vpk'));$install=$KnownInstall;if(-not $install){try{$install=ReadInstall}catch{$install=$null}};$result=@()
    foreach($f in @(Get-ChildItem -LiteralPath (Join-Path $game 'game\citadel\addons') -Filter '*.vpk' -File -ErrorAction SilentlyContinue)){
        NoLinks $f.FullName
        if($install -and $f.Name -match '^pak\d{2,3}_dir\.vpk$' -and (Hash $f.FullName) -eq $install.vpkHash.ToLowerInvariant()){continue}
        try{$paths=@(VpkPaths $f.FullName);if(Test-DltQolPackage $paths){Write-DltInstallTrace 'conflict-scan' $f.FullName 'qol-ignored';continue};$exact=@($paths|Where-Object {$_ -in $own});$related=@($paths|Where-Object {$_ -match 'chattranslat|chat_translat|top_bar_chat\.vcss'})
            if($exact.Count -or $related.Count){$result+=@{file=$f.Name;hash=(Hash $f.FullName);kind=if($exact.Count){'exact'}else{'related'};resources=@($exact+$related|Select-Object -Unique)}}
        }catch{$result+=@{file=$f.Name;hash=(Hash $f.FullName);kind='unreadable';resources=@('无法检查该资源包')}}
    };return @($result)
}
function StopBackend([string]$runtime){
    if(-not $runtime){return};$own=@(Get-CimInstance Win32_Process -Filter "Name='DeadlockTranslate.exe'"|Where-Object {$_.ExecutablePath -ieq (Join-Path $runtime 'DeadlockTranslate.exe')})
    if(-not $own.Count){return}
    $pairFile=Join-Path $runtime 'pairing.key';if(-not(Test-Path -LiteralPath $pairFile)){if($own.Count){throw '旧后台缺少配对文件，请手动停止。'};return}
    $key=([IO.File]::ReadAllText($pairFile)).Trim();if($key -notmatch '^[0-9a-fA-F]{48}$'){throw '旧后台配对文件无效，请在设置页停止后台。'}
    try{$health=Invoke-RestMethod 'http://localhost:17865/health' -TimeoutSec 2}catch{if($own.Count){throw '旧后台仍运行但无法连接，请在设置页停止它。'};return}
    if($health.app -ne 'DeadlockTranslate'){throw '本地端口被其他程序占用；未停止其他程序。'}
    $base='http://localhost:17865';$session=Invoke-RestMethod -Method Post -Uri ($base+'/control/'+$key+'/session') -TimeoutSec 3
    $uri=[Uri]$session.url;if($uri.Host -ne 'localhost' -or $uri.Port -ne 17865 -or $uri.Scheme -ne 'http' -or -not $uri.AbsolutePath.StartsWith('/settings/')){throw '后台身份响应无效。'}
    $null=Invoke-WebRequest -UseBasicParsing -Uri $uri -SessionVariable cookies -TimeoutSec 3
    $null=Invoke-RestMethod -Method Post -Uri ($base+'/api/stop') -WebSession $cookies -Headers @{Origin=$base;'X-DLT-Request'='1'} -TimeoutSec 3
    foreach($p in $own){try{$proc=Get-Process -Id $p.ProcessId -ErrorAction Stop;if(-not $proc.WaitForExit(10000)){throw '旧后台没有正常退出。'}}catch [Microsoft.PowerShell.Commands.ProcessCommandException]{}}
}
function Get-SelectedSteamUsers {
    $ids=if($q.PSObject.Properties['userIds']){@($q.userIds|Where-Object {$_})}else{@($q.userId|Where-Object {$_})}
    $ids=@($ids)
    if($ids.Count -gt 32){throw '一次最多配置 32 个 Steam 账号。'}
    $seen=[Collections.Generic.HashSet[string]]::new()
    foreach($id in $ids){
        if([string]$id -notmatch '^[1-9]\d{0,9}$' -or [long]$id -gt [uint32]::MaxValue){throw 'Steam 账号编号无效。'}
        if($seen.Add([string]$id)){[string]$id}
    }
}
function SteamArgs([string]$runtime,[switch]$Apply,[switch]$Remove,[string]$AccountId){
    if(-not $AccountId){$AccountId=@(Get-SelectedSteamUsers|Select-Object -First 1)[0]}
    if(-not $q.steam -or -not $AccountId){throw '请选择 Steam 目录和要配置的账号；也可以手动粘贴启动项。'}
    $a=@('-RuntimeFolder',$runtime,'-SteamPath',$q.steam,'-UserId',$AccountId)
    if($Apply){$a+='-Apply';if($q.stopSteam){$a+='-StopSteam';$a+='-RestartSteam'}}
    if($Remove){$a+='-RemoveTranslator'}elseif($q.admin){$a+='-AdministratorCompatibility'}elseif($q.action -notin @('steam-review','steam-retry')){$a+='-NormalPermissions'}
    return $a
}
function Get-SteamAccountPreview([string]$runtime,[string]$id){
    $file=Join-Path ([IO.Path]::GetDirectoryName($ResultFile)) ('steam-account-'+[Guid]::NewGuid().ToString('N')+'.json')
    try{
        Run 'Configure-Steam.ps1' @((SteamArgs $runtime -AccountId $id)+@('-PreviewRuntime','-ResultFile',$file))
        Read-DltJournalJson $file
    }finally{if([IO.File]::Exists($file)){[IO.File]::Delete($file)}}
}
function Get-SteamBatchPreview([string]$runtime,[string[]]$AccountIds){
    $ids=if($AccountIds){$AccountIds}else{@(Get-SelectedSteamUsers)}
    if(-not $ids.Count){throw '请至少勾选一个 Steam 账号。'}
    foreach($id in $ids){
        try{Get-SteamAccountPreview $runtime $id}
        catch{
            $fault=Get-DltFault $_ 'steam-preflight'
            if($fault.code -eq 'steam-no-game-record'){
                [pscustomobject]@{userId=$id;skipped=$true;reason=$fault.message;code=$fault.code}
            }else{[pscustomobject]@{userId=$id;error=$fault.message;code=$fault.code}}
        }
    }
}
function ConvertTo-SteamPreviewSummary([object[]]$plans){
    foreach($plan in $plans){@{id=[string]$plan.userId;file=$plan.file;alreadyConfigured=[bool]$plan.alreadyConfigured;skipped=[bool]$plan.skipped;reason=$plan.reason;error=$plan.error;code=$plan.code}}
}
function Get-SteamReviewToken([string]$runtime,[object[]]$plans){
    $facts=@((Full $q.steam),$runtime,[string][bool]$q.admin)+@($plans|Sort-Object userId|ForEach-Object {([string]$_.userId)+'|'+$_.code+'|'+$_.beforeHash})
    Get-DltBytesHash ([Text.Encoding]::UTF8.GetBytes(($facts -join [char]10)))
}
function Test-SelectedSteamRunning {
    $exe=Join-Path (Full $q.steam) 'Steam.exe'
    return [bool]@(Get-CimInstance Win32_Process -Filter "Name='steam.exe'" -ErrorAction Stop|Where-Object {-not $_.ExecutablePath -or $_.ExecutablePath -ieq $exe}).Count
}
function Invoke-SteamBatchApply([string]$runtime,[object[]]$plans){
    $failed=@($plans|Where-Object {$_.error})
    if($failed.Count){throw ('账号 '+$failed[0].userId+'：'+$failed[0].error)}
    $plans=@($plans|Where-Object {-not $_.skipped})
    $changed=@($plans|Where-Object {-not $_.alreadyConfigured})
    if(-not $changed.Count){return}
    if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw '请退出 Deadlock，再配置所选 Steam 账号。'}
    $stopped=$false;$committed=@()
    try{
        if(Test-SelectedSteamRunning){
            if(-not $q.stopSteam){throw '请先正常退出所选 Steam，或允许安装器退出并重新打开它。'}
            $exe=Join-Path (Full $q.steam) 'Steam.exe';NoLinks $exe
            Emit '正在等待所选 Steam 正常退出…'
            $null=Start-Process -FilePath $exe -ArgumentList '-shutdown' -WindowStyle Hidden -PassThru
            $deadline=[DateTime]::UtcNow.AddSeconds(25)
            do{if(-not(Test-SelectedSteamRunning)){$stopped=$true;break};Start-Sleep -Milliseconds 250}while([DateTime]::UtcNow -lt $deadline)
            if(-not $stopped){throw 'Steam 未在限定时间内退出。'}
        }
        # Normal Steam shutdown may flush unrelated data. Recheck all launch options before the first write.
        # A previously skipped account stays out of the write set, even if Steam created its record during shutdown.
        $eligibleIds=@($plans|ForEach-Object {[string]$_.userId})
        $fresh=@(Get-SteamBatchPreview $runtime -AccountIds $eligibleIds)
        foreach($account in $fresh){
            $before=@($plans|Where-Object userId -eq $account.userId)[0]
            if($account.skipped -or $account.error -or $account.before -cne $before.before -or $account.after -cne $before.after){throw ('账号 '+$account.userId+' 的启动项在预览后变化，请重新检查。')}
        }
        foreach($account in @($fresh|Where-Object {-not $_.alreadyConfigured})){
            Test-DltStop
            if(Test-SelectedSteamRunning){throw 'Steam 已重新启动，请重新预览。'}
            $file=Join-Path ([IO.Path]::GetDirectoryName($ResultFile)) ('steam-apply-'+[Guid]::NewGuid().ToString('N')+'.json')
            try{
                Run 'Configure-Steam.ps1' @((SteamArgs $runtime -AccountId ([string]$account.userId))+@('-Apply','-ExpectedReviewHash',$account.beforeHash,'-ResultFile',$file))
                $result=Read-DltJournalJson $file
                if($result.changed){$committed+=@{file=$result.file;backup=$result.backup;hash=(Hash $result.file)}}
            }finally{if([IO.File]::Exists($file)){[IO.File]::Delete($file)}}
        }
    }catch{
        $failure=$_
        if($env:DLT_INSTALL_JOURNAL -and -not(Test-SelectedSteamRunning)){
            $issues=@(Restore-DltTransaction $env:DLT_INSTALL_JOURNAL -SteamOnly)
            if($issues.Count){throw '部分 Steam 配置无法安全回退。请关闭 Steam 后在安装器中恢复。'}
            $script:DltSteamRollbackSucceeded=$true
        }elseif($committed.Count){RestoreSteamPrefixes @{changes=@($committed)}}
        throw $failure
    }finally{if($stopped -and -not(Test-SelectedSteamRunning)){RestartSteam @{stopped=$true}}}
}
function RemoveSteamPrefixes([string]$runtime,[string]$backup){
    if(-not $q.steam){throw '请选择 Steam 目录。'}
    $plans=@();$committed=@();$stopped=$false
    foreach($dir in @(Get-ChildItem -LiteralPath (Join-Path $q.steam 'userdata') -Directory)){
        if($dir.Name -notmatch '^\d+$' -or -not(Test-Path -LiteralPath (Join-Path $dir.FullName 'config\localconfig.vdf'))){continue}
        $result=Join-Path $backup ('steam-'+$dir.Name+'.json')
        try{Run 'Configure-Steam.ps1' @('-RuntimeFolder',$runtime,'-SteamPath',$q.steam,'-UserId',$dir.Name,'-RemoveTranslator','-ResultFile',$result)}
        catch{if((Get-DltFault $_).code -eq 'steam-no-game-record'){continue};throw}
        $p=Get-Content -LiteralPath $result -Raw -Encoding UTF8|ConvertFrom-Json
        if($p.before -cne $p.after){$plans+=@{id=$dir.Name;result=$result}}
    }
    try{
        foreach($plan in $plans){
            $a=@('-RuntimeFolder',$runtime,'-SteamPath',$q.steam,'-UserId',$plan.id,'-RemoveTranslator','-Apply','-ResultFile',$plan.result)
            if($q.stopSteam){$a+='-StopSteam'}
            Run 'Configure-Steam.ps1' $a
            $p=Get-Content -LiteralPath $plan.result -Raw -Encoding UTF8|ConvertFrom-Json;$stopped=$stopped -or $p.steamStoppedNormally
            if($p.changed){$committed+=@{file=$p.file;backup=$p.backup;hash=(Hash $p.file)}}
        }
        return @{changes=@($committed);stopped=$stopped}
    }catch{try{RestoreSteamPrefixes @{changes=@($committed);stopped=$stopped}}finally{RestartSteam @{stopped=$stopped}};throw}
}
function RestoreSteamPrefixes($cleanup){
    if(-not $cleanup){return}
    if(@(Get-CimInstance Win32_Process -Filter "Name='steam.exe'"|Where-Object {-not $_.ExecutablePath -or $_.ExecutablePath -ieq (Join-Path $q.steam 'Steam.exe')}).Count){throw 'Steam 已重新运行，启动项备份不能安全自动回退；请保留备份。'}
    foreach($entry in @($cleanup.changes)){
        if((Hash $entry.file) -ne $entry.hash){throw 'Steam 配置在回退前变化，请使用备份人工核对。'}
        $temp=$entry.file+'.restore-'+[Guid]::NewGuid().ToString('N')+'.tmp'
        [IO.File]::WriteAllBytes($temp,[IO.File]::ReadAllBytes($entry.backup));[IO.File]::Replace($temp,$entry.file,[NullString]::Value)
    }
}
function RestartSteam($cleanup){if($cleanup -and $cleanup.stopped){$null=Start-Process -FilePath (Join-Path $q.steam 'Steam.exe') -WindowStyle Hidden -PassThru}}
function Recover([string]$game,[string]$runtime){
    $recordFile=Join-Path $state 'install-manifest.json';$oldRecord=$null
    if(Test-Path -LiteralPath $recordFile){try{$oldRecord=ReadInstall}catch{};if($oldRecord){throw '已有可读取的安装记录，不能覆盖。请选择原目录或使用修复更新。'};if(-not $q.replaceBrokenRecord){throw '需要明确确认备份并恢复损坏记录。'}}
    $vpk=Join-Path $runtime 'dlt_translate_dir.vpk';NoLinks $runtime
    if(-not(Test-Path -LiteralPath $vpk -PathType Leaf)){return @{reinstallRequired=$true;message='旧翻译 VPK 已不存在，无需先卸载或手动恢复旧包；已切换重新安装，将保留个人数据并检查残留目录。'}}
    $h=Hash $vpk
    $paths=@(VpkPaths $vpk)
    if(-not(Test-DltIndependentResources $paths)){throw '所选 VPK 不能确认为独立的本翻译 Mod。'}
    if(([IO.File]::ReadAllText((Join-Path $runtime 'pairing.key'))).Trim() -notmatch '^[a-fA-F0-9]{48}$'){throw '配对文件无效。'}
    $matches=@(Get-ChildItem -LiteralPath (Join-Path $game 'game\citadel\addons') -Filter '*_dir.vpk' -File -ErrorAction SilentlyContinue|Where-Object {(Hash $_.FullName) -eq $h})
    if($matches.Count -eq 0){return @{reinstallRequired=$true;message='游戏中的旧翻译包已不存在，无需先恢复记录；已切换重新安装，保留个人数据并成套恢复。'}}
    if($matches.Count -ne 1){Throw-DltFault 'vpk-recovery-duplicate' 'recover' $runtime '发现多个相同的旧翻译包。' '选择安装或修复重新预检；独立重复翻译包会备份整理，不需要手动改安装记录。'}
    $meta=Get-Content -LiteralPath (Join-Path $runtime 'package-manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $gi=Join-Path $game 'game\citadel\gameinfo.gi';New-Item -ItemType Directory -Force -Path $state|Out-Null
    if(Test-Path -LiteralPath $recordFile){NoLinks $recordFile;Move-DltTrackedFile $recordFile ($recordFile+'.invalid-'+[Guid]::NewGuid().ToString('N')+'.bak') 'recover-record'}
    $recordBytes=[Text.UTF8Encoding]::new($false).GetBytes((@{target=$matches[0].FullName;vpkHash=$h;version=$meta.version;runtimeFolder=$runtime;gameinfoChanged=$false;gameinfoBefore='';gameinfoAfterHash=(Hash $gi)}|ConvertTo-Json))
    Write-DltAtomic $recordFile $recordBytes -ExpectedHash '-' -Stage 'recover-record'
}
function RemoveOwnedRuntime([string]$runtime,[string]$backup){
    $metaPath=Join-Path $runtime 'package-manifest.json';if(-not(Test-Path -LiteralPath $metaPath)){throw '缺少程序文件清单，保留程序目录；可人工检查后删除。'}
    NoLinks $metaPath;$meta=Get-Content -LiteralPath $metaPath -Raw -Encoding UTF8|ConvertFrom-Json;$removed=0;$kept=0
    foreach($entry in @($meta.files)+@([pscustomobject]@{file='package-manifest.json';sha256=(Hash $metaPath)})){
        $parts=$entry.file.Replace('\','/').Split('/')
        if([IO.Path]::IsPathRooted($entry.file) -or @($parts|Where-Object {$_ -in @('','.','..') -or $_.TrimEnd(' ','.') -cne $_ -or $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0}).Count){throw '文件清单有越界路径，停止清理。'}
        if($parts[-1].ToLowerInvariant() -in @('settings.json','secrets.dpapi','install-manifest.json','localhost-certificate.cer','localhost-certificate.dpapi') -or [IO.Path]::GetExtension($parts[-1]).ToLowerInvariant() -in @('.pfx','.cer','.dpapi')){$kept++;continue}
        if($parts.Count -gt 1 -and $parts[0] -notin @('data','wwwroot','LICENSES','source')){$kept++;continue}
        if($parts.Count -eq 1 -and $parts[0] -notmatch '^(DeadlockTranslate\.(exe|pdb|staticwebassets\.endpoints\.json)|dlt_translate_dir\.vpk|pairing\.key|package-manifest\.json|Install\.cmd|Uninstall\.cmd|Launch(?:-Admin)?\.cmd|Settings\.cmd|Manage-Mod\.ps1|Find-Deadlock\.ps1|Game-SearchPaths\.ps1|Install-Safety\.ps1|Installer-(?:Transaction|Workflow)\.ps1|Vpk-Recovery\.ps1|Configure-Steam\.ps1|Export-Diagnostics\.cmd|Repair-GameConfig\.cmd|Trust-Local-Https\.(ps1|cmd)|Renew-Local-Https\.cmd|LICENSE|NOTICE|SOURCE\.txt|web\.config|README\.md|GUI-INSTALLER\.md|USER-GUIDE\.md|AI-INSTALL-GUIDE\.md|TEST-REPORT\.md|VALIDATION\.md|CHANGELOG\.md|THIRD-PARTY-NOTICES\.md|Steam-Launch-Options\.txt|使用说明-先读我\.txt|AI安装说明书\.txt)$'){$kept++;continue}
        $path=Full (Join-Path $runtime ($parts -join '\'));if(-not $path.StartsWith($runtime+'\',[StringComparison]::OrdinalIgnoreCase)){throw '清理目标超出程序目录。'};NoLinks $path
        if(Test-Path -LiteralPath $path -PathType Leaf){if((Hash $path) -ne $entry.sha256.ToLowerInvariant()){$kept++;continue};$saved=Join-Path (Join-Path $backup 'removed-runtime') ($parts -join '\');[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved))|Out-Null;[IO.File]::Copy($path,$saved);if((Hash $saved) -ne $entry.sha256.ToLowerInvariant()){throw '删除前备份校验失败，保留原程序文件。'};if((Hash $path) -ne $entry.sha256.ToLowerInvariant()){$kept++;continue};Remove-DltTrackedFile $path $entry.sha256 'uninstall-runtime';$removed++}
    }
    return "已备份并移除 $removed 个原程序文件；保留 $kept 个不符合清单或被改动的文件。个人设置未删除。"
}
$operationGuard=$null
$env:DLT_INSTALL_JOURNAL=$null
try{
    NoLinks $state;NoLinks $payload
    if($q.action -in @('apply','recover','restore-transaction','steam-retry','certificate','remove-certificate')){
        $operationGuard=Enter-DltOperation $state
    }
    if($q.action -in @('apply','recover','certificate','remove-certificate') -and @(Get-DltPendingTransactions $state).Count){
        Throw-DltFault 'recovery-required' 'preflight' '' '请先处理上次未完成的安装。'
    }
    $result=@{}
    switch($q.action){
        'inspect'{$result=Inspect;$result.pending=@(Get-DltPendingTransactions $state)}
        'restore-transaction'{
            if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw '请先退出 Deadlock，再恢复安装。'}
            if($q.recoveryId -notmatch '^[a-f0-9]{32}$'){throw '无效的恢复编号。'}
            $folder=Join-Path (Join-Path $state 'transactions') $q.recoveryId
            $tx=Read-DltTransaction $folder
            $workerCheck=Enter-DltJournalWorker $folder
            try{
            if($tx.state -ine $state -or $tx.phase -in @('complete','rolled-back','preserved-conflicts')){throw '恢复记录已结束或不属于当前目录。'}
            if($q.keepCore -and $tx.phase -ne 'steam'){throw '核心安装尚未完成。'}
            if($tx.steam -and @(Get-ChildItem -LiteralPath (Join-Path $folder 'files') -Filter '*.json' | Where-Object { (Read-DltJournalJson $_.FullName).stage -eq 'steam-launch-options' }).Count){
                $selectedSteam=Join-Path $tx.steam 'Steam.exe'
                $running=@(Get-CimInstance Win32_Process -Filter "Name='steam.exe'" -ErrorAction Stop | Where-Object {-not $_.ExecutablePath -or $_.ExecutablePath -ieq $selectedSteam})
                if($running.Count){throw '恢复启动项前请正常退出所选 Steam。'}
            }
            if($q.resumeTransaction -and $tx.phase -ne 'steam'){
                $retained=Join-Path $folder 'payload';$retainedMeta=Join-Path $retained 'package-manifest.json'
                if((Get-DltJournalHash $retainedMeta) -ne $tx.manifestHash -or (Get-DltJournalHash (Join-Path $payload 'package-manifest.json')) -ne $tx.manifestHash){throw '无法确认继续安装所需的完整包。可先恢复安装前状态，再用本安装器检查。'}
                foreach($item in (Read-DltJournalJson $retainedMeta).files){
                    if([IO.Path]::IsPathRooted($item.file) -or ($item.file -split '[\\/]') -contains '..' -or (Get-DltJournalHash (Join-Path $retained $item.file)) -ne $item.sha256){throw '继续安装所需文件未通过校验。'}
                }
            }
            $steamOnly=($q.resumeTransaction -or $q.keepCore) -and $tx.phase -eq 'steam'
            if(-not $steamOnly){StopBackend $tx.runtime}
            $env:DLT_INSTALL_JOURNAL=$folder
            Emit '按文件哈希检查并恢复上一次安装…'
            $issues=@(Restore-DltTransaction $folder -SteamOnly:$steamOnly)
            if($issues.Count -eq 0){Set-DltTransactionPhase 'rolled-back';Clear-DltTransactionStaging $folder}
            $preserved=$q.acceptChanged -and $issues.Count -gt 0 -and -not @($issues|Where-Object {$_.reason -ne 'changed'}).Count
            if($preserved){Set-DltTransactionPhase 'preserved-conflicts'}
            $result=@{issues=$issues;restored=($issues.Count -eq 0 -or $preserved);preserved=[bool]$preserved;selection=$tx.request;steamOnly=[bool]$steamOnly;phase=$tx.phase}
            }finally{$workerCheck.Dispose()}
        }
        'health'{$result=Get-MaintenanceHealth}
        'steam-accounts'{
            $view=Inspect
            $result=@{users=@($view.users);activeUser=$view.activeUser}
        }
        'steam-review'{
            $installed=ReadInstall
            if(-not $installed -or (Full $installed.runtimeFolder) -ine (Full $q.runtime)){throw '无法确认当前程序目录。请先完成安装。'}
            if(-not $q.steam){$q|Add-Member -NotePropertyName steam -NotePropertyValue (Inspect).steam -Force}
            $plans=@(Get-SteamBatchPreview (Full $q.runtime))
            $blocked=[bool]@($plans|Where-Object {$_.error}).Count
            $result=@{accounts=@(ConvertTo-SteamPreviewSummary $plans);blocked=$blocked;reviewToken=if(-not $blocked){Get-SteamReviewToken (Full $q.runtime) $plans}else{''}}
            if($plans.Count -eq 1 -and -not $blocked -and -not $plans[0].skipped){$result.file=$plans[0].file;$result.hash=$plans[0].beforeHash;$result.alreadyConfigured=$plans[0].alreadyConfigured}
        }
        'steam-retry'{
            if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw '请先退出 Deadlock，再修复 Steam 启动项。'}
            if(@(Get-DltPendingTransactions $state).Count){throw '请先恢复未完成的安装。'}
            $installed=ReadInstall
            if(-not $installed -or (Full $installed.runtimeFolder) -ine (Full $q.runtime)){throw '无法确认 Steam 启动项对应的当前安装。'}
            $game=Get-DltRecordedGame $installed
            $plans=@(Get-SteamBatchPreview (Full $q.runtime))
            if(@($plans|Where-Object {$_.error}).Count){throw '至少一个 Steam 账号无法配置，请重新预览。'}
            $token=Get-SteamReviewToken (Full $q.runtime) $plans
            if($q.reviewedSteamToken -cne $token -and -not ($plans.Count -eq 1 -and $q.reviewedSteamHash -and $q.reviewedSteamHash -ceq $plans[0].beforeHash)){
                throw 'Steam 账号选择或启动项在预览后变化，请重新预览。'
            }
            $eligible=@($plans|Where-Object {-not $_.skipped})
            if(-not @($eligible|Where-Object {-not $_.alreadyConfigured}).Count){
                $result=@{steamDone=[bool]$eligible.Count;accounts=@(ConvertTo-SteamPreviewSummary $plans)};break
            }
            Start-DltTransaction $state $q $payload $game (Full $q.runtime) '' ''
            Set-DltTransactionPhase 'steam'
            try{Invoke-SteamBatchApply (Full $q.runtime) $plans}catch{
                if($script:DltSteamRollbackSucceeded){Set-DltTransactionPhase 'rolled-back';Clear-DltTransactionStaging $env:DLT_INSTALL_JOURNAL}
                throw
            }
            Complete-DltTransaction
            $result=@{steamDone=$true;accounts=@(ConvertTo-SteamPreviewSummary $plans)}
        }
        'deep'{$result=Find-DeadlockDeep -Roots @($q.scanRoot) -TimeoutSeconds 60}
        'certificate'{
            if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw '请先退出游戏，再准备或续期证书。'}
            if($q.renew){$old=ReadInstall;if($old){StopBackend $old.runtimeFolder}}
            $info=CertStatus
            if($info.ready -and -not $q.renew){$result=$info;break}
            Emit '正在准备证书；Windows 信任弹窗需要你亲自确认。'
            # This script uses its own directory for the verified backend executable.
            $trust=Join-Path $PSScriptRoot 'Trust-Local-Https.ps1'
            $a=@('-RuntimeFolder',$payload,'-StateDirectory',$state,'-ConfirmTrust');if($q.renew){$a+='-Renew'}
            $output=& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $trust @a 2>&1
            if($LASTEXITCODE -ne 0){throw '证书没有准备完成或信任被取消。请使用原 Windows 用户重试。'}
            $result=CertStatus;if(-not $result.ready){throw '尚未确认有效证书信任，安装没有继续。'}
        }
        'remove-certificate'{
            if(Get-Process -Name deadlock,citadel,DeadlockTranslate -ErrorAction SilentlyContinue){throw '请先退出游戏并停止翻译后台，再撤销证书信任。'}
            Emit '等待 Windows 确认撤销当前用户的本程序证书信任…'
            Run 'Trust-Local-Https.ps1' @('-RuntimeFolder',$payload,'-StateDirectory',$state,'-RemoveTrust','-ConfirmTrust')
            $result=CertStatus
        }
        'plan'{
            $game=ResolveGame;NoLinks $game
            $decision=ResolveInstall $game $q.runtime
            if($decision.runtimeChoice){$result=@{runtimeChoice=$decision.runtimeChoice;game=$game;runtime=$decision.runtime};break}
            $install=$decision.install;$runtime=$decision.runtime;$q.runtime=$runtime
            ValidateTargets $game $runtime $install -NewRuntime:([bool]$decision.runtimeMove)
            if(-not $install -and $q.mode -eq 'install' -and (Test-Path -LiteralPath $runtime) -and @(Get-ChildItem -LiteralPath $runtime -Force).Count){throw '首次安装目录必须为空。若这是旧程序，请先使用“恢复安装记录”。'}
            $conflicts=Conflicts $game $install
            $vpkState=if($install){Get-DltGameVpkState $install (Join-Path $game 'game\citadel\addons') $runtime}else{$null}
            $integrity=if($install){Get-DltRuntimeIntegrity $runtime $install}else{$null}
            $launch='"'+(Join-Path $runtime 'DeadlockTranslate.exe')+'"'+$(if($q.admin){' --admin-compatible'}else{''})+' --steam-game %command%'
            $needsRepair=$install -and (-not(Test-Path -LiteralPath (Join-Path $runtime 'DeadlockTranslate.exe')) -or -not(Test-Path -LiteralPath (Join-Path $runtime 'data\terms.json')) -or -not(Test-Path -LiteralPath (Join-Path $runtime 'dlt_translate_dir.vpk')))
            if($install -and (Test-Path -LiteralPath (Join-Path $runtime 'dlt_translate_dir.vpk')) -and (Hash (Join-Path $runtime 'dlt_translate_dir.vpk')) -ne $install.vpkHash.ToLowerInvariant()){$needsRepair=$true}
            $needsRepair=$needsRepair -or ($integrity -and $integrity.requiresRepair)
            if(($decision.reinstall -or $decision.migration -or $decision.runtimeMove) -and $install){$needsRepair=$true}
            $reinstallInfo=if($decision.reinstall){@{kind=$decision.reinstall.kind;originalRuntime=$decision.reinstall.originalRuntime;runtime=$runtime;message=$decision.reinstall.message;recordState=$decision.recordState}}else{$null}
            $result=@{reinstallSuggested=[bool]($q.mode -eq 'uninstall' -and -not $install);reinstall=$reinstallInfo;fullRepair=[bool]($q.mode -eq 'repair' -and ($install -or $decision.reinstall));gameVpk=$vpkState;runtimeIntegrity=$integrity;needsGameRepair=[bool]($vpkState -and $vpkState.requiresRepair);game=$game;runtime=$runtime;installed=[bool]$install;needsRepair=[bool]$needsRepair;version=if($install){$install.version}else{''};conflicts=@($conflicts);launch=$launch;gameRunning=[bool](Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue);certificate=(CertStatus)}
            $result.migration=if($decision.migration){@{oldGame=$decision.migration.oldGame;newGame=$game;oldState=$decision.migration.oldState;message=$decision.migration.message}}else{$null}
            $result.gameCorrection=$script:DltGameCorrection
            $result.runtimeMove=$decision.runtimeMove
            $result.runtimeDefaulted=$decision.defaulted
            $result.recordHash=$decision.recordHash
            $result.reviewFingerprint=Get-ReviewedFingerprint $game $runtime $(if($vpkState){$vpkState.target}else{''})
            $checks=@(Readiness $game $runtime $install);$result.preflight=$checks;$result.blocked=[bool]@($checks|Where-Object {$_.blocking}).Count;$result.needsReadOnlyConsent=[bool]@($checks|Where-Object {$_.requiresReadOnlyConsent}).Count
            if($script:DltSteamLaunch){$result.launch=$script:DltSteamLaunch}
            $result.steamAccounts=@($script:DltSteamAccounts|Where-Object {$_})
        }
        'recover'{
            $game=ResolveGame;$decision=ResolveInstall $game $q.runtime;$runtime=$decision.runtime;$q.runtime=$runtime
            if($decision.runtimeChoice){$result=@{runtimeChoice=$decision.runtimeChoice;game=$game;runtime=$runtime};break}
            if($decision.migration){
                $result=Inspect;$result.reinstallRequired=$true;$result.game=$game;$result.runtime=$runtime
                $result.message='检测到游戏位置变化。继续安装或修复即可备份旧记录并自动迁移，无需手动删除记录。'
            }elseif($decision.recordState -eq 'ready'){
                $result=Inspect;$result.reinstallRequired=$true;$result.game=$game;$result.runtime=$runtime
                $result.message='原安装记录可读取。继续安装或修复，在预览中检查本次程序位置。'
            }else{
                # Search the old folder first. A suggested reinstall destination has no old VPK to adopt.
                $runtime=$decision.requestedRuntime;$q.runtime=$runtime
                if($q.transactionId){
                    Start-DltTransaction $state $q $payload $game $runtime '' ''
                }
                try{
                    $recovered=Recover $game $runtime;$result=Inspect
                    if($recovered -and $recovered.reinstallRequired){$result.reinstallRequired=$true;$result.message=$recovered.message}
                    Complete-DltTransaction
                }catch{
                    if($env:DLT_INSTALL_JOURNAL){
                        $issues=@(Restore-DltTransaction $env:DLT_INSTALL_JOURNAL)
                        if(-not $issues.Count){Set-DltTransactionPhase 'rolled-back';Clear-DltTransactionStaging $env:DLT_INSTALL_JOURNAL}
                    }
                    throw
                }
            }
        }
        'apply'{
            if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw '请退出 Deadlock 后再开始；不会强制结束游戏。'}
            $game=ResolveGame;NoLinks $game
            $decision=ResolveInstall $game $q.runtime
            if($decision.runtimeChoice){Throw-DltFault 'runtime-choice-required' 'runtime-preflight' $decision.runtime '程序目录未确认或已经变化，请重新选择并预览。'}
            $install=$decision.install;$runtime=$decision.runtime;$q.runtime=$runtime;$backup=Join-Path $state ('backups\gui-'+[Guid]::NewGuid().ToString('N'))
            if($q.reviewedRecordHash -and $q.reviewedRecordHash -ne $decision.recordHash){Throw-DltFault 'file-changed' 'record-review' (Join-Path $state 'install-manifest.json') '安装记录在预览后变化，请重新检查；尚未修改文件。'}
            ValidateTargets $game $runtime $install -NewRuntime:([bool]$decision.runtimeMove)
            if($q.reviewFingerprint){
                $currentTarget=if($install){(Get-DltGameVpkState $install (Join-Path $game 'game\citadel\addons') $runtime).target}else{''}
                if((Get-ReviewedFingerprint $game $runtime $currentTarget) -ne $q.reviewFingerprint){
                    Throw-DltFault 'file-changed' 'review' '' '游戏文件、程序清单或 Steam 配置在预览后发生变化，请重新检查。'
                }
            }
            Emit '检查目标文件、写入权限和回退空间…';$null=Readiness $game $runtime $install -Apply
            New-Item -ItemType Directory -Force -Path $backup|Out-Null
            $disabled=@();$installedNow=$false;$coreDone=$false;$warnings=@();$optionalFailures=@();$steamDone=$false;$steamHandled=$false;$steamCleanup=$null;$stage=$null;$adoptedRecordHash='';$savedInvalidRecord=''
            $migrationRecord='';$migrationHash='';$migrationVpk=$null
            try{
                if($q.transactionId){
                    $oldGame=if($decision.migration){$decision.migration.oldGame}else{''}
                    Start-DltTransaction $state $q $payload $game $runtime $oldGame $backup
                }
                if($q.mode -eq 'install' -or ($q.mode -eq 'repair' -and ($install -or $decision.reinstall))){
                    if(-not (CertStatus).ready){throw '请先完成第 1 步的证书确认。'}
                    Emit '校验证书私钥…';$p=Start-Process -FilePath (Join-Path $payload 'DeadlockTranslate.exe') -ArgumentList @('--prepare-https','--no-shell','--data-dir',('"'+$state+'"')) -WindowStyle Hidden -PassThru -Wait;if($p.ExitCode){throw '证书私钥无法读取，请使用原安装用户。'}
                    if(-not (CertStatus).ready){throw '证书私钥对应的证书与当前信任不一致，请返回证书步骤重新检测并确认信任。'}
                    if($decision.runtimeMove -and $decision.runtimeMove.oldRuntime){
                        Emit '正在正常停止上次目录中的翻译后台…'
                        StopBackend $decision.runtimeMove.oldRuntime
                    }
                    if($decision.migration){
                        Emit '正在备份旧安装记录并迁移到当前游戏…';$recordFile=Join-Path $state 'install-manifest.json'
                        if((Hash $recordFile) -ne $decision.recordHash){Throw-DltFault 'file-changed' 'migration-record' $recordFile '旧记录在检测后变化，已保留原文件，请重新检查。'}
                        $migrationRecord=Join-Path $backup 'previous-game-install-manifest.json';[IO.File]::Copy($recordFile,$migrationRecord,$false)
                        StopBackend $decision.migration.oldRuntime
                        $oldVpk=$decision.migration.oldOwnedVpk
                        if($oldVpk){NoLinks $oldVpk;if((Hash $oldVpk) -ne $decision.migration.oldHash.ToLowerInvariant()){Throw-DltFault 'file-changed' 'migration-old-vpk' $oldVpk '旧翻译文件在检测后变化，请重新检查。'}
                            $saved=$oldVpk+'.dlt-migrated-'+[Guid]::NewGuid().ToString('N')+'.bak';Move-DltTrackedFile $oldVpk $saved 'migration-old-vpk';$migrationVpk=@{original=$oldVpk;backup=$saved;hash=$decision.migration.oldHash.ToLowerInvariant()};Write-DltInstallTrace 'migration-old-vpk' $oldVpk 'backed-up-and-disabled' $migrationVpk.hash
                        }
                        Write-DltAtomic $recordFile ([Text.UTF8Encoding]::new($false).GetBytes(($install|ConvertTo-Json -Depth 6))) -ExpectedHash $decision.recordHash -Stage 'migration-record';$migrationHash=Hash $recordFile
                        Write-DltInstallTrace 'game-migration' $game 'new-game-record-staged' $decision.recordHash $migrationHash
                    }
                    foreach($c in @(Conflicts $game $install)){
                        if($c.kind -eq 'related'){if(-not $q.allowRelated){throw '检测到相关聊天/HUD Mod，请先在预览中明确确认共存。'};continue}
                        $approved=@($q.disable|Where-Object {$_.file -ceq $c.file -and $_.hash -ceq $c.hash});if($approved.Count -ne 1){throw ('存在未确认的冲突或不可读资源包：'+$c.file)}
                        $original=Join-Path (Join-Path $game 'game\citadel\addons') $c.file;NoLinks $original;$saved=$original+'.dlt-disabled-'+[Guid]::NewGuid().ToString('N')+'.bak';if((Hash $original) -ne $c.hash){throw '冲突包在确认后发生变化，已停止。'};Move-DltTrackedFile $original $saved 'conflict-disable';$disabled+=@{original=$original;disabled=$saved;hash=$c.hash};$disabled|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $backup 'disabled-conflicts.json') -Encoding UTF8;Emit ('已按选择停用：'+$c.file)
                    }
                    if($decision.recordState -eq 'invalid'){
                        $recordFile=Join-Path $state 'install-manifest.json';$savedInvalidRecord=Join-Path $backup 'invalid-install-manifest.json'
                        if((Hash $recordFile) -ne $decision.recordHash){Throw-DltFault 'file-changed' 'record-rebuild' $recordFile '安装记录在检测后变化，请重新检查。'}
                        Move-DltTrackedFile $recordFile $savedInvalidRecord 'record-rebuild';Write-DltInstallTrace 'record-rebuild' $recordFile 'invalid-record-backed-up' $decision.recordHash
                    }
                    if($decision.reinstall -and $install){
                        Emit '旧安装记录已丢失，将保留数据并成套重装…';$recordFile=Join-Path $state 'install-manifest.json'
                        Write-DltAtomic $recordFile ([Text.UTF8Encoding]::new($false).GetBytes(($install|ConvertTo-Json -Depth 6))) -ExpectedHash '-' -Stage 'record-rebuild';$adoptedRecordHash=Hash $recordFile
                    }
                    if($install){Emit '正在成套更新后台、设置页和 VPK…';$a=@('-PackageFolder',$payload,'-RuntimeFolder',$runtime,'-GamePath',$game,'-StateDirectory',$state,'-NonInteractive');if($decision.runtimeMove){$a+='-RequireEmptyRuntime'};if($q.repairMissing -or $decision.reinstall -or $decision.migration -or $decision.runtimeMove){$a+='-RepairMissingRuntime'};if($q.repairGameVpk -or $decision.reinstall -or $decision.migration){$a+='-RepairGameVpk';if($q.recoveryTarget){$a+=@('-RecoveryTarget',$q.recoveryTarget)}};Run 'Update-Mod.ps1' $a}
                    else{
                        if($env:DLT_INSTALL_JOURNAL){
                            Emit '正在复制完整程序并校验各文件…'
                            $meta=Read-DltJournalJson (Join-Path $payload 'package-manifest.json')
                            foreach($item in @($meta.files)+@(@{file='package-manifest.json';sha256=(Hash (Join-Path $payload 'package-manifest.json'))})){
                                $source=Join-Path $payload $item.file;$target=Join-Path $runtime $item.file
                                $bytes=[IO.File]::ReadAllBytes($source)
                                if((Get-DltBytesHash $bytes) -ne $item.sha256){throw '安装文件已变化。'}
                                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
                                Write-DltAtomic $target $bytes -ExpectedHash '-' -Stage 'runtime-copy'
                            }
                        }else{
                        $stage=$runtime+'.dlt-stage-'+[Guid]::NewGuid().ToString('N');NoLinks $stage
                        Emit '正在复制完整程序并安装游戏 Mod…';New-Item -ItemType Directory -Force -Path $stage|Out-Null;Copy-Item -Path (Join-Path $payload '*') -Destination $stage -Recurse
                        if(Test-Path -LiteralPath $runtime){[IO.Directory]::Delete($runtime,$false)}
                        Move-DltStagedDirectory $stage $runtime;$installedNow=$true
                        }
                        Run 'Manage-Mod.ps1' @('-Action','Install','-PackageFolder',$runtime,'-GamePath',$game,'-StateDirectory',$state,'-AllowRelated','-NonInteractive')
                    }
                    $coreDone=$true
                    if($disabled.Count){try{$m=ReadInstall;$previous=@($m.disabledConflicts|Where-Object {$_});$m|Add-Member -NotePropertyName disabledConflicts -NotePropertyValue @($previous+$disabled) -Force;$tempManifest=Join-Path $state ('install-'+[Guid]::NewGuid().ToString('N')+'.tmp');[IO.File]::WriteAllText($tempManifest,($m|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));Write-DltAtomic (Join-Path $state 'install-manifest.json') ([IO.File]::ReadAllBytes($tempManifest)) -Backup (Join-Path $backup 'manifest-before-conflicts.json') -Stage 'conflict-record';[IO.File]::Delete($tempManifest)}catch{$warnings+='冲突包备份清单保存在本次备份目录，但未合并到安装记录。'}}
                }elseif($q.mode -eq 'repair'){
                    if($install){StopBackend $runtime};Emit '正在修复游戏搜索路径…';Run 'Manage-Mod.ps1' @('-Action','Repair','-PackageFolder',$payload,'-GamePath',$game,'-StateDirectory',$state,'-NonInteractive');$coreDone=$true
                }elseif($q.mode -eq 'uninstall'){
                    if(-not $install){throw '没有可验证的安装记录；请先恢复安装记录。'};StopBackend $runtime
                    # Do not remove files while Steam still points to the translator unless manual mode was chosen.
                    if($q.configureSteam){Emit '正在移除此 Steam 目录中指向本程序的启动前缀…';$steamCleanup=RemoveSteamPrefixes $runtime $backup;$steamDone=$true}
                    if(-not (Normalize-DeadlockPath $game)){
                        Emit '旧游戏已不完整，正在备份清理本程序的失效安装记录…';$recordFile=Join-Path $state 'install-manifest.json';$oldTarget=$install.target;NoLinks $oldTarget
                        $savedVpk=$null
                        try{
                            if(Test-Path -LiteralPath $oldTarget -PathType Leaf){if((Hash $oldTarget) -eq $install.vpkHash.ToLowerInvariant()){$savedVpk=$oldTarget+'.dlt-uninstalled-'+[Guid]::NewGuid().ToString('N')+'.bak';Move-DltTrackedFile $oldTarget $savedVpk 'uninstall-orphan-vpk'}else{$warnings+='旧 VPK 槽位内容已变更，已保留该文件。'}}
                            if((Hash $recordFile) -ne $decision.recordHash){throw '安装记录在检查后变化。'};Move-DltTrackedFile $recordFile (Join-Path $backup 'uninstalled-install-manifest.json') 'uninstall-orphan-record'
                        }catch{if($savedVpk -and -not(Test-Path -LiteralPath $oldTarget) -and (Hash $savedVpk) -eq $install.vpkHash.ToLowerInvariant()){[IO.File]::Move($savedVpk,$oldTarget)};throw}
                        $warnings+='旧游戏已移除或不完整，未改写其游戏配置；个人数据保留，可直接重新安装。'
                    }else{Emit '正在卸载本程序 VPK…';Run 'Manage-Mod.ps1' @('-Action','Uninstall','-PackageFolder',$runtime,'-GamePath',$game,'-StateDirectory',$state,'-NonInteractive')};$coreDone=$true
                    if($q.restoreConflicts){foreach($c in @($install.disabledConflicts|Where-Object {$_})){
                        if((Full (Split-Path $c.original -Parent)) -ine (Full (Join-Path $game 'game\citadel\addons')) -or (Full (Split-Path $c.disabled -Parent)) -ine (Full (Split-Path $c.original -Parent)) -or -not $c.disabled.StartsWith($c.original+'.dlt-disabled-',[StringComparison]::OrdinalIgnoreCase)){$warnings+='旧冲突备份路径不属于当前游戏，已保留。';continue}
                        NoLinks $c.original;NoLinks $c.disabled
                        if((Test-Path -LiteralPath $c.disabled) -and -not(Test-Path -LiteralPath $c.original) -and (Hash $c.disabled) -eq $c.hash){Move-DltTrackedFile $c.disabled $c.original 'restore-conflicting-mod'}else{$warnings+='冲突备份已有变动或原路径被占用，未覆盖。'}
                    }}
                    if($q.removeCertificate){Emit '等待 Windows 确认移除本程序证书信任…';$output=& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Trust-Local-Https.ps1') -RuntimeFolder $runtime -StateDirectory $state -RemoveTrust -ConfirmTrust 2>&1;if($LASTEXITCODE){$warnings+='证书信任未移除；可稍后使用原程序的证书脚本重试。'}}
                    # Runtime files are retained intentionally so repair, diagnostics and rollback remain available.
                    if($q.removeRuntime){try{$warnings+=RemoveOwnedRuntime $runtime $backup}catch{$warnings+='程序清理未全部完成；已移除文件有备份，剩余文件保留。'}}else{$warnings+='程序文件和个人设置已保留；游戏 Mod 已卸载。可从完成页打开原目录。'}

                }else{throw '未知操作。'}
                if($coreDone -and $q.mode -ne 'uninstall'){Set-DltTransactionPhase 'steam'}
                Test-DltStop
                if($q.configureSteam -and $q.mode -ne 'uninstall' -and $script:DltSteamUnavailable){$optionalFailures+=$script:DltSteamUnavailable;$warnings+='核心安装已完成。Steam 自动配置不可用，请在完成页复制启动项，或重新运行安装器重试。'}
                elseif($q.configureSteam -and $q.mode -ne 'uninstall'){
                    try{
                        $eligible=@($script:DltSteamPlans|Where-Object {-not $_.skipped})
                        if(@($eligible|Where-Object {-not $_.alreadyConfigured}).Count){
                            Emit '正在备份并配置有 Deadlock 记录的所选 Steam 账号…'
                            if(@($script:DltSteamPlans).Count -gt 1){Invoke-SteamBatchApply $runtime $script:DltSteamPlans}
                            else{Run 'Configure-Steam.ps1' (SteamArgs $runtime -Apply)}
                        }
                        $steamDone=[bool]$eligible.Count;$steamHandled=$true
                    }catch{$optionalFailures+=Get-DltFault $_ 'steam-configuration';$warnings+='程序操作已完成，但 Steam 启动项未配置。请复制界面给出的启动项手动设置，或重试。'}
                }
                if($decision.runtimeMove -and -not $steamHandled){$warnings+='请在启动游戏前，将所选 Steam 账号的启动项切换到本次程序目录。'}
                if(-not $q.configureSteam){$warnings+=if($q.mode -eq 'uninstall'){'请从所有相关 Steam 账号手动移除翻译启动前缀，避免残留启动项。'}else{'已按选择跳过 Steam 配置，请复制启动项到 Steam 后再启动游戏。'}}
                Test-DltStop
                if($q.mode -eq 'uninstall' -or -not $q.configureSteam -or $steamHandled -or -not $env:DLT_INSTALL_JOURNAL){Complete-DltTransaction}
                $result=@{pendingSteam=[bool]($env:DLT_INSTALL_JOURNAL -and $q.configureSteam -and -not $steamHandled -and $q.mode -ne 'uninstall');complete=($warnings.Count -eq 0);coreDone=$coreDone;steamDone=$steamDone;steamAccounts=@($script:DltSteamAccounts|Where-Object {$_});warnings=@($warnings);failures=@($optionalFailures);runtime=$runtime;backup=$backup;mode=$q.mode;reinstalled=[bool]$decision.reinstall;migrated=[bool]$decision.migration}
            }catch{
                $originalFailure=$_
                if($env:DLT_INSTALL_JOURNAL){
                    $tx=Read-DltTransaction $env:DLT_INSTALL_JOURNAL
                    if($tx.phase -ne 'steam'){
                        try{
                            $issues=@(Restore-DltTransaction $env:DLT_INSTALL_JOURNAL)
                            if($issues.Count){$warnings+=@($issues|ForEach-Object {'保留待处理文件：'+$_.path})}
                            else{Set-DltTransactionPhase 'rolled-back';Clear-DltTransactionStaging $env:DLT_INSTALL_JOURNAL}
                        }catch{$warnings+='回退未完成，下次打开安装器可以继续恢复。'}
                    }
                }elseif(-not $coreDone){foreach($c in $disabled){try{if((Test-Path -LiteralPath $c.disabled) -and -not(Test-Path -LiteralPath $c.original) -and (Hash $c.disabled) -eq $c.hash){Move-Item -LiteralPath $c.disabled -Destination $c.original}else{$warnings+='冲突包回退未完成，保留备份：'+$c.disabled}}catch{$warnings+='冲突包回退未完成，保留备份：'+$c.disabled}}
                    if($migrationRecord -and $migrationHash){try{if((Hash $recordFile) -ne $migrationHash){throw 'Record changed'};Write-DltAtomic $recordFile ([IO.File]::ReadAllBytes($migrationRecord)) -ExpectedHash $migrationHash -Stage 'rollback-migration-record';Write-DltInstallTrace 'game-migration' $game 'rolled-back'}catch{$warnings+='迁移记录未能安全回退，原记录备份保留：'+$migrationRecord}}
                    if($migrationVpk){try{if(Test-Path -LiteralPath $migrationVpk.original){throw 'Old slot changed'};if((Hash $migrationVpk.backup) -ne $migrationVpk.hash){throw 'Backup changed'};[IO.File]::Move($migrationVpk.backup,$migrationVpk.original)}catch{$warnings+='旧翻译文件回退未完成，备份保留：'+$migrationVpk.backup}}
                    $recordFile=Join-Path $state 'install-manifest.json'
                    if($adoptedRecordHash){try{if((Hash $recordFile) -ne $adoptedRecordHash){throw 'Record changed'};Remove-Item -LiteralPath $recordFile;Write-DltInstallTrace 'rollback-record' $recordFile 'temporary-record-removed'}catch{$warnings+='临时安装记录无法安全回退，原备份保留：'+$backup}}
                    if($savedInvalidRecord -and (Test-Path -LiteralPath $savedInvalidRecord)){try{if(Test-Path -LiteralPath $recordFile){throw 'Record slot changed'};[IO.File]::Move($savedInvalidRecord,$recordFile)}catch{$warnings+='原损坏记录备份保留：'+$savedInvalidRecord}}
                    if($steamCleanup){try{RestoreSteamPrefixes $steamCleanup}catch{$warnings+='Steam 启动项回退未完成，请保留本次备份。'}}
                    if($installedNow){try{NoLinks $runtime;$failed=$runtime+'.dlt-failed-'+[Guid]::NewGuid().ToString('N');[IO.Directory]::Move($runtime,$failed);$warnings+='首次安装未完成，已复制文件归档到：'+$failed+'；可重新安装。'}catch{$warnings+='部分文件保留在目标目录，请检查后重试。'}}
                };$fault=Get-DltFault $originalFailure 'apply';$fault.rollback=@($fault.rollback)+@($warnings);$originalFailure.Exception.Data['dltFault']=$fault;throw $originalFailure
            }finally{if($steamCleanup){try{RestartSteam $steamCleanup}catch{Emit 'Steam 未能重新打开，请手动启动。'}};if($stage -and (Test-Path -LiteralPath $stage)){Emit ('未完成的文件保留在：'+$stage)}}
        }
        default{throw '未知安装器操作。'}
    }
    @{ok=$true;data=$result}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $ResultFile -Encoding UTF8
}catch{
    $fault=Get-DltFault $_ $q.action
    @{ok=$false;error=$fault.message;failure=$fault}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $ResultFile -Encoding UTF8
    exit 1
}finally{if($operationGuard){$operationGuard.Dispose()}}
