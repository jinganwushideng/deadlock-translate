param(
    [string]$PackageFolder=$PSScriptRoot,
    [string]$RuntimeFolder,
    [string]$GamePath,
    [string]$StateDirectory=(Join-Path $env:LOCALAPPDATA 'DeadlockTranslate'),
    [string]$SteamPath,
    [switch]$NonInteractive,
    [switch]$RepairMissingRuntime,
    [switch]$AllowReadOnlyGameinfo,
    [string]$ErrorResultFile,
    [switch]$RepairGameVpk,
    [string]$RecoveryTarget,
    [string]$TraceFile,
    [switch]$ValidatePackagePaths,
    [switch]$RequireEmptyRuntime,
    [switch]$RequireStoppedBackend
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Game-SearchPaths.ps1')
. (Join-Path $PSScriptRoot 'Install-Safety.ps1')
. (Join-Path $PSScriptRoot 'Vpk-Recovery.ps1')
. (Join-Path $PSScriptRoot 'Backup-Maintenance.ps1') -StateDirectory $StateDirectory
$script:DltTraceFile=$TraceFile
$taskBackup=$null;$taskBackupLock=$null;$taskUpdateSucceeded=$false

function Full-Path([string]$path){
    $taskFull=[IO.Path]::GetFullPath($path)
    if($taskFull -eq [IO.Path]::GetPathRoot($taskFull)){return $taskFull}
    return $taskFull.TrimEnd('\','/')
}
function Is-Inside([string]$path,[string]$parent){(Full-Path $path).StartsWith((Full-Path $parent).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)}
function Hash-File([string]$path){
    $taskHasher=[Security.Cryptography.SHA256]::Create();$taskStream=[IO.File]::OpenRead($path)
    try{([BitConverter]::ToString($taskHasher.ComputeHash($taskStream))).Replace('-','').ToLowerInvariant()}finally{$taskStream.Dispose();$taskHasher.Dispose()}
}
function Hash-Bytes([byte[]]$bytes){$taskHasher=[Security.Cryptography.SHA256]::Create();try{([BitConverter]::ToString($taskHasher.ComputeHash($bytes))).Replace('-','').ToLowerInvariant()}finally{$taskHasher.Dispose()}}
function Read-Json([string]$path){Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json}
function No-Links([string]$path){
    $taskCursor=Full-Path $path
    while($taskCursor){
        if(Test-Path -LiteralPath $taskCursor){if((Get-Item -LiteralPath $taskCursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "更新目标包含链接，已停止：$taskCursor"}}
        $taskNext=[IO.Path]::GetDirectoryName($taskCursor)
        if($taskNext -eq $taskCursor){break};$taskCursor=$taskNext
    }
}
function Safe-File([string]$root,[string]$relative){
    if([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')){throw '安装包包含无效文件路径。'}
    $taskParts=$relative.Replace('\','/').Split('/')
    if(@($taskParts|Where-Object {$_ -in @('','.','..') -or $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0}).Count){throw '安装包包含越界或无效文件路径。'}
    $taskPrivate=@('settings.json','secrets.dpapi','install-manifest.json','localhost-certificate.cer','localhost-certificate.dpapi')
    if($taskParts[-1].ToLowerInvariant() -in $taskPrivate -or [IO.Path]::GetExtension($relative).ToLowerInvariant() -in @('.pfx','.dpapi','.cer')){throw '安装包包含个人配置或证书文件，已停止。'}
    if($taskParts.Length -gt 1 -and $taskParts[0] -notin @('wwwroot','data','LICENSES','source','voice')){throw '安装包包含未支持的程序目录。'}
    if($taskParts[0] -eq 'voice' -and ($taskParts -join '/') -notmatch '^voice/(DeadlockVoiceWorker\.exe|runtimes/(?:cuda/|vulkan/)?win-x64/(?:whisper|ggml(?:-(?:base|cpu|cuda|vulkan))?-whisper)\.dll)$'){throw '安装包包含未支持的语音组件文件。'}
    if($taskParts.Length -eq 1 -and $taskParts[0] -notmatch '^(DeadlockTranslate\.(exe|pdb|staticwebassets\.endpoints\.json)|dlt_translate_dir\.vpk|pairing\.key|Manage-Mod\.ps1|Configure-Steam\.ps1|Find-Deadlock\.ps1|Game-SearchPaths\.ps1|Install-Safety\.ps1|Installer-Workflow\.ps1|Installer-Transaction\.ps1|Vpk-Recovery\.ps1|Backup-Maintenance\.ps1|Repair-GameConfig\.cmd|Export-Diagnostics\.cmd|Update-Mod\.ps1|Install\.cmd|Uninstall\.cmd|Launch(?:-Admin)?\.cmd|Settings\.cmd|Update\.cmd|覆盖更新\.cmd|Trust-Local-Https\.(ps1|cmd)|Renew-Local-Https\.cmd|LICENSE|NOTICE|SOURCE\.txt|web\.config|.*\.(md|txt|json))$'){
        Throw-DltFault 'package-file-unsupported' 'package-validation' $relative ('安装包包含未支持的程序文件：'+$relative) '请使用配套的完整安装包，或导出日志联系维护者。'
    }
    $taskPath=Join-Path $root ($taskParts -join [IO.Path]::DirectorySeparatorChar)
    if(-not(Is-Inside $taskPath $root)){throw '安装包文件超出程序目录。'}
    No-Links $taskPath
    return $taskPath
}
function Replace-Bytes([string]$path,[byte[]]$bytes,[string]$ExpectedHash=''){
    Write-DltInstallTrace 'update-file' $path 'prepare'
    Write-DltAtomic $path $bytes -ExpectedHash $ExpectedHash -AllowReadOnly:($AllowReadOnlyGameinfo -and $path -ieq $taskGameinfo) -Stage 'update-file'
    Write-DltInstallTrace 'update-file' $path 'committed' '' (Get-DltHash $path)
}
function Vdf-String([string]$token){if(-not $token.StartsWith('"')){return $token};[regex]::Replace($token.Substring(1,$token.Length-2),'\\([\\"])',{param($taskMatch)$taskMatch.Groups[1].Value})}
function Steam-LaunchRuntime([string]$file){
    $taskTokens=@([regex]::Matches([IO.File]::ReadAllText($file,[Text.Encoding]::UTF8),'//[^\r\n]*|"(?:\\.|[^"\\])*"|[{}]|[^\s{}"]+') | Where-Object {$_.Value -notlike '//*'} | ForEach-Object Value)
    $taskStack=[Collections.Generic.List[string]]::new();$taskOptions=@()
    for($taskIndex=0;$taskIndex -lt $taskTokens.Count;){
        if($taskTokens[$taskIndex] -eq '}'){if($taskStack.Count -eq 0){throw 'Invalid VDF'};$taskStack.RemoveAt($taskStack.Count-1);$taskIndex++;continue}
        if($taskIndex+1 -ge $taskTokens.Count){throw 'Invalid VDF'}
        $taskName=Vdf-String $taskTokens[$taskIndex];$taskValue=$taskTokens[$taskIndex+1];$taskIndex+=2
        if($taskValue -eq '{'){$taskStack.Add($taskName.ToLowerInvariant());continue}
        if(($taskStack -join '/') -eq 'userlocalconfigstore/software/valve/steam/apps/1422450' -and $taskName -ieq 'LaunchOptions'){$taskOptions+=Vdf-String $taskValue}
    }
    if($taskOptions.Count -ne 1){return}
    $taskMatch=[regex]::Match($taskOptions[0],'^\s*"([^"]+DeadlockTranslate\.exe)"\s+(?:--admin-compatible\s+)?--steam-game\s+%command%(?:\s|$)',[Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if($taskMatch.Success){[IO.Path]::GetDirectoryName($taskMatch.Groups[1].Value)}
}
function Runtime-Matches([string]$folder,[switch]$AllowInPlace){
    try{
        if(-not $folder){return $false};$taskFolder=Full-Path $folder
        if(-not(Test-Path -LiteralPath (Join-Path $taskFolder 'DeadlockTranslate.exe')) -or -not(Test-Path -LiteralPath (Join-Path $taskFolder 'dlt_translate_dir.vpk'))){return $false}
        $taskHash=Hash-File (Join-Path $taskFolder 'dlt_translate_dir.vpk')
        return $taskHash -eq $taskInstall.vpkHash.ToLowerInvariant() -or ($AllowInPlace -and $taskFolder -ieq $PackageFolder -and $taskHash -eq $taskNewVpkHash)
    }catch{return $false}
}
function Stop-Backend([string]$folder){
    $taskOwn=@(Get-CimInstance Win32_Process -Filter "Name='DeadlockTranslate.exe'" -ErrorAction SilentlyContinue | Where-Object {$_.ExecutablePath -ieq (Join-Path $folder 'DeadlockTranslate.exe')})
    $taskPairPath=Join-Path $folder 'pairing.key'
    if(-not(Test-Path -LiteralPath $taskPairPath)){if($taskOwn.Count){throw '旧后台仍在运行，但缺少配对文件，请先手动停止。'};return}
    $taskPair=([IO.File]::ReadAllText($taskPairPath)).Trim()
    if($taskPair -notmatch '\A[0-9a-fA-F]{48}\z'){
        if($taskOwn.Count){Throw-DltFault 'runtime-pairing-invalid' 'stop-backend' $taskPairPath '配对文件损坏，无法安全请求旧后台停止。' '退出游戏，让旧后台随游戏关闭；如仍运行，请手动退出该翻译后台后成套修复。'}
        return
    }
    $taskBase='http://localhost:17865'
    $taskHealth=$null
    try{
        $taskHealth=Invoke-RestMethod ($taskBase+'/health') -TimeoutSec 2
        if($taskHealth.app -ne 'DeadlockTranslate'){if($taskOwn.Count){throw 'Local port belongs to another application.'};return}
        $taskSession=Invoke-RestMethod -Method Post -Uri ($taskBase+'/control/'+$taskPair+'/session') -TimeoutSec 2
        $taskUri=[Uri]$taskSession.url
        if($taskUri.Scheme -ne 'http' -or $taskUri.Host -ne 'localhost' -or $taskUri.Port -ne 17865 -or -not $taskUri.AbsolutePath.StartsWith('/settings/')){throw 'Invalid local settings URL'}
        $null=Invoke-WebRequest -Uri $taskUri -SessionVariable taskCookies -UseBasicParsing -TimeoutSec 3
        $null=Invoke-RestMethod -Method Post -Uri ($taskBase+'/api/stop') -WebSession $taskCookies -Headers @{Origin=$taskBase;'X-DLT-Request'='1'} -TimeoutSec 3
        foreach($taskProcess in $taskOwn){$taskLive=Get-Process -Id $taskProcess.ProcessId -ErrorAction SilentlyContinue;if($taskLive -and -not $taskLive.WaitForExit(10000)){throw 'Backend did not stop'}}
        # The stop endpoint acknowledges before its delayed shutdown completes.
        $taskDeadline=[DateTime]::UtcNow.AddSeconds(10)
        do{try{$taskStill=Invoke-RestMethod ($taskBase+'/health') -TimeoutSec 1}catch{$taskStill=$null};if(-not $taskStill){break};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $taskDeadline)
        if($taskStill){throw 'Backend did not stop'}
    }catch{if($taskOwn.Count -or ($taskHealth -and $taskHealth.app -eq 'DeadlockTranslate')){throw '旧翻译后台未能正常停止，请在设置页点击“停止后台”后重试。'}}
}
function Term-Index($items){
    $taskMap=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($taskTerm in $items){
        if(-not($taskTerm.source -is [string]) -or -not($taskTerm.target -is [string]) -or -not($taskTerm.english -is [string]) -or -not($taskTerm.ambiguous -is [bool]) -or $taskTerm.kind -notin @('name','phrase') -or $taskTerm.source.Length -notin 1..100 -or $taskTerm.target.Length -notin 1..150 -or $taskTerm.english.Length -gt 100){throw '词库格式无效，更新已停止。'}
        $taskLegacy=$taskTerm.PSObject.Properties['legacyTarget'];if($null -ne $taskLegacy -and (-not($taskLegacy.Value -is [string]) -or $taskLegacy.Value.Length -gt 150)){throw '旧译名格式无效，更新已停止。'}
        $taskStandalone=$taskTerm.PSObject.Properties['outgoingStandalone'];if($null -ne $taskStandalone -and -not($taskStandalone.Value -is [bool])){throw '昵称匹配规则格式无效，更新已停止。'}
        if($taskMap.ContainsKey($taskTerm.source)){throw '词库包含重复词条。'};$taskMap.Add($taskTerm.source,$taskTerm)
    }
    return ,$taskMap
}
function Legacy-TermValue($term){$taskProperty=$term.PSObject.Properties['legacyTarget'];if($null -eq $taskProperty){return ''};return $taskProperty.Value}
function Same-Term($left,$right){
    foreach($taskKey in @('source','target','english','kind','ambiguous')){if($left.$taskKey -cne $right.$taskKey){return $false}}
    $leftStandalone=$left.PSObject.Properties['outgoingStandalone']
    $rightStandalone=$right.PSObject.Properties['outgoingStandalone']
    if(($null -ne $leftStandalone -and $leftStandalone.Value -eq $true) -ne ($null -ne $rightStandalone -and $rightStandalone.Value -eq $true)){return $false}
    return (Legacy-TermValue $left) -ceq (Legacy-TermValue $right)
}
function Merge-Terms($old,$updated,$saved){
    $taskPrevious=Term-Index $old;$taskNew=Term-Index $updated;$taskUser=Term-Index $saved
    $taskMerged=[Collections.Generic.List[object]]::new();$taskSeen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($taskItem in $saved){
        if($taskPrevious.ContainsKey($taskItem.source) -and (Same-Term $taskItem $taskPrevious[$taskItem.source])){if(-not $taskNew.ContainsKey($taskItem.source)){continue};$taskItem=$taskNew[$taskItem.source]}
        $taskMerged.Add($taskItem);$null=$taskSeen.Add($taskItem.source)
    }
    foreach($taskItem in $updated){if(-not $taskPrevious.ContainsKey($taskItem.source) -and -not $taskSeen.Contains($taskItem.source)){$taskMerged.Add($taskItem)}}
    if($taskMerged.Count -gt 5000){throw '合并词库超过5000条，更新已停止。'}
    return ,$taskMerged.ToArray()
}

function Read-ProfileFile([string]$Path){
    No-Links $Path
    if(-not(Test-Path -LiteralPath $Path)){return @{path=$Path;bytes=$null;hash='-'}}
    $bytes=[IO.File]::ReadAllBytes($Path)
    return @{path=$Path;bytes=$bytes;hash=(Hash-Bytes $bytes)}
}
function Read-ProfileJson($File){
    [Text.Encoding]::UTF8.GetString($File.bytes).TrimStart([char]0xFEFF) | ConvertFrom-Json
}
function Read-ProfileTerms($File){
    if(-not $File.bytes){return}
    # Windows PowerShell emits a JSON array as one pipeline object.
    $rows=Read-ProfileJson $File
    foreach($row in $rows){$row}
}
function New-ProfileByteChange($File,[byte[]]$Bytes,[string]$Name){
    $hash=Hash-Bytes $Bytes
    if($hash -ne $File.hash){return @{before=$File;bytes=$Bytes;hash=$hash;name=$Name}}
}
function New-ProfileChange($File,$Value,[string]$Name){
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Value -Depth 32))
    New-ProfileByteChange $File $bytes $Name
}
function Read-ProfileSecrets($File){
    $secrets=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    if($File.hash -eq '-'){return ,$secrets}
    $plain=$null
    try{
        Add-Type -AssemblyName System.Security
        $plain=[Security.Cryptography.ProtectedData]::Unprotect($File.bytes,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
        $document=[Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json
        if($document -isnot [pscustomobject]){throw 'Invalid credentials'}
        foreach($entry in $document.PSObject.Properties){
            if($entry.Value -isnot [string]){throw 'Invalid credential value'}
            $secrets.Add($entry.Name,$entry.Value)
        }
        return ,$secrets
    }catch{
        throw '无法解密或读取大模型密钥。请使用保存该密钥的 Windows 用户切换版本。'
    }finally{
        if($null -ne $plain){[Array]::Clear($plain,0,$plain.Length)}
    }
}
function New-ProfileSecretChange($File,$Secrets){
    $plain=$null
    try{
        $plain=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Secrets -Compress))
        $encrypted=[Security.Cryptography.ProtectedData]::Protect($plain,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
        New-ProfileByteChange $File $encrypted 'secrets.dpapi'
    }catch{
        throw '无法加密保存迁移的大模型密钥。'
    }finally{
        if($null -ne $plain){[Array]::Clear($plain,0,$plain.Length)}
    }
}
function Get-LlmProfileDefaults {
    return [ordered]@{
        llmBaseUrl='https://api.deepseek.com'; llmPreset='auto'; llmCustomBaseUrl=''; model='deepseek-flash'
        llmCustomModel=''; llmCustomIncomingModel=''; llmCustomOutgoingModel=''
        llmMaxTokens=4096; llmThinkingEnabled=$true; llmThinkingApi='auto'; llmStreaming=$true
        llmIndependentProfiles=$false; incomingModel=''; outgoingModel=''; incomingThinking=$null; outgoingThinking=$null
    }
}
function Test-UnconfiguredLlmProfile($Settings){
    $defaults=Get-LlmProfileDefaults
    foreach($key in $defaults.Keys){
        $value=$Settings.PSObject.Properties[$key]
        if(-not $value){continue}
        if($key -eq 'llmPreset' -and $value.Value -eq 'deepseek'){continue}
        if($key -eq 'llmBaseUrl' -and $value.Value -in @('https://api.deepseek.com/','https://api.deepseek.com/v1','https://api.deepseek.com/v1/')){continue}
        if($value.Value -cne $defaults[$key]){return $false}
    }
    return $true
}
function Copy-LlmProfileSettings($Source,$Next,[bool]$HasSourceKey,[bool]$HasDestinationKey){
    $keys=(Get-LlmProfileDefaults).Keys
    if(-not $HasSourceKey -and -not @($keys | Where-Object {$Source.PSObject.Properties[$_]}).Count){return}
    $sourceAddress=if($Source.llmBaseUrl){$Source.llmBaseUrl}else{'https://api.deepseek.com'}
    $targetAddress=if($Next.llmBaseUrl){$Next.llmBaseUrl}else{'https://api.deepseek.com'}
    # An existing key stays with its configured provider when the source has no saved LLM credential.
    if(-not $HasSourceKey -and $HasDestinationKey -and $sourceAddress.TrimEnd('/') -cne $targetAddress.TrimEnd('/')){
        $script:DltEditionLlmProfileRetained=$true
        return
    }
    foreach($key in $keys){
        $value=$Source.PSObject.Properties[$key]
        if($value){$Next | Add-Member -NotePropertyName $key -NotePropertyValue $value.Value -Force}
        else{$Next.PSObject.Properties.Remove($key)}
    }
}
function Get-LegacyLiteLlmMigration([string]$FullDirectory,[string]$LiteDirectory){
    $sourceSecretFile=Read-ProfileFile (Join-Path $FullDirectory 'secrets.dpapi')
    if($sourceSecretFile.hash -eq '-'){return}
    $targetSettings=Read-ProfileFile (Join-Path $LiteDirectory 'settings.json')
    $next=if($targetSettings.bytes){Read-ProfileJson $targetSettings}else{
        [pscustomobject]@{mode='fast';outgoingMode='fast';outgoingSplitLong=$true;deepSeekCostPopup=$false}
    }
    if($next -isnot [pscustomobject] -or -not(Test-UnconfiguredLlmProfile $next)){return}
    $targetSecretFile=Read-ProfileFile (Join-Path $LiteDirectory 'secrets.dpapi')
    $targetSecrets=Read-ProfileSecrets $targetSecretFile
    if($targetSecrets.ContainsKey('llm')){$targetSecrets.Clear();return}
    $sourceSecrets=Read-ProfileSecrets $sourceSecretFile
    try{
        if(-not $sourceSecrets['llm']){return}
        $sourceSettings=Read-ProfileFile (Join-Path $FullDirectory 'settings.json')
        $source=if($sourceSettings.bytes){Read-ProfileJson $sourceSettings}else{[pscustomobject]@{}}
        if($source -isnot [pscustomobject]){throw '原完整版的大模型设置格式无效，无法迁移。'}
        Copy-LlmProfileSettings $source $next $true $false
        $targetSecrets['llm']=$sourceSecrets['llm']
        $changes=@(New-ProfileChange $targetSettings $next 'settings.json')
        $changes+=@(New-ProfileSecretChange $targetSecretFile $targetSecrets)
        return @{inputs=@($sourceSettings,$sourceSecretFile,$targetSettings,$targetSecretFile);changes=$changes}
    }finally{
        $sourceSecrets.Clear()
        $targetSecrets.Clear()
    }
}
function Test-ProfileShortcutOverlap([string]$First,[string]$Second){
    $left=$First -split '\+';$right=$Second -split '\+'
    if($left[-1].Trim() -ine $right[-1].Trim()){return $false}
    foreach($modifier in @('Ctrl|Control','Alt','Shift')){
        $pattern='^(L|R)?('+$modifier+')$'
        $a=@($left | Where-Object {$_.Trim() -match $pattern})
        $b=@($right | Where-Object {$_.Trim() -match $pattern})
        if(($a.Count -eq 0) -ne ($b.Count -eq 0)){return $false}
        if($a.Count -and $b.Count){
            $aSide=[regex]::Match($a[0].Trim(),$pattern,'IgnoreCase').Groups[1].Value
            $bSide=[regex]::Match($b[0].Trim(),$pattern,'IgnoreCase').Groups[1].Value
            if($aSide -and $bSide -and $aSide -ine $bSide){return $false}
        }
    }
    return $true
}
function New-SwitchedSettings($Source,$Destination,[bool]$Lite,[bool]$HasSourceKey,[bool]$HasDestinationKey){
    $sourceSettings=if($Source.bytes){Read-ProfileJson $Source}else{[pscustomobject]@{}}
    $next=if($Destination.bytes){Read-ProfileJson $Destination}else{[pscustomobject]@{}}
    if($sourceSettings -isnot [pscustomobject] -or $next -isnot [pscustomobject]){throw '聊天设置格式无效，未执行版本切换。'}
    if($Lite -and -not $Destination.bytes){
        $next=[pscustomobject]@{mode='fast';outgoingMode='fast';outgoingSplitLong=$true;deepSeekCostPopup=$false}
    }
    $keys=@('enabled','outgoingEnabled','incomingTargetLanguage','incomingCustomLanguage','incomingCustomLanguageCode',
        'outgoingTargetLanguage','outgoingCustomLanguage','outgoingCustomLanguageCode','outgoingDisplayMode','outgoingHotkey')
    foreach($key in $keys){
        $value=$sourceSettings.PSObject.Properties[$key]
        if(-not $value -or $null -eq $value.Value){continue}
        if($key -eq 'outgoingHotkey' -and -not $Lite){
            $otherKeys=@($next.quickMessages | Where-Object {$_.enabled} | ForEach-Object {$_.hotkey})
            $otherKeys+=@($next.outgoingLanguageBindings | Where-Object {$_.enabled} | ForEach-Object {$_.hotkey})
            $otherKeys+=@($next.voiceInputTeamKey,$next.voiceInputAllKey,$next.voiceInputTranslateKey,$next.voiceInputChannelKey)
            $conflicts=@($otherKeys | Where-Object {$_ -and (Test-ProfileShortcutOverlap $value.Value $_)})
            if($conflicts.Count){
                $script:DltEditionHotkeyConflict=$true
                continue
            }
        }
        $next | Add-Member -NotePropertyName $key -NotePropertyValue $value.Value -Force
    }
    Copy-LlmProfileSettings $sourceSettings $next $HasSourceKey $HasDestinationKey
    return $next
}
function Merge-FirstSwitchTerms($Active,$Inactive,$OldDefaults,$NewDefaults){
    $activeIndex=Term-Index $Active
    $previous=Term-Index $OldDefaults
    $next=Term-Index $NewDefaults
    foreach($item in $Inactive){
        $before=if($previous.ContainsKey($item.source)){$previous[$item.source]}else{$null}
        $upcoming=if($next.ContainsKey($item.source)){$next[$item.source]}else{$null}
        if(($before -and (Same-Term $item $before)) -or ($upcoming -and (Same-Term $item $upcoming))){continue}
        if($activeIndex.ContainsKey($item.source)){
            if($before -and (Same-Term $activeIndex[$item.source] $before)){$activeIndex[$item.source]=$item}
        }elseif(-not $before){$activeIndex.Add($item.source,$item)}
    }
    return ,@($activeIndex.Values)
}

try{
    $PackageFolder=Full-Path $PackageFolder;$StateDirectory=Full-Path $StateDirectory
    if($ValidatePackagePaths){
        No-Links $PackageFolder
        $entries=@((Read-Json (Join-Path $PackageFolder 'package-manifest.json')).files)
        if(-not $entries.Count -or $entries.Count -gt 2000){throw '新包文件清单无效。'}
        foreach($entry in $entries){$null=Safe-File $PackageFolder ([string]$entry.file)}
        Write-Output ('Update package paths accepted: '+$entries.Count)
        return
    }
    if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw '请先退出 Deadlock，再双击覆盖更新；不会强制关闭游戏。'}
    No-Links $PackageFolder;No-Links $StateDirectory
    if($env:DLT_INSTALL_CHILD -ne '1'){
        $taskOperationGuard=Enter-DltOperation $StateDirectory
        if(@(Get-DltPendingTransactions $StateDirectory).Count){throw '请先用图形安装器恢复未完成的操作。'}
    }
    $taskManifestPath=Join-Path $StateDirectory 'install-manifest.json'
    if(-not(Test-Path -LiteralPath $taskManifestPath)){Throw-DltFault 'record-missing-reinstall' 'record-inspection' $taskManifestPath '安装记录已丢失，需要使用完整安装器重装。' '直接运行新版完整安装器，选择安装或修复。它会保留个人设置、检查残留目录并自动重装，不必先卸载或手动恢复旧 VPK。'}
    No-Links $taskManifestPath
    $taskInitialManifestHash=Hash-File $taskManifestPath
    $taskInstall=Read-Json $taskManifestPath;$taskTarget=Full-Path $taskInstall.target
    if(-not $GamePath){$GamePath=Split-Path (Split-Path (Split-Path (Split-Path $taskTarget -Parent) -Parent) -Parent) -Parent}
    $GamePath=Full-Path $GamePath;$taskAddons=Join-Path $GamePath 'game\citadel\addons'
    if((Split-Path $taskTarget -Parent) -ine $taskAddons -or (Split-Path $taskTarget -Leaf) -notmatch '^pak\d{2,3}_dir\.vpk$' -or -not(Test-Path -LiteralPath (Join-Path $GamePath 'game\citadel\gameinfo.gi'))){throw '旧安装指向无效的游戏位置，已停止。'}
    No-Links $taskTarget
    $taskGameState=Get-DltGameVpkState $taskInstall $taskAddons $(if($RuntimeFolder){$RuntimeFolder}else{$taskInstall.runtimeFolder})
    Write-DltInstallTrace 'vpk-inspection' $taskGameState.recordedTarget $taskGameState.state $taskGameState.expectedHash $taskGameState.actualHash
    if($taskGameState.blocked){Throw-DltFault 'vpk-ambiguous' 'vpk-preflight' $taskTarget $taskGameState.message}
    if($taskGameState.requiresRepair){$RepairGameVpk=$true}
    $taskTarget=$taskGameState.target
    if($RecoveryTarget){$taskRequested=Full-Path $RecoveryTarget;No-Links $taskRequested;if((Split-Path $taskRequested -Parent) -ine $taskAddons -or (Split-Path $taskRequested -Leaf) -notmatch '^pak\d{2,3}_dir\.vpk$'){throw '恢复目标路径不安全。'}
        if($taskGameState.state -in @('missing','occupied')){if(Test-Path -LiteralPath $taskRequested){Throw-DltFault 'file-changed' 'vpk-preflight' $taskRequested '已确认的空 VPK 槽位被占用，未覆盖。'};$taskTarget=$taskRequested}elseif($taskTarget -ine $taskRequested){Throw-DltFault 'file-changed' 'vpk-preflight' $taskRequested 'VPK 在确认后再次改名，请重新检查。'}
    }
    $taskVpkExisted=Test-Path -LiteralPath $taskTarget -PathType Leaf
    $taskPackageManifest=Join-Path $PackageFolder 'package-manifest.json'
    if(Test-Path -LiteralPath $taskPackageManifest){$taskMetadata=Read-Json $taskPackageManifest;$taskEntries=@($taskMetadata.files)}else{
        $taskPackageManifest=Join-Path $PackageFolder 'build-manifest.json'
        if(-not(Test-Path -LiteralPath $taskPackageManifest)){throw '新包缺少文件清单，请完整解压官方运行包。'}
        $taskMetadata=Read-Json $taskPackageManifest;$taskEntries=@($taskMetadata.artifacts)
    }
    if($taskMetadata.version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or -not $taskEntries.Count -or $taskEntries.Count -gt 2000){throw '新包清单或版本无效。'}
    if(-not(Test-DltVersionReplacement (Get-DltInstalledEditionInfo $taskInstall $RuntimeFolder) $taskMetadata)){throw '不允许覆盖成更旧版本；请使用同版或新版安装包。'}
    $taskFiles=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $taskHashes=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($taskEntry in $taskEntries){
        $taskRelative=([string]$taskEntry.file).Replace('\','/')
        $taskSource=Safe-File $PackageFolder $taskRelative
        $taskObservedHash=if(Test-Path -LiteralPath $taskSource -PathType Leaf){Hash-File $taskSource}else{''}
        $taskValidEntry=-not $taskFiles.ContainsKey($taskRelative) -and $taskEntry.sha256 -match '\A[0-9a-fA-F]{64}\z' -and $taskObservedHash -eq $taskEntry.sha256.ToLowerInvariant()
        Write-DltInstallTrace 'package-validation' $taskSource $(if($taskValidEntry){'verified'}else{'failed'}) $taskEntry.sha256 $taskObservedHash
        if(-not $taskValidEntry){Throw-DltFault 'package-hash-mismatch' 'package-validation' $taskSource '新包文件或清单校验失败。' '请重新下载/解压完整运行包，不混用不同包的文件。'}
        $taskFiles.Add($taskRelative,$taskSource)
        $taskHashes.Add($taskRelative,$taskEntry.sha256.ToLowerInvariant())
    }
    foreach($taskRequired in @('DeadlockTranslate.exe','dlt_translate_dir.vpk','pairing.key','wwwroot/app.js','wwwroot/index.html','wwwroot/style.css','data/terms.json','Install.cmd','Manage-Mod.ps1')){if(-not $taskFiles.ContainsKey($taskRequired)){throw "新包缺少必要文件：$taskRequired"}}
    $taskNewVpkHash=Hash-File $taskFiles['dlt_translate_dir.vpk']
    if((Test-DltVpkPairing $taskFiles['dlt_translate_dir.vpk'] $taskFiles['pairing.key']) -eq $false){Throw-DltFault 'package-pairing-mismatch' 'package-validation' $PackageFolder '新运行包中的 VPK 与后台配对文件不匹配。' '请重新下载完整包，不要混用不同安装包的文件。'}
    if($taskGameState.requiresRepair -and $taskGameState.state -ne 'duplicate'){Assert-DltRecoveryConflicts $taskAddons $taskFiles['dlt_translate_dir.vpk'] $(if($taskVpkExisted){$taskTarget}else{''})}
    if(([IO.File]::ReadAllText($taskFiles['pairing.key'])).Trim() -notmatch '\A[0-9a-fA-F]{48}\z'){throw '新配对文件无效。'}
    if(-not $RuntimeFolder){
        $taskCandidates=@()
        if($taskInstall.runtimeFolder){$taskCandidates+=$taskInstall.runtimeFolder}
        foreach($taskProcess in @(Get-CimInstance Win32_Process -Filter "Name='DeadlockTranslate.exe'" -ErrorAction SilentlyContinue)){if($taskProcess.ExecutablePath){$taskCandidates+=Split-Path $taskProcess.ExecutablePath -Parent}}
        $taskSteam=$SteamPath
        if(-not $taskSteam){$taskSteam=(Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath}
        if($taskSteam){foreach($taskVdf in @(Get-ChildItem (Join-Path $taskSteam 'userdata') -Filter localconfig.vdf -File -Recurse -ErrorAction SilentlyContinue)){try{$taskCandidates+=Steam-LaunchRuntime $taskVdf.FullName}catch{}}}
        $taskMatches=@($taskCandidates|Where-Object {$_ -and (Runtime-Matches $_)}|ForEach-Object {Full-Path $_}|Select-Object -Unique)
        if($taskInstall.runtimeFolder){$RuntimeFolder=Full-Path $taskInstall.runtimeFolder}
        elseif($taskMatches.Count -eq 1){$RuntimeFolder=$taskMatches[0]}
        elseif($taskMatches.Count -eq 0 -and $taskInstall.runtimeFolder -and (Full-Path $taskInstall.runtimeFolder) -ieq $PackageFolder -and (Runtime-Matches $PackageFolder -AllowInPlace)){$RuntimeFolder=$PackageFolder}
        elseif($NonInteractive){throw '无法唯一定位旧程序目录，请提供 -RuntimeFolder。'}
        else{
            Add-Type -AssemblyName System.Windows.Forms
            $taskDialog=New-Object Windows.Forms.FolderBrowserDialog;$taskDialog.Description='请选择旧版本 DeadlockTranslate.exe 所在文件夹（用于保留 Steam 启动路径）';$taskDialog.ShowNewFolderButton=$false
            try{if($taskDialog.ShowDialog() -ne [Windows.Forms.DialogResult]::OK){throw '已取消，没有替换文件。'};$RuntimeFolder=$taskDialog.SelectedPath}finally{$taskDialog.Dispose()}
        }
    }
    $RuntimeFolder=Full-Path $RuntimeFolder;No-Links $RuntimeFolder
    if($RequireEmptyRuntime -and (([IO.File]::Exists($RuntimeFolder)) -or ([IO.Directory]::Exists($RuntimeFolder) -and @(Get-ChildItem -LiteralPath $RuntimeFolder -Force).Count))){Throw-DltFault 'file-changed' 'runtime-preflight' $RuntimeFolder '新的程序目录已出现文件，请重新预览。'}
    if(-not(Runtime-Matches $RuntimeFolder -AllowInPlace)){
        $taskRecorded=$false
        try{if([IO.Path]::IsPathRooted($taskInstall.runtimeFolder)){$taskRecorded=(Full-Path $taskInstall.runtimeFolder) -ieq $RuntimeFolder}}catch{}
        $taskEmpty=-not(Test-Path -LiteralPath $RuntimeFolder) -or @(Get-ChildItem -LiteralPath $RuntimeFolder -Force).Count -eq 0
        if($taskRecorded){$RepairMissingRuntime=$true}
        elseif(-not $RepairMissingRuntime -or -not $taskEmpty){Throw-DltFault 'runtime-location-unconfirmed' 'runtime-preflight' $RuntimeFolder '所选目录不能确认为原翻译程序目录。' '选择安装记录中的原程序位置，或原目录已丢失时选择空目录；安装器可以打开原目录供核对。'}
    }
    if($RuntimeFolder -ieq [IO.Path]::GetPathRoot($RuntimeFolder) -or $RuntimeFolder -ieq $GamePath -or $RuntimeFolder -ieq $StateDirectory -or (Is-Inside $RuntimeFolder $StateDirectory) -or (Is-Inside $StateDirectory $RuntimeFolder) -or (Is-Inside $RuntimeFolder $GamePath) -or (Is-Inside $GamePath $RuntimeFolder)){throw '旧程序目录与系统、用户状态或游戏目录重叠，已停止。'}
    $taskIntegrity=Get-DltRuntimeIntegrity $RuntimeFolder $taskInstall
    Write-DltInstallTrace 'runtime-inspection' $RuntimeFolder $(if($taskIntegrity.healthy){'verified'}else{'repair-needed'})
    $taskCritical=@($taskIntegrity.issues|Where-Object {$_.code -in @('hash-mismatch','invalid-pairing-format','pairing-vpk-mismatch','version-mismatch','record-runtime-mismatch')})
    if($taskCritical.Count -and $RuntimeFolder -ine $PackageFolder){$RepairMissingRuntime=$true;Write-Output '检测到运行文件不匹配，将从已校验的新包成套修复；个人设置、密钥、证书和自定义词库保留。'}
    if($PackageFolder -ine $RuntimeFolder -and ((Is-Inside $PackageFolder $RuntimeFolder) -or (Is-Inside $RuntimeFolder $PackageFolder))){throw '新旧目录不能互相包含，请把新包解压到独立目录。'}
    $taskLite=$taskMetadata.edition -eq 'lite'
    $taskOldPackage=Join-Path $RuntimeFolder 'package-manifest.json'
    $taskOldMetadata=$null
    if(Test-Path -LiteralPath $taskOldPackage -PathType Leaf){
        No-Links $taskOldPackage
        try{$taskOldMetadata=Read-Json $taskOldPackage}catch{}
    }
    $taskWasLite=(Get-DltInstalledEditionInfo $taskInstall $RuntimeFolder).edition -eq 'lite'
    $taskSwitchEdition=$taskWasLite -ne $taskLite
    $taskTermsDirectory=if($taskLite){Join-Path $StateDirectory 'lite'}else{$StateDirectory}
    $taskPreviousRuntime=if($taskInstall.runtimeFolder){$taskInstall.runtimeFolder}else{$RuntimeFolder}
    $taskOldDefaults=Join-Path $taskPreviousRuntime 'data\terms.json'
    $taskRetired=@()
    if($taskLite -and -not $taskWasLite){
        foreach($entry in @($taskOldMetadata.files)){
            $relative=[string]$entry.file
            if($relative -notmatch '^(?:voice/(?:DeadlockVoiceWorker\.exe|runtimes/(?:cuda/|vulkan/)?win-x64/(?:whisper|ggml(?:-(?:base|cpu|cuda|vulkan))?-whisper)\.dll)|wwwroot/(?:voice-settings\.js|voice-benchmark\.json))$' -or
               $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $taskFiles.ContainsKey($relative)){continue}
            $path=Safe-File $RuntimeFolder $relative
            if((Test-Path -LiteralPath $path -PathType Leaf) -and (Hash-File $path) -eq $entry.sha256){
                $taskRetired+=@{relative=$relative;source=$path;hash=$entry.sha256}
            }
        }
    }
    $taskGameinfo=Join-Path $GamePath 'game\citadel\gameinfo.gi';No-Links $taskGameinfo
    $taskOriginalGameinfo=[IO.File]::ReadAllBytes($taskGameinfo)
    $taskGameinfoHash=Hash-Bytes $taskOriginalGameinfo
    $taskGameinfoPlan=Get-DltSearchPathsPlan $taskOriginalGameinfo -EnsureAddons
    if($RequireStoppedBackend){Confirm-DltBackendStop @(Get-DltRunningBackends @($RuntimeFolder)) ''}
    else{Stop-Backend $RuntimeFolder}
    $taskProfileChanges=@();$taskProfileInputs=@()
    $taskDestinationTerms=Read-ProfileFile (Join-Path $taskTermsDirectory 'terms.json')
    if($taskSwitchEdition){
        $sourceDirectory=if($taskWasLite){Join-Path $StateDirectory 'lite'}else{$StateDirectory}
        $sourceSettings=Read-ProfileFile (Join-Path $sourceDirectory 'settings.json')
        $sourceTerms=Read-ProfileFile (Join-Path $sourceDirectory 'terms.json')
        $sourceSecretFile=Read-ProfileFile (Join-Path $sourceDirectory 'secrets.dpapi')
        $taskProfileInputs=@($sourceSettings,$sourceTerms,$sourceSecretFile)
        $sourceSecrets=Read-ProfileSecrets $sourceSecretFile
        $hasSourceLlm=$sourceSecrets.ContainsKey('llm')
        if($sourceSettings.bytes -or $hasSourceLlm){
            $targetSettings=Read-ProfileFile (Join-Path $taskTermsDirectory 'settings.json')
            $targetSecretFile=Read-ProfileFile (Join-Path $taskTermsDirectory 'secrets.dpapi')
            $targetSecrets=Read-ProfileSecrets $targetSecretFile
            $taskProfileInputs+=@($targetSettings,$targetSecretFile)
            $targetLlm=$targetSecrets['llm']
            $taskProfileChanges+=@(New-ProfileChange $targetSettings (New-SwitchedSettings $sourceSettings $targetSettings $taskLite $hasSourceLlm ([bool]$targetLlm)) 'settings.json')
            if($hasSourceLlm -and (-not $targetSecrets.ContainsKey('llm') -or $sourceSecrets['llm'] -cne $targetLlm)){
                $targetSecrets['llm']=$sourceSecrets['llm']
                $taskProfileChanges+=@(New-ProfileSecretChange $targetSecretFile $targetSecrets)
            }
            $targetSecrets.Clear()
        }
        $sourceSecrets.Clear()
        $oldDefaultFile=Read-ProfileFile $taskOldDefaults
        if($sourceTerms.bytes -or $oldDefaultFile.bytes){
            $active=@(if($sourceTerms.bytes){Read-ProfileTerms $sourceTerms}else{Read-ProfileTerms $oldDefaultFile})
            $oldDefaults=@(Read-ProfileTerms $oldDefaultFile)
            $newDefaults=@(Read-ProfileTerms (Read-ProfileFile $taskFiles['data/terms.json']))
            $previousEdition=if($taskWasLite){'lite'}else{'full'}
            if($taskDestinationTerms.bytes -and $taskInstall.sharedProfileEdition -ne $previousEdition){
                $active=Merge-FirstSwitchTerms $active @(Read-ProfileTerms $taskDestinationTerms) $oldDefaults $newDefaults
            }
            $merged=Merge-Terms $oldDefaults $newDefaults $active
            $taskProfileChanges+=@(New-ProfileChange $taskDestinationTerms $merged 'terms.json')
        }
    }elseif($taskDestinationTerms.bytes -and $PackageFolder -ine $RuntimeFolder -and -not $RepairMissingRuntime){
        $oldDefaultFile=Read-ProfileFile $taskOldDefaults
        if(-not $oldDefaultFile.bytes){throw '旧默认词库缺失，无法安全保留自定义词库。'}
        $merged=Merge-Terms @(Read-ProfileTerms $oldDefaultFile) @(Read-ProfileTerms (Read-ProfileFile $taskFiles['data/terms.json'])) @(Read-ProfileTerms $taskDestinationTerms)
        $taskProfileChanges+=@(New-ProfileChange $taskDestinationTerms $merged 'terms.json')
    }
    if($taskLite -and $taskWasLite -and -not $taskInstall.llmProfileMigration){
        $legacyProfile=Get-LegacyLiteLlmMigration $StateDirectory $taskTermsDirectory
        if($legacyProfile){
            $taskProfileInputs+=@($legacyProfile.inputs)
            $taskProfileChanges+=@($legacyProfile.changes)
        }
    }
    foreach($folder in @($RuntimeFolder,$StateDirectory,(Split-Path $taskTarget -Parent))){Assert-DltWritableDirectory $folder 'update-preflight'}
    $taskCheckTargets=@($taskTarget,$taskManifestPath)+@($taskRetired|ForEach-Object {$_.source})
    if($taskGameinfoPlan.Changed){$taskCheckTargets+=$taskGameinfo}
    foreach($file in $taskEntries){$taskCheckTargets+=Safe-File $RuntimeFolder $file.file}
    $taskCheckTargets+=@($taskProfileChanges | ForEach-Object {$_.before.path})
    foreach($target in $taskCheckTargets){
        $check=Get-DltFileCheck $target 'update-preflight' -AllowReadOnly:($target -ieq $taskGameinfo)
        if($check.blocking){Throw-DltCheck $check}
        if($check.requiresReadOnlyConsent -and -not $AllowReadOnlyGameinfo){Throw-DltFault 'read-only' 'update-preflight' $target '游戏配置为只读，更新尚未写入。' $check.remedy}
    }
    $taskBackupLock=Enter-DltBackupLock $StateDirectory
    $taskBackup=Join-Path $StateDirectory ('backups\update-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff')+'-'+[Guid]::NewGuid().ToString('N'))
    No-Links $taskBackup;New-Item -ItemType Directory -Path $taskBackup | Out-Null
    Set-DltBackupState $taskBackup 'in-progress'
    $taskOriginalManifest=[IO.File]::ReadAllBytes($taskManifestPath);$taskOriginalVpk=if($taskVpkExisted){[IO.File]::ReadAllBytes($taskTarget)}else{$null}
    [IO.File]::WriteAllBytes((Join-Path $taskBackup 'install-manifest-before.json'),$taskOriginalManifest);if($taskVpkExisted){[IO.File]::WriteAllBytes((Join-Path $taskBackup 'translation-before.vpk'),$taskOriginalVpk)}
    [IO.File]::WriteAllBytes((Join-Path $taskBackup 'gameinfo-before.gi'),$taskOriginalGameinfo)
    $taskGameinfoCommitted=$false
    $taskDuplicateBackups=@();$taskRetiredBackups=@()
    $taskPlan=@();$taskCommitted=[Collections.Generic.List[object]]::new();$taskVpkCommitted=$false;$taskManifestCommitted=$false
    $taskProfileCommitted=[Collections.Generic.List[object]]::new()
    $taskFiles[(Split-Path $taskPackageManifest -Leaf)]=$taskPackageManifest
    $taskHashes[(Split-Path $taskPackageManifest -Leaf)]=Hash-File $taskPackageManifest
    foreach($taskRelative in $taskFiles.Keys){
        $taskDestination=Safe-File $RuntimeFolder $taskRelative;$taskBeforeHash=$null;$taskSaved=Join-Path $taskBackup ('runtime/'+$taskRelative)
        if(Test-Path -LiteralPath $taskDestination){if($RequireEmptyRuntime){Throw-DltFault 'file-changed' 'runtime-preflight' $taskDestination '新的程序目录已出现同名文件，请重新预览。'};$taskBeforeHash=Hash-File $taskDestination;New-Item -ItemType Directory -Force -Path (Split-Path $taskSaved -Parent)|Out-Null;Copy-Item -LiteralPath $taskDestination -Destination $taskSaved;if((Hash-File $taskSaved) -ne $taskBeforeHash){throw '旧程序备份校验失败。'}}
        $taskPlan+=[pscustomobject]@{relative=$taskRelative;source=$taskFiles[$taskRelative];destination=$taskDestination;backup=$taskSaved;beforeHash=$taskBeforeHash;newHash=$taskHashes[$taskRelative]}
    }
    foreach($change in $taskProfileChanges){
        if($change.before.bytes){
            $name='user-'+[IO.Path]::GetFileNameWithoutExtension($change.name)+'-before'+[IO.Path]::GetExtension($change.name)
            [IO.File]::WriteAllBytes((Join-Path $taskBackup $name),$change.before.bytes)
        }
    }
    $taskPlan|ConvertTo-Json -Depth 4|Set-Content (Join-Path $taskBackup 'file-plan.json') -Encoding utf8
    try{
        foreach($duplicate in @($taskGameState.duplicates)){
            if(-not $duplicate){continue};No-Links $duplicate
            if((Hash-File $duplicate) -ne $taskInstall.vpkHash.ToLowerInvariant()){Throw-DltFault 'file-changed' 'duplicate-repair' $duplicate '重复翻译包在检测后变化，请重新检查。'}
            $saved=Join-Path $taskBackup ('duplicate-'+[IO.Path]::GetFileName($duplicate)+'.bak');Move-DltTrackedFile $duplicate $saved 'duplicate-repair';$taskDuplicateBackups+=@{original=$duplicate;backup=$saved;hash=$taskInstall.vpkHash.ToLowerInvariant()};Write-DltInstallTrace 'duplicate-repair' $duplicate 'backed-up-and-disabled' $taskInstall.vpkHash
        }
        if($taskGameState.state -eq 'duplicate'){Assert-DltRecoveryConflicts $taskAddons $taskFiles['dlt_translate_dir.vpk'] $taskTarget}
        if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw '游戏在更新前重新启动，已停止。'}
        if((Hash-File $taskManifestPath) -ne $taskInitialManifestHash){throw '安装记录在准备期间变化，已停止。'}
        if($taskVpkExisted){if((Hash-File $taskTarget) -ne $taskInstall.vpkHash.ToLowerInvariant()){Throw-DltFault 'file-changed' 'vpk-commit' $taskTarget '游戏 VPK 在准备期间变化，已停止。'}}elseif(Test-Path -LiteralPath $taskTarget){Throw-DltFault 'file-changed' 'vpk-commit' $taskTarget '空 VPK 槽位被占用，未覆盖。'}
        if((Hash-File $taskGameinfo) -ne $taskGameinfoHash){throw '游戏搜索路径在准备期间变化，已停止。'}
        foreach($taskStep in $taskPlan){
            if($taskStep.beforeHash){if((Hash-File $taskStep.destination) -ne $taskStep.beforeHash){throw '旧程序在准备期间变化，已停止。'}}elseif(Test-Path -LiteralPath $taskStep.destination){throw '旧目录出现同名新文件，已停止。'}
            New-Item -ItemType Directory -Force -Path (Split-Path $taskStep.destination -Parent)|Out-Null
            $taskBytes=[IO.File]::ReadAllBytes($taskStep.source)
            if((Hash-Bytes $taskBytes) -ne $taskStep.newHash){throw '新包在更新期间变化，已停止。'}
            Replace-Bytes $taskStep.destination $taskBytes $(if($taskStep.beforeHash){$taskStep.beforeHash}else{'-'});$taskCommitted.Add($taskStep)
            if((Hash-File $taskStep.destination) -ne $taskStep.newHash){throw '程序替换后校验失败。'}
        }
        foreach($old in $taskRetired){
            if((Hash-File $old.source) -ne $old.hash){throw '语音组件在切换期间被修改，请重新检查。'}
            $saved=Join-Path $taskBackup ('retired/'+$old.relative)
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $saved) | Out-Null
            No-Links $saved
            [IO.File]::Copy($old.source,$saved,$false)
            if((Hash-File $saved) -ne $old.hash){throw '语音组件备份校验失败。'}
            # The durable journal already stores the original source bytes. The
            # separate update backup is retained independently of that journal.
            Remove-DltTrackedFile $old.source $old.hash 'retire-full-edition-file'
            $taskRetiredBackups+=@{source=$old.source;backup=$saved;hash=$old.hash}
        }
        No-Links $taskAddons;[IO.Directory]::CreateDirectory($taskAddons)|Out-Null
        Write-DltAtomic $taskTarget ([IO.File]::ReadAllBytes($taskFiles['dlt_translate_dir.vpk'])) -ExpectedHash $(if($taskVpkExisted){$taskInstall.vpkHash}else{'-'}) -Stage 'game-vpk-commit';$taskVpkCommitted=$true
        Write-DltInstallTrace 'game-vpk-commit' $taskTarget 'committed' $taskInstall.vpkHash $taskNewVpkHash
        if((Hash-File $taskTarget) -ne $taskNewVpkHash){throw '游戏 VPK 替换后校验失败。'}
        if($taskGameinfoPlan.Changed){
            if((Hash-File $taskGameinfo) -ne $taskGameinfoHash){throw '游戏搜索路径在更新期间变化，已停止。'}
            Replace-Bytes $taskGameinfo $taskGameinfoPlan.Bytes;$taskGameinfoCommitted=$true
            if((Hash-File $taskGameinfo) -ne (Hash-Bytes $taskGameinfoPlan.Bytes)){throw '游戏搜索路径修复后校验失败。'}
            Update-DltGameinfoOwnership $taskInstall $taskOriginalGameinfo $taskGameinfoHash (Hash-File $taskGameinfo) $taskGameinfoPlan.AddedAddon
        }
        foreach($profileInput in $taskProfileInputs){
            if((Read-ProfileFile $profileInput.path).hash -ne $profileInput.hash){throw '个人设置、词库或密钥在安装期间变化，未迁移。'}
        }
        foreach($change in $taskProfileChanges){
            No-Links $change.before.path
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($change.before.path))|Out-Null
            Write-DltAtomic $change.before.path $change.bytes -ExpectedHash $change.before.hash -Stage 'edition-profile-commit'
            $taskProfileCommitted.Add($change)
        }
        $taskInstall.target=$taskTarget;$taskInstall.vpkHash=$taskNewVpkHash
        if($taskSwitchEdition){$taskInstall|Add-Member -NotePropertyName sharedProfileEdition -NotePropertyValue $(if($taskLite){'lite'}else{'full'}) -Force}
        if($taskLite){$taskInstall|Add-Member -NotePropertyName llmProfileMigration -NotePropertyValue $true -Force}
        $taskInstall|Add-Member -NotePropertyName releaseTag -NotePropertyValue $taskMetadata.releaseTag -Force
        $taskInstall|Add-Member -NotePropertyName edition -NotePropertyValue $(if($taskLite){'lite'}else{'full'}) -Force
        $taskInstall|Add-Member -NotePropertyName version -NotePropertyValue $taskMetadata.version -Force
        $taskInstall|Add-Member -NotePropertyName runtimeFolder -NotePropertyValue $RuntimeFolder -Force
        Replace-Bytes $taskManifestPath ([Text.UTF8Encoding]::new($false).GetBytes(($taskInstall|ConvertTo-Json -Depth 6)))
        $taskManifestCommitted=$true
        Write-Output ('覆盖更新完成：'+$taskMetadata.version)
        if($script:DltEditionHotkeyConflict){Write-Output 'dlt:edition-hotkey-conflict'}
        if($script:DltEditionLlmProfileRetained){Write-Output 'dlt:edition-llm-profile-retained'}
        if($RequireEmptyRuntime){Write-Output ('程序已恢复到本次选择的目录：'+$RuntimeFolder)}else{Write-Output ('程序目录保持不变：'+$RuntimeFolder)}
        Write-Output ('备份：'+$taskBackup)
        if($taskGameinfoPlan.Changed){Write-Output ('已修复默认按键配置的搜索路径：'+($taskGameinfoPlan.Added -join ', '))}
        Write-Output '原配置、密钥和证书保留，无需重新安装证书或改 Steam 启动路径。按原方式启动即可。'
    }catch{
        $taskFailure=$_;$taskRollbackErrors=@();$script:DltRestoring=$true
        foreach($taskStep in @($taskCommitted.ToArray())|Sort-Object -Property relative -Descending){try{if((Hash-File $taskStep.destination) -ne $taskStep.newHash){throw 'File changed after commit'};if($taskStep.beforeHash){Replace-Bytes $taskStep.destination ([IO.File]::ReadAllBytes($taskStep.backup))}elseif(Test-Path -LiteralPath $taskStep.destination){Remove-Item -LiteralPath $taskStep.destination}}catch{$taskRollbackErrors+=$taskStep.relative}}
        if($taskVpkCommitted){try{if((Hash-File $taskTarget) -ne $taskNewVpkHash){throw 'VPK changed after commit'};if($taskVpkExisted){Replace-Bytes $taskTarget $taskOriginalVpk}else{Remove-Item -LiteralPath $taskTarget};Write-DltInstallTrace 'rollback-vpk' $taskTarget 'restored'}catch{$taskRollbackErrors+='game-vpk';Write-DltInstallTrace 'rollback-vpk' $taskTarget 'failed'}}
        if($taskGameinfoCommitted){try{if((Hash-File $taskGameinfo) -ne (Hash-Bytes $taskGameinfoPlan.Bytes)){throw 'gameinfo changed after repair'};Replace-Bytes $taskGameinfo $taskOriginalGameinfo}catch{$taskRollbackErrors+='gameinfo'}}
        foreach($change in $taskProfileCommitted){
            try{
                if($change.before.bytes){Write-DltAtomic $change.before.path $change.before.bytes -ExpectedHash $change.hash -Stage 'rollback-profile'}
                elseif((Hash-File $change.before.path) -eq $change.hash){[IO.File]::Delete($change.before.path)}
                else{throw 'Profile changed after commit'}
            }catch{$taskRollbackErrors+=$change.before.path}
        }
        if($taskManifestCommitted){try{Replace-Bytes $taskManifestPath $taskOriginalManifest}catch{$taskRollbackErrors+='manifest'}}
        foreach($old in $taskRetiredBackups){
            try{
                if((Test-Path -LiteralPath $old.source) -or (Hash-File $old.backup) -ne $old.hash){throw 'Retired file or backup changed'}
                Replace-Bytes $old.source ([IO.File]::ReadAllBytes($old.backup)) '-'
            }catch{$taskRollbackErrors+=$old.source}
        }
        foreach($duplicate in $taskDuplicateBackups){try{if(Test-Path -LiteralPath $duplicate.original){throw 'Original slot changed'};if((Hash-File $duplicate.backup) -ne $duplicate.hash){throw 'Backup changed'};[IO.File]::Move($duplicate.backup,$duplicate.original);Write-DltInstallTrace 'rollback-duplicate' $duplicate.original 'restored'}catch{$taskRollbackErrors+='duplicate-vpk';Write-DltInstallTrace 'rollback-duplicate' $duplicate.original 'failed'}}
        $fault=Get-DltFault $taskFailure 'update';$fault.rollback=@($taskRollbackErrors);$fault|Add-Member -NotePropertyName backup -NotePropertyValue $taskBackup -Force;$taskFailure.Exception.Data['dltFault']=$fault
        throw $taskFailure
    }
    # Cleanup is outside the commit/rollback block. A cleanup error cannot undo an update.
    $taskUpdateSucceeded=$true
    try{
        Set-DltBackupState $taskBackup 'succeeded'
        $taskCleanup=Invoke-DltBackupCleanup $StateDirectory
        Write-DltInstallTrace 'backup-cleanup' $taskBackup ('removed-'+$taskCleanup.removedCount+'-preserved-warnings-'+@($taskCleanup.warnings).Count)
        if($taskCleanup.removedCount){Write-Output ('已清理旧更新备份：'+$taskCleanup.removedCount+' 份；保留最近两份成功备份。')}
    }catch{Write-DltInstallTrace 'backup-cleanup' $taskBackup 'preserved-cleanup-unavailable';Write-Output '更新已完成；旧备份清理未完成，可在翻译设置的“诊断与日志”中重新检查。'}
}catch{Save-DltFailure $_ $ErrorResultFile 'update';throw}
finally{
    if($taskBackup -and -not $taskUpdateSucceeded -and (Test-Path -LiteralPath $taskBackup)){try{Set-DltBackupState $taskBackup 'failed'}catch{}}
    if($taskBackupLock){$taskBackupLock.Dispose()}
    if($taskOperationGuard){$taskOperationGuard.Dispose()}
}
