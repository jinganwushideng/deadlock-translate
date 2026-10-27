"""Windows PowerShell 5.1 must parse every shipped installer/runtime helper."""
import json
from pathlib import Path
import subprocess

ROOT=Path(__file__).resolve().parents[1]
names=['Installer-Workflow.ps1','Manage-Mod.ps1','Find-Deadlock.ps1','Game-SearchPaths.ps1',
       'Install-Safety.ps1','Vpk-Recovery.ps1','Configure-Steam.ps1','Update-Mod.ps1',
       'Update-Archive.ps1','Backup-Maintenance.ps1','Trust-Local-Https.ps1']
passed=0
for name in names:
    path=ROOT/'scripts'/name
    raw=path.read_bytes()
    if any(byte>=128 for byte in raw):
        assert raw.startswith(b'\xef\xbb\xbf'),name+': non-ASCII script requires UTF-8 BOM for Windows PowerShell 5.1'
    passed+=1
command=r'''$files=Get-Content -LiteralPath $env:DLT_PS_PARSE_LIST -Raw|ConvertFrom-Json;$result=@();foreach($file in $files){$tokens=$null;$parseErrors=$null;[System.Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$parseErrors)|Out-Null;$result+=@{name=[IO.Path]::GetFileName($file);errors=@($parseErrors|ForEach-Object {$_.ErrorId})}};$result|ConvertTo-Json -Depth 4 -Compress'''
folder=ROOT/'artifacts/powershell-source-validation'
folder.mkdir(exist_ok=True)
inputs=folder/'powershell-source-inputs.json'
inputs.write_text(json.dumps([str(ROOT/'scripts'/name) for name in names]),encoding='utf-8-sig')
import os
env={**os.environ,'DLT_PS_PARSE_LIST':str(inputs)}
result=subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-Command',command],env=env,capture_output=True,check=True,timeout=30)
parsed=json.loads(result.stdout.decode('utf-8-sig'))
assert len(parsed)==len(names)
for item in parsed:
    assert not item['errors'],item
    passed+=1
report={'passed':passed,'scripts':len(names),'engine':'Windows PowerShell 5.1','scriptExecution':False}
(folder/'powershell-source-tests.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report))
