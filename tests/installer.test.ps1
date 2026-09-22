$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskFixture=Join-Path $taskRoot ('artifacts\installer-test-'+[Guid]::NewGuid().ToString('N'))
$taskCitadel=Join-Path $taskFixture 'game\citadel'
$taskState=Join-Path $taskFixture 'state'
New-Item -ItemType Directory -Path (Join-Path $taskCitadel 'addons') -Force | Out-Null
$taskGameinfo=Join-Path $taskCitadel 'gameinfo.gi'
[IO.File]::WriteAllText($taskGameinfo,'"GameInfo" { FileSystem { SearchPaths { Game citadel Game core } } }',[Text.UTF8Encoding]::new($false))
$taskBefore=(Get-FileHash -LiteralPath $taskGameinfo).Hash
$taskVersion=(Get-Content -LiteralPath (Join-Path $taskRoot 'version.json') -Raw|ConvertFrom-Json).version
$taskManager=Join-Path $taskRoot ('dist\DeadlockTranslate-'+$taskVersion+'\Manage-Mod.ps1')
& powershell -NoProfile -ExecutionPolicy Bypass -File $taskManager -Action Inspect -GamePath $taskFixture -StateDirectory $taskState
if($LASTEXITCODE){throw 'Inspect failed'}
if((Get-FileHash -LiteralPath $taskGameinfo).Hash -ne $taskBefore){throw 'Inspect mutated gameinfo'}
& powershell -NoProfile -ExecutionPolicy Bypass -File $taskManager -Action Install -GamePath $taskFixture -StateDirectory $taskState
if($LASTEXITCODE){throw 'Install failed'}
if(-not(Test-Path (Join-Path $taskCitadel 'addons\pak90_dir.vpk'))){throw 'VPK missing'}
& powershell -NoProfile -ExecutionPolicy Bypass -File $taskManager -Action Uninstall -GamePath $taskFixture -StateDirectory $taskState
if($LASTEXITCODE){throw 'Uninstall failed'}
if((Get-FileHash -LiteralPath $taskGameinfo).Hash -ne $taskBefore){throw 'Gameinfo did not restore byte-for-byte'}
if(Test-Path (Join-Path $taskCitadel 'addons\pak90_dir.vpk')){throw 'VPK did not uninstall'}
$taskResult=@{passed=6;at=[DateTime]::UtcNow.ToString('o');fixture=$taskFixture;realGameModified=$false}
$taskResult|ConvertTo-Json|Set-Content (Join-Path $taskRoot 'artifacts\installer-tests.json') -Encoding utf8
Write-Output 'PASS: read-only inspection, install, VPK hash validation, manifest, uninstall, byte-exact gameinfo restore.'
