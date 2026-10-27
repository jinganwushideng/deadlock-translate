$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskFixture=Join-Path $taskRoot ('artifacts\installer-test-'+[Guid]::NewGuid().ToString('N'))
$taskCitadel=Join-Path $taskFixture 'game\citadel'
$taskState=Join-Path $taskFixture 'state'
New-Item -ItemType Directory -Path (Join-Path $taskCitadel 'addons') -Force | Out-Null
$taskGameinfo=Join-Path $taskCitadel 'gameinfo.gi'
[IO.File]::WriteAllText($taskGameinfo,'"GameInfo" { FileSystem { SearchPaths { Game citadel Game core } } }',[Text.UTF8Encoding]::new($false))
New-Item -ItemType Directory -Path (Join-Path $taskFixture 'game\bin\win64') -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $taskFixture 'game\bin\win64\deadlock.exe'),[byte[]]@(1))
[IO.File]::WriteAllBytes((Join-Path $taskCitadel 'pak01_dir.vpk'),[byte[]]@(1))
$taskBefore=(Get-FileHash -LiteralPath $taskGameinfo).Hash
$taskVersion=(Get-Content -LiteralPath (Join-Path $taskRoot 'version.json') -Raw|ConvertFrom-Json).version
$taskManager=Join-Path $taskRoot ('dist\DeadlockTranslate-'+$taskVersion+'\Manage-Mod.ps1')
& powershell -NoProfile -ExecutionPolicy Bypass -File $taskManager -Action Inspect -GamePath $taskFixture -StateDirectory $taskState
if($LASTEXITCODE){throw 'Inspect failed'}
if((Get-FileHash -LiteralPath $taskGameinfo).Hash -ne $taskBefore){throw 'Inspect mutated gameinfo'}
& powershell -NoProfile -ExecutionPolicy Bypass -File $taskManager -Action Install -GamePath $taskFixture -StateDirectory $taskState
if($LASTEXITCODE){throw 'Install failed'}
if(-not(Test-Path (Join-Path $taskCitadel 'addons\pak90_dir.vpk'))){throw 'VPK missing'}
$taskInstalledText=[IO.File]::ReadAllText($taskGameinfo)
foreach($taskPath in @('Mod citadel','Write citadel','Mod core','Write core')){if(-not $taskInstalledText.Contains($taskPath)){throw 'Installed search paths lack '+$taskPath}}
& powershell -NoProfile -ExecutionPolicy Bypass -File $taskManager -Action Uninstall -GamePath $taskFixture -StateDirectory $taskState
if($LASTEXITCODE){throw 'Uninstall failed'}
if((Get-FileHash -LiteralPath $taskGameinfo).Hash -ne $taskBefore){throw 'Gameinfo did not restore byte-for-byte'}
if(Test-Path (Join-Path $taskCitadel 'addons\pak90_dir.vpk')){throw 'VPK did not uninstall'}
$taskBroken='GameInfo { FileSystem { SearchPaths { Game citadel/addons Game citadel Game core } } Custom "keep" }'
[IO.File]::WriteAllText($taskGameinfo,$taskBroken,[Text.UTF8Encoding]::new($false))
New-Item -ItemType Directory -Path (Join-Path $taskCitadel 'cfg') -Force|Out-Null
$taskKeys=Join-Path $taskCitadel 'cfg\user_keys_0_slot0.vcfg';[IO.File]::WriteAllText($taskKeys,'my custom keybinds')
$taskKeysHash=(Get-FileHash -LiteralPath $taskKeys).Hash
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $taskManager -Action Repair -GamePath $taskFixture -StateDirectory $taskState
if($LASTEXITCODE){throw 'Repair failed'}
$taskRepaired=[IO.File]::ReadAllText($taskGameinfo)
foreach($taskPath in @('Mod citadel','Write citadel','Mod core','Write core','Custom "keep"')){if(-not $taskRepaired.Contains($taskPath)){throw 'Repair missing required/preserved value'}}
if((Get-FileHash -LiteralPath $taskKeys).Hash -ne $taskKeysHash){throw 'Repair changed personal keybindings'}
$taskRepairHash=(Get-FileHash -LiteralPath $taskGameinfo).Hash
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $taskManager -Action Repair -GamePath $taskFixture -StateDirectory $taskState
if($LASTEXITCODE -or (Get-FileHash -LiteralPath $taskGameinfo).Hash -ne $taskRepairHash){throw 'Repeated repair is not a no-op'}
$taskBackups=@(Get-ChildItem -LiteralPath $taskCitadel -Filter 'gameinfo.gi.dlt-before-repair-*.bak')
if($taskBackups.Count -ne 1 -or [IO.File]::ReadAllText($taskBackups[0].FullName) -cne $taskBroken){throw 'Repair backup is not exact'}
$taskResult=@{passed=11;at=[DateTime]::UtcNow.ToString('o');fixture=$taskFixture;realGameModified=$false}
$taskResult|ConvertTo-Json|Set-Content (Join-Path $taskRoot 'artifacts\installer-tests.json') -Encoding utf8
Write-Output 'PASS: read-only inspection, install, VPK hash validation, manifest, uninstall, byte-exact gameinfo restore.'
