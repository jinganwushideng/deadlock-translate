# Shared by the GUI, command-line installer and updater. Never logs config contents.
function Assert-DltNoLinks([string]$Path) {
    for($cursor=[IO.Path]::GetFullPath($Path);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Throw-DltFault 'linked-path' 'preflight' $cursor '路径包含链接，请选择普通目录。'
        }
    }
}
function Get-DltHash([string]$Path) {
    $hasher=[Security.Cryptography.SHA256]::Create();$stream=$null
    try{$stream=[IO.File]::OpenRead($Path);([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}
    catch{$ex=[IO.IOException]::new('无法读取文件校验值。',$_.Exception);$ex.Data['dltFault']=Get-DltFault $_ 'hash-read' $Path;throw $ex}
    finally{if($stream){$stream.Dispose()};$hasher.Dispose()}
}
function Get-DltFault($ErrorRecord,[string]$Stage='operation',[string]$Path='') {
    $ex=$ErrorRecord.Exception
    while($ex) {
        if($ex.Data.Contains('dltFault')) {return $ex.Data['dltFault']}
        if(-not $ex.InnerException) {break};$ex=$ex.InnerException
    }
    $native=$ex.HResult -band 65535
    $code='operation-failed';$message=$ErrorRecord.Exception.Message;$remedy='导出安装日志，并保留本次备份；不要删除其他 Mod 或个人配置。'
    if($message.Length -gt 2000){$message=$message.Substring(0,2000)}
    switch($native) {
        5 {$code='access-denied';$message='写入被拒绝。';$remedy='先退出游戏；检查下列文件是否只读。若目录受保护，使用“以管理员权限重开”，仍选择原 Windows 用户。不要修改整个磁盘权限。'}
        32 {$code='file-locked';$message='文件正被其他程序占用。';$remedy='退出游戏及旧后台，等待安全软件扫描完成后重新检测；不要强制结束无关程序。'}
        33 {$code='file-locked';$message='文件正被锁定。';$remedy='退出游戏及旧后台，等待扫描完成后重新检测。'}
        112 {$code='disk-full';$message='磁盘空间不足。';$remedy='释放目标磁盘空间后重新检测；安装与回退需要额外备份空间。'}
        206 {$code='path-too-long';$message='文件路径过长。';$remedy='选择较短的程序目录，避免多层嵌套解压。'}
        50 {$code='atomic-replace-unsupported';$message='该文件系统不支持当前原子替换操作。';$remedy='请选择本机 NTFS 程序目录；保留原文件和备份，不要通过先删原文件的方式继续。'}
    }
    $attributes='';try{if($Path -and (Test-Path -LiteralPath $Path)){$attributes=(Get-Item -LiteralPath $Path -Force).Attributes.ToString()}}catch{}
    return [ordered]@{code=$code;stage=$Stage;path=$Path;message=$message;remedy=$remedy;nativeError=$native;hresult=('0x{0:X8}' -f $ex.HResult);attributes=$attributes;rollback=@()}
}
function Throw-DltFault([string]$Code,[string]$Stage,[string]$Path,[string]$Message,[string]$Remedy='返回检查页重新检测，或导出安装日志。') {
    $ex=[IO.IOException]::new($Message)
    $attributes='';try{if($Path -and (Test-Path -LiteralPath $Path)){$attributes=[IO.File]::GetAttributes($Path).ToString()}}catch{}
    $ex.Data['dltFault']=[ordered]@{code=$Code;stage=$Stage;path=$Path;message=$Message;remedy=$Remedy;nativeError=0;hresult='';attributes=$attributes;rollback=@()}
    throw $ex
}
function Get-DltFileCheck([string]$Path,[string]$Stage,[switch]$AllowReadOnly,[switch]$DeferLock) {
    $entry=[ordered]@{path=$Path;stage=$Stage;code='ready';message='文件检查通过';blocking=$false;requiresReadOnlyConsent=$false;attributes='';remedy='';nativeError=0;hresult='';rollback=@()}
    try {
        Assert-DltNoLinks $Path
        if(Test-Path -LiteralPath $Path) {
            $item=Get-Item -LiteralPath $Path -Force
            if($item.PSIsContainer){Throw-DltFault 'file-is-directory' $Stage $Path '目标文件名被目录占用。'}
            $entry.attributes=$item.Attributes.ToString()
            $readonly=[bool]($item.Attributes -band [IO.FileAttributes]::ReadOnly)
            if($readonly -and $AllowReadOnly) {$entry.code='read-only';$entry.message='游戏配置为只读，需要明确允许临时解除只读';$entry.requiresReadOnlyConsent=$true;$entry.remedy='仅临时解除 gameinfo.gi 的只读属性，操作后恢复；不会更改目录权限。'}
            elseif($readonly){Throw-DltFault 'read-only' $Stage $Path '目标文件为只读。' '检查这个文件的属性；不自动修改其他文件或目录。'}
            $access=if($readonly){[IO.FileAccess]::Read}else{[IO.FileAccess]::ReadWrite}
            $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,$access,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));$stream.Dispose()
        }else{$entry.message='待创建文件；执行前将检查目录写入权限'}
    }catch{
        $fault=Get-DltFault $_ $Stage $Path;$entry.code=$fault.code;$entry.message=$fault.message;$entry.remedy=$fault.remedy
        $entry.nativeError=$fault.nativeError;$entry.hresult=$fault.hresult
        $entry.blocking=-not ($DeferLock -and $fault.code -eq 'file-locked')
        if(-not $entry.blocking){$entry.message='旧后台文件正在使用；更新时先正常停止，再检查写入权限'}
    }
    return $entry
}
function Throw-DltCheck($Check){$ex=[IO.IOException]::new($Check.message);$ex.Data['dltFault']=$Check;throw $ex}
function Assert-DltWritableDirectory([string]$Path,[string]$Stage) {
    $probe=$null
    try {
        Assert-DltNoLinks $Path
        $parent=[IO.Path]::GetFullPath($Path)
        while(-not (Test-Path -LiteralPath $parent)) {$parent=[IO.Path]::GetDirectoryName($parent);if(-not $parent){throw 'No existing parent'}}
        if(-not (Get-Item -LiteralPath $parent -Force).PSIsContainer){Throw-DltFault 'parent-is-file' $Stage $parent '目标目录的上级路径被文件占用。'}
        $probe=Join-Path $parent ('.dlt-write-test-'+[Guid]::NewGuid().ToString('N')+'.tmp')
        $stream=[IO.FileStream]::new($probe,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::DeleteOnClose)
        try{$stream.WriteByte(0);$stream.Flush($true)}finally{$stream.Dispose()}
    }catch{
        $ex=[IO.IOException]::new('目录写入检查失败。',$_.Exception);$ex.Data['dltFault']=Get-DltFault $_ $Stage $Path;throw $ex
    }
}
function Write-DltAtomic([string]$Path,[byte[]]$Bytes,[string]$ExpectedHash='', [string]$Backup='',[switch]$AllowReadOnly,[string]$Stage='commit') {
    if(Get-Command Write-DltInstallTrace -ErrorAction SilentlyContinue){Write-DltInstallTrace $Stage $Path 'prepare' $ExpectedHash}
    $temporary=$Path+'.dlt-write-'+[Guid]::NewGuid().ToString('N')+'.tmp';$attributes=$null;$changedAttribute=$false
    try {
        Assert-DltNoLinks $Path
        if(Test-Path -LiteralPath $Path) {
            $attributes=[IO.File]::GetAttributes($Path)
            if($attributes -band [IO.FileAttributes]::ReadOnly) {
                if(-not $AllowReadOnly){Throw-DltFault 'read-only' $Stage $Path '目标文件为只读。' '请在检查页确认仅临时解除游戏配置的只读属性。'}
                [IO.File]::SetAttributes($Path,($attributes -band (-bnot [IO.FileAttributes]::ReadOnly)));$changedAttribute=$true
            }
        }
        $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write($Bytes,0,$Bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        if($ExpectedHash -eq '-') {if(Test-Path -LiteralPath $Path){Throw-DltFault 'file-changed' $Stage $Path '目标文件在检查后出现，未覆盖。'}}
        elseif($ExpectedHash -and (-not(Test-Path -LiteralPath $Path) -or (Get-DltHash $Path) -ne $ExpectedHash.ToLowerInvariant())) {Throw-DltFault 'file-changed' $Stage $Path '文件在检查后发生变化，未覆盖。'}
        if(Test-Path -LiteralPath $Path) {
            $backupPath=if($Backup){$Backup}else{[NullString]::Value}
            [IO.File]::Replace($temporary,$Path,$backupPath)
        }else{[IO.File]::Move($temporary,$Path)}
        if(Get-Command Write-DltInstallTrace -ErrorAction SilentlyContinue){Write-DltInstallTrace $Stage $Path 'committed' $ExpectedHash (Get-DltHash $Path)}
    }catch{
        if(Get-Command Write-DltInstallTrace -ErrorAction SilentlyContinue){Write-DltInstallTrace $Stage $Path 'failed' $ExpectedHash}
        $ex=[IO.IOException]::new('文件提交失败。',$_.Exception);$ex.Data['dltFault']=Get-DltFault $_ $Stage $Path;throw $ex
    }finally{
        if($changedAttribute -and (Test-Path -LiteralPath $Path)){[IO.File]::SetAttributes($Path,$attributes)}
        if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue}
    }
}
function Save-DltFailure($ErrorRecord,[string]$File,[string]$Stage='operation',[string]$Path='') {
    if($File){try{[IO.File]::WriteAllText($File,((Get-DltFault $ErrorRecord $Stage $Path)|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))}catch{}}
}
function Move-DltStagedDirectory([string]$Source,[string]$Destination) {
    Assert-DltNoLinks $Source;Assert-DltNoLinks $Destination
    for($attempt=0;$attempt -lt 4;$attempt++) {
        if(Test-Path -LiteralPath $Destination){Throw-DltFault 'file-changed' 'runtime-commit' $Destination '程序目标目录在准备期间出现，未覆盖。'}
        try{[IO.Directory]::Move($Source,$Destination);return}catch{
            $fault=Get-DltFault $_ 'runtime-commit' $Destination
            if($attempt -lt 3 -and $fault.nativeError -in @(5,32,33) -and (Test-Path -LiteralPath $Source)) {Start-Sleep -Milliseconds (200*($attempt+1));continue}
            $ex=[IO.IOException]::new('程序目录提交失败。',$_.Exception);$ex.Data['dltFault']=$fault;throw $ex
        }
    }
}
