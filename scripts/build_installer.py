"""Build the standalone offline GUI, embedding the exact released runtime ZIP."""
import argparse,hashlib,json,pathlib,subprocess,shutil
from release_history import check as check_release_history
from build_identity import edition_version, stamp
import zipfile
root=pathlib.Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--release-dir', type=pathlib.Path, help='Directory containing the verified runtime ZIP')
parser.add_argument('--lite',action='store_true')
args=parser.parse_args()
version=edition_version(root,args.lite)
check_release_history(root,lite=args.lite)
product='DeadlockTranslate-Lite' if args.lite else 'DeadlockTranslate'
release=(args.release_dir or root/'release'/version).resolve()
payload=release/f'{product}-{version}-win-x64.zip'
if not payload.is_file():raise SystemExit('Run package_share.py before building the GUI installer.')
info=stamp(check=True,lite=args.lite)
with zipfile.ZipFile(payload) as z:
 embedded=json.loads(z.read(f'{product}-{version}/build-info.json'))
 if embedded!=info:raise SystemExit('Installer payload and current source build do not match.')
out=root/'artifacts'/f'installer-publish-{product}-{version}'
subprocess.run(['dotnet','restore',str(root/'src/Installer'),'--runtime','win-x64'],check=True)
destination=release/f'{product}-Setup-{version}.exe'
# Final publication below embeds the collected notices as well.

# Keep exact-version runtime notices next to the executable; they are also in the source distribution.
assets=json.loads((root/'src/Installer/obj/project.assets.json').read_text(encoding='utf-8-sig'))
folders=[pathlib.Path(p) for p in assets['packageFolders']]
notices=[('同频精简版安装器 ' if args.lite else '同频 GUI 安装器 ')+version+'\n\n'+(root/'LICENSE').read_text(encoding='utf-8')]
seen=set()
for f in assets['project']['frameworks'].values():
 for dep in f.get('downloadDependencies',[]):
  name=dep['name'].lower();v=dep['version'].strip('[]').split(',')[0].strip()
  if not name.startswith('microsoft.') or (name,v) in seen:continue
  seen.add((name,v));package=next((p/name/v for p in folders if (p/name/v).is_dir()),None)
  if package is None:raise FileNotFoundError(name+'/'+v)
  found=[p for p in package.rglob('*') if p.is_file() and p.suffix.lower() in ('.txt','.md','') and ('license' in p.name.lower() or 'thirdparty' in p.name.lower() or 'third-party' in p.name.lower())]
  if not found:raise RuntimeError('Runtime license files missing: '+name)
  for p in found:notices.append('\n\n'+name+'/'+v+'/'+p.name+'\n'+p.read_text(encoding='utf-8-sig'))
(release/'Installer-LICENSES.txt').write_text('\n'.join(notices),encoding='utf-8-sig')
subprocess.run(['dotnet','publish',str(root/'src/Installer'),'--configuration','Release','--runtime','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true',*(['-p:LiteEdition=true',f'-p:Version={version}'] if args.lite else []),'-p:InstallerPayload='+str(payload),'-p:InstallerNotices='+str(release/'Installer-LICENSES.txt'),'-o',str(out)],check=True)
shutil.copy2(out/'DeadlockTranslate-Setup.exe',destination)
destination.with_suffix('.exe.sha256').write_text(hashlib.sha256(destination.read_bytes()).hexdigest()+'  '+destination.name+'\n',encoding='utf-8')
print(json.dumps({'installer':str(destination),'bytes':destination.stat().st_size,'sha256':hashlib.sha256(destination.read_bytes()).hexdigest()},indent=2))
