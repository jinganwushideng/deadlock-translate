# The installer is the sole writer. Child scripts inherit the active journal and cancel signal.
function Write-DltDurable([string]$Path,[byte[]]$Bytes,[switch]$Cancelable) {
    Assert-DltNoLinks $Path
    $temp=$Path+'.tmp-'+[Guid]::NewGuid().ToString('N')
    try {
        $s=[IO.FileStream]::new($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {
            for($offset=0;$offset -lt $Bytes.Length;$offset+=$count){
                if($Cancelable){Test-DltStop}
                $count=[Math]::Min(1048576,$Bytes.Length-$offset)
                $s.Write($Bytes,$offset,$count)
            }
            $s.Flush($true)
        }finally{$s.Dispose()}
        if([IO.File]::Exists($Path)){[IO.File]::Replace($temp,$Path,[NullString]::Value)}else{[IO.File]::Move($temp,$Path)}
    }finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function Write-DltJournalJson([string]$Path,$Value){
    Write-DltDurable $Path ([Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 15 -Compress)))
}
function Read-DltJournalJson([string]$Path){
    Assert-DltNoLinks $Path
    if((Get-Item -LiteralPath $Path).Length -gt 2097152){throw '安装恢复记录过大。'}
    [IO.File]::ReadAllText($Path,[Text.Encoding]::UTF8)|ConvertFrom-Json
}
function Test-DltStop {
    if(-not $script:DltRestoring -and $env:DLT_INSTALL_CANCEL -and [IO.File]::Exists($env:DLT_INSTALL_CANCEL)){
        Throw-DltFault 'cancelled' 'safe-boundary' '' '已请求取消，正在恢复已写入的文件。'
    }
}
function Send-DltJournalProgress([string]$Stage){
    if($env:DLT_INSTALL_PROGRESS){
        try{Write-DltJournalJson $env:DLT_INSTALL_PROGRESS @{stage=$Stage;utc=[DateTime]::UtcNow.ToString('o')}}catch{}
    }
}
function Get-DltJournalHash([string]$Path){
    Assert-DltNoLinks $Path
    if([IO.File]::Exists($Path)){return Get-DltHash $Path}
    if(Test-Path -LiteralPath $Path){throw '恢复目标被目录占用。'}
    return '-'
}
function Get-DltOwner { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
function Read-DltTransaction([string]$Folder){
    $tx=Read-DltJournalJson (Join-Path $Folder 'transaction.json')
    if($tx.schema -ne 1 -or $tx.owner -ne (Get-DltOwner) -or $tx.id -notmatch '^[a-f0-9]{32}$' -or $tx.id -cne [IO.Path]::GetFileName($Folder)){throw '安装恢复记录不属于当前 Windows 用户或格式不受支持。'}
    return $tx
}
function Get-DltPendingTransactions([string]$State){
    $root=Join-Path $State 'transactions';Assert-DltNoLinks $root
    if(-not [IO.Directory]::Exists($root)){return}
    foreach($dir in Get-ChildItem -LiteralPath $root -Directory | Sort-Object LastWriteTime -Descending){
        if($dir.Name -notmatch '^[a-f0-9]{32}$'){continue}
        if(-not [IO.File]::Exists((Join-Path $dir.FullName 'transaction.json'))){continue}
        try{$tx=Read-DltTransaction $dir.FullName}catch{throw '存在无法读取的安装恢复记录。请导出安装日志，保留该目录。'}
        if($tx.phase -notin @('complete','rolled-back','preserved-conflicts')){[pscustomobject]@{id=$tx.id;phase=$tx.phase;version=$tx.version;game=$tx.game;runtime=$tx.runtime;mode=$tx.request.mode;buildId=$tx.buildId}}
    }
}
function Enter-DltOperation([string]$State){
    $path=Join-Path $State 'installation-operation.lock';Assert-DltNoLinks $path
    [IO.Directory]::CreateDirectory($State)|Out-Null
    try{[IO.File]::Open($path,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}catch{Throw-DltFault 'operation-active' 'installation-lock' $path '另一安装、修复或清理步骤仍在运行。'}
}
function Start-DltTransaction([string]$State,$Request,[string]$Payload,[string]$Game,[string]$Runtime,[string]$OldGame,[string]$Backup){
    if($Request.transactionId -notmatch '^[a-f0-9]{32}$'){throw '无效的安装事务编号。'}
    if(@(Get-DltPendingTransactions $State).Count){throw '有未完成的安装，请先恢复。'}
    $folder=Join-Path (Join-Path $State 'transactions') $Request.transactionId
    Assert-DltNoLinks $folder
    if(Test-Path -LiteralPath $folder){throw '安装事务编号已存在。'}
    $metaFile=Join-Path $Payload 'package-manifest.json';$meta=Read-DltJournalJson $metaFile
    [IO.Directory]::CreateDirectory((Join-Path $folder 'files'))|Out-Null
    $tx=[ordered]@{schema=1;id=$Request.transactionId;owner=(Get-DltOwner);phase='staging';state=$State;backup=$Backup;game=$Game;oldGame=$OldGame;runtime=$Runtime;steam=[string]$Request.steam;version=$meta.version;buildId=$meta.buildId;manifestHash=(Get-DltHash $metaFile);request=$Request}
    # Persist the verified package before modifying live files. An interrupted staging copy has no journal and no game writes.
    Write-DltJournalJson (Join-Path $folder 'transaction.json') $tx
    $env:DLT_INSTALL_JOURNAL=$folder
    $retained=Join-Path $folder 'payload';[IO.Directory]::CreateDirectory($retained)|Out-Null
    foreach($item in @($meta.files)+@(@{file='package-manifest.json';sha256=$tx.manifestHash})){
        $relative=[string]$item.file
        if([IO.Path]::IsPathRooted($relative) -or ($relative -split '[\\/]') -contains '..'){throw '安装包路径越界。'}
        $from=Join-Path $Payload $relative;$to=Join-Path $retained $relative
        Assert-DltNoLinks $from;Assert-DltNoLinks $to
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))|Out-Null
        Send-DltJournalProgress '正在保留安装恢复所需文件'
        Write-DltDurable $to ([IO.File]::ReadAllBytes($from)) -Cancelable
        if((Get-DltHash $to) -ne $item.sha256){throw '恢复安装包复制校验失败。'}
    }
    Set-DltTransactionPhase 'core'
}
function Set-DltTransactionPhase([string]$Phase){
    if(-not $env:DLT_INSTALL_JOURNAL){return}
    $path=Join-Path $env:DLT_INSTALL_JOURNAL 'transaction.json';$tx=Read-DltTransaction $env:DLT_INSTALL_JOURNAL
    $tx.phase=$Phase;Write-DltJournalJson $path $tx
}
function Enter-DltJournalWorker([string]$Folder){
    $path=Join-Path $Folder 'worker.lock';Assert-DltNoLinks $path
    try{[IO.File]::Open($path,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}catch{throw '上次安装的工作进程仍未退出，请稍后重试。'}
}
function Clear-DltTransactionStaging([string]$Folder){
    # Only terminal, owned transaction data is disposable. Keep the small transaction summary.
    try{
        $tx=Read-DltTransaction $Folder
        if($tx.phase -notin @('complete','rolled-back')){return}
        foreach($name in @('payload','files')){
            $sub=Join-Path $Folder $name;Assert-DltNoLinks $sub
            if(-not [IO.Directory]::Exists($sub)){continue}
            $all=@(Get-ChildItem -LiteralPath $sub -Recurse -Force)
            if(@($all|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){continue}
            [IO.Directory]::Delete($sub,$true)
        }
    }catch{}
}
function Complete-DltTransaction {
    if($env:DLT_INSTALL_JOURNAL){Set-DltTransactionPhase 'complete';Clear-DltTransactionStaging $env:DLT_INSTALL_JOURNAL}
}
function Assert-DltJournalTarget($Tx,[string]$Path){
    Assert-DltNoLinks $Path
    $p=[IO.Path]::GetFullPath($Path)
    $comparison=[StringComparison]::OrdinalIgnoreCase
    foreach($g in @($Tx.game,$Tx.oldGame)){
        if(-not $g){continue}
        if($p -ieq (Join-Path $g 'game\citadel\gameinfo.gi')){return}
        $addons=(Join-Path $g 'game\citadel\addons')+'\'
        if($p.StartsWith($addons,$comparison) -and $p.Substring($addons.Length) -match '^[^\\/:]+\.vpk(?:\.dlt-(?:disabled|uninstall|uninstalled|migrated)-[a-f0-9]{32}\.bak)?$'){return}
    }
    if($Tx.runtime -and $p.StartsWith($Tx.runtime.TrimEnd('\')+'\',$comparison)){
        $name=[IO.Path]::GetFileName($p)
        if($name -notin @('settings.json','secrets.dpapi','install-manifest.json') -and [IO.Path]::GetExtension($name) -notin @('.dpapi','.cer','.pfx')){return}
    }
    if($p -in @((Join-Path $Tx.state 'install-manifest.json'),(Join-Path $Tx.state 'terms.json'))){return}
    if([IO.Path]::GetDirectoryName($p) -ieq $Tx.state -and [IO.Path]::GetFileName($p) -match '^(uninstalled-[a-f0-9]{32}\.json|install-manifest\.json\.invalid-[a-f0-9]{32}\.bak)$'){return}
    if($Tx.backup -and $p.StartsWith($Tx.backup.TrimEnd('\')+'\',$comparison) -and $Tx.backup.StartsWith((Join-Path $Tx.state 'backups')+'\',$comparison)){return}
    if($p.StartsWith((Join-Path $Tx.state 'backups')+'\',$comparison) -and $p -match '\\update-[^\\]+\\duplicate-pak\d{2,3}_dir\.vpk\.bak$'){return}
    if($Tx.steam){$root=[IO.Path]::GetFullPath($Tx.steam).TrimEnd('\')+'\userdata\';if($p.StartsWith($root,$comparison) -and $p.Substring($root.Length) -match '^\d+\\config\\localconfig\.vdf$'){return}}
    throw '恢复记录中的文件不在允许的安装范围。'
}
function Add-DltJournalOperation([object[]]$Changes,[string]$Stage){
    Test-DltStop
    $root=$env:DLT_INSTALL_JOURNAL
    if(-not $root -or $script:DltRestoring -or $Stage.StartsWith('rollback')){return}
    Send-DltJournalProgress $Stage
    $tx=Read-DltTransaction $root
    $files=Join-Path $root 'files';Assert-DltNoLinks $files
    $number=@(Get-ChildItem -LiteralPath $files -Filter '*.json' -File).Count+1
    $id=$number.ToString('D6');$entries=@();$i=0
    foreach($change in $Changes){
        $path=[IO.Path]::GetFullPath($change.path);Assert-DltJournalTarget $tx $path
        $before=Get-DltJournalHash $path
        if($change.expected -and $before -ne $change.expected){throw '文件在准备恢复记录时发生变化。'}
        $blob='';$attrs=0
        if($before -ne '-'){
            $blob="$id-$i.bin";$attrs=[int][IO.File]::GetAttributes($path)
            Write-DltDurable (Join-Path $files $blob) ([IO.File]::ReadAllBytes($path)) -Cancelable
            if((Get-DltHash (Join-Path $files $blob)) -ne $before){throw '安装恢复备份校验失败。'}
        }
        $entries+=@{path=$path;before=$before;after=$change.after;blob=$blob;attributes=$attrs};$i++
    }
    Write-DltJournalJson (Join-Path $files ($id+'.json')) @{stage=$Stage;phase=$tx.phase;changes=$entries}
}
function Get-DltBytesHash([byte[]]$Bytes){
    $sha=[Security.Cryptography.SHA256]::Create()
    try{[BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
}
function Move-DltTrackedFile([string]$Source,[string]$Destination,[string]$Stage='move'){
    Test-DltStop
    if($env:DLT_INSTALL_JOURNAL -and -not $script:DltRestoring){
        $hash=Get-DltJournalHash $Source
        if($hash -eq '-'){throw '移动源文件不存在。'}
        Add-DltJournalOperation @(@{path=$Source;expected=$hash;after='-'},@{path=$Destination;expected='-';after=$hash}) $Stage
        if((Get-DltJournalHash $Source) -ne $hash){throw '移动源文件已变化。'}
    }
    [IO.File]::Move($Source,$Destination)
}
function Remove-DltTrackedFile([string]$Path,[string]$Expected,[string]$Stage='remove'){
    Test-DltStop
    Add-DltJournalOperation @(@{path=$Path;expected=$Expected;after='-'}) $Stage
    if((Get-DltJournalHash $Path) -ne $Expected){throw '要移除的文件已变化。'}
    [IO.File]::Delete($Path)
}
function Restore-DltTransaction([string]$Folder,[switch]$SteamOnly){
    $tx=Read-DltTransaction $Folder
    $paths=[ordered]@{};$issues=@();$files=Join-Path $Folder 'files';Assert-DltNoLinks $files
    foreach($record in Get-ChildItem -LiteralPath $files -Filter '*.json' -File | Sort-Object Name){
        if($record.Name -notmatch '^\d{6}\.json$'){throw '恢复步骤名称无效。'}
        $op=Read-DltJournalJson $record.FullName
        if($SteamOnly -and $op.phase -ne 'steam'){continue}
        foreach($entry in @($op.changes)){
            Assert-DltJournalTarget $tx $entry.path
            if($entry.before -notmatch '^(?:-|[a-f0-9]{64})$' -or $entry.after -notmatch '^(?:-|[a-f0-9]{64})$' -or ($entry.before -ne '-' -and $entry.blob -notmatch '^\d{6}-\d+\.bin$')){throw '恢复文件记录无效。'}
            if(-not $paths.Contains($entry.path)){$paths[$entry.path]=@{original=$entry;known=@($entry.before)}}
            $paths[$entry.path].known+=@($entry.after)
        }
    }
    $script:DltRestoring=$true
    try{
        $targets=@($paths.Keys);[array]::Reverse($targets)
        foreach($path in $targets){
            try{
                $entry=$paths[$path].original;$actual=Get-DltJournalHash $path
                if($actual -notin $paths[$path].known){$issues+=@{path=$path;reason='changed'};continue}
                if($actual -ne $entry.before){
                    if($entry.before -eq '-'){
                        if((Get-DltJournalHash $path) -ne $actual){throw '文件已变化。'}
                        [IO.File]::Delete($path)
                    }else{
                        $blob=Join-Path $files $entry.blob
                        if((Get-DltJournalHash $blob) -ne $entry.before){throw '恢复备份损坏。'}
                        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null
                        Write-DltAtomic $path ([IO.File]::ReadAllBytes($blob)) -ExpectedHash $actual -AllowReadOnly -Stage 'rollback-interrupted'
                    }
                }
                if($entry.before -ne '-') {[IO.File]::SetAttributes($path,[IO.FileAttributes]$entry.attributes)}
            }catch{$issues+=@{path=$path;reason='restore-unavailable'}}
        }
    }finally{$script:DltRestoring=$false}
    return @($issues)
}
