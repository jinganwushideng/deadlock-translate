$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts/Vpk-Recovery.ps1')
$taskBase=@('panorama/layout/chat.vxml_c','panorama/layout/dlt_menu.vxml_c','panorama/layout/dlt_settings_window.vxml_c','panorama/scripts/dlt_chat.vjs_c','panorama/scripts/dlt_menu.vjs_c','panorama/scripts/dlt_settings_window.vjs_c','panorama/styles/dlt_chat.vcss_c','panorama/styles/dlt_menu.vcss_c','panorama/styles/dlt_settings_window.vcss_c')
$taskNative=@('panorama/layout/citadel_hud_top_bar_chat.vxml_c','panorama/styles/dlt_native_bubble.vcss_c','panorama/scripts/dlt_native_bubble.vjs_c')
$taskBuild=@('panorama/scripts/dlt_build_text.vjs_c','panorama/styles/dlt_build_text.vcss_c','panorama/layout/citadel_ui_build_details.vxml_c','panorama/layout/citadel_shop_mods_build.vxml_c','panorama/layout/citadel_shop_mods_build_category.vxml_c','panorama/layout/tooltips/citadel_tooltip_mod_details.vxml_c')
$taskChecks=0
function Assert-Identity([bool]$Value,[string]$Name){if(-not $Value){throw $Name};$script:taskChecks++;Write-Output ('PASS '+$Name)}
Assert-Identity (Test-DltIndependentResources $taskBase) 'legacy nine resources are accepted'
Assert-Identity (Test-DltIndependentResources ($taskBase+$taskNative[0..1])) 'eleven-resource prebuilt template is accepted'
Assert-Identity (Test-DltIndependentResources ($taskBase+$taskNative)) 'twelve-resource native controller is accepted'
Assert-Identity (Test-DltIndependentResources ($taskBase+$taskNative+$taskBuild)) 'eighteen-resource build translation package is accepted'
$taskBrowser=@('panorama/scripts/dlt_build_transport.vjs_c','panorama/scripts/dlt_build_browser.vjs_c','panorama/layout/citadel_ui_build_selector.vxml_c')
Assert-Identity (Test-DltIndependentResources ($taskBase+$taskNative+$taskBuild+$taskBrowser)) 'twenty-one-resource source and browser package is accepted'
Assert-Identity (-not(Test-DltIndependentResources ($taskBase+$taskNative+$taskBuild+$taskBrowser[0..1]))) 'incomplete browser adapter is rejected'
Assert-Identity (-not(Test-DltIndependentResources ($taskBase+$taskNative+$taskBuild[0..4]))) 'partial build translation package is rejected'
Assert-Identity (-not(Test-DltIndependentResources ($taskBase+$taskNative+$taskBuild[0..4]+'models/foreign.vmdl_c'))) 'foreign resource cannot replace a build translation resource'
Assert-Identity (-not(Test-DltIndependentResources ($taskBase+$taskNative[2]))) 'controller without native template is rejected'
Assert-Identity (-not(Test-DltIndependentResources ($taskBase+$taskNative[0]+$taskNative[2]))) 'controller without native stylesheet is rejected'
Assert-Identity (-not(Test-DltIndependentResources ($taskBase[1..8]+$taskNative[0]))) 'missing base resource is rejected'
Assert-Identity (-not(Test-DltIndependentResources ($taskBase[0..7]+$taskBase[0]))) 'duplicate resource is rejected'
Assert-Identity (-not(Test-DltIndependentResources ($taskBase+'panorama/styles/foreign.vcss_c'+'panorama/styles/dlt_foreign.vcss_c'))) 'unrelated or mixed resources are rejected'
Assert-Identity (-not(Test-DltIndependentResources ($taskBase+$taskNative+'models/foreign.vmdl_c'))) 'larger mixed packages are rejected'
Write-Output ('TOTAL '+$taskChecks+' passed; pure identity checks, no installation changes')
