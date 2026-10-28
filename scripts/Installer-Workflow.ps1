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
function Emit([string]$s){Write-Output ($s|ConvertTo-Json -Compress)}
function Run([string]$script,[string[]]$arguments){
    $failureFile=Join-Path ([IO.Path]::GetDirectoryName($ResultFile)) ('failure-'+[Guid]::NewGuid().ToString('N')+'.json')
    if($script -in @('Manage-Mod.ps1','Update-Mod.ps1','Configure-Steam.ps1')){$arguments+=@('-ErrorResultFile',$failureFile);if($script -in @('Manage-Mod.ps1','Update-Mod.ps1')){$arguments+=@('-TraceFile',$script:DltTraceFile)}}
    if($script -in @('Manage-Mod.ps1','Update-Mod.ps1') -and $q.allowReadOnlyGameinfo){$arguments+='-AllowReadOnlyGameinfo'}
    $previousPreference=$ErrorActionPreference
    try{$ErrorActionPreference='Continue';$out=& powershell.exe -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $script) @arguments 2>&1;$childExit=$LASTEXITCODE}finally{$ErrorActionPreference=$previousPreference}
    if($childExit -ne 0){
        if(Test-Path -LiteralPath $failureFile){
            $fault=Get-Content -LiteralPath $failureFile -Raw -Encoding UTF8|ConvertFrom-Json
            $ex=[IO.IOException]::new($fault.message);$ex.Data['dltFault']=$fault;throw $ex
        }
        # Never publish Configure-Steam's JSON, launch arguments or config contents to logs.
        $detail='请检查所选目录、文件占用和权限；此步骤没有返回结构化诊断。'
        throw ($script+'：'+$detail)
    }
}
function Readiness([string]$game,[string]$runtime,$install,[switch]$Apply){
    NoLinks $state
    $gi=Join-Path $game 'game\citadel\gameinfo.gi';$checks=@()
    try{$change=if($q.mode -eq 'uninstall'){$install -and $install.gameinfoChanged -and (Hash $gi) -eq $install.gameinfoAfterHash.ToLowerInvariant()}else{(Get-DltSearchPathsPlan ([IO.File]::ReadAllBytes($gi)) -EnsureAddons).Changed}}catch{$ex=[IO.IOException]::new('无法读取游戏配置。',$_.Exception);$ex.Data['dltFault']=Get-DltFault $_ 'gameinfo-preflight' $gi;throw $ex}
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
        try{Assert-DltNoLinks $folder;if($Apply -and ($q.mode -in @('install','repair') -or $folder -ne $runtime)){Assert-DltWritableDirectory $folder 'directory-preflight'}}catch{
            $fault=Get-DltFault $_ 'directory-preflight' $folder;$checks+=@{path=$folder;stage=$fault.stage;code=$fault.code;message=$fault.message;remedy=$fault.remedy;blocking=$true;requiresReadOnlyConsent=$false;nativeError=$fault.nativeError;hresult=$fault.hresult;attributes=$fault.attributes;rollback=@()}
        }
    }
    if($q.configureSteam -and $q.mode -ne 'uninstall'){
        try{
            $steamPreview=Join-Path ([IO.Path]::GetDirectoryName($ResultFile)) ('steam-preview-'+[Guid]::NewGuid().ToString('N')+'.json')
            Run 'Configure-Steam.ps1' @((SteamArgs $runtime)+@('-PreviewRuntime','-ResultFile',$steamPreview))
            $steamPlan=Get-Content -LiteralPath $steamPreview -Raw -Encoding UTF8|ConvertFrom-Json
            if($steamPlan.before -cne $steamPlan.after){$checks+=Get-DltFileCheck $steamPlan.file 'steam-preflight'}
            else{$checks+=@{path=$steamPlan.file;stage='steam-preflight';code='ready';message='所选账号的启动项已匹配；不重复写入';blocking=$false;requiresReadOnlyConsent=$false}}
        }catch{
            $fault=Get-DltFault $_ 'steam-preflight';$checks+=@{path=$fault.path;stage=$fault.stage;code=$fault.code;message=$fault.message;remedy=$fault.remedy;blocking=$true;requiresReadOnlyConsent=$false;nativeError=$fault.nativeError;hresult=$fault.hresult;attributes=$fault.attributes;rollback=@()}
        }
    }
    if($Apply){foreach($check in $checks){
        if($check.blocking){Throw-DltCheck $check}
        if($check.requiresReadOnlyConsent -and -not $q.allowReadOnlyGameinfo){Throw-DltFault 'read-only' $check.stage $check.path '游戏配置为只读；尚未复制程序或停用冲突包。' $check.remedy}
    }}
    return @($checks)
}
function ReadInstall { $f=Join-Path $state 'install-manifest.json';if(Test-Path -LiteralPath $f){$record=Get-Content -LiteralPath $f -Raw -Encoding UTF8|ConvertFrom-Json;if(-not $record.target -or $record.vpkHash -notmatch '^[a-fA-F0-9]{64}$'){throw '安装记录缺失关键字段，请手动定位旧程序并恢复记录。'};return $record} }
function ResolveInstall([string]$game,[string]$runtime){
    if(-not [IO.Path]::IsPathRooted($q.runtime)){Throw-DltFault 'runtime-path-relative' 'reinstall-preflight' '' '程序目录需要完整的磁盘路径。' '请选择程序文件夹，或输入 C:\Apps\DeadlockTranslate 这样的完整路径。'}
    $record=$null;$issue='missing';$originalHash='';$file=Join-Path $state 'install-manifest.json';NoLinks $file
    if(Test-Path -LiteralPath $file -PathType Leaf){$originalHash=Hash $file;try{$record=ReadInstall;$issue='ready'}catch{$issue='invalid'}}
    $reinstall=$null
    if(-not $record -and $q.mode -ne 'uninstall'){$reinstall=Get-DltReinstallPlan $game $runtime $q.mode;$runtime=$reinstall.runtime;$record=$reinstall.record}
    Write-DltInstallTrace 'record-inspection' $file $issue $originalHash
    if($reinstall){Write-DltInstallTrace 'reinstall-plan' $runtime $reinstall.kind}
    return @{install=$record;runtime=$runtime;reinstall=$reinstall;recordState=$issue;recordHash=$originalHash}
}
function ValidateTargets([string]$game,[string]$runtime,$install){
    NoLinks $game;NoLinks $runtime
    if(-not [IO.Path]::IsPathRooted($q.runtime)){throw '程序目录必须是完整的磁盘路径。'}
    foreach($protected in @($game,$state,$payload,[IO.Path]::GetTempPath())){
        $p=Full $protected
        if($runtime -ieq $p -or $runtime.StartsWith($p+'\',[StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($runtime+'\',[StringComparison]::OrdinalIgnoreCase)){throw '程序目录不能与游戏、个人状态、临时目录或安装包重叠。'}
    }
    if($runtime -ieq [IO.Path]::GetPathRoot($runtime).TrimEnd('\')){throw '不能使用磁盘根目录作为程序目录。'}
    if($runtime.Length -gt 160){throw '程序目录过长，请选择较短的目录。'}
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
        $recorded=$install.runtimeFolder -and $runtime -ieq (Full $install.runtimeFolder)
        $empty=-not(Test-Path -LiteralPath $runtime) -or @(Get-ChildItem -LiteralPath $runtime -Force).Count -eq 0
        $missingRecorded=$install.runtimeFolder -and -not(Test-Path -LiteralPath $install.runtimeFolder)
        if(-not $validRuntime -and -not $recorded -and -not($missingRecorded -and $empty)){throw '请更新原程序目录。只有原目录已丢失时，才可以选择新的空目录修复重装。'}
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
    if($steam -and (Test-Path -LiteralPath (Join-Path $steam 'config\loginusers.vdf'))){try{$logins=[IO.File]::ReadAllText((Join-Path $steam 'config\loginusers.vdf'),[Text.Encoding]::UTF8);foreach($match in [regex]::Matches($logins,'"(\d{17})"\s*\{([^{}]*)\}')){$id=[uint64]$match.Groups[1].Value-[uint64]76561197960265728;if($id -le [uint32]::MaxValue){$nick=[regex]::Match($match.Groups[2].Value,'"PersonaName"\s+"((?:\\.|[^"\\])*)"',[Text.RegularExpressions.RegexOptions]::IgnoreCase);if($nick.Success){$names[[string]$id]=[regex]::Replace($nick.Groups[1].Value,'\\([\\"])',{param($m)$m.Groups[1].Value})}}}}catch{}}
    $users=@();if($steam -and (Test-Path -LiteralPath (Join-Path $steam 'userdata'))){foreach($d in Get-ChildItem -LiteralPath (Join-Path $steam 'userdata') -Directory){if($d.Name -match '^\d+$' -and [long]$d.Name -gt 0 -and [long]$d.Name -le [uint32]::MaxValue -and (Test-Path -LiteralPath (Join-Path $d.FullName 'config\localconfig.vdf'))){$users+=@{id=$d.Name;name=$names[$d.Name];active=([string]$active -eq $d.Name)}}}}
    $runtime=if($install){$install.runtimeFolder}else{Join-Path $env:LOCALAPPDATA 'Programs\DeadlockTranslate'}
    $game=if($install){Split-Path (Split-Path (Split-Path (Split-Path $install.target -Parent) -Parent) -Parent) -Parent}elseif($games.Count -eq 1){$games[0]}else{''}
    @{games=@($games);game=$game;runtime=$runtime;recordError=$recordError;installedVersion=if($install){$install.version}else{''};installed=[bool]$install;steam=$steam;users=@($users);activeUser=[string]$active;certificate=(CertStatus);gameRunning=[bool](Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue);steamRunning=[bool](Get-Process -Name steam -ErrorAction SilentlyContinue)}
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
function SteamArgs([string]$runtime,[switch]$Apply,[switch]$Remove){
    if(-not $q.steam -or -not $q.userId -or [long]$q.userId -le 0){throw '请选择 Steam 目录和明确的用户账号；也可取消自动配置后手动粘贴启动项。'}
    $a=@('-RuntimeFolder',$runtime,'-SteamPath',$q.steam,'-UserId',[string]$q.userId)
    if($Apply){$a+='-Apply';if($q.stopSteam){$a+='-StopSteam';$a+='-RestartSteam'}}
    if($Remove){$a+='-RemoveTranslator'}elseif($q.admin){$a+='-AdministratorCompatibility'}else{$a+='-NormalPermissions'}
    return $a
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
    if(Test-Path -LiteralPath $recordFile){NoLinks $recordFile;Move-Item -LiteralPath $recordFile -Destination ($recordFile+'.invalid-'+[Guid]::NewGuid().ToString('N')+'.bak')}
    @{target=$matches[0].FullName;vpkHash=$h;version=$meta.version;runtimeFolder=$runtime;gameinfoChanged=$false;gameinfoBefore='';gameinfoAfterHash=(Hash $gi)}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $state 'install-manifest.json') -Encoding UTF8
}
function RemoveOwnedRuntime([string]$runtime,[string]$backup){
    $metaPath=Join-Path $runtime 'package-manifest.json';if(-not(Test-Path -LiteralPath $metaPath)){throw '缺少程序文件清单，保留程序目录；可人工检查后删除。'}
    NoLinks $metaPath;$meta=Get-Content -LiteralPath $metaPath -Raw -Encoding UTF8|ConvertFrom-Json;$removed=0;$kept=0
    foreach($entry in @($meta.files)+@([pscustomobject]@{file='package-manifest.json';sha256=(Hash $metaPath)})){
        $parts=$entry.file.Replace('\','/').Split('/')
        if([IO.Path]::IsPathRooted($entry.file) -or @($parts|Where-Object {$_ -in @('','.','..') -or $_.TrimEnd(' ','.') -cne $_ -or $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0}).Count){throw '文件清单有越界路径，停止清理。'}
        if($parts[-1].ToLowerInvariant() -in @('settings.json','secrets.dpapi','install-manifest.json','localhost-certificate.cer','localhost-certificate.dpapi') -or [IO.Path]::GetExtension($parts[-1]).ToLowerInvariant() -in @('.pfx','.cer','.dpapi')){$kept++;continue}
        if($parts.Count -gt 1 -and $parts[0] -notin @('data','wwwroot','LICENSES','source')){$kept++;continue}
        if($parts.Count -eq 1 -and $parts[0] -notmatch '^(DeadlockTranslate\.(exe|pdb|staticwebassets\.endpoints\.json)|dlt_translate_dir\.vpk|pairing\.key|package-manifest\.json|Install\.cmd|Uninstall\.cmd|Launch(?:-Admin)?\.cmd|Settings\.cmd|Manage-Mod\.ps1|Find-Deadlock\.ps1|Game-SearchPaths\.ps1|Install-Safety\.ps1|Vpk-Recovery\.ps1|Configure-Steam\.ps1|Export-Diagnostics\.cmd|Repair-GameConfig\.cmd|Trust-Local-Https\.(ps1|cmd)|Renew-Local-Https\.cmd|LICENSE|NOTICE|SOURCE\.txt|web\.config|README\.md|GUI-INSTALLER\.md|USER-GUIDE\.md|AI-INSTALL-GUIDE\.md|TEST-REPORT\.md|VALIDATION\.md|CHANGELOG\.md|THIRD-PARTY-NOTICES\.md|Steam-Launch-Options\.txt|使用说明-先读我\.txt|AI安装说明书\.txt)$'){$kept++;continue}
        $path=Full (Join-Path $runtime ($parts -join '\'));if(-not $path.StartsWith($runtime+'\',[StringComparison]::OrdinalIgnoreCase)){throw '清理目标超出程序目录。'};NoLinks $path
        if(Test-Path -LiteralPath $path -PathType Leaf){if((Hash $path) -ne $entry.sha256.ToLowerInvariant()){$kept++;continue};$saved=Join-Path (Join-Path $backup 'removed-runtime') ($parts -join '\');[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved))|Out-Null;[IO.File]::Copy($path,$saved);if((Hash $saved) -ne $entry.sha256.ToLowerInvariant()){throw '删除前备份校验失败，保留原程序文件。'};if((Hash $path) -ne $entry.sha256.ToLowerInvariant()){$kept++;continue};[IO.File]::Delete($path);$removed++}
    }
    return "已备份并移除 $removed 个原程序文件；保留 $kept 个不符合清单或被改动的文件。个人设置未删除。"
}
try{
    NoLinks $state;NoLinks $payload
    $result=@{}
    switch($q.action){
        'inspect'{$result=Inspect}
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
            $game=Normalize-DeadlockPath $q.game;if(-not $game){throw '请选择完整游戏目录、citadel 目录或 deadlock.exe。'};NoLinks $game
            $runtime=Full $q.runtime;NoLinks $runtime
            if($runtime -ieq [IO.Path]::GetPathRoot($runtime).TrimEnd('\') -or $runtime.StartsWith((Full $game)+'\',[StringComparison]::OrdinalIgnoreCase) -or $runtime -ieq (Full $game) -or (Full $game).StartsWith($runtime+'\',[StringComparison]::OrdinalIgnoreCase) -or $runtime -ieq (Full $state) -or $runtime.StartsWith((Full $state)+'\',[StringComparison]::OrdinalIgnoreCase) -or (Full $state).StartsWith($runtime+'\',[StringComparison]::OrdinalIgnoreCase)){throw '程序目录不能是磁盘根目录、游戏目录或个人状态目录。'}
            $decision=ResolveInstall $game $runtime;$install=$decision.install;$runtime=$decision.runtime;$q.runtime=$runtime
            ValidateTargets $game $runtime $install
            if(-not $install -and $q.mode -eq 'install' -and (Test-Path -LiteralPath $runtime) -and @(Get-ChildItem -LiteralPath $runtime -Force).Count){throw '首次安装目录必须为空。若这是旧程序，请先使用“恢复安装记录”。'}
            $conflicts=Conflicts $game $install
            $vpkState=if($install){Get-DltGameVpkState $install (Join-Path $game 'game\citadel\addons') $runtime}else{$null}
            $integrity=if($install){Get-DltRuntimeIntegrity $runtime $install}else{$null}
            $launch='"'+(Join-Path $runtime 'DeadlockTranslate.exe')+'"'+$(if($q.admin){' --admin-compatible'}else{''})+' --steam-game %command%'
            $needsRepair=$install -and (-not(Test-Path -LiteralPath (Join-Path $runtime 'DeadlockTranslate.exe')) -or -not(Test-Path -LiteralPath (Join-Path $runtime 'data\terms.json')) -or -not(Test-Path -LiteralPath (Join-Path $runtime 'dlt_translate_dir.vpk')))
            if($install -and (Test-Path -LiteralPath (Join-Path $runtime 'dlt_translate_dir.vpk')) -and (Hash (Join-Path $runtime 'dlt_translate_dir.vpk')) -ne $install.vpkHash.ToLowerInvariant()){$needsRepair=$true}
            $needsRepair=$needsRepair -or ($integrity -and $integrity.requiresRepair)
            if($decision.reinstall -and $install){$needsRepair=$true}
            $reinstallInfo=if($decision.reinstall){@{kind=$decision.reinstall.kind;originalRuntime=$decision.reinstall.originalRuntime;runtime=$runtime;message=$decision.reinstall.message;recordState=$decision.recordState}}else{$null}
            $result=@{reinstallSuggested=[bool]($q.mode -eq 'uninstall' -and -not $install);reinstall=$reinstallInfo;fullRepair=[bool]($q.mode -eq 'repair' -and ($install -or $decision.reinstall));gameVpk=$vpkState;runtimeIntegrity=$integrity;needsGameRepair=[bool]($vpkState -and $vpkState.requiresRepair);game=$game;runtime=$runtime;installed=[bool]$install;needsRepair=[bool]$needsRepair;version=if($install){$install.version}else{''};conflicts=@($conflicts);launch=$launch;gameRunning=[bool](Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue);certificate=(CertStatus)}
            $checks=@(Readiness $game $runtime $install);$result.preflight=$checks;$result.blocked=[bool]@($checks|Where-Object {$_.blocking}).Count;$result.needsReadOnlyConsent=[bool]@($checks|Where-Object {$_.requiresReadOnlyConsent}).Count
        }
        'recover'{$game=Normalize-DeadlockPath $q.game;if(-not $game){throw '游戏目录无效。'};$recovered=Recover $game (Full $q.runtime);$result=Inspect;if($recovered -and $recovered.reinstallRequired){$result.reinstallRequired=$true;$result.message=$recovered.message}}
        'apply'{
            if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw '请退出 Deadlock 后再开始；不会强制结束游戏。'}
            $game=Normalize-DeadlockPath $q.game;if(-not $game){throw '游戏目录无效。'};$runtime=Full $q.runtime;NoLinks $game;NoLinks $runtime
            $decision=ResolveInstall $game $runtime;$install=$decision.install;$runtime=$decision.runtime;$q.runtime=$runtime;$backup=Join-Path $state ('backups\gui-'+[Guid]::NewGuid().ToString('N'))
            ValidateTargets $game $runtime $install
            Emit '检查目标文件、写入权限和回退空间…';$null=Readiness $game $runtime $install -Apply
            New-Item -ItemType Directory -Force -Path $backup|Out-Null
            $disabled=@();$installedNow=$false;$coreDone=$false;$warnings=@();$optionalFailures=@();$steamDone=$false;$steamCleanup=$null;$stage=$null;$adoptedRecordHash='';$savedInvalidRecord=''
            try{
                if($q.mode -eq 'install' -or ($q.mode -eq 'repair' -and ($install -or $decision.reinstall))){
                    if(-not (CertStatus).ready){throw '请先完成第 1 步的证书确认。'}
                    Emit '校验证书私钥…';$p=Start-Process -FilePath (Join-Path $payload 'DeadlockTranslate.exe') -ArgumentList @('--prepare-https','--no-shell','--data-dir',('"'+$state+'"')) -WindowStyle Hidden -PassThru -Wait;if($p.ExitCode){throw '证书私钥无法读取，请使用原安装用户。'}
                    if(-not (CertStatus).ready){throw '证书私钥对应的证书与当前信任不一致，请返回证书步骤重新检测并确认信任。'}
                    foreach($c in @(Conflicts $game $install)){
                        if($c.kind -eq 'related'){if(-not $q.allowRelated){throw '检测到相关聊天/HUD Mod，请先在预览中明确确认共存。'};continue}
                        $approved=@($q.disable|Where-Object {$_.file -ceq $c.file -and $_.hash -ceq $c.hash});if($approved.Count -ne 1){throw ('存在未确认的冲突或不可读资源包：'+$c.file)}
                        $original=Join-Path (Join-Path $game 'game\citadel\addons') $c.file;NoLinks $original;$saved=$original+'.dlt-disabled-'+[Guid]::NewGuid().ToString('N')+'.bak';if((Hash $original) -ne $c.hash){throw '冲突包在确认后发生变化，已停止。'};Move-Item -LiteralPath $original -Destination $saved;$disabled+=@{original=$original;disabled=$saved;hash=$c.hash};$disabled|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $backup 'disabled-conflicts.json') -Encoding UTF8;Emit ('已按选择停用：'+$c.file)
                    }
                    if($decision.recordState -eq 'invalid'){
                        $recordFile=Join-Path $state 'install-manifest.json';$savedInvalidRecord=Join-Path $backup 'invalid-install-manifest.json'
                        if((Hash $recordFile) -ne $decision.recordHash){Throw-DltFault 'file-changed' 'record-rebuild' $recordFile '安装记录在检测后变化，请重新检查。'}
                        [IO.File]::Move($recordFile,$savedInvalidRecord);Write-DltInstallTrace 'record-rebuild' $recordFile 'invalid-record-backed-up' $decision.recordHash
                    }
                    if($decision.reinstall -and $install){
                        Emit '旧安装记录已丢失，将保留数据并成套重装…';$recordFile=Join-Path $state 'install-manifest.json'
                        Write-DltAtomic $recordFile ([Text.UTF8Encoding]::new($false).GetBytes(($install|ConvertTo-Json -Depth 6))) -ExpectedHash '-' -Stage 'record-rebuild';$adoptedRecordHash=Hash $recordFile
                    }
                    if($install){Emit '正在成套更新后台、设置页和 VPK…';$a=@('-PackageFolder',$payload,'-RuntimeFolder',$runtime,'-GamePath',$game,'-StateDirectory',$state,'-NonInteractive');if($q.repairMissing -or $decision.reinstall){$a+='-RepairMissingRuntime'};if($q.repairGameVpk -or $decision.reinstall){$a+='-RepairGameVpk';if($q.recoveryTarget){$a+=@('-RecoveryTarget',$q.recoveryTarget)}};Run 'Update-Mod.ps1' $a}
                    else{
                        $stage=$runtime+'.dlt-stage-'+[Guid]::NewGuid().ToString('N');NoLinks $stage
                        Emit '正在复制完整程序并安装游戏 Mod…';New-Item -ItemType Directory -Force -Path $stage|Out-Null;Copy-Item -Path (Join-Path $payload '*') -Destination $stage -Recurse
                        if(Test-Path -LiteralPath $runtime){[IO.Directory]::Delete($runtime,$false)}
                        Move-DltStagedDirectory $stage $runtime;$installedNow=$true
                        Run 'Manage-Mod.ps1' @('-Action','Install','-PackageFolder',$runtime,'-GamePath',$game,'-StateDirectory',$state,'-AllowRelated','-NonInteractive')
                    }
                    $coreDone=$true
                    if($disabled.Count){try{$m=ReadInstall;$previous=@($m.disabledConflicts|Where-Object {$_});$m|Add-Member -NotePropertyName disabledConflicts -NotePropertyValue @($previous+$disabled) -Force;$tempManifest=Join-Path $state ('install-'+[Guid]::NewGuid().ToString('N')+'.tmp');[IO.File]::WriteAllText($tempManifest,($m|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));[IO.File]::Replace($tempManifest,(Join-Path $state 'install-manifest.json'),(Join-Path $backup 'manifest-before-conflicts.json'))}catch{$warnings+='冲突包备份清单保存在本次备份目录，但未合并到安装记录。'}}
                }elseif($q.mode -eq 'repair'){
                    if($install){StopBackend $runtime};Emit '正在修复游戏搜索路径…';Run 'Manage-Mod.ps1' @('-Action','Repair','-PackageFolder',$payload,'-GamePath',$game,'-StateDirectory',$state,'-NonInteractive');$coreDone=$true
                }elseif($q.mode -eq 'uninstall'){
                    if(-not $install){throw '没有可验证的安装记录；请先恢复安装记录。'};StopBackend $runtime
                    # Do not remove files while Steam still points to the translator unless manual mode was chosen.
                    if($q.configureSteam){Emit '正在移除此 Steam 目录中指向本程序的启动前缀…';$steamCleanup=RemoveSteamPrefixes $runtime $backup;$steamDone=$true}
                    Emit '正在卸载本程序 VPK…';Run 'Manage-Mod.ps1' @('-Action','Uninstall','-PackageFolder',$runtime,'-GamePath',$game,'-StateDirectory',$state,'-NonInteractive');$coreDone=$true
                    if($q.restoreConflicts){foreach($c in @($install.disabledConflicts|Where-Object {$_})){
                        if((Full (Split-Path $c.original -Parent)) -ine (Full (Join-Path $game 'game\citadel\addons')) -or (Full (Split-Path $c.disabled -Parent)) -ine (Full (Split-Path $c.original -Parent)) -or -not $c.disabled.StartsWith($c.original+'.dlt-disabled-',[StringComparison]::OrdinalIgnoreCase)){$warnings+='旧冲突备份路径不属于当前游戏，已保留。';continue}
                        NoLinks $c.original;NoLinks $c.disabled
                        if((Test-Path -LiteralPath $c.disabled) -and -not(Test-Path -LiteralPath $c.original) -and (Hash $c.disabled) -eq $c.hash){Move-Item -LiteralPath $c.disabled -Destination $c.original}else{$warnings+='冲突备份已有变动或原路径被占用，未覆盖。'}
                    }}
                    if($q.removeCertificate){Emit '等待 Windows 确认移除本程序证书信任…';$output=& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Trust-Local-Https.ps1') -RuntimeFolder $runtime -StateDirectory $state -RemoveTrust -ConfirmTrust 2>&1;if($LASTEXITCODE){$warnings+='证书信任未移除；可稍后使用原程序的证书脚本重试。'}}
                    # Runtime files are retained intentionally so repair, diagnostics and rollback remain available.
                    if($q.removeRuntime){try{$warnings+=RemoveOwnedRuntime $runtime $backup}catch{$warnings+='程序清理未全部完成；已移除文件有备份，剩余文件保留。'}}else{$warnings+='程序文件和个人设置已保留；游戏 Mod 已卸载。可从完成页打开原目录。'}

                }else{throw '未知操作。'}
                if($q.configureSteam -and $q.mode -ne 'uninstall'){
                    try{Emit '正在正常退出 Steam、备份并配置所选账号…';Run 'Configure-Steam.ps1' (SteamArgs $runtime -Apply);$steamDone=$true}catch{$optionalFailures+=Get-DltFault $_ 'steam-configuration';$warnings+='程序操作已完成，但 Steam 启动项未配置。请复制界面给出的启动项手动设置，或重试。'}
                }
                if(-not $q.configureSteam){$warnings+=if($q.mode -eq 'uninstall'){'请从所有相关 Steam 账号手动移除翻译启动前缀，避免残留启动项。'}else{'已按选择跳过 Steam 配置，请复制启动项到 Steam 后再启动游戏。'}}
                $result=@{complete=($warnings.Count -eq 0);coreDone=$coreDone;steamDone=$steamDone;warnings=@($warnings);failures=@($optionalFailures);runtime=$runtime;backup=$backup;mode=$q.mode;reinstalled=[bool]$decision.reinstall}
            }catch{
                $originalFailure=$_
                if(-not $coreDone){foreach($c in $disabled){try{if((Test-Path -LiteralPath $c.disabled) -and -not(Test-Path -LiteralPath $c.original) -and (Hash $c.disabled) -eq $c.hash){Move-Item -LiteralPath $c.disabled -Destination $c.original}else{$warnings+='冲突包回退未完成，保留备份：'+$c.disabled}}catch{$warnings+='冲突包回退未完成，保留备份：'+$c.disabled}}
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
}
