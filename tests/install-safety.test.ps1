param()
$ErrorActionPreference='Stop'
$taskRepo=Split-Path $PSScriptRoot -Parent
. (Join-Path $taskRepo 'scripts\Install-Safety.ps1')
$taskRoot=Join-Path $taskRepo ('artifacts\safety-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $taskRoot|Out-Null
$taskPassed=0
function Check($v,[string]$name){if(-not $v){throw $name};$script:taskPassed++;Write-Output ('PASS '+$name)}
$taskFile=Join-Path $taskRoot '中文 file.gi';[IO.File]::WriteAllText($taskFile,'before');$taskBefore=Get-DltHash $taskFile
$taskBackup=$taskFile+'.bak';Write-DltAtomic $taskFile ([Text.Encoding]::UTF8.GetBytes('after')) -ExpectedHash $taskBefore -Backup $taskBackup
Check (([IO.File]::ReadAllText($taskFile) -eq 'after') -and ([IO.File]::ReadAllText($taskBackup) -eq 'before')) 'atomic replacement preserves exact backup'
try{Write-DltAtomic $taskFile ([Text.Encoding]::UTF8.GetBytes('bad')) -ExpectedHash $taskBefore;throw 'expected rejection'}catch{Check ((Get-DltFault $_).code -eq 'file-changed') 'stale hash refused'}
Check (([IO.File]::ReadAllText($taskFile)) -eq 'after') 'stale hash preserves target'
[IO.File]::SetAttributes($taskFile,[IO.FileAttributes]::ReadOnly)
try{
 $check=Get-DltFileCheck $taskFile 'fixture' -AllowReadOnly;Check ($check.requiresReadOnlyConsent -and -not $check.blocking) 'readonly requires explicit consent'
 try{Write-DltAtomic $taskFile ([Text.Encoding]::UTF8.GetBytes('no'));throw 'expected rejection'}catch{Check ((Get-DltFault $_).code -eq 'read-only') 'readonly rejected without consent'}
 Write-DltAtomic $taskFile ([Text.Encoding]::UTF8.GetBytes('yes')) -AllowReadOnly
 Check ([bool]([IO.File]::GetAttributes($taskFile) -band [IO.FileAttributes]::ReadOnly)) 'readonly attribute restored after successful commit'
 try{Write-DltAtomic $taskFile ([Text.Encoding]::UTF8.GetBytes('no')) -ExpectedHash $taskBefore -AllowReadOnly;throw 'expected rejection'}catch{Check ((Get-DltFault $_).code -eq 'file-changed') 'readonly stale hash rejected'}
 Check ([bool]([IO.File]::GetAttributes($taskFile) -band [IO.FileAttributes]::ReadOnly)) 'readonly attribute restored after failed commit'
}finally{[IO.File]::SetAttributes($taskFile,[IO.FileAttributes]::Normal)}
$stream=[IO.File]::Open($taskFile,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
try{$check=Get-DltFileCheck $taskFile 'fixture';Check ($check.blocking -and $check.code -eq 'file-locked' -and $check.nativeError -eq 32) 'exclusive lock classified with native error'}finally{$stream.Dispose()}
Assert-DltWritableDirectory (Join-Path $taskRoot 'not-created\child') 'fixture'
Check (-not(Test-Path -LiteralPath (Join-Path $taskRoot 'not-created'))) 'directory write probe does not create target tree'
Check (@(Get-ChildItem -LiteralPath $taskRoot -Filter '*.tmp' -Force).Count -eq 0) 'temporary and probe files removed'
foreach($case in @(@(5,'access-denied'),@(32,'file-locked'),@(112,'disk-full'),@(206,'path-too-long'))){
 $ex=[IO.IOException]::new('fixture',[int](-2147024896+$case[0]));$er=[Management.Automation.ErrorRecord]::new($ex,'fixture',[Management.Automation.ErrorCategory]::WriteError,$null)
 Check ((Get-DltFault $er 'fixture' $taskFile).code -eq $case[1]) ('native error '+$case[0]+' classified')
}
@{passed=$taskPassed;actualInstallChanged=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskRepo 'artifacts\install-safety-tests.json') -Encoding UTF8
