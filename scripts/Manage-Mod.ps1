param([ValidateSet('Inspect','Install','Uninstall','Repair')][string]$Action='Inspect',[string]$GamePath,[switch]$AllowRelated,[string]$StateDirectory,[ValidateSet('Auto','Deep','Manual')][string]$LocateMode='Auto',[string[]]$ScanRoots,[switch]$NonInteractive,[string]$PackageFolder=$PSScriptRoot,[switch]$AllowReadOnlyGameinfo,[string]$ErrorResultFile,[string]$TraceFile)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
. (Join-Path $PSScriptRoot 'Game-SearchPaths.ps1')
. (Join-Path $PSScriptRoot 'Install-Safety.ps1')
. (Join-Path $PSScriptRoot 'Vpk-Recovery.ps1')
$script:DltTraceFile=$TraceFile
try {
if(-not $StateDirectory){$StateDirectory=Join-Path $env:LOCALAPPDATA 'DeadlockTranslate'}
if($Action -eq 'Uninstall' -and -not $GamePath -and (Test-Path -LiteralPath (Join-Path $StateDirectory 'install-manifest.json'))){$taskPrevious=Get-Content -LiteralPath (Join-Path $StateDirectory 'install-manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json;$GamePath=Split-Path (Split-Path (Split-Path (Split-Path $taskPrevious.target -Parent) -Parent) -Parent) -Parent}
if($Action -ne 'Uninstall'){
    . (Join-Path $PSScriptRoot 'Find-Deadlock.ps1')
    function Save-InstallLocationStatus([string]$Outcome){
        try{
            if((Test-Path -LiteralPath $StateDirectory) -and ((Get-Item -LiteralPath $StateDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){return}
            New-Item -ItemType Directory -Path $StateDirectory -Force|Out-Null
            $taskStatus=[ordered]@{version='0.6.25';at=[DateTime]::UtcNow.ToString('o');action=$Action;mode=$LocateMode;outcome=$Outcome;failureCode=if($Outcome -eq 'located'){'none'}else{'game-location-failed'}}
            [IO.File]::WriteAllText((Join-Path $StateDirectory 'installer-status.json'),($taskStatus|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
        }catch{}
    }
    try{
        $taskResolution=@(Resolve-DeadlockPath -Requested $GamePath -Mode $LocateMode -ScanRoots $ScanRoots -NonInteractive:$NonInteractive)
        $GamePath=$taskResolution[-1]
        if($taskResolution.Count -gt 1){$taskResolution[0..($taskResolution.Count-2)]|ForEach-Object {Write-Host $_}}
        Save-InstallLocationStatus 'located'
    }catch{Save-InstallLocationStatus 'not-found';throw}
}
$GamePath=[IO.Path]::GetFullPath($GamePath)
$taskCitadel=Join-Path $GamePath 'game\citadel'
$taskAddons=Join-Path $taskCitadel 'addons'
$taskGameinfo=Join-Path $taskCitadel 'gameinfo.gi'
if(-not(Test-Path -LiteralPath $taskGameinfo)){throw 'Invalid Deadlock folder.'}
if(-not $StateDirectory){$StateDirectory=Join-Path $env:LOCALAPPDATA 'DeadlockTranslate'}
$taskManifest=Join-Path $StateDirectory 'install-manifest.json'
$taskPackage=Join-Path $PackageFolder 'dlt_translate_dir.vpk'
function Read-VpkString($reader){$taskBytes=[Collections.Generic.List[byte]]::new();while(($taskByte=$reader.ReadByte()) -ne 0){$taskBytes.Add($taskByte)};[Text.Encoding]::UTF8.GetString($taskBytes.ToArray())}
function Read-VpkPaths([string]$path){
    $taskReader=[IO.BinaryReader]::new([IO.File]::OpenRead($path))
    try {
        if($taskReader.ReadUInt32() -ne 0x55AA1234){throw "Not a VPK: $path"}
        $taskVersion=$taskReader.ReadUInt32();$taskTreeSize=$taskReader.ReadUInt32()
        if($taskVersion -eq 2){$null=$taskReader.BaseStream.Seek(16,[IO.SeekOrigin]::Current)}elseif($taskVersion -ne 1){throw 'Unsupported VPK version'}
        while(($taskExt=Read-VpkString $taskReader) -ne ''){while(($taskDir=Read-VpkString $taskReader) -ne ''){while(($taskName=Read-VpkString $taskReader) -ne ''){
            $null=$taskReader.ReadUInt32();$taskPreload=$taskReader.ReadUInt16();$null=$taskReader.BaseStream.Seek(12+$taskPreload,[IO.SeekOrigin]::Current)
            "$taskDir/$taskName.$taskExt"
        }}}
    }finally{$taskReader.Dispose()}
}
if($Action -eq 'Uninstall'){
    if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw 'Close Deadlock before uninstalling.'}
    if(-not(Test-Path -LiteralPath $taskManifest)){throw 'No installation manifest found.'}
    $taskState=Get-Content -LiteralPath $taskManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $taskTarget=[IO.Path]::GetFullPath($taskState.target)
    if([IO.Path]::GetDirectoryName($taskTarget) -ne $taskAddons){throw 'Manifest points outside the selected addons folder.'}
    Assert-DltWritableDirectory $taskAddons 'uninstall-preflight';Assert-DltWritableDirectory $StateDirectory 'uninstall-preflight'
    Assert-DltNoLinks $taskTarget;Assert-DltNoLinks $taskManifest
    if((Test-Path -LiteralPath $taskTarget) -and (Get-DltHash $taskTarget) -ne $taskState.vpkHash.ToLowerInvariant()){throw 'Installed VPK changed; refusing to remove it.'}
    $taskRestore=$taskState.gameinfoChanged -and (Get-DltHash $taskGameinfo) -eq $taskState.gameinfoAfterHash.ToLowerInvariant()
    $taskCurrent=[IO.File]::ReadAllBytes($taskGameinfo);$taskMoved=$false;$taskRestored=$false
    $taskSaved=$taskTarget+'.dlt-uninstall-'+[Guid]::NewGuid().ToString('N')+'.bak'
    try{
        if($taskRestore){Write-DltAtomic $taskGameinfo ([Convert]::FromBase64String($taskState.gameinfoBefore)) -ExpectedHash $taskState.gameinfoAfterHash -AllowReadOnly:$AllowReadOnlyGameinfo -Stage 'uninstall-gameinfo';$taskRestored=$true}
        elseif($taskState.gameinfoChanged){Write-Warning 'Gameinfo changed after installation; preserved.'}
        if(Test-Path -LiteralPath $taskTarget){[IO.File]::Move($taskTarget,$taskSaved);$taskMoved=$true}
        Move-Item -LiteralPath $taskManifest -Destination (Join-Path $StateDirectory ('uninstalled-'+[Guid]::NewGuid().ToString('N')+'.json'))
    }catch{
        $taskFailure=$_;$taskRollback=@()
        if($taskMoved){try{if(Test-Path -LiteralPath $taskTarget){throw 'Target changed'};[IO.File]::Move($taskSaved,$taskTarget)}catch{$taskRollback+='VPK 恢复未完成：'+$taskSaved}}
        if($taskRestored){try{$taskHasher=[Security.Cryptography.SHA256]::Create();try{$taskRestoredHash=([BitConverter]::ToString($taskHasher.ComputeHash([Convert]::FromBase64String($taskState.gameinfoBefore)))).Replace('-','')}finally{$taskHasher.Dispose()};Write-DltAtomic $taskGameinfo $taskCurrent -ExpectedHash $taskRestoredHash -AllowReadOnly:$AllowReadOnlyGameinfo -Stage 'rollback-gameinfo'}catch{$taskRollback+='游戏配置回退未完成；原记录已保留。'}}
        $taskFault=Get-DltFault $taskFailure 'uninstall';$taskFault.rollback=@($taskRollback);$taskFailure.Exception.Data['dltFault']=$taskFault;throw $taskFailure
    }
    Write-Output 'Uninstalled only this translation VPK. Settings and other mods are preserved.'
    exit 0
}
if($Action -eq 'Repair'){
    if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw 'Close Deadlock before repairing game configuration.'}
    $taskCursor=$taskGameinfo
    while($taskCursor){if((Get-Item -LiteralPath $taskCursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked game path; no changes made.'};$taskCursor=[IO.Path]::GetDirectoryName($taskCursor)}
    $taskBefore=[IO.File]::ReadAllBytes($taskGameinfo);$taskBeforeHash=(Get-FileHash -LiteralPath $taskGameinfo).Hash
    $taskPathPlan=Get-DltSearchPathsPlan $taskBefore -EnsureAddons
    if(-not $taskPathPlan.Changed){Write-Output 'SearchPaths already contains the required Game/Mod/Write paths; no changes made.';exit 0}
    $taskBackup=$taskGameinfo+'.dlt-before-repair-'+[Guid]::NewGuid().ToString('N')+'.bak'
    $taskTemporary=$taskGameinfo+'.dlt-repair-'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try{
        Assert-DltWritableDirectory $taskCitadel 'repair-preflight'
        Write-DltAtomic $taskGameinfo $taskPathPlan.Bytes -ExpectedHash $taskBeforeHash -Backup $taskBackup -AllowReadOnly:$AllowReadOnlyGameinfo -Stage 'repair-gameinfo'
        if(Test-Path -LiteralPath $taskManifest){
            try{$taskState=Get-Content -LiteralPath $taskManifest -Raw -Encoding UTF8|ConvertFrom-Json
                if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($taskState.target)) -ieq $taskAddons){
                    Update-DltGameinfoOwnership $taskState $taskBefore $taskBeforeHash (Get-FileHash -LiteralPath $taskGameinfo).Hash $taskPathPlan.AddedAddon
                    $taskMetadataTemp=$taskManifest+'.repair-'+[Guid]::NewGuid().ToString('N')+'.tmp'
                    try{
                        [IO.File]::WriteAllText($taskMetadataTemp,($taskState|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
                        [IO.File]::Replace($taskMetadataTemp,$taskManifest,$taskManifest+'.before-repair-'+[Guid]::NewGuid().ToString('N')+'.bak')
                    }finally{if(Test-Path -LiteralPath $taskMetadataTemp){Remove-Item -LiteralPath $taskMetadataTemp}}
                }
            }catch{Write-Warning 'SearchPaths repaired; metadata update failed. The gameinfo backup is retained.'}
        }
        Write-Output ('Repaired SearchPaths: '+($taskPathPlan.Added -join ', '));Write-Output ('Backup: '+$taskBackup)
    }finally{if(Test-Path -LiteralPath $taskTemporary){Remove-Item -LiteralPath $taskTemporary}}
    exit 0
}
if(-not(Test-Path -LiteralPath $taskPackage)){throw 'Place this script next to dlt_translate_dir.vpk.'}
$taskOwnPaths=@(Read-VpkPaths $taskPackage)
$taskExact=@();$taskRelated=@()
foreach($taskFile in @(Get-ChildItem -LiteralPath $taskAddons -Filter '*.vpk' -File -ErrorAction SilentlyContinue)){
    $taskPaths=@(Read-VpkPaths $taskFile.FullName)
    if(Test-DltQolPackage $taskPaths){Write-DltInstallTrace 'conflict-scan' $taskFile.FullName 'qol-ignored';continue}
    $taskCollisions=@($taskPaths | Where-Object {$_ -in $taskOwnPaths})
    if($taskCollisions.Count){$taskExact+=[pscustomobject]@{File=$taskFile.Name;Paths=($taskCollisions -join ', ')}}
    elseif(@($taskPaths | Where-Object {$_ -match 'chattranslat|chat_translat|top_bar_chat\.vcss'}).Count){$taskRelated+=$taskFile.Name}
}
Write-Output "Deadlock: $GamePath"
if($taskExact.Count){$taskExact | Format-Table -Wrap; if($Action -eq 'Install'){throw 'Overlapping chat resources detected. Resolve the listed mods before installing.'}}
if($taskRelated.Count){Write-Output ('Related chat/HUD mods: '+($taskRelated -join ', '))}
if($Action -eq 'Inspect'){
    $taskCheck=Get-DltSearchPathsPlan ([IO.File]::ReadAllBytes($taskGameinfo)) -EnsureAddons
    if($taskCheck.Changed){Write-Output ('Missing search paths: '+($taskCheck.Added -join ', '))}
    else{Write-Output 'Default keybinding search paths are configured.'}
    Write-Output 'Read-only inspection complete.';exit 0
}
if(Test-Path -LiteralPath $taskManifest){throw 'Already installed. Exit Deadlock and run the external updater next to the new runtime ZIP; no uninstall is needed.'}
if($taskRelated.Count -and -not $AllowRelated){
    Write-Output 'Related mods may already translate chat or restyle bubbles. This installer preserves them.'
    $taskAnswer=Read-Host 'Keep these mods and install the additional translator? Type YES to continue'
    if($taskAnswer -cne 'YES'){Write-Output 'No files changed.';exit 2}
}
if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw 'Close Deadlock before installing.'}
Assert-DltWritableDirectory $StateDirectory 'install-preflight';Assert-DltWritableDirectory $taskAddons 'install-preflight'
New-Item -ItemType Directory -Force -Path $StateDirectory,$taskAddons | Out-Null
$taskTarget=$null
foreach($taskIndex in 90..199){$taskCandidate=Join-Path $taskAddons ('pak{0:D2}_dir.vpk' -f $taskIndex);if(-not(Test-Path -LiteralPath $taskCandidate)){$taskTarget=$taskCandidate;break}}
if(-not $taskTarget){throw 'No unused VPK slot found.'}
$taskBefore=[IO.File]::ReadAllBytes($taskGameinfo)
$taskBeforeHash=Get-DltHash $taskGameinfo
$taskPathPlan=Get-DltSearchPathsPlan $taskBefore -EnsureAddons
$taskChanged=$taskPathPlan.Changed
$taskInstalledVersion=$null
foreach($taskMetadataName in @('package-manifest.json','build-manifest.json')){if(Test-Path -LiteralPath (Join-Path $PackageFolder $taskMetadataName)){$taskInstalledVersion=(Get-Content -LiteralPath (Join-Path $PackageFolder $taskMetadataName) -Raw -Encoding UTF8|ConvertFrom-Json).version;break}}
$taskState=[ordered]@{target=$taskTarget;vpkHash=(Get-FileHash -LiteralPath $taskPackage).Hash;version=$taskInstalledVersion;runtimeFolder=[IO.Path]::GetFullPath($PackageFolder);gameinfoChanged=($taskChanged -and $taskPathPlan.AddedAddon);gameinfoBefore=[Convert]::ToBase64String($taskBefore);gameinfoAfterHash=$null}
$taskVpkCommitted=$false;$taskGiCommitted=$false
$taskBackup=$taskGameinfo+'.dlt-before-install-'+[Guid]::NewGuid().ToString('N')+'.bak'
try{
    $taskCheck=Get-DltFileCheck $taskGameinfo 'install-gameinfo' -AllowReadOnly
    if($taskChanged -and $taskCheck.requiresReadOnlyConsent -and -not $AllowReadOnlyGameinfo){Throw-DltFault 'read-only' 'install-gameinfo' $taskGameinfo '游戏配置为只读；未开始写入。' '在检查页确认临时解除只读，或手动检查该文件属性。'}
    if($taskChanged -and $taskCheck.blocking){Throw-DltFault $taskCheck.code 'install-gameinfo' $taskGameinfo $taskCheck.message $taskCheck.remedy}
    Write-DltAtomic $taskTarget ([IO.File]::ReadAllBytes($taskPackage)) -ExpectedHash '-' -Stage 'install-vpk';$taskVpkCommitted=$true
    if($taskChanged){Write-DltAtomic $taskGameinfo $taskPathPlan.Bytes -ExpectedHash $taskBeforeHash -Backup $taskBackup -AllowReadOnly:$AllowReadOnlyGameinfo -Stage 'install-gameinfo';$taskGiCommitted=$true}
    $taskState.gameinfoAfterHash=(Get-FileHash -LiteralPath $taskGameinfo).Hash
    Write-DltAtomic $taskManifest ([Text.UTF8Encoding]::new($false).GetBytes(($taskState|ConvertTo-Json -Depth 3))) -ExpectedHash '-' -Stage 'install-record'
}catch{
    $taskFailure=$_;$taskRollback=@()
    if($taskGiCommitted){try{Write-DltAtomic $taskGameinfo $taskBefore -ExpectedHash $taskState.gameinfoAfterHash -AllowReadOnly:$AllowReadOnlyGameinfo -Stage 'rollback-gameinfo'}catch{$taskRollback+='游戏配置无法回退；原文件备份：'+$taskBackup}}
    if($taskVpkCommitted){try{if((Get-DltHash $taskTarget) -ne $taskState.vpkHash.ToLowerInvariant()){throw 'VPK changed'};Remove-Item -LiteralPath $taskTarget}catch{$taskRollback+='VPK 回退未完成：'+$taskTarget}}
    $taskFault=Get-DltFault $taskFailure 'install';$taskFault.rollback=@($taskRollback);$taskFailure.Exception.Data['dltFault']=$taskFault;throw $taskFailure
}
Write-Output "Installed: $taskTarget"
Write-Output 'Use Launch.cmd to start the translator and Deadlock, or Settings.cmd for browser settings.'

}catch{Save-DltFailure $_ $ErrorResultFile;throw}
