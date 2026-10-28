# Modify only GameInfo/FileSystem/SearchPaths. Preserve the rest of the user's config.
function Dlt-DecodeToken([string]$value){
    if(-not $value.StartsWith('"')){return $value}
    [regex]::Replace($value.Substring(1,$value.Length-2),'\\([\\"])',{param($m)$m.Groups[1].Value})
}
function Get-DltSearchPathsPlan([byte[]]$Bytes,[switch]$EnsureAddons){
    $bom=$Bytes.Length -ge 3 -and $Bytes[0] -eq 239 -and $Bytes[1] -eq 187 -and $Bytes[2] -eq 191
    $offset=if($bom){3}else{0}
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($Bytes,$offset,$Bytes.Length-$offset)
    $tokens=@([regex]::Matches($text,'//[^\r\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"|[{}]|[^\s{}"]+')|Where-Object {-not $_.Value.StartsWith('//') -and -not $_.Value.StartsWith('/*')})
    $frames=[Collections.Generic.List[object]]::new();$found=[Collections.Generic.List[object]]::new()
    for($i=0;$i -lt $tokens.Count;){
        $key=$tokens[$i]
        if($key.Value -match '^\[.*\]$'){$i++;continue}
        if($key.Value -eq '}'){
            if(-not $frames.Count){throw 'Unbalanced gameinfo; verify game files in Steam first.'}
            $frame=$frames[$frames.Count-1];$frames.RemoveAt($frames.Count-1)
            if($frame.path -eq 'gameinfo/filesystem/searchpaths'){$found.Add($frame)}
            $i++;continue
        }
        if($key.Value -eq '{' -or $i+1 -ge $tokens.Count){throw 'Invalid gameinfo key/value layout.'}
        $value=$tokens[$i+1];$i+=2;$name=(Dlt-DecodeToken $key.Value).ToLowerInvariant()
        if($value.Value -eq '{'){
            $prefix=if($frames.Count){$frames[$frames.Count-1].path+'/'}else{''}
            $frames.Add([pscustomobject]@{path=$prefix+$name;insert=$value.Index+1;entries=[Collections.Generic.List[object]]::new()});continue
        }
        if($value.Value -eq '}' -or -not $frames.Count){throw 'Invalid gameinfo value.'}
        $frames[$frames.Count-1].entries.Add([pscustomobject]@{key=$name;value=(Dlt-DecodeToken $value.Value).Replace('\','/').TrimEnd('/').ToLowerInvariant()})
    }
    if($frames.Count -or $found.Count -ne 1){throw 'No unique GameInfo/FileSystem/SearchPaths block; no changes made.'}
    $block=$found[0]
    foreach($base in @('citadel','core')){if(-not @($block.entries|Where-Object {$_.key -eq 'game' -and $_.value -eq $base}).Count){throw ('Missing base Game '+$base+' search path; verify game files in Steam first.')}}
    $required=@(@('Mod','citadel'),@('Write','citadel'),@('Mod','core'),@('Write','core'))
    if($EnsureAddons){$required+=,@('Game','citadel/addons')}
    $added=[Collections.Generic.List[string]]::new()
    foreach($pair in $required){if(-not @($block.entries|Where-Object {$_.key -eq $pair[0].ToLowerInvariant() -and $_.value -eq $pair[1]}).Count){$added.Add($pair[0]+' '+$pair[1])}}
    if(-not $added.Count){return [pscustomobject]@{Changed=$false;Bytes=$Bytes;Added=@();AddedAddon=$false}}
    $newline=if($text.Contains("`r`n")){"`r`n"}else{"`n"}
    $insert=$newline+(($added|ForEach-Object {'            '+$_}) -join $newline)+$newline
    $updated=$text.Substring(0,$block.insert)+$insert+$text.Substring($block.insert)
    [byte[]]$encoded=[Text.UTF8Encoding]::new($false).GetBytes($updated)
    if($bom){$encoded=[byte[]](@(239,187,191)+$encoded)}
    [pscustomobject]@{Changed=$true;Bytes=$encoded;Added=@($added.ToArray());AddedAddon=$added.Contains('Game citadel/addons')}
}
function Update-DltGameinfoOwnership($Manifest,[byte[]]$Before,[string]$BeforeHash,[string]$AfterHash,[bool]$AddedAddon){
    $owned=$Manifest.gameinfoChanged -and $Manifest.gameinfoAfterHash -ieq $BeforeHash
    if($AddedAddon -and -not $owned){$Manifest|Add-Member -NotePropertyName gameinfoBefore -NotePropertyValue ([Convert]::ToBase64String($Before)) -Force;$owned=$true}
    # External modifications invalidate the older full-file restore snapshot.
    $Manifest|Add-Member -NotePropertyName gameinfoChanged -NotePropertyValue ([bool]$owned) -Force
    $Manifest|Add-Member -NotePropertyName gameinfoAfterHash -NotePropertyValue $AfterHash -Force
}
