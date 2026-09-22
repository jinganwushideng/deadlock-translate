param([ValidateSet('Inspect','Install','Uninstall')][string]$Action='Inspect',[string]$GamePath,[switch]$AllowRelated,[string]$StateDirectory)
$ErrorActionPreference='Stop'
if (-not $GamePath) {
    $taskSteam=(Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    $taskLibraries=@($taskSteam)
    if($taskSteam -and (Test-Path (Join-Path $taskSteam 'steamapps\libraryfolders.vdf'))) {
        $taskLibraryText=Get-Content -LiteralPath (Join-Path $taskSteam 'steamapps\libraryfolders.vdf') -Raw
        $taskLibraries+=@([regex]::Matches($taskLibraryText,'"path"\s+"([^"]+)"') | ForEach-Object {$_.Groups[1].Value.Replace('\\','\')})
    }
    $taskMatches=@($taskLibraries | Where-Object {$_} | Select-Object -Unique | ForEach-Object {Join-Path $_ 'steamapps\common\Deadlock'} | Where-Object {Test-Path (Join-Path $_ 'game\citadel\gameinfo.gi')})
    if($taskMatches.Count -ne 1){throw 'Cannot uniquely locate Deadlock. Supply -GamePath.'}
    $GamePath=$taskMatches[0]
}
$GamePath=[IO.Path]::GetFullPath($GamePath)
$taskCitadel=Join-Path $GamePath 'game\citadel'
$taskAddons=Join-Path $taskCitadel 'addons'
$taskGameinfo=Join-Path $taskCitadel 'gameinfo.gi'
if(-not(Test-Path -LiteralPath $taskGameinfo)){throw 'Invalid Deadlock folder.'}
if(-not $StateDirectory){$StateDirectory=Join-Path $env:LOCALAPPDATA 'DeadlockTranslate'}
$taskManifest=Join-Path $StateDirectory 'install-manifest.json'
$taskPackage=Join-Path $PSScriptRoot 'dlt_translate_dir.vpk'
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
    if(-not(Test-Path -LiteralPath $taskManifest)){throw 'No installation manifest found.'}
    $taskState=Get-Content -LiteralPath $taskManifest -Raw | ConvertFrom-Json
    $taskTarget=[IO.Path]::GetFullPath($taskState.target)
    if([IO.Path]::GetDirectoryName($taskTarget) -ne $taskAddons){throw 'Manifest points outside the selected addons folder.'}
    if(Test-Path -LiteralPath $taskTarget){
        if((Get-FileHash -LiteralPath $taskTarget).Hash -ne $taskState.vpkHash){throw 'Installed VPK changed; refusing to remove it.'}
        Remove-Item -LiteralPath $taskTarget
    }
    if($taskState.gameinfoChanged){
        if((Get-FileHash -LiteralPath $taskGameinfo).Hash -eq $taskState.gameinfoAfterHash){[IO.File]::WriteAllBytes($taskGameinfo,[Convert]::FromBase64String($taskState.gameinfoBefore))}
        else{Write-Warning 'Gameinfo changed after installation. It was preserved; saved backup remains in install-manifest.json.'}
    }
    Move-Item -LiteralPath $taskManifest -Destination (Join-Path $StateDirectory ('uninstalled-'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')+'.json'))
    Write-Output 'Uninstalled only this translation VPK. Settings and other mods are preserved.'
    exit 0
}
if(-not(Test-Path -LiteralPath $taskPackage)){throw 'Place this script next to dlt_translate_dir.vpk.'}
$taskOwnPaths=@(Read-VpkPaths $taskPackage)
$taskExact=@();$taskRelated=@()
foreach($taskFile in @(Get-ChildItem -LiteralPath $taskAddons -Filter '*.vpk' -File -ErrorAction SilentlyContinue)){
    $taskPaths=@(Read-VpkPaths $taskFile.FullName)
    $taskCollisions=@($taskPaths | Where-Object {$_ -in $taskOwnPaths})
    if($taskCollisions.Count){$taskExact+=[pscustomobject]@{File=$taskFile.Name;Paths=($taskCollisions -join ', ')}}
    elseif(@($taskPaths | Where-Object {$_ -match 'chattranslat|chat_translat|top_bar_chat\.vcss'}).Count){$taskRelated+=$taskFile.Name}
}
Write-Output "Deadlock: $GamePath"
if($taskExact.Count){$taskExact | Format-Table -Wrap; if($Action -eq 'Install'){throw 'Overlapping chat resources detected. Resolve the listed mods before installing.'}}
if($taskRelated.Count){Write-Output ('Related chat/HUD mods: '+($taskRelated -join ', '))}
if($Action -eq 'Inspect'){Write-Output 'Read-only inspection complete.';exit 0}
if(Test-Path -LiteralPath $taskManifest){throw 'An installation manifest exists. Uninstall the previous package before installing again.'}
if($taskRelated.Count -and -not $AllowRelated){
    Write-Output 'Related mods may already translate chat or restyle bubbles. This installer preserves them.'
    $taskAnswer=Read-Host 'Keep these mods and install the additional translator? Type YES to continue'
    if($taskAnswer -cne 'YES'){Write-Output 'No files changed.';exit 2}
}
if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw 'Close Deadlock before installing.'}
New-Item -ItemType Directory -Force -Path $StateDirectory,$taskAddons | Out-Null
$taskTarget=$null
foreach($taskIndex in 90..199){$taskCandidate=Join-Path $taskAddons ('pak{0:D2}_dir.vpk' -f $taskIndex);if(-not(Test-Path -LiteralPath $taskCandidate)){$taskTarget=$taskCandidate;break}}
if(-not $taskTarget){throw 'No unused VPK slot found.'}
$taskBefore=[IO.File]::ReadAllBytes($taskGameinfo)
$taskText=[Text.Encoding]::UTF8.GetString($taskBefore)
$taskChanged=$false
if($taskText -notmatch '(?im)^\s*Game\s+"?citadel[/\\]addons"?\s*$'){
    $taskSearch=[regex]'(?im)(SearchPaths\s*\{)'
    if($taskSearch.Matches($taskText).Count -ne 1){throw 'Unexpected gameinfo layout; refusing automatic edit.'}
    $taskText=$taskSearch.Replace($taskText,'$1'+"`r`n            Game citadel/addons",1)
    $taskChanged=$true
}
$taskState=[ordered]@{target=$taskTarget;vpkHash=(Get-FileHash -LiteralPath $taskPackage).Hash;gameinfoChanged=$taskChanged;gameinfoBefore=[Convert]::ToBase64String($taskBefore);gameinfoAfterHash=$null}
try{
    Copy-Item -LiteralPath $taskPackage -Destination $taskTarget
    if($taskChanged){[IO.File]::WriteAllText($taskGameinfo,$taskText,[Text.UTF8Encoding]::new($false))}
    $taskState.gameinfoAfterHash=(Get-FileHash -LiteralPath $taskGameinfo).Hash
    $taskState | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $taskManifest -Encoding utf8
}catch{
    if(Test-Path -LiteralPath $taskTarget){if((Get-FileHash -LiteralPath $taskTarget).Hash -eq $taskState.vpkHash){Remove-Item -LiteralPath $taskTarget}}
    if($taskChanged){[IO.File]::WriteAllBytes($taskGameinfo,$taskBefore)}
    throw
}
Write-Output "Installed: $taskTarget"
Write-Output 'Use Launch.cmd to start the translator and Deadlock, or Settings.cmd for browser settings.'
