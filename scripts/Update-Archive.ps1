param(
    [string]$DownloadFolder=$PSScriptRoot,
    [string]$PackageZip,
    [string]$RuntimeFolder,
    [string]$StateDirectory=(Join-Path $env:LOCALAPPDATA 'DeadlockTranslate'),
    [string]$SteamPath,
    [string]$TempParent=[IO.Path]::GetTempPath(),
    [switch]$NonInteractive
)
$ErrorActionPreference='Stop'

# DLT_PACKAGE_CORE

function Zip-NoLinks([string]$path){
    $taskZipCursor=[IO.Path]::GetFullPath($path)
    while($taskZipCursor){
        if(Test-Path -LiteralPath $taskZipCursor){if((Get-Item -LiteralPath $taskZipCursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '更新路径包含链接，已停止。'}}
        $taskZipCursor=[IO.Path]::GetDirectoryName($taskZipCursor)
    }
}
function Zip-Hash([string]$path){
    $taskZipHasher=[Security.Cryptography.SHA256]::Create();$taskZipStream=[IO.File]::OpenRead($path)
    try{([BitConverter]::ToString($taskZipHasher.ComputeHash($taskZipStream))).Replace('-','').ToLowerInvariant()}finally{$taskZipStream.Dispose();$taskZipHasher.Dispose()}
}
function Zip-Relative([string]$name){
    $taskZipRelative=$name.Replace('\','/').TrimEnd('/')
    if([string]::IsNullOrWhiteSpace($taskZipRelative) -or [IO.Path]::IsPathRooted($taskZipRelative) -or $taskZipRelative.Contains(':')){throw 'ZIP 中包含无效或越界路径。'}
    $taskZipParts=$taskZipRelative.Split('/')
    foreach($taskZipPart in $taskZipParts){
        if($taskZipPart -in @('','.','..') -or $taskZipPart.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $taskZipPart.TrimEnd(' ','.') -cne $taskZipPart -or $taskZipPart -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)'){throw 'ZIP 中包含不安全的 Windows 文件路径。'}
    }
    return $taskZipRelative
}

$taskZipTemporary=$null;$taskZipArchive=$null;$taskZipCreated=$false
try{
    if(Get-Process -Name deadlock,citadel -ErrorAction SilentlyContinue){throw '请先退出 Deadlock，再运行覆盖更新；不会强制关闭游戏。'}
    if(-not(Test-Path -LiteralPath (Join-Path $StateDirectory 'install-manifest.json'))){throw '安装记录已丢失。请直接运行新版完整安装器，选择“安装 / 覆盖更新”或“修复已有安装”：它会保留个人设置，自动检查残留并成套重装，不需要先卸载或手动复制旧 VPK。'}
    $DownloadFolder=[IO.Path]::GetFullPath($DownloadFolder);Zip-NoLinks $DownloadFolder
    if(-not $PackageZip){
        $taskZipCandidates=@(Get-ChildItem -LiteralPath $DownloadFolder -File | ForEach-Object {
            $taskZipMatch=[regex]::Match($_.Name,'^DeadlockTranslate-(\d+\.\d+\.\d+(?:\.\d+)?)-win-x64(?: \(\d+\))?\.zip$',[Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if($taskZipMatch.Success){[pscustomobject]@{path=$_.FullName;version=[Version]$taskZipMatch.Groups[1].Value;modified=$_.LastWriteTimeUtc}}
        } | Sort-Object -Property @{Expression='version';Descending=$true},@{Expression='modified';Descending=$true})
        if(-not $taskZipCandidates.Count){throw '请把新版 DeadlockTranslate-版本-win-x64.zip 与覆盖更新.cmd 放在同一文件夹，无需手动解压。'}
        $PackageZip=$taskZipCandidates[0].path
    }
    $PackageZip=[IO.Path]::GetFullPath($PackageZip);Zip-NoLinks $PackageZip
    $taskZipNameMatch=[regex]::Match([IO.Path]::GetFileName($PackageZip),'^DeadlockTranslate-(\d+\.\d+\.\d+(?:\.\d+)?)-win-x64(?: \(\d+\))?\.zip$',[Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if(-not $taskZipNameMatch.Success){throw '压缩包文件名无效，请保留官方运行包名称。'}
    $taskZipVersion=$taskZipNameMatch.Groups[1].Value
    Write-Output ('使用安装包：'+[IO.Path]::GetFileName($PackageZip))
    if(Test-Path -LiteralPath ($PackageZip+'.sha256')){
        Zip-NoLinks ($PackageZip+'.sha256')
        $taskZipChecksum=([IO.File]::ReadAllText($PackageZip+'.sha256')).Trim()
        if($taskZipChecksum -notmatch '\A([0-9a-fA-F]{64})(?:\s+[^\r\n]+)?\z' -or (Zip-Hash $PackageZip) -ne $Matches[1].ToLowerInvariant()){throw '压缩包 SHA-256 校验失败，请重新下载。'}
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $taskZipArchive=[IO.Compression.ZipFile]::OpenRead($PackageZip)
    if($taskZipArchive.Entries.Count -eq 0 -or $taskZipArchive.Entries.Count -gt 2000){throw '压缩包文件数量无效。'}
    $taskZipRoot='DeadlockTranslate-'+$taskZipVersion
    $taskZipSeen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $taskZipSize=[long]0
    foreach($taskZipEntry in $taskZipArchive.Entries){
        $taskZipRelative=Zip-Relative $taskZipEntry.FullName
        if(-not $taskZipSeen.Add($taskZipRelative)){throw 'ZIP 中包含重复或大小写冲突的路径。'}
        if($taskZipRelative -cne $taskZipRoot -and -not $taskZipRelative.StartsWith($taskZipRoot+'/',[StringComparison]::Ordinal)){throw 'ZIP 不是单一完整版本的运行包。'}
        $taskZipFileMode=($taskZipEntry.ExternalAttributes -shr 16) -band 61440
        if($taskZipFileMode -notin @(0,16384,32768) -or ($taskZipEntry.ExternalAttributes -band 1024)){throw 'ZIP 中包含链接或特殊文件，已停止。'}
        $taskZipSize+=$taskZipEntry.Length
        if($taskZipEntry.Length -gt 536870912 -or $taskZipSize -gt 1073741824){throw '安装包解压大小异常，已停止。'}
    }
    $taskZipManifestEntry=$taskZipArchive.GetEntry($taskZipRoot+'/package-manifest.json')
    if(-not $taskZipManifestEntry -or $taskZipManifestEntry.Length -gt 1048576){throw 'ZIP 缺少有效运行包文件清单。'}
    $taskZipReader=[IO.StreamReader]::new($taskZipManifestEntry.Open(),[Text.Encoding]::UTF8)
    try{$taskZipMetadata=$taskZipReader.ReadToEnd()|ConvertFrom-Json}finally{$taskZipReader.Dispose()}
    if($taskZipMetadata.version -cne $taskZipVersion){throw 'ZIP 文件名与包内版本不一致。'}
    $TempParent=[IO.Path]::GetFullPath($TempParent);Zip-NoLinks $TempParent
    if(-not(Test-Path -LiteralPath $TempParent -PathType Container)){throw '临时目录不存在。'}
    $taskZipTemporary=Join-Path $TempParent ('DeadlockTranslate-update-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $taskZipTemporary | Out-Null;$taskZipCreated=$true
    Write-Output '正在自动解压并校验文件…'
    foreach($taskZipEntry in $taskZipArchive.Entries){
        $taskZipDestination=Join-Path $taskZipTemporary ((Zip-Relative $taskZipEntry.FullName).Replace('/',[IO.Path]::DirectorySeparatorChar))
        $taskZipResolved=[IO.Path]::GetFullPath($taskZipDestination)
        if(-not $taskZipResolved.StartsWith($taskZipTemporary+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '解压文件越界，已停止。'}
        if($taskZipEntry.FullName.EndsWith('/') -or $taskZipEntry.FullName.EndsWith('\')){New-Item -ItemType Directory -Path $taskZipDestination -Force|Out-Null;continue}
        New-Item -ItemType Directory -Path (Split-Path $taskZipDestination -Parent) -Force|Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($taskZipEntry,$taskZipDestination)
    }
    $taskZipArchive.Dispose();$taskZipArchive=$null
    $taskZipArguments=@{PackageFolder=(Join-Path $taskZipTemporary $taskZipRoot);StateDirectory=$StateDirectory;NonInteractive=$NonInteractive}
    if($RuntimeFolder){$taskZipArguments.RuntimeFolder=$RuntimeFolder}
    if($SteamPath){$taskZipArguments.SteamPath=$SteamPath}
    Invoke-LocalPackageUpdate @taskZipArguments
}finally{
    if($taskZipArchive){$taskZipArchive.Dispose()}
    if($taskZipCreated -and (Test-Path -LiteralPath $taskZipTemporary)){
        try{
            # Remove only the unique extraction directory created by this invocation.
            $taskZipActual=[IO.Path]::GetFullPath((Get-Item -LiteralPath $taskZipTemporary -Force).FullName)
            if([IO.Path]::GetDirectoryName($taskZipActual).TrimEnd('\','/') -ine $TempParent.TrimEnd('\','/') -or [IO.Path]::GetFileName($taskZipActual) -notmatch '^DeadlockTranslate-update-[0-9a-f]{32}$'){throw '临时清理目录校验失败。'}
            Zip-NoLinks $taskZipActual
            if(@(Get-ChildItem -LiteralPath $taskZipActual -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw '临时目录出现链接，已保留。'}
            Remove-Item -LiteralPath $taskZipActual -Recurse -Force
        }catch{Write-Warning ('临时解压目录未能清理：'+$taskZipTemporary)}
    }
}
