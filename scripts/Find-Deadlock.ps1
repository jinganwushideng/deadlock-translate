# Dot-sourced by Manage-Mod.ps1. No automatic full-disk scan.
function Normalize-DeadlockPath([string]$Path){
    if([string]::IsNullOrWhiteSpace($Path)){return $null}
    $taskInput=[Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    try{$taskFull=[IO.Path]::GetFullPath($taskInput).TrimEnd('\','/')}catch{return $null}
    $taskCandidates=@($taskFull)
    if(Test-Path -LiteralPath $taskFull -PathType Leaf){$taskCandidates+=Split-Path $taskFull -Parent}
    $taskParent=$taskFull
    for($taskLevel=0;$taskLevel -lt 4;$taskLevel++){$taskParent=Split-Path $taskParent -Parent;if(-not $taskParent){break};$taskCandidates+=$taskParent}
    foreach($taskCandidate in $taskCandidates){
        if((Test-Path -LiteralPath (Join-Path $taskCandidate 'game\citadel\gameinfo.gi') -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $taskCandidate 'game\citadel\pak01_dir.vpk') -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $taskCandidate 'game\bin\win64\deadlock.exe') -PathType Leaf)){return [IO.Path]::GetFullPath($taskCandidate).TrimEnd('\','/')}
    }
    return $null
}
function Find-DeadlockQuick([string[]]$Libraries){
    # PS 5.1 optimizes @($nullTypedArray) to null; += would then concatenate
    # registry strings into one invalid path. Pin the collection type explicitly.
    [object[]]$taskRoots=@($Libraries | Where-Object {$_})
    foreach($taskRegistry in @('HKCU:\Software\Valve\Steam','HKLM:\Software\WOW6432Node\Valve\Steam','HKLM:\Software\Valve\Steam')){
        $taskRecord=Get-ItemProperty $taskRegistry -ErrorAction SilentlyContinue
        if($taskRecord.SteamPath){$taskRoots+=$taskRecord.SteamPath};if($taskRecord.InstallPath){$taskRoots+=$taskRecord.InstallPath}
    }
    foreach($taskSteam in @($taskRoots|Where-Object {$_}|Select-Object -Unique)){
        $taskVdf=Join-Path $taskSteam 'steamapps\libraryfolders.vdf'
        if(Test-Path -LiteralPath $taskVdf){try{$taskLibraryText=[IO.File]::ReadAllText($taskVdf,[Text.Encoding]::UTF8);$taskRoots+=@([regex]::Matches($taskLibraryText,'"path"\s+"((?:\\.|[^"\\])*)"',[Text.RegularExpressions.RegexOptions]::IgnoreCase)|ForEach-Object {[regex]::Replace($_.Groups[1].Value,'\\([\\"])',{param($m)$m.Groups[1].Value})})}catch{}}
    }
    $taskFound=@()
    foreach($taskRoot in @($taskRoots|Where-Object {$_}|Select-Object -Unique)){
        foreach($taskCandidate in @($taskRoot,(Join-Path $taskRoot 'steamapps\common\Deadlock'))){$taskValid=Normalize-DeadlockPath $taskCandidate;if($taskValid){$taskFound+=$taskValid}}
    }
    return @($taskFound|Select-Object -Unique)
}
function Find-DeadlockDeep([string[]]$Roots,[int]$MaximumFolders=50000,[int]$TimeoutSeconds=60){
    if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw '请先退出游戏，再进行深层扫描。'}
    if(-not $Roots){$Roots=@([IO.DriveInfo]::GetDrives()|Where-Object {$_.IsReady -and $_.DriveType -eq [IO.DriveType]::Fixed}|ForEach-Object {$_.RootDirectory.FullName})}
    $taskQueue=[Collections.Generic.Queue[string]]::new();$taskSeen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($taskRoot in $Roots){try{$taskResolved=[IO.Path]::GetFullPath($taskRoot);if(Test-Path -LiteralPath $taskResolved -PathType Container){$taskQueue.Enqueue($taskResolved)}}catch{}}
    $taskSkip=@('Windows','WindowsApps','ProgramData','AppData','OneDrive','$Recycle.Bin','System Volume Information','.git','.net','node_modules','__pycache__')
    $taskFound=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$taskWatch=[Diagnostics.Stopwatch]::StartNew();$taskCount=0;$taskDenied=0;$taskLimited=$false
    while($taskQueue.Count){
        if($taskCount -ge $MaximumFolders -or $taskWatch.Elapsed.TotalSeconds -ge $TimeoutSeconds){$taskLimited=$true;break}
        $taskDirectory=$taskQueue.Dequeue();if(-not $taskSeen.Add($taskDirectory)){continue}
        try{
            if([IO.File]::GetAttributes($taskDirectory) -band [IO.FileAttributes]::ReparsePoint){continue}
            $taskCount++;$taskValid=Normalize-DeadlockPath $taskDirectory
            if($taskValid){$null=$taskFound.Add($taskValid);continue}
            foreach($taskChild in [IO.Directory]::EnumerateDirectories($taskDirectory)){
                if([IO.Path]::GetFileName($taskChild) -in $taskSkip){continue}
                if($taskQueue.Count -ge 10000){$taskLimited=$true;continue}
                $taskQueue.Enqueue($taskChild)
            }
        }catch{$taskDenied++}
        if($taskCount%500 -eq 0){Write-Progress -Activity '深层查找 Deadlock' -Status ("已检查 $taskCount 个目录；最多 $TimeoutSeconds 秒")}
    }
    Write-Progress -Activity '深层查找 Deadlock' -Completed
    return [pscustomobject]@{Paths=@($taskFound);Scanned=$taskCount;Denied=$taskDenied;Limited=$taskLimited;ElapsedMs=[int]$taskWatch.Elapsed.TotalMilliseconds}
}
function Choose-DeadlockPath([string[]]$Paths,[switch]$NonInteractive){
    if($Paths.Count -eq 1){return $Paths[0]}
    if(-not $Paths.Count){return $null}
    if($NonInteractive){throw '发现多个游戏目录，请明确提供 -GamePath。'}
    for($taskIndex=0;$taskIndex -lt $Paths.Count;$taskIndex++){Write-Output ("[$($taskIndex+1)] "+$Paths[$taskIndex])}
    $taskChoice=Read-Host '输入游戏目录编号，或输入 0 取消';$taskNumber=0
    if(-not [int]::TryParse($taskChoice,[ref]$taskNumber) -or $taskNumber -lt 1 -or $taskNumber -gt $Paths.Count){throw '已取消；没有修改游戏文件。'}
    return $Paths[$taskNumber-1]
}
function Resolve-DeadlockPath([string]$Requested,[string]$Mode='Auto',[string[]]$ScanRoots,[string[]]$Libraries,[int]$MaximumFolders=50000,[int]$TimeoutSeconds=60,[switch]$NonInteractive){
    if($Requested){$taskValid=Normalize-DeadlockPath $Requested;if($taskValid){return $taskValid};throw '游戏目录无效：需包含 game\citadel\gameinfo.gi、pak01_dir.vpk 和 game\bin\win64\deadlock.exe。可选择根目录、citadel 文件夹或游戏 EXE。'}
    if($Mode -eq 'Auto'){
        $taskMatches=@(Find-DeadlockQuick $Libraries);if($taskMatches.Count){return Choose-DeadlockPath $taskMatches -NonInteractive:$NonInteractive}
        if($NonInteractive){throw '快速定位失败。请使用 -GamePath 手动指定，或 -LocateMode Deep -ScanRoots 指定深层扫描范围。'}
        Write-Output '快速定位未找到完整的 Deadlock 安装。';Write-Output '[1] 选择文件夹  [2] 手动输入路径  [3] 深层扫描  [0] 取消'
        $taskOption=Read-Host '选择定位方式'
        if($taskOption -eq '1'){Add-Type -AssemblyName System.Windows.Forms;$taskDialog=New-Object Windows.Forms.FolderBrowserDialog;$taskDialog.Description='选择 Deadlock 游戏目录（或 game/citadel 文件夹）';$taskDialog.ShowNewFolderButton=$false;try{if($taskDialog.ShowDialog() -ne [Windows.Forms.DialogResult]::OK){throw '已取消；没有修改游戏文件。'};$Requested=$taskDialog.SelectedPath}finally{$taskDialog.Dispose()}}
        elseif($taskOption -eq '2'){$Mode='Manual'}elseif($taskOption -eq '3'){$Mode='Deep'}else{throw '已取消；没有修改游戏文件。'}
        if($Requested){return Resolve-DeadlockPath -Requested $Requested}
    }
    if($Mode -eq 'Manual'){
        if($NonInteractive){throw '手动模式需要 -GamePath。'}
        return Resolve-DeadlockPath -Requested (Read-Host '粘贴 Deadlock 根目录或游戏 EXE 完整路径，可带双引号')
    }
    if($Mode -eq 'Deep'){
        $taskDeep=Find-DeadlockDeep -Roots $ScanRoots -MaximumFolders $MaximumFolders -TimeoutSeconds $TimeoutSeconds
        Write-Output ("深层扫描检查 $($taskDeep.Scanned) 个目录，跳过 $($taskDeep.Denied) 个无法访问的目录。")
        if($taskDeep.Limited){Write-Output '已达到扫描范围/时间限额，结果可能不完整；可改用指定扫描根目录或手动输入。'}
        $taskSelected=Choose-DeadlockPath $taskDeep.Paths -NonInteractive:$NonInteractive
        if($taskSelected){return $taskSelected}
        throw '扫描范围内未找到完整安装；没有修改游戏文件。请用 -GamePath 手动指定或扩大 -ScanRoots。'
    }
    throw '定位方式无效。'
}
