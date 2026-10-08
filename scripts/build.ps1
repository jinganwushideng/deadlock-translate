param(
    [Parameter(Mandatory=$true)][string]$Compiler,
    [Parameter(Mandatory=$true)][string]$CorePath,
    [Parameter(Mandatory=$true)][string]$ModBinPath,
    [Parameter(Mandatory=$true)][string]$GamePath,
    [switch]$ModOnly
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskVersionInfo=Get-Content -LiteralPath (Join-Path $taskRoot 'version.json') -Raw|ConvertFrom-Json
$taskVersion=$taskVersionInfo.version
Push-Location $taskRoot
try {
    $env:DEADLOCK_TRANSLATE_GAME=$GamePath
    if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Source 2 resource compiler not found.' }
    foreach($taskRequired in @($CorePath,$ModBinPath,(Join-Path $GamePath 'game\citadel\pak01_dir.vpk'),'tools\vpkedit\vpkeditcli.exe')) {
        if(-not(Test-Path -LiteralPath $taskRequired)){throw "Missing build prerequisite: $taskRequired"}
    }
    python scripts\build_terms.py
    if ($LASTEXITCODE) { throw 'Terminology generation failed.' }
    python scripts\prepare_mod.py
    if ($LASTEXITCODE) { throw 'Mod preparation failed.' }
    $taskStage = Join-Path $taskRoot 'artifacts\addon\game'
    foreach ($taskLink in @(@{Name='core';Target=$CorePath},@{Name='citadel\bin';Target=$ModBinPath})) {
        $taskLinkPath = Join-Path $taskStage $taskLink.Name
        if (-not (Test-Path -LiteralPath $taskLinkPath)) { New-Item -ItemType Junction -Path $taskLinkPath -Target $taskLink.Target | Out-Null }
        $taskActualLink = Get-Item -LiteralPath $taskLinkPath
        if ($taskActualLink.LinkType -ne 'Junction' -or $taskActualLink.Target -ne $taskLink.Target) { throw "Unexpected tool junction: $taskLinkPath" }
    }
    foreach ($taskSource in @('scripts\dlt_chat.js','styles\dlt_chat.css','layout\chat.xml','scripts\dlt_menu.js','styles\dlt_menu.css','layout\dlt_menu.xml','scripts\dlt_settings_window.js','styles\dlt_settings_window.css','layout\dlt_settings_window.xml')) {
        & $Compiler -i (Join-Path $taskRoot ('artifacts\addon\content\citadel\panorama\'+$taskSource)) -game (Join-Path $taskStage 'citadel') -f -nop4
        if ($LASTEXITCODE) { throw "Resource compilation failed: $taskSource" }
    }
    $taskDist = Join-Path $taskRoot ('dist\DeadlockTranslate-'+$taskVersion)
    New-Item -ItemType Directory -Force -Path $taskDist | Out-Null
    if(-not $ModOnly) {
    Push-Location src\Settings
    try { npm ci --ignore-scripts; if($LASTEXITCODE){throw 'npm restore failed'}; npm run build; if($LASTEXITCODE){throw 'TypeScript build failed'} } finally { Pop-Location }
    $taskDist = Join-Path $taskRoot ('dist\DeadlockTranslate-'+$taskVersion)
    New-Item -ItemType Directory -Force -Path $taskDist | Out-Null
    dotnet publish src\Bridge -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $taskDist
    if($LASTEXITCODE){throw 'Backend publish failed'}
    python scripts\collect_licenses.py --output $taskDist
    if($LASTEXITCODE){throw 'Runtime license collection failed'}
    }
    Copy-Item -LiteralPath artifacts\pairing.key -Destination (Join-Path $taskDist 'pairing.key') -Force
    $taskPackRoot=Join-Path $taskRoot ('artifacts\pack-'+$taskVersion)
    New-Item -ItemType Directory -Force -Path (Join-Path $taskPackRoot 'panorama') | Out-Null
    foreach($taskResource in @('layout/chat.vxml_c','layout/dlt_menu.vxml_c','scripts/dlt_chat.vjs_c','scripts/dlt_menu.vjs_c','styles/dlt_chat.vcss_c','styles/dlt_menu.vcss_c','layout/dlt_settings_window.vxml_c','scripts/dlt_settings_window.vjs_c','styles/dlt_settings_window.vcss_c')) {
        $taskDestination=Join-Path $taskPackRoot ('panorama/'+$taskResource)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskDestination)|Out-Null
        Copy-Item -LiteralPath (Join-Path $taskStage ('citadel/panorama/'+$taskResource)) -Destination $taskDestination -Force
    }
    & tools\vpkedit\vpkeditcli.exe $taskPackRoot -o (Join-Path $taskDist 'dlt_translate_dir.vpk') --single-file --no-progress
    if($LASTEXITCODE){throw 'VPK packing failed'}
    & tools\vpkedit\vpkeditcli.exe (Join-Path $taskDist 'dlt_translate_dir.vpk') --verify-checksums all --no-progress
    if($LASTEXITCODE){throw 'VPK checksums failed'}
    Copy-Item -LiteralPath scripts\Manage-Mod.ps1,scripts\Install.cmd,scripts\Uninstall.cmd,scripts\Launch.cmd,scripts\Settings.cmd,scripts\Trust-Local-Https.ps1,scripts\Trust-Local-Https.cmd,scripts\Renew-Local-Https.cmd -Destination $taskDist -Force
    Copy-Item -LiteralPath artifacts\term-sources.json -Destination (Join-Path $taskDist 'terms-provenance.json') -Force
    if(Test-Path docs\README.md){Copy-Item -LiteralPath docs\README.md -Destination (Join-Path $taskDist 'README.md') -Force}
    if(Test-Path docs\TEST-REPORT.md){Copy-Item -LiteralPath docs\TEST-REPORT.md -Destination (Join-Path $taskDist 'TEST-REPORT.md') -Force}
    python scripts\package_docs.py --output $taskDist
    if($LASTEXITCODE){throw 'Documentation packaging failed'}
    if(Test-Path docs\CHAT-PIPELINE-AUDIT-0.2.1.md){Copy-Item -LiteralPath docs\CHAT-PIPELINE-AUDIT-0.2.1.md -Destination $taskDist -Force}
    foreach($taskDoc in @('UPDATE-0.2.2.md','CHECKLIST-0.2.2.md','REVIEW-FIX-0.2.3.md')){if(Test-Path ('docs\'+$taskDoc)){Copy-Item -LiteralPath ('docs\'+$taskDoc) -Destination $taskDist -Force}}
    $taskManifest = [ordered]@{version=$taskVersion;chatPipelineFix=$taskVersionInfo.chatPipelineFix;builtUtc=[DateTime]::UtcNow.ToString('o');gamePath=$GamePath;compilerPath=$Compiler;compilerSha256=(Get-FileHash -LiteralPath $Compiler).Hash;viewerVersion='20.0';vpkeditRelease='v5.0.0.4';artifacts=@()}
    $taskManifest.artifacts=@(Get-ChildItem -LiteralPath $taskDist -Recurse -File | Where-Object Name -ne 'build-manifest.json' | ForEach-Object {[ordered]@{file=$_.FullName.Substring($taskDist.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
    $taskManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskDist 'build-manifest.json') -Encoding utf8
    Write-Output "PACKAGE: $taskDist"
} finally { Pop-Location }
