$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
. (Join-Path $taskRoot 'scripts\Game-SearchPaths.ps1')
$taskPassed=0
function Check($ok,[string]$message){if(-not $ok){throw $message};$script:taskPassed++;Write-Output ('PASS '+$message)}
function Plan([string]$text){Get-DltSearchPathsPlan ([Text.Encoding]::UTF8.GetBytes($text)) -EnsureAddons}
$taskStock='GameInfo { FileSystem { SearchPaths { Game citadel Game core } } untouched "Chinese and { braces }" }'
$taskPlan=Plan $taskStock
Check ($taskPlan.Added.Count -eq 5 -and $taskPlan.AddedAddon) 'stock install gains mount plus all four explicit boot paths'
$taskText=[Text.Encoding]::UTF8.GetString($taskPlan.Bytes)
Check ($taskText.Contains('untouched "Chinese and { braces }"')) 'unrelated data and quoted braces preserved'
Check (-not (Get-DltSearchPathsPlan $taskPlan.Bytes -EnsureAddons).Changed) 'repeated repair is byte-stable and adds no duplicate entries'
$taskComments="// SearchPaths { Mod citadel }`r`nGameInfo { FileSystem { SearchPaths { // Mod citadel`r`n Game citadel/addons Game citadel Game core } } }"
$taskPlan=Plan $taskComments
Check ($taskPlan.Added.Count -eq 4 -and -not $taskPlan.AddedAddon) 'comments do not count as active paths, existing addon mount is preserved'
$taskBytes=[byte[]](@(239,187,191)+[Text.Encoding]::UTF8.GetBytes($taskComments))
$taskPlan=Get-DltSearchPathsPlan $taskBytes -EnsureAddons
Check ($taskPlan.Bytes[0] -eq 239 -and [Text.Encoding]::UTF8.GetString($taskPlan.Bytes).Contains("`r`n")) 'UTF-8 BOM and CRLF preserved'
$taskQuoted='"GameInfo" { "FileSystem" { "SearchPaths" { "Mod" "citadel" "Write" "citadel" "Mod" "core" "Write" "core" "Game" "citadel\addons" "Game" "citadel" "Game" "core" } } }'
Check (-not (Plan $taskQuoted).Changed) 'quoted keys and Windows slash paths are recognized'
foreach($taskBad in @('GameInfo { FileSystem { SearchPaths { Game citadel } } }','GameInfo { FileSystem { SearchPaths { Game citadel Game core }','GameInfo { FileSystem { SearchPaths { Game citadel Game core } SearchPaths { Game citadel Game core } } }')){
    $taskRejected=$false;try{Plan $taskBad|Out-Null}catch{$taskRejected=$true};Check $taskRejected 'ambiguous, missing base, or malformed config is refused'
}
$taskOwned=[pscustomobject]@{gameinfoChanged=$true;gameinfoBefore='original';gameinfoAfterHash='ABC'}
Update-DltGameinfoOwnership $taskOwned ([byte[]]@(1)) 'abc' 'after' $false
Check ($taskOwned.gameinfoChanged -and $taskOwned.gameinfoBefore -eq 'original') 'known installer ownership keeps original uninstall snapshot'
Update-DltGameinfoOwnership $taskOwned ([byte[]]@(2)) 'externally-edited' 'after2' $false
Check (-not $taskOwned.gameinfoChanged) 'external changes cannot be overwritten by stale uninstall snapshot'
Update-DltGameinfoOwnership $taskOwned ([byte[]]@(3)) 'new-stock' 'after3' $true
Check ($taskOwned.gameinfoChanged -and $taskOwned.gameinfoBefore -eq [Convert]::ToBase64String([byte[]]@(3))) 'newly mounted addon takes a fresh uninstall snapshot'
$taskFixture=Join-Path $taskRoot ('artifacts\keybinding-fixture-'+[Guid]::NewGuid().ToString('N'))
$taskCfg=Join-Path $taskFixture 'game\citadel\cfg';New-Item -ItemType Directory -Path $taskCfg -Force|Out-Null
[IO.File]::WriteAllText((Join-Path $taskCfg 'user_keys_default.vcfg'),'synthetic default keys')
$taskBefore='GameInfo { FileSystem { SearchPaths { Game citadel/addons Game citadel Game core } } }'
Check (-not(Test-Path -LiteralPath (Join-Path $taskFixture 'game\citadel\addons\cfg\user_keys_default.vcfg'))) 'old implicit MOD=first Game path cannot resolve default keys in clean fixture'
$taskAfter=[Text.Encoding]::UTF8.GetString((Plan $taskBefore).Bytes)
$taskMod=[regex]::Match($taskAfter,'\bMod\s+(\S+)').Groups[1].Value
Check (Test-Path -LiteralPath (Join-Path $taskFixture ('game\'+$taskMod+'\cfg\user_keys_default.vcfg'))) 'explicit MOD resolves original default keys without moving or rewriting them'
@{passed=$taskPassed;realGameStarted=$false}|ConvertTo-Json|Set-Content (Join-Path $taskRoot 'artifacts\keybinding-067\searchpaths-tests.json') -Encoding UTF8
