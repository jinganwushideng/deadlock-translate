import argparse, hashlib, json, pathlib, re, secrets, subprocess, shutil, os
from vpk import Vpk
root = pathlib.Path(__file__).resolve().parents[1]
game = pathlib.Path(os.environ['DEADLOCK_TRANSLATE_GAME'])/'game/citadel'

def prepare():
    source = root/'artifacts/decompiled/panorama/layout/chat.xml'
    source.parent.mkdir(parents=True, exist_ok=True)
    viewer = pathlib.Path(os.environ.get('DLT_RESOURCE_VIEWER', str(root/'tools/vrf/Source2Viewer-CLI.exe')))
    if not viewer.is_file():
        raise FileNotFoundError('Install Source 2 Viewer CLI or set DLT_RESOURCE_VIEWER.')
    # Refresh from this game build; never silently use stale decompiled resources.
    subprocess.run([str(viewer), '-i', str(game/'pak01_dir.vpk'), '-o', str(source),
                    '--vpk_filepath', 'panorama/layout/chat.vxml_c', '-d'], check=True)
    xml = source.read_text(encoding='utf-8-sig')
    for anchor in ('</styles>', '<snippets>', '<Panel id="ChatControls">'):
        if xml.count(anchor) != 1:
            raise ValueError('Game chat layout changed: expected exactly one ' + anchor)
    xml = xml.replace('</styles>', '\t<include src="s2r://panorama/styles/dlt_chat.vcss_c" />\n\t</styles>')
    xml = xml.replace('<snippets>', '<scripts><include src="s2r://panorama/scripts/dlt_chat.vjs_c" /></scripts>\n\t<snippets>')
    xml = xml.replace('<Panel id="ChatControls">','<HTML id="DltBridge" class="DltBridge" hittest="false" />\n\t\t<Panel id="ChatControls">')
    out = root/'mod/panorama/layout/chat.xml'; out.parent.mkdir(parents=True,exist_ok=True); out.write_text(xml,encoding='utf-8')
    key_path = root/'artifacts/pairing.key'
    if not key_path.exists(): key_path.write_text(secrets.token_hex(24),encoding='ascii')
    stage = root/'artifacts/addon/content/citadel'
    for f in (root/'mod').rglob('*'):
        if not f.is_file(): continue
        dest = stage/f.relative_to(root/'mod'); dest.parent.mkdir(parents=True,exist_ok=True)
        dest.write_text(f.read_text(encoding='utf-8').replace('__PAIRING__',key_path.read_text().strip()),encoding='utf-8')
    game_out=root/'artifacts/addon/game/citadel'; game_out.mkdir(parents=True,exist_ok=True)
    (game_out/'gameinfo.gi').write_text('"GameInfo" { "game" "citadel" FileSystem { SearchPaths { Game "citadel" Game "core" } } }',encoding='utf-8')
    return stage,game_out

def conflicts():
    targets={'panorama/layout/chat.vxml_c','panorama/layout/dlt_menu.vxml_c','panorama/scripts/dlt_chat.vjs_c','panorama/styles/dlt_chat.vcss_c','panorama/scripts/dlt_menu.vjs_c','panorama/styles/dlt_menu.vcss_c','panorama/layout/dlt_settings_window.vxml_c','panorama/scripts/dlt_settings_window.vjs_c','panorama/styles/dlt_settings_window.vcss_c'}
    results=[]
    for file in (game/'addons').glob('*.vpk'):
        vpk=Vpk(file)
        exact=sorted(targets & vpk.entries.keys())
        related=[p for p in vpk.entries if ('chattranslat' in p.lower() or 'chat_translat' in p.lower() or 'top_bar_chat.vcss' in p.lower())]
        if exact or related: results.append({'file':str(file),'sha256':hashlib.sha256(file.read_bytes()).hexdigest(),'exact':exact,'related':related})
    (root/'artifacts/conflicts.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(results,ensure_ascii=False,indent=2))

if __name__=='__main__':
    prepare(); conflicts()
