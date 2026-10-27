"""Generate one external CMD with the trusted ZIP reader and updater embedded."""
import argparse
import base64
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def bundle():
    core = (ROOT / 'scripts/Update-Mod.ps1').read_text(encoding='utf-8-sig')
    helper = (ROOT / 'scripts/Game-SearchPaths.ps1').read_text(encoding='utf-8-sig')
    include = ". (Join-Path $PSScriptRoot 'Game-SearchPaths.ps1')"
    assert core.count(include) == 1
    core = core.replace(include, helper)
    safety_include = ". (Join-Path $PSScriptRoot 'Install-Safety.ps1')"
    assert core.count(safety_include) == 1
    core = core.replace(safety_include, (ROOT / 'scripts/Install-Safety.ps1').read_text(encoding='utf-8-sig'))
    recovery_include = ". (Join-Path $PSScriptRoot 'Vpk-Recovery.ps1')"
    assert core.count(recovery_include) == 1
    core = core.replace(recovery_include, (ROOT / 'scripts/Vpk-Recovery.ps1').read_text(encoding='utf-8-sig'))
    maintenance_include = ". (Join-Path $PSScriptRoot 'Backup-Maintenance.ps1') -StateDirectory $StateDirectory"
    assert core.count(maintenance_include) == 1
    maintenance = (ROOT / 'scripts/Backup-Maintenance.ps1').read_text(encoding='utf-8-sig')
    maintenance = maintenance[maintenance.index('# Shared by'):maintenance.index("if($MyInvocation.InvocationName -ne '.')")]
    core = core.replace(maintenance_include, maintenance)
    archive = (ROOT / 'scripts/Update-Archive.ps1').read_text(encoding='utf-8-sig')
    marker = '# DLT_PACKAGE_CORE'
    assert archive.count(marker) == 1
    return archive.replace(marker, 'function Invoke-LocalPackageUpdate {\n' + core + '\n}')


def build(destination):
    payload = base64.b64encode(bundle().encode('utf-8')).decode('ascii')
    command = r'''@echo off
setlocal
set "DLT_UPDATE_SELF=%~f0"
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -Command "$s=[IO.File]::ReadAllText($env:DLT_UPDATE_SELF);$m=[regex]::Match($s,'(?m)^:DLT_PAYLOAD\r?\n([A-Za-z0-9+/=]+)\s*$');if(-not $m.Success){throw 'Updater payload missing'};$b=[scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($m.Groups[1].Value)));& $b -DownloadFolder ([IO.Path]::GetDirectoryName($env:DLT_UPDATE_SELF))"
set "DLT_UPDATE_EXIT=%ERRORLEVEL%"
pause
exit /b %DLT_UPDATE_EXIT%
:DLT_PAYLOAD
'''
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes((command + payload + '\n').replace('\n', '\r\n').encode('ascii'))
    return destination


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    print(build(args.output))
