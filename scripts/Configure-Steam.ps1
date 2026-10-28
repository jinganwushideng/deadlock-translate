param(
    [string]$RuntimeFolder=$PSScriptRoot,
    [string]$SteamPath,
    [uint32]$UserId,
    [switch]$Apply,
    [switch]$StopSteam,
    [switch]$RestartSteam,
    [switch]$AdministratorCompatibility,
    [switch]$NormalPermissions,
    [switch]$RemoveTranslator,
    [string]$ResultFile,
    [string]$ErrorResultFile,
    [switch]$PreviewRuntime
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Install-Safety.ps1')
try {

function Launch-Hash([byte[]]$bytes){$taskHasher=[Security.Cryptography.SHA256]::Create();try{([BitConverter]::ToString($taskHasher.ComputeHash($bytes))).Replace('-','').ToLowerInvariant()}finally{$taskHasher.Dispose()}}
function Launch-Decode([string]$token){if(-not $token.StartsWith('"')){return $token};[regex]::Replace($token.Substring(1,$token.Length-2),'\\([\\"])',{param($taskMatch)$taskMatch.Groups[1].Value})}
function Launch-Read([string]$path){
    $taskRaw=[IO.File]::ReadAllBytes($path);$taskBom=$taskRaw.Length -ge 3 -and $taskRaw[0] -eq 239 -and $taskRaw[1] -eq 187 -and $taskRaw[2] -eq 191
    $taskOffset=if($taskBom){3}else{0}
    [pscustomobject]@{bytes=$taskRaw;bom=$taskBom;text=[Text.UTF8Encoding]::new($false,$true).GetString($taskRaw,$taskOffset,$taskRaw.Length-$taskOffset)}
}
function Launch-Locate([string]$text){
    $taskTokens=@([regex]::Matches($text,'//[^\r\n]*|"(?:\\.|[^"\\])*"|[{}]|[^\s{}"]+')|Where-Object {$_.Value -notlike '//*'})
    $taskFrames=[Collections.Generic.List[object]]::new();$taskFound=[Collections.Generic.List[object]]::new()
    for($taskIndex=0;$taskIndex -lt $taskTokens.Count;){
        $taskKey=$taskTokens[$taskIndex]
        if($taskKey.Value -eq '}'){
            if(-not $taskFrames.Count){throw 'Invalid VDF closing brace'}
            $taskFrame=$taskFrames[$taskFrames.Count-1];$taskFrames.RemoveAt($taskFrames.Count-1)
            if($taskFrame.path -eq 'userlocalconfigstore/software/valve/steam/apps/1422450'){$taskFound.Add([pscustomobject]@{fields=$taskFrame.fields;end=$taskKey})}
            $taskIndex++;continue
        }
        if($taskKey.Value -eq '{' -or $taskIndex+1 -ge $taskTokens.Count){throw 'Invalid VDF key/value'}
        $taskValue=$taskTokens[$taskIndex+1];$taskIndex+=2;$taskName=(Launch-Decode $taskKey.Value).ToLowerInvariant()
        if($taskValue.Value -eq '{'){
            $taskParent=if($taskFrames.Count){$taskFrames[$taskFrames.Count-1].path+'/'}else{''}
            $taskFrames.Add([pscustomobject]@{path=$taskParent+$taskName;fields=[Collections.Generic.List[object]]::new()});continue
        }
        if($taskValue.Value -eq '}' -or -not $taskFrames.Count){throw 'Invalid VDF value'}
        $taskFrames[$taskFrames.Count-1].fields.Add([pscustomobject]@{name=$taskName;token=$taskValue})
    }
    if($taskFrames.Count){throw 'Unclosed VDF block'}
    if($taskFound.Count -ne 1){Throw-DltFault 'steam-no-game-record' 'steam-preflight' $taskFile '所选 Steam 账号没有唯一的 Deadlock（1422450）记录。' '核对所选账号；必要时在该账号启动一次游戏后重试，或复制启动项到 Steam 属性页。不要创建猜测的 Apps 区块。'}
    $taskLaunch=@($taskFound[0].fields|Where-Object name -eq 'launchoptions')
    if($taskLaunch.Count -gt 1){throw 'Duplicate LaunchOptions; refusing automatic edit'}
    return [pscustomobject]@{value=if($taskLaunch.Count){$taskLaunch[0].token}else{$null};end=$taskFound[0].end}
}
function Launch-Plan([string]$text,[string]$executable){
    $taskBlock=Launch-Locate $text;$taskBefore=if($taskBlock.value){Launch-Decode $taskBlock.value.Value}else{''}
    if($AdministratorCompatibility -and $NormalPermissions){throw 'Choose either AdministratorCompatibility or NormalPermissions'}
    $taskAdmin=[bool]$AdministratorCompatibility;$taskExtra=$taskBefore
    if($taskBefore -match '%command%'){
        $taskOld=[regex]::Match($taskBefore,'^\s*"([^"\r\n]+DeadlockTranslate\.exe)"\s+(?:(--admin-compatible)\s+)?--steam-game\s+%command%(?:\s+(.*))?\s*$',[Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if($RemoveTranslator -and -not $taskOld.Success -and $taskBefore.IndexOf($executable,[StringComparison]::OrdinalIgnoreCase) -lt 0){return [pscustomobject]@{before=$taskBefore;after=$taskBefore;text=$text}}
        if(-not $taskOld.Success -or ([regex]::Matches($taskBefore,'%command%',[Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count -ne 1){throw 'Another launch wrapper exists; do not overwrite it automatically'}
        $taskExtra=$taskOld.Groups[3].Value
        if(-not $NormalPermissions -and $taskOld.Groups[2].Success){$taskAdmin=$true}
        if($RemoveTranslator -and $taskOld.Groups[1].Value -ine $executable){return [pscustomobject]@{before=$taskBefore;after=$taskBefore;text=$text}}
    }
    elseif($RemoveTranslator){return [pscustomobject]@{before=$taskBefore;after=$taskBefore;text=$text}}
    $taskPrefix='"'+$executable+'"'+$(if($taskAdmin){' --admin-compatible'}else{''})+' --steam-game %command%'
    $taskAfter=$taskPrefix+$(if($taskExtra){' '+$taskExtra}else{''})
    if($RemoveTranslator){$taskAfter=$taskExtra}
    if($taskBefore -ceq $taskAfter){return [pscustomobject]@{before=$taskBefore;after=$taskAfter;text=$text}}
    $taskEncoded='"'+$taskAfter.Replace('\','\\').Replace('"','\"')+'"'
    if($taskBlock.value){$taskUpdated=$text.Substring(0,$taskBlock.value.Index)+$taskEncoded+$text.Substring($taskBlock.value.Index+$taskBlock.value.Length)}else{
        $taskEnd=$taskBlock.end.Index;$taskLine=$text.LastIndexOf("`n",$taskEnd)+1;$taskIndent=$text.Substring($taskLine,$taskEnd-$taskLine)
        if($taskIndent -match '^\s*$'){$taskNewline=if($text.Contains("`r`n")){"`r`n"}else{"`n"};$taskInsert=$taskIndent+"`t"+'"LaunchOptions"'+"`t`t"+$taskEncoded+$taskNewline;$taskUpdated=$text.Substring(0,$taskLine)+$taskInsert+$text.Substring($taskLine)}else{$taskUpdated=$text.Substring(0,$taskEnd)+' "LaunchOptions" '+$taskEncoded+' '+$text.Substring($taskEnd)}
    }
    $taskChecked=Launch-Locate $taskUpdated
    if(-not $taskChecked.value -or (Launch-Decode $taskChecked.value.Value) -cne $taskAfter){throw 'VDF plan validation failed'}
    [pscustomobject]@{before=$taskBefore;after=$taskAfter;text=$taskUpdated}
}
function Launch-SteamRunning([string]$root){
    $taskProcesses=@(Get-CimInstance Win32_Process -Filter "Name='steam.exe'" -ErrorAction Stop)
    if(@($taskProcesses|Where-Object {-not $_.ExecutablePath}).Count){throw 'Cannot identify a running Steam process; exit Steam manually'}
    return [bool]@($taskProcesses|Where-Object {$_.ExecutablePath -ieq (Join-Path $root 'Steam.exe')}).Count
}
function Launch-NoLinks([string]$path){
    $taskCursor=[IO.Path]::GetFullPath($path)
    while($taskCursor){if((Test-Path -LiteralPath $taskCursor) -and ((Get-Item -LiteralPath $taskCursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Configuration path contains a link; refusing edit'};$taskCursor=[IO.Path]::GetDirectoryName($taskCursor)}
}

if(-not $SteamPath){$SteamPath=(Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath}
$SteamPath=[IO.Path]::GetFullPath($SteamPath).TrimEnd('\','/');$RuntimeFolder=[IO.Path]::GetFullPath($RuntimeFolder).TrimEnd('\','/')
$taskExecutable=Join-Path $RuntimeFolder 'DeadlockTranslate.exe'
if($PreviewRuntime -and $Apply){throw 'PreviewRuntime cannot apply changes'}
if(-not $RemoveTranslator -and -not $PreviewRuntime -and -not(Test-Path -LiteralPath $taskExecutable -PathType Leaf)){throw 'RuntimeFolder must contain the extracted DeadlockTranslate.exe'}
if(-not $UserId){
    $taskUser=[long](Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction Stop).ActiveUser
    if($taskUser -lt 0){$taskUser+=4294967296}
    if($taskUser -le 0){throw 'ActiveUser is zero. Supply the account ID captured before Steam exit via -UserId; do not guess another userdata folder.'}
    $UserId=[uint32]$taskUser
}
$taskFile=Join-Path $SteamPath ('userdata\'+$UserId+'\config\localconfig.vdf');Launch-NoLinks $taskFile
if(-not(Test-Path -LiteralPath $taskFile -PathType Leaf)){throw 'This UserId has no localconfig.vdf; confirm the account'}
$taskOriginal=Launch-Read $taskFile;$taskPlan=Launch-Plan $taskOriginal.text $taskExecutable
$taskChanged=$taskPlan.text -cne $taskOriginal.text;$taskBackup=$null;$taskStopped=$false;$taskRestarted=$false
if($Apply -and $taskChanged){
    if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw 'Exit Deadlock before changing the launch wrapper'}
    if(Launch-SteamRunning $SteamPath){
        if(-not $StopSteam){throw 'Steam is running. Exit it normally, or supply -StopSteam to request Steam.exe -shutdown'}
        $taskSteamExe=Join-Path $SteamPath 'Steam.exe'
        $null=Start-Process -FilePath $taskSteamExe -ArgumentList '-shutdown' -WindowStyle Hidden -PassThru
        $taskDeadline=[DateTime]::UtcNow.AddSeconds(25)
        do{if(-not(Launch-SteamRunning $SteamPath)){break};Start-Sleep -Milliseconds 250}while([DateTime]::UtcNow -lt $taskDeadline)
        if(Launch-SteamRunning $SteamPath){throw 'Steam did not exit normally; no VDF changed. Do not force-kill it.'}
        $taskStopped=$true
    }
    # Normal Steam exit can flush settings. Build the plan again from those final bytes.
    $taskOriginal=Launch-Read $taskFile;$taskPlan=Launch-Plan $taskOriginal.text $taskExecutable
    $taskChanged=$taskPlan.text -cne $taskOriginal.text
    if($taskChanged){
        $taskBackupRoot=Join-Path $env:LOCALAPPDATA 'DeadlockTranslate\backups\steam-launch';Launch-NoLinks $taskBackupRoot
        New-Item -ItemType Directory -Path $taskBackupRoot -Force|Out-Null
        $taskBackup=Join-Path $taskBackupRoot ('localconfig-'+$UserId+'-'+[Guid]::NewGuid().ToString('N')+'.vdf')
        [IO.File]::WriteAllBytes($taskBackup,$taskOriginal.bytes)
        [byte[]]$taskEncoded=[Text.UTF8Encoding]::new($false).GetBytes($taskPlan.text)
        if($taskOriginal.bom){$taskEncoded=[byte[]](@(239,187,191)+$taskEncoded)}
        $taskTemporary=$taskFile+'.dlt-launch-'+[Guid]::NewGuid().ToString('N')+'.tmp'
        try{
            if((Launch-SteamRunning $SteamPath) -or (Launch-Hash ([IO.File]::ReadAllBytes($taskFile))) -ne (Launch-Hash $taskOriginal.bytes)){throw 'Steam or configuration changed during preparation; no write performed'}
            [IO.File]::WriteAllBytes($taskTemporary,$taskEncoded)
            [IO.File]::Replace($taskTemporary,$taskFile,[NullString]::Value)
            if((Launch-Hash ([IO.File]::ReadAllBytes($taskFile))) -ne (Launch-Hash $taskEncoded)){throw 'Persisted VDF hash mismatch'}
        }finally{if(Test-Path -LiteralPath $taskTemporary){Remove-Item -LiteralPath $taskTemporary}}
    }
    if($RestartSteam -and $taskStopped){$null=Start-Process -FilePath (Join-Path $SteamPath 'Steam.exe') -WindowStyle Hidden -PassThru;$taskRestarted=$true}
}
$taskFinal=Launch-Read $taskFile;$taskFinalBlock=Launch-Locate $taskFinal.text
$taskActual=if($taskFinalBlock.value){Launch-Decode $taskFinalBlock.value.Value}else{''}
if($Apply -and $taskActual -cne $taskPlan.after){throw 'LaunchOptions read-back mismatch'}
$taskResult=[ordered]@{userId=$UserId;file=$taskFile;before=$taskPlan.before;after=$taskPlan.after;actual=$taskActual;applied=[bool]$Apply;changed=([bool]$Apply -and $taskChanged);alreadyConfigured=(-not $taskChanged);backup=$taskBackup;steamStoppedNormally=$taskStopped;steamRestartRequested=$taskRestarted}
$taskJson=$taskResult|ConvertTo-Json -Depth 3
if($ResultFile){$taskOutput=[IO.Path]::GetFullPath($ResultFile);if($taskOutput -ieq $taskFile -or $taskOutput -ieq $taskExecutable){throw 'ResultFile overlaps protected input'};[IO.File]::WriteAllText($taskOutput,$taskJson,[Text.UTF8Encoding]::new($true))}
Write-Output $taskJson
}catch{Save-DltFailure $_ $ErrorResultFile 'steam-configuration' $taskFile;throw}
