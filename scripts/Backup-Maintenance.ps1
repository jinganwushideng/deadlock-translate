param(
    [ValidateSet('Inspect','Clean')][string]$Action='Inspect',
    [string]$StateDirectory=(Join-Path $env:LOCALAPPDATA 'DeadlockTranslate'),
    [switch]$IncludeLegacy,
    [string]$PlanToken
)
# Shared by the updater and the on-demand settings API. Never runs in a game callback.
function Assert-DltBackupPath([string]$Path){
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    if($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\','/')){throw 'Backup path cannot be a drive root'}
    for($cursor=$full;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Backup path contains a link'}
    }
    return $full
}
function Enter-DltBackupLock([string]$State){
    $state=Assert-DltBackupPath $State
    [IO.Directory]::CreateDirectory($state)|Out-Null
    $lock=Join-Path $state 'backup-maintenance.lock';$null=Assert-DltBackupPath $lock
    try{return [IO.File]::Open($lock,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
    catch{throw '备份正在更新或清理，请稍后重新检查。'}
}
function Set-DltBackupState([string]$Backup,[string]$Status){
    $null=Assert-DltBackupPath $Backup
    if($Status -notin @('in-progress','succeeded','failed','deleting','cleanup-failed')){throw 'Invalid backup status'}
    $path=Join-Path $Backup 'backup-state.json';$null=Assert-DltBackupPath $path
    $temp=Join-Path $Backup ('state-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    try{
        [IO.File]::WriteAllText($temp,(@{schema=1;kind='update';status=$Status;updatedUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
        if([IO.File]::Exists($path)){
            $previous=$temp+'.previous'
            [IO.File]::Replace($temp,$path,$previous)
            [IO.File]::Delete($previous)
        }else{[IO.File]::Move($temp,$path)}
    }finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function Get-DltBackupTree([string]$Path){
    $null=Assert-DltBackupPath $Path
    $stack=[Collections.Generic.Stack[string]]::new();$stack.Push($Path)
    $files=[Collections.Generic.List[object]]::new();$bytes=0L;$visited=0
    while($stack.Count){
        foreach($item in @(Get-ChildItem -LiteralPath $stack.Pop() -Force -ErrorAction Stop)){
            $visited++;if($visited -gt 10000){throw 'Backup inventory limit exceeded'}
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Backup contains a link'}
            if($item.PSIsContainer){$stack.Push($item.FullName)}else{
                $bytes+=$item.Length
                $files.Add([pscustomobject]@{relative=$item.FullName.Substring($Path.Length+1).Replace('\','/');length=$item.Length;ticks=$item.LastWriteTimeUtc.Ticks})
            }
        }
    }
    return @{files=@($files.ToArray()|Sort-Object relative);bytes=$bytes}
}
function Get-DltBackupHash([string]$Path){
    $sha=[Security.Cryptography.SHA256]::Create();$stream=[IO.File]::OpenRead($Path)
    try{return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}
    finally{$stream.Dispose();$sha.Dispose()}
}
function Test-DltBackupContents([string]$Path,$Tree,[switch]$VerifyHashes){
    try{
        $planPath=Join-Path $Path 'file-plan.json';$record=Join-Path $Path 'install-manifest-before.json'
        if(-not [IO.File]::Exists($planPath) -or -not [IO.File]::Exists($record) -or (Get-Item -LiteralPath $planPath).Length -gt 2097152 -or (Get-Item -LiteralPath $record).Length -gt 2097152){return $false}
        $old=Get-Content -LiteralPath $record -Raw -Encoding UTF8|ConvertFrom-Json -ErrorAction Stop
        # PS 5.1 can emit a JSON array as one pipeline object. Flatten after assignment.
        $decoded=Get-Content -LiteralPath $planPath -Raw -Encoding UTF8|ConvertFrom-Json -ErrorAction Stop
        $plan=@($decoded)
        if($plan.Count -eq 0 -or $plan.Count -gt 512){return $false}
        $present=@{};foreach($file in $Tree.files){$present[$file.relative]=$true}
        foreach($critical in @('DeadlockTranslate.exe','dlt_translate_dir.vpk','pairing.key','data/terms.json','wwwroot/index.html','wwwroot/app.js','wwwroot/style.css')){
            if(-not $present.ContainsKey('runtime/'+$critical)){return $false}
        }
        # A successful repair may have started from an incomplete/mixed runtime. Keep
        # that snapshot protected; it must not displace a complete paired recovery point.
        $oldVpk=@($plan|Where-Object relative -eq 'dlt_translate_dir.vpk')
        if($oldVpk.Count -ne 1 -or -not $old.vpkHash -or $oldVpk[0].beforeHash -ine $old.vpkHash){return $false}
        if((Get-Item -LiteralPath (Join-Path $Path 'runtime/pairing.key')).Length -gt 256 -or ([IO.File]::ReadAllText((Join-Path $Path 'runtime/pairing.key'))).Trim() -notmatch '^[a-fA-F0-9]{48}$'){return $false}
        $allowed=@{};foreach($name in @('file-plan.json','install-manifest-before.json','translation-before.vpk','gameinfo-before.gi','user-terms-before.json','backup-state.json')){$allowed[$name]=$true}
        foreach($step in $plan){
            $relative=[string]$step.relative
            if(-not $relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or @($relative.Replace('\','/').Split('/')|Where-Object {$_ -in @('','.','..') -or $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0}).Count){return $false}
            $key='runtime/'+$relative.Replace('\','/');if($allowed.ContainsKey($key)){return $false}
            if($step.beforeHash){
                if([string]$step.beforeHash -notmatch '^[a-fA-F0-9]{64}$'){return $false}
                # Tree already checked every descendant for links. Match its exact relative paths
                # rather than re-walking all ancestors for every file during a read-only preview.
                $file=Join-Path $Path $key
                if(-not $present.ContainsKey($key)){return $false}
                if($VerifyHashes -and (Get-DltBackupHash $file) -ne $step.beforeHash.ToLowerInvariant()){return $false}
                $allowed[$key]=$true
            }
        }
        # Never delete unexpected files, conflict-recovery copies, or unknown user material.
        foreach($file in $Tree.files){if(-not $allowed.ContainsKey($file.relative)){return $false}}
        if($VerifyHashes -and [IO.File]::Exists((Join-Path $Path 'translation-before.vpk'))){
            if(-not $old.vpkHash -or (Get-DltBackupHash (Join-Path $Path 'translation-before.vpk')) -ne $old.vpkHash.ToLowerInvariant()){return $false}
        }
        return $true
    }catch{return $false}
}
function Get-DltBackupInventory([string]$State,[switch]$Legacy){
    $state=Assert-DltBackupPath $State;$root=Join-Path $state 'backups';$null=Assert-DltBackupPath $root
    $entries=[Collections.Generic.List[object]]::new();$fingerprint=[Text.StringBuilder]::new()
    if(Test-Path -LiteralPath $root){
        $dirs=@(Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction Stop|Sort-Object Name -Descending)
        if($dirs.Count -gt 1000){throw '备份数量过多，请导出诊断后处理。'}
        foreach($dir in $dirs){
            $bytes=0L;$reason='unknown';$kind='other';$valid=$false;$eligible=$false
            try{
                $tree=Get-DltBackupTree $dir.FullName;$bytes=$tree.bytes
                $null=$fingerprint.Append($dir.Name).Append('|').Append(($tree.files|ConvertTo-Json -Depth 4 -Compress)).Append('|')
                if($dir.Name -match '^update-\d{8}-\d{9}-[a-f0-9]{32}$'){
                    $marker=Join-Path $dir.FullName 'backup-state.json';$kind='legacy';$reason='legacy-protected'
                    if(Test-Path -LiteralPath $marker){
                        if((Get-Item -LiteralPath $marker).Length -gt 4096){throw 'Invalid marker'}
                        $s=Get-Content -LiteralPath $marker -Raw -Encoding UTF8|ConvertFrom-Json
                        $null=$fingerprint.Append((Get-DltBackupHash $marker))
                        $kind='update';$reason='incomplete-or-failed'
                        if($s.schema -eq 1 -and $s.kind -eq 'update' -and $s.status -eq 'succeeded'){$valid=Test-DltBackupContents $dir.FullName $tree;$eligible=$valid;$reason=if($valid){'candidate'}else{'invalid-backup'}}
                    }else{
                        $valid=Test-DltBackupContents $dir.FullName $tree
                        if($Legacy -and $valid){$eligible=$true;$reason='candidate'}elseif(-not $valid){$reason='invalid-backup'}
                    }
                }elseif($bytes -eq 0){$reason='empty-directory'}
            }catch{$reason='linked-unreadable-or-invalid';$null=$fingerprint.Append($dir.Name).Append('|protected|').Append($dir.LastWriteTimeUtc.Ticks)}
            $entries.Add([pscustomobject]@{name=$dir.Name;bytes=$bytes;kind=$kind;reason=$reason;eligible=$eligible;delete=$false})
        }
    }
    $kept=0;foreach($entry in $entries){if($entry.eligible){if($kept -lt 2){$entry.reason='kept-recent';$kept++}else{$entry.delete=$true}}}
    $null=$fingerprint.Append('|legacy=').Append([bool]$Legacy)
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$token=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($fingerprint.ToString())))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
    $total=0L;$cleanBytes=0L;$cleanCount=0;$protected=0
    foreach($entry in $entries){$total+=$entry.bytes;if($entry.delete){$cleanCount++;$cleanBytes+=$entry.bytes}elseif(-not $entry.eligible){$protected++}}
    return [pscustomobject]@{keepCount=2;includeLegacy=[bool]$Legacy;backupCount=$entries.Count;totalBytes=$total;cleanupCount=$cleanCount;cleanupBytes=$cleanBytes;keptCount=$kept;protectedCount=$protected;planToken=$token;entries=$entries.ToArray()}
}
function Write-DltBackupAudit([string]$State,[string]$ActionName,[long]$Bytes,[int]$Count,[int]$Failures){
    try{
        $path=Join-Path $State 'backup-maintenance.jsonl';$null=Assert-DltBackupPath $path
        if([IO.File]::Exists($path) -and (Get-Item -LiteralPath $path).Length -gt 131072){$old=$path+'.1';$null=Assert-DltBackupPath $old;if([IO.File]::Exists($old)){[IO.File]::Delete($old)};[IO.File]::Move($path,$old)}
        [IO.File]::AppendAllText($path,(@{utc=[DateTime]::UtcNow.ToString('o');action=$ActionName;removedBytes=$Bytes;removedCount=$Count;failures=$Failures}|ConvertTo-Json -Compress)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
    }catch{} # Storage maintenance must never break a successful installation.
}
function Invoke-DltBackupCleanup([string]$State,[switch]$Legacy,[string]$ExpectedToken){
    $plan=Get-DltBackupInventory $State -Legacy:$Legacy
    if($ExpectedToken -and $ExpectedToken -cne $plan.planToken){throw '备份已变化，请重新检查占用后再清理。'}
    $root=Assert-DltBackupPath (Join-Path $State 'backups');$removed=0;$removedBytes=0L;$warnings=@()
    # Do not discard older recovery points unless the two retained snapshots are intact.
    foreach($recent in @($plan.entries|Where-Object reason -eq 'kept-recent')){
        $path=Join-Path $root $recent.name;$tree=Get-DltBackupTree $path
        if(-not(Test-DltBackupContents $path $tree -VerifyHashes)){throw '最近保留的备份校验未通过，已停止清理，请导出诊断。'}
    }
    foreach($entry in $plan.entries){
        if(-not $entry.delete){continue}
        $path=Join-Path $root $entry.name
        try{
            $full=Assert-DltBackupPath $path
            if([IO.Path]::GetDirectoryName($full) -ine $root -or $entry.name -notmatch '^update-\d{8}-\d{9}-[a-f0-9]{32}$'){throw 'Unsafe cleanup target'}
            $tree=Get-DltBackupTree $full
            if(-not(Test-DltBackupContents $full $tree -VerifyHashes)){throw 'Backup validation failed'}
            Set-DltBackupState $full 'deleting'
            # Validate the resolved absolute target and every descendant immediately before removal.
            $null=Assert-DltBackupPath $full;$null=Get-DltBackupTree $full
            Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop
            $removed++;$removedBytes+=$entry.bytes
        }catch{
            $warnings+=@{name=$entry.name;code='preserved-or-cleanup-failed'}
            if([IO.Directory]::Exists($path)){try{Set-DltBackupState $path 'cleanup-failed'}catch{}}
        }
    }
    Write-DltBackupAudit $State $(if($ExpectedToken){'manual-clean'}else{'automatic-clean'}) $removedBytes $removed $warnings.Count
    return @{removedCount=$removed;removedBytes=$removedBytes;warnings=@($warnings);inventory=(Get-DltBackupInventory $State -Legacy:$Legacy)}
}
if($MyInvocation.InvocationName -ne '.'){
    $ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);$held=$null
    try{
        $held=Enter-DltBackupLock $StateDirectory
        if($Action -eq 'Inspect'){$result=Get-DltBackupInventory $StateDirectory -Legacy:$IncludeLegacy}
        else{
            if($PlanToken -notmatch '^[a-f0-9]{64}$'){throw '请先检查备份占用，再执行清理。'}
            $result=Invoke-DltBackupCleanup $StateDirectory -Legacy:$IncludeLegacy -ExpectedToken $PlanToken
        }
        $result|ConvertTo-Json -Depth 8 -Compress|Write-Output
    }catch{@{error=$_.Exception.Message}|ConvertTo-Json -Compress|Write-Output;exit 1}
    finally{if($held){$held.Dispose()}}
}
