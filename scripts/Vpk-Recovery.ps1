# Pure inspection plus typed traces; never changes game files or pairing material.
function Test-DltIndependentResources([string[]]$Resources) {
    $base=@('panorama/layout/chat.vxml_c','panorama/layout/dlt_menu.vxml_c','panorama/layout/dlt_settings_window.vxml_c','panorama/scripts/dlt_chat.vjs_c','panorama/scripts/dlt_menu.vjs_c','panorama/scripts/dlt_settings_window.vjs_c','panorama/styles/dlt_chat.vcss_c','panorama/styles/dlt_menu.vcss_c','panorama/styles/dlt_settings_window.vcss_c')
    $native=@('panorama/layout/citadel_hud_top_bar_chat.vxml_c','panorama/styles/dlt_native_bubble.vcss_c','panorama/scripts/dlt_native_bubble.vjs_c')
    if($Resources.Count -notin @(9,11,12)){return $false}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($path in $Resources){if($path -notin ($base+$native) -or -not $seen.Add($path)){return $false}}
    foreach($path in $base){if(-not $seen.Contains($path)){return $false}}
    if($Resources.Count -gt 9 -and (-not $seen.Contains($native[0]) -or -not $seen.Contains($native[1]))){return $false}
    return $true
}
function Test-DltQolPackage([string[]]$Resources) {
    # Numbered VPK names are assigned by mod managers. Match QOL's own resources.
    foreach($resource in $Resources){
        if($resource.Replace('\','/') -in @('panorama/styles/qollock_global.vcss_c','panorama/styles/qollock_defines.vcss_c','scripts/qollock_translations.vjs_c')){return $true}
    }
    return $false
}
function Get-DltReinstallPlan([string]$Game,[string]$Runtime,[string]$Mode) {
    $runtimeFull=[IO.Path]::GetFullPath($Runtime).TrimEnd('\','/');Assert-DltNoLinks $runtimeFull
    $nonempty=(Test-Path -LiteralPath $runtimeFull -PathType Container) -and @(Get-ChildItem -LiteralPath $runtimeFull -Force).Count -gt 0
    $record=$null;$identity='unrecognized';$vpk=Join-Path $runtimeFull 'dlt_translate_dir.vpk';$meta=Join-Path $runtimeFull 'package-manifest.json'
    if($nonempty -and (Test-Path -LiteralPath $vpk -PathType Leaf) -and (Test-Path -LiteralPath $meta -PathType Leaf)){
        Assert-DltNoLinks $vpk;Assert-DltNoLinks $meta
        try{
            if((Get-Item -LiteralPath $meta).Length -gt 2097152){throw 'Manifest too large'}
            $metadata=Get-Content -LiteralPath $meta -Raw -Encoding UTF8|ConvertFrom-Json
            $hash=Get-DltHash $vpk;$entry=@($metadata.files|Where-Object {$_.file -ceq 'dlt_translate_dir.vpk' -and $_.sha256 -match '^[a-fA-F0-9]{64}$'})
            $paths=@(Get-DltVpkResources $vpk)
            $required=@('DeadlockTranslate.exe','pairing.key','data/terms.json','wwwroot/index.html')
            $declared=@($metadata.files|Where-Object {$_.file -in $required}|Select-Object -ExpandProperty file -Unique)
            if($metadata.version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or $entry.Count -ne 1 -or $hash -ne $entry[0].sha256.ToLowerInvariant() -or $declared.Count -ne $required.Count -or -not(Test-DltIndependentResources $paths)){throw 'Not an independently verifiable translator runtime'}
            # Reconstructed ownership claims only the hash-verified independent VPK.
            # Never invent old gameinfo ownership or disabled foreign-mod backups.
            $record=[pscustomobject]@{target=Join-Path $Game 'game\citadel\addons\pak90_dir.vpk';vpkHash=$hash;version=$metadata.version;runtimeFolder=$runtimeFull;gameinfoChanged=$false;gameinfoBefore='';gameinfoAfterHash=Get-DltHash (Join-Path $Game 'game\citadel\gameinfo.gi')}
            $identity='verified-runtime'
        }catch{if($_.Exception.Data.Contains('dltFault')){throw}}
    }
    if($record){return @{kind='reinstall-in-place';originalRuntime=$runtimeFull;runtime=$runtimeFull;identity=$identity;message='安装记录缺失，已确认原程序目录；将备份旧文件、重建记录并成套重装，保留个人数据';record=$record}}
    if(-not $nonempty -or $Mode -eq 'uninstall'){return @{kind='fresh';originalRuntime=$runtimeFull;runtime=$runtimeFull;identity=if($nonempty){'unrecognized'}else{'empty'};message='安装记录不存在，按首次安装恢复完整程序与游戏 VPK';record=$null}}
    $parent=Join-Path $env:LOCALAPPDATA 'Programs';$target=$null
    foreach($n in 1..100){$candidate=Join-Path $parent ('DeadlockTranslate-Reinstall-'+$n);Assert-DltNoLinks $candidate;if(-not(Test-Path -LiteralPath $candidate)){$target=$candidate;break}}
    if(-not $target){Throw-DltFault 'reinstall-path-unavailable' 'reinstall-preflight' $parent '无法找到可用的空程序目录。' '请选择其他空目录，原残留文件和个人数据保持原样。'}
    return @{kind='fresh-directory';originalRuntime=$runtimeFull;runtime=$target;identity=$identity;message='安装记录缺失且残留目录不能确认归属；保留该目录，改用新的空目录安装，保留个人设置并更新 Steam 启动路径';record=$null}
}
function Write-DltInstallTrace([string]$Stage,[string]$Path,[string]$Outcome,[string]$ExpectedHash='',[string]$ActualHash='') {
    if(-not $script:DltTraceFile){return}
    try{
        if([IO.Path]::GetExtension($script:DltTraceFile) -ine '.jsonl'){return}
        Assert-DltNoLinks $script:DltTraceFile
        if((Test-Path -LiteralPath $script:DltTraceFile) -and (Get-Item -LiteralPath $script:DltTraceFile).Length -gt 1048576){Assert-DltNoLinks ($script:DltTraceFile+'.1');Move-Item -LiteralPath $script:DltTraceFile -Destination ($script:DltTraceFile+'.1') -Force}
        $entry=[ordered]@{at=[DateTime]::UtcNow.ToString('o');stage=$Stage;path=$Path;outcome=$Outcome}
        if($ExpectedHash -match '^[a-fA-F0-9]{64}$'){$entry.expectedHash=$ExpectedHash.ToLowerInvariant()}
        if($ActualHash -match '^[a-fA-F0-9]{64}$'){$entry.actualHash=$ActualHash.ToLowerInvariant()}
        [IO.File]::AppendAllText($script:DltTraceFile,($entry|ConvertTo-Json -Compress)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
    }catch{}
}
function Get-DltGameVpkState($Record,[string]$Addons,[string]$Runtime) {
    $target=[IO.Path]::GetFullPath($Record.target);$addonsFull=[IO.Path]::GetFullPath($Addons).TrimEnd('\')
    if([IO.Path]::GetDirectoryName($target) -ine $addonsFull -or [IO.Path]::GetFileName($target) -notmatch '^pak\d{2,3}_dir\.vpk$' -or $Record.vpkHash -notmatch '^[a-fA-F0-9]{64}$'){Throw-DltFault 'vpk-record-invalid' 'vpk-preflight' $target '安装记录指向无效的 VPK 路径。'}
    Assert-DltNoLinks $target
    $actual='';if(Test-Path -LiteralPath $target -PathType Leaf){$actual=Get-DltHash $target}
    $taskAtRecorded=$actual -eq $Record.vpkHash.ToLowerInvariant()
    $size=if($taskAtRecorded){(Get-Item -LiteralPath $target).Length}else{$null};$oldRuntime=if($Runtime){Join-Path $Runtime 'dlt_translate_dir.vpk'}else{$null}
    if($null -eq $size -and $oldRuntime -and (Test-Path -LiteralPath $oldRuntime -PathType Leaf)){Assert-DltNoLinks $oldRuntime;if((Get-DltHash $oldRuntime) -eq $Record.vpkHash.ToLowerInvariant()){$size=(Get-Item -LiteralPath $oldRuntime).Length}}
    $taskHashMatches=@();$timer=[Diagnostics.Stopwatch]::StartNew();$inspected=0
    foreach($file in @(Get-ChildItem -LiteralPath $Addons -Filter '*.vpk' -File -ErrorAction SilentlyContinue)){
        if($file.Name -notmatch '^pak\d{2,3}_dir\.vpk$' -or ($size -ne $null -and $file.Length -ne $size) -or ($size -eq $null -and $file.Length -gt 16777216)){continue}
        if(++$inspected -gt 512 -or $timer.ElapsedMilliseconds -gt 5000){Throw-DltFault 'vpk-search-limited' 'vpk-preflight' $Addons '已达到 VPK 身份搜索限额，不能确认旧包不存在。' '导出诊断人工核对；不自动重装以免重复加载。'}
        Assert-DltNoLinks $file.FullName
        if((Get-DltHash $file.FullName) -eq $Record.vpkHash.ToLowerInvariant()){$taskHashMatches+=$file.FullName}
    }
    if($taskHashMatches.Count -gt 1){
        $safe=$false;try{$paths=@(Get-DltVpkResources $taskHashMatches[0]);$safe=Test-DltIndependentResources $paths}catch{}
        $primary=if($target -in $taskHashMatches){$target}else{@($taskHashMatches|Sort-Object)[0]}
        return @{state='duplicate';recordedTarget=$target;target=$primary;expectedHash=$Record.vpkHash.ToLowerInvariant();actualHash=$Record.vpkHash.ToLowerInvariant();requiresRepair=$true;blocked=(-not $safe);message=if($safe){'发现重复的独立翻译包；保留一个活动槽位，备份停用其余相同副本后成套修复'}else{'重复包无法确认为独立翻译资源，请选择具体冲突包或在管理器关闭翻译功能后重新检查'};matches=@($taskHashMatches);duplicates=@($taskHashMatches|Where-Object {$_ -ine $primary})}
    }
    if($taskAtRecorded -and $taskHashMatches.Count -eq 1){return @{state='ready';recordedTarget=$target;target=$target;expectedHash=$Record.vpkHash.ToLowerInvariant();actualHash=$actual;requiresRepair=$false;blocked=$false;message='游戏 VPK 与安装记录一致';matches=@()}}
    if($taskHashMatches.Count -eq 1){return @{state='renamed';recordedTarget=$target;target=$taskHashMatches[0];expectedHash=$Record.vpkHash.ToLowerInvariant();actualHash=$Record.vpkHash.ToLowerInvariant();requiresRepair=$true;blocked=$false;message='旧翻译 VPK 被改名，找到唯一哈希匹配的活动文件';matches=@($taskHashMatches)}}
    $next=$target
    if(Test-Path -LiteralPath $next){$next=$null;foreach($index in 90..199){$candidate=Join-Path $Addons ('pak{0:D2}_dir.vpk' -f $index);if(-not(Test-Path -LiteralPath $candidate)){$next=$candidate;break}}}
    $occupied=Test-Path -LiteralPath $target
    return @{state=if($occupied){'occupied'}else{'missing'};recordedTarget=$target;target=$next;expectedHash=$Record.vpkHash.ToLowerInvariant();actualHash=$actual;requiresRepair=$true;blocked=($null -eq $next);message=if(-not $next){'没有可用的新 VPK 槽位'}elseif($occupied){'旧位置内容已改变；保留该文件，在空槽位恢复完整翻译包'}else{'游戏翻译 VPK 已缺失，可从校验后的新运行包成套恢复'};matches=@()}
}
function Get-DltRuntimeIntegrity([string]$Runtime,$Record) {
    $required=@('DeadlockTranslate.exe','dlt_translate_dir.vpk','pairing.key','wwwroot/index.html','wwwroot/app.js','wwwroot/style.css','data/terms.json')
    $issues=@();$manifest=Join-Path $Runtime 'package-manifest.json';$metadata=$null
    Assert-DltNoLinks $Runtime
    if(Test-Path -LiteralPath $manifest -PathType Leaf){try{Assert-DltNoLinks $manifest;$metadata=Get-Content -LiteralPath $manifest -Raw -Encoding UTF8|ConvertFrom-Json}catch{$issues+=@{file='package-manifest.json';code='invalid-manifest'}}}else{$issues+=@{file='package-manifest.json';code='missing-manifest'}}
    foreach($name in $required){
        $path=Join-Path $Runtime $name;Assert-DltNoLinks $path
        if(-not(Test-Path -LiteralPath $path -PathType Leaf)){$issues+=@{file=$name;code='missing-file'};continue}
        $entries=@($metadata.files|Where-Object {$_.file -is [string] -and $_.file.Replace('\','/') -ceq $name})
        if($entries.Count -ne 1 -or $entries[0].sha256 -notmatch '^[a-fA-F0-9]{64}$'){$issues+=@{file=$name;code='missing-file-hash'};continue}
        $hash=Get-DltHash $path
        if($hash -ne $entries[0].sha256.ToLowerInvariant()){$issues+=@{file=$name;code='hash-mismatch';expectedHash=$entries[0].sha256.ToLowerInvariant();actualHash=$hash}}
    }
    $pair=Join-Path $Runtime 'pairing.key';$pairValid=$false
    if(Test-Path -LiteralPath $pair -PathType Leaf){$pairValid=([IO.File]::ReadAllText($pair)).Trim() -match '^[a-fA-F0-9]{48}$';if(-not $pairValid){$issues+=@{file='pairing.key';code='invalid-pairing-format'}}}
    $vpk=Join-Path $Runtime 'dlt_translate_dir.vpk';$recordMatch=(Test-Path -LiteralPath $vpk -PathType Leaf) -and (Get-DltHash $vpk) -eq $Record.vpkHash.ToLowerInvariant()
    $pairingMatch=$null
    if($pairValid -and (Test-Path -LiteralPath $vpk -PathType Leaf)){$pairingMatch=Test-DltVpkPairing $vpk $pair;if($pairingMatch -eq $false){$issues+=@{file='pairing.key';code='pairing-vpk-mismatch'}}}
    if(-not $recordMatch){$issues+=@{file='dlt_translate_dir.vpk';code='record-runtime-mismatch'}}
    if($metadata.version -and $Record.version -and $metadata.version -ne $Record.version){$issues+=@{file='package-manifest.json';code='version-mismatch'}}
    return @{healthy=($issues.Count -eq 0);requiresRepair=($issues.Count -gt 0);recordVersion=if($Record.version -match '^\d+\.\d+\.\d+(?:\.\d+)?$'){$Record.version}else{$null};runtimeVersion=if($metadata.version -match '^\d+\.\d+\.\d+(?:\.\d+)?$'){$metadata.version}else{$null};pairingFormatValid=$pairValid;pairingMatchesVpk=$pairingMatch;recordVpkMatches=[bool]$recordMatch;issues=@($issues)}
}
function Test-DltVpkPairing([string]$Vpk,[string]$PairFile) {
    if((Get-Item -LiteralPath $Vpk).Length -gt 16777216){return $null}
    # The current uncompressed Panorama resource embeds the bridge identifier twice.
    # Never return or log that value; only return a comparison result. Unknown encodings
    # remain inconclusive rather than inventing evidence of a mismatch.
    $keys=@([regex]::Matches([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($Vpk)),'/bridge/([0-9a-fA-F]{48})(?![0-9a-fA-F])')|ForEach-Object {$_.Groups[1].Value}|Select-Object -Unique)
    if($keys.Count -eq 0){return $null};if($keys.Count -ne 1){return $false}
    return $keys[0] -ceq ([IO.File]::ReadAllText($PairFile)).Trim()
}
function Get-DltVpkResources([string]$Path) {
    Assert-DltNoLinks $Path;$reader=[IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try{
        if($reader.ReadUInt32() -ne 0x55AA1234){throw 'Invalid VPK signature'}
        $version=$reader.ReadUInt32();$size=$reader.ReadUInt32();if($version -eq 2){$null=$reader.BaseStream.Seek(16,1)}elseif($version -ne 1){throw 'Invalid VPK version'}
        if($size -gt 4194304 -or $reader.BaseStream.Position+$size -gt $reader.BaseStream.Length){throw 'Invalid VPK tree'};$end=$reader.BaseStream.Position+$size
        function ReadString {
            $bytes=[Collections.Generic.List[byte]]::new();while($reader.BaseStream.Position -lt $end){$b=$reader.ReadByte();if($b -eq 0){return [Text.Encoding]::UTF8.GetString($bytes.ToArray())};$bytes.Add($b);if($bytes.Count -gt 1024){throw 'Invalid VPK string'}};throw 'Unterminated VPK tree'
        }
        while(($extension=ReadString) -ne ''){while(($directory=ReadString) -ne ''){while(($name=ReadString) -ne ''){
            $null=$reader.ReadUInt32();$preload=$reader.ReadUInt16();$null=$reader.BaseStream.Seek(12+$preload,1);if($reader.BaseStream.Position -gt $end){throw 'Truncated VPK entry'}
            "$directory/$name.$extension"
        }}}
    }finally{$reader.Dispose()}
}
function Assert-DltRecoveryConflicts([string]$Addons,[string]$PackageVpk,[string]$OwnedPath) {
    $own=@(Get-DltVpkResources $PackageVpk)
    foreach($file in @(Get-ChildItem -LiteralPath $Addons -Filter '*.vpk' -File -ErrorAction SilentlyContinue)){
        if($file.FullName -ieq $OwnedPath){continue}
        try{$resources=@(Get-DltVpkResources $file.FullName)}catch{Throw-DltFault 'vpk-unreadable-conflict' 'vpk-recovery' $file.FullName '无法检查资源包，不能安全恢复翻译。' '使用安装器预检确认具体冲突包，或先在 Mod 管理器中处理。'}
        if(Test-DltQolPackage $resources){Write-DltInstallTrace 'conflict-scan' $file.FullName 'qol-ignored';continue}
        if(@($resources|Where-Object {$_ -in $own}).Count){Throw-DltFault 'vpk-resource-conflict' 'vpk-recovery' $file.FullName '其他资源包中已有翻译资源；不能重复恢复。' '在安装器确认具体冲突项；整合包优先在其管理器关闭翻译功能，不自动覆盖整包。'}
    }
}
