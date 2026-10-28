$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Find-Deadlock.ps1')
$taskFixture=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts\game-locate-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $taskFixture|Out-Null
function Check($v,[string]$n){if(-not $v){throw "FAIL: $n"};$script:passed++;Write-Output "PASS: $n"}
function Fake-Game([string]$path){New-Item -ItemType Directory -Path (Join-Path $path 'game\citadel'),(Join-Path $path 'game\bin\win64') -Force|Out-Null;foreach($name in @('game\citadel\gameinfo.gi','game\citadel\pak01_dir.vpk','game\bin\win64\deadlock.exe')){[IO.File]::WriteAllBytes((Join-Path $path $name),[byte[]]@(1))};return [IO.Path]::GetFullPath($path)}
$passed=0;$taskGame=Fake-Game (Join-Path $taskFixture 'Odd Folder\中文 游戏\Deadlock')
Check ((Normalize-DeadlockPath $taskGame) -eq $taskGame) 'exact game root'
Check ((Normalize-DeadlockPath (Join-Path $taskGame 'game\citadel')) -eq $taskGame) 'citadel folder normalizes to root'
Check ((Normalize-DeadlockPath (Join-Path $taskGame 'game\bin\win64\deadlock.exe')) -eq $taskGame) 'game EXE normalizes to root'
Check ((Normalize-DeadlockPath ('"'+$taskGame+'"')) -eq $taskGame) 'quoted manual path'
[IO.File]::WriteAllText((Join-Path $taskFixture 'gameinfo.gi'),'not an installation')
Check (-not (Normalize-DeadlockPath $taskFixture)) 'gameinfo alone is not a valid installation'
$taskDeep=Find-DeadlockDeep -Roots @($taskFixture) -MaximumFolders 100 -TimeoutSeconds 10
Check ($taskDeep.Paths.Count -eq 1 -and $taskDeep.Paths[0] -eq $taskGame) 'bounded deep scan finds nonstandard folder'
Check (-not $taskDeep.Limited) 'complete scan marked complete'
$taskLimited=Find-DeadlockDeep -Roots @($taskFixture) -MaximumFolders 1 -TimeoutSeconds 10
Check $taskLimited.Limited 'budget exhaustion is reported as partial, not complete failure'
$taskOther=Fake-Game (Join-Path $taskFixture 'Second\Deadlock')
$taskDeep=Find-DeadlockDeep -Roots @($taskFixture) -MaximumFolders 100 -TimeoutSeconds 10
Check ($taskDeep.Paths.Count -eq 2) 'multiple game installs are enumerated'
$taskRejected=$false;try{Choose-DeadlockPath $taskDeep.Paths -NonInteractive|Out-Null}catch{$taskRejected=$true};Check $taskRejected 'multiple installs require an explicit choice'
$taskRejected=$false;try{Resolve-DeadlockPath -Mode Manual -NonInteractive|Out-Null}catch{$taskRejected=$true};Check $taskRejected 'noninteractive manual requires a path'
Check ((Resolve-DeadlockPath -Requested (Join-Path $taskGame 'game') -NonInteractive) -eq $taskGame) 'explicit input does not need Steam registry'
$taskLibrary=Join-Path $taskFixture 'Library';$taskLibraryGame=Fake-Game (Join-Path $taskLibrary 'steamapps\common\Deadlock')
Check (@(Find-DeadlockQuick @($taskLibrary)) -contains $taskLibraryGame) 'explicit library quick detection'
$taskSteam=Join-Path $taskFixture 'Steam';New-Item -ItemType Directory -Path (Join-Path $taskSteam 'steamapps') -Force|Out-Null
$taskEncoded=$taskLibrary.Replace('\','\\');[IO.File]::WriteAllText((Join-Path $taskSteam 'steamapps\libraryfolders.vdf'),('"libraryfolders" { "0" { "path" "'+$taskEncoded+'" } }'),[Text.UTF8Encoding]::new($false))
Check (@(Find-DeadlockQuick @($taskSteam)) -contains $taskLibraryGame) 'escaped UTF-8 library path parsed'
function Get-ItemProperty { param($Path,$ErrorAction);[pscustomobject]@{SteamPath=$taskSteam;InstallPath=$taskSteam} }
Check (@(Find-DeadlockQuick) -contains $taskLibraryGame) 'no Libraries argument keeps registry roots as an array on PowerShell 5.1'
Check (@(Find-DeadlockQuick -Libraries $null) -contains $taskLibraryGame) 'explicit null Libraries does not concatenate registry paths'
Remove-Item Function:\Get-ItemProperty
Write-Output "PASS TOTAL $passed"
