param(
    [Parameter(Mandatory=$true)][string]$Compiler,
    [Parameter(Mandatory=$true)][string]$CorePath,
    [Parameter(Mandatory=$true)][string]$ModBinPath,
    [Parameter(Mandatory=$true)][string]$GamePath,
    [switch]$ModOnly,
    [switch]$Lite
)
$ErrorActionPreference = 'Stop'
function Get-BuildFileSha256([string]$Path) {
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        try { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash }
        catch {
            if ($attempt -eq 6) { throw }
            Start-Sleep -Milliseconds 700
        }
    }
}
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskVersionInfo=Get-Content -LiteralPath (Join-Path $taskRoot 'version.json') -Raw|ConvertFrom-Json
$taskVersion=if($Lite){$taskVersionInfo.liteVersion}else{$taskVersionInfo.version}
$taskEditionArgs = @(if($Lite){'--lite'})
$taskProduct = if($Lite){'DeadlockTranslate-Lite'}else{'DeadlockTranslate'}
Push-Location $taskRoot
try {
    python scripts\build_identity.py @taskEditionArgs
    if ($LASTEXITCODE) { throw 'Build identity failed.' }
    python scripts\release_history.py @taskEditionArgs
    if ($LASTEXITCODE) { throw 'Installer update history is incomplete.' }
    if ($ModOnly) {
        $taskIdentity=Get-Content -LiteralPath (Join-Path $taskRoot 'build-info.json') -Raw|ConvertFrom-Json
        $taskPrevious=Join-Path $taskRoot ('dist/'+$taskProduct+'-'+$taskVersion+'/build-info.json')
        if (-not (Test-Path -LiteralPath $taskPrevious) -or (Get-Content -LiteralPath $taskPrevious -Raw|ConvertFrom-Json).buildId -ne $taskIdentity.buildId) { throw 'Source changed: perform a full backend + VPK build; ModOnly would mix build identities.' }
    }
    $env:DEADLOCK_TRANSLATE_GAME=$GamePath
    $taskViewer=if($env:DLT_RESOURCE_VIEWER){$env:DLT_RESOURCE_VIEWER}else{Join-Path $taskRoot 'tools/vrf/Source2Viewer-CLI.exe'}
    if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Source 2 resource compiler not found.' }
    foreach($taskRequired in @($CorePath,$ModBinPath,(Join-Path $GamePath 'game\citadel\pak01_dir.vpk'),'tools\vpkedit\vpkeditcli.exe')) {
        if(-not(Test-Path -LiteralPath $taskRequired)){throw "Missing build prerequisite: $taskRequired"}
    }
    python scripts\build_terms.py
    if ($LASTEXITCODE) { throw 'Terminology generation failed.' }
    python scripts\build_hero_labels.py
    if ($LASTEXITCODE) { throw 'Hero identity generation failed.' }
    python scripts\prepare_mod.py @taskEditionArgs
    if ($LASTEXITCODE) { throw 'Mod preparation failed.' }
    $taskAddon=if($Lite){'artifacts\addon-lite'}else{'artifacts\addon'}
    $taskStage = Join-Path $taskRoot ($taskAddon+'\game')
    foreach ($taskLink in @(@{Name='core';Target=$CorePath},@{Name='citadel\bin';Target=$ModBinPath})) {
        $taskLinkPath = Join-Path $taskStage $taskLink.Name
        if (-not (Test-Path -LiteralPath $taskLinkPath)) { New-Item -ItemType Junction -Path $taskLinkPath -Target $taskLink.Target | Out-Null }
        $taskActualLink = Get-Item -LiteralPath $taskLinkPath
        if ($taskActualLink.LinkType -ne 'Junction' -or $taskActualLink.Target -ne $taskLink.Target) { throw "Unexpected tool junction: $taskLinkPath" }
    }
    foreach ($taskSource in @('scripts\dlt_build_transport.js','scripts\dlt_build_browser.js','layout\citadel_ui_build_selector.xml','scripts\dlt_build_text.js','styles\dlt_build_text.css','layout\citadel_ui_build_details.xml','layout\citadel_shop_mods_build.xml','layout\citadel_shop_mods_build_category.xml','layout\tooltips\citadel_tooltip_mod_details.xml','scripts\dlt_native_bubble.js','styles\dlt_native_bubble.css','layout\citadel_hud_top_bar_chat.xml','scripts\dlt_chat.js','styles\dlt_toast.css','styles\dlt_chat.css','layout\chat.xml','scripts\dlt_menu.js','styles\dlt_menu.css','layout\dlt_toast.xml','layout\dlt_menu.xml','scripts\dlt_settings_window.js','styles\dlt_settings_window.css','layout\dlt_settings_window.xml')) {
        if($Lite -and $taskSource -match 'dlt_build|citadel_ui_build|citadel_shop|citadel_tooltip_mod_details'){continue}
        & $Compiler -i (Join-Path $taskRoot ($taskAddon+'\content\citadel\panorama\'+$taskSource)) -game (Join-Path $taskStage 'citadel') -f -nop4
        if ($LASTEXITCODE) { throw "Resource compilation failed: $taskSource" }
    }
    $taskDist = Join-Path $taskRoot ('dist\'+$taskProduct+'-'+$taskVersion)
    New-Item -ItemType Directory -Force -Path $taskDist | Out-Null
    if(-not $ModOnly) {
    Push-Location src\Settings
    try { npm ci --ignore-scripts; if($LASTEXITCODE){throw 'npm restore failed'}; node node_modules/typescript/bin/tsc -p $(if($Lite){'tsconfig.lite.json'}else{'.'}); if($LASTEXITCODE){throw 'TypeScript build failed'} } finally { Pop-Location }
    $taskDist = Join-Path $taskRoot ('dist\'+$taskProduct+'-'+$taskVersion)
    New-Item -ItemType Directory -Force -Path $taskDist | Out-Null
    $taskPublishArgs=@(if($Lite){'-p:LiteEdition=true';"-p:Version=$taskVersion"})
    dotnet publish src\Bridge @taskPublishArgs -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $taskDist
    if($LASTEXITCODE){throw 'Backend publish failed'}
    if($Lite){Copy-Item -LiteralPath 'src/Bridge/wwwroot/lite.html' -Destination (Join-Path $taskDist 'wwwroot/index.html') -Force}
    python scripts\collect_licenses.py --output $taskDist
    if($LASTEXITCODE){throw 'Runtime license collection failed'}
    if(-not $Lite){
    dotnet publish src\VoiceWorker -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:VoiceIncludeCuda=true -o (Join-Path $taskDist 'voice')
    if($LASTEXITCODE){throw 'Voice engine publish failed'}
    Copy-Item -LiteralPath LICENSES\voice-whispernet-MIT.txt,LICENSES\voice-whispercpp-MIT.txt -Destination (Join-Path $taskDist 'LICENSES')
    }
    }
    Copy-Item -LiteralPath artifacts\pairing.key -Destination (Join-Path $taskDist 'pairing.key') -Force
    $taskPackRoot=Join-Path $taskRoot ('artifacts\pack-'+$taskProduct+'-'+$taskVersion)
    New-Item -ItemType Directory -Force -Path (Join-Path $taskPackRoot 'panorama') | Out-Null
    foreach($taskResource in @('scripts/dlt_build_transport.vjs_c','scripts/dlt_build_browser.vjs_c','layout/citadel_ui_build_selector.vxml_c','scripts/dlt_build_text.vjs_c','styles/dlt_build_text.vcss_c','layout/citadel_ui_build_details.vxml_c','layout/citadel_shop_mods_build.vxml_c','layout/citadel_shop_mods_build_category.vxml_c','layout/tooltips/citadel_tooltip_mod_details.vxml_c','scripts/dlt_native_bubble.vjs_c','layout/citadel_hud_top_bar_chat.vxml_c','styles/dlt_native_bubble.vcss_c','layout/chat.vxml_c','layout/dlt_toast.vxml_c','layout/dlt_menu.vxml_c','scripts/dlt_chat.vjs_c','scripts/dlt_menu.vjs_c','styles/dlt_toast.vcss_c','styles/dlt_chat.vcss_c','styles/dlt_menu.vcss_c','layout/dlt_settings_window.vxml_c','scripts/dlt_settings_window.vjs_c','styles/dlt_settings_window.vcss_c')) {
        if($Lite -and $taskResource -match 'dlt_build|citadel_ui_build|citadel_shop|citadel_tooltip_mod_details'){continue}
        $taskDestination=Join-Path $taskPackRoot ('panorama/'+$taskResource)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskDestination)|Out-Null
        Copy-Item -LiteralPath (Join-Path $taskStage ('citadel/panorama/'+$taskResource)) -Destination $taskDestination -Force
    }
    & tools\vpkedit\vpkeditcli.exe $taskPackRoot -o (Join-Path $taskDist 'dlt_translate_dir.vpk') --single-file --no-progress
    if($LASTEXITCODE){throw 'VPK packing failed'}
    & tools\vpkedit\vpkeditcli.exe (Join-Path $taskDist 'dlt_translate_dir.vpk') --verify-checksums all --no-progress
    if($LASTEXITCODE){throw 'VPK checksums failed'}
    Copy-Item -LiteralPath scripts\Manage-Mod.ps1,scripts\Install.cmd,scripts\Uninstall.cmd,scripts\Launch.cmd,scripts\Launch-Admin.cmd,scripts\Settings.cmd,scripts\Trust-Local-Https.ps1,scripts\Trust-Local-Https.cmd,scripts\Renew-Local-Https.cmd -Destination $taskDist -Force
    Copy-Item -LiteralPath scripts\Configure-Steam.ps1,scripts\Find-Deadlock.ps1,scripts\Game-SearchPaths.ps1,scripts\Install-Safety.ps1,scripts\Installer-Transaction.ps1,scripts\Installer-Workflow.ps1,scripts\Vpk-Recovery.ps1,scripts\Backup-Maintenance.ps1,scripts\Repair-GameConfig.cmd,scripts\Export-Diagnostics.cmd -Destination $taskDist -Force
    Copy-Item -LiteralPath artifacts\term-sources.json -Destination (Join-Path $taskDist 'terms-provenance.json') -Force
    if(Test-Path docs\README.md){Copy-Item -LiteralPath docs\README.md -Destination (Join-Path $taskDist 'README.md') -Force}
    if(Test-Path docs\TEST-REPORT.md){Copy-Item -LiteralPath docs\TEST-REPORT.md -Destination (Join-Path $taskDist 'TEST-REPORT.md') -Force}
    python scripts\package_docs.py --output $taskDist @taskEditionArgs
    if($LASTEXITCODE){throw 'Documentation packaging failed'}
    if(Test-Path docs\CHAT-PIPELINE-AUDIT-0.2.1.md){Copy-Item -LiteralPath docs\CHAT-PIPELINE-AUDIT-0.2.1.md -Destination $taskDist -Force}
    foreach($taskDoc in @('UPDATE-0.2.2.md','CHECKLIST-0.2.2.md','REVIEW-FIX-0.2.3.md')){if(Test-Path ('docs\'+$taskDoc)){Copy-Item -LiteralPath ('docs\'+$taskDoc) -Destination $taskDist -Force}}
    $taskBuildInfo=Get-Content -LiteralPath (Join-Path $taskRoot 'build-info.json') -Raw|ConvertFrom-Json
    Copy-Item -LiteralPath (Join-Path $taskRoot 'build-info.json') -Destination (Join-Path $taskDist 'build-info.json') -Force
    $taskManifest = [ordered]@{buildId=$taskBuildInfo.buildId;version=$taskVersion;featureRevision=$taskVersionInfo.featureRevision;chatPipelineFix=$taskVersionInfo.chatPipelineFix;builtUtc=[DateTime]::UtcNow.ToString('o');gamePath=$GamePath;compilerPath=$Compiler;compilerSha256=(Get-FileHash -LiteralPath $Compiler).Hash;viewerVersion=(Get-Item -LiteralPath $taskViewer).VersionInfo.ProductVersion;vpkeditRelease='v5.0.0.4';artifacts=@()}
    $taskManifest.nativeBubbleTemplate=Get-Content -LiteralPath (Join-Path $taskRoot 'artifacts/native-bubble-provenance.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $taskManifest.artifacts=@(Get-ChildItem -LiteralPath $taskDist -Recurse -File | Where-Object Name -ne 'build-manifest.json' | ForEach-Object {[ordered]@{file=$_.FullName.Substring($taskDist.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-BuildFileSha256 $_.FullName)}})
    $taskManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskDist 'build-manifest.json') -Encoding utf8
    Write-Output "PACKAGE: $taskDist"
} finally { Pop-Location }
