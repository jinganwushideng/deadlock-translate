import hashlib,json,pathlib,struct,sys,re
root=pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0,str(root/'scripts'))
from vpk import Vpk
package=root/('dist/DeadlockTranslate-'+json.loads((root/'version.json').read_text())['version'])
exe=(package/'DeadlockTranslate.exe').read_bytes()
pe=struct.unpack_from('<I',exe,0x3c)[0]
assert exe[pe:pe+4]==b'PE\0\0'
assert struct.unpack_from('<H',exe,pe+24+68)[0]==2,'must be a Windows GUI-subsystem process'
vpk=Vpk(package/'dlt_translate_dir.vpk')
expected={'panorama/layout/chat.vxml_c','panorama/scripts/dlt_chat.vjs_c','panorama/styles/dlt_chat.vcss_c','panorama/layout/dlt_menu.vxml_c','panorama/scripts/dlt_menu.vjs_c','panorama/styles/dlt_menu.vcss_c','panorama/layout/dlt_settings_window.vxml_c','panorama/scripts/dlt_settings_window.vjs_c','panorama/styles/dlt_settings_window.vcss_c'}
assert set(vpk.entries)==expected
for key in vpk.entries:vpk.read(key)
assert len((package/'pairing.key').read_text().strip())==48
manifest=json.loads((package/'build-manifest.json').read_text(encoding='utf-8-sig'))
for entry in manifest['artifacts']:
    assert hashlib.sha256((package/entry['file']).read_bytes()).hexdigest().lower()==entry['sha256'].lower(),entry['file']
for file in package.rglob('*'):
    if file.is_file():assert not re.search(rb'sk-tp-[A-Za-z0-9_=\-]{20,}',file.read_bytes()),'provider credential found in package'
result={'passed':6,'vpkEntries':len(vpk.entries),'exeMiB':round(len(exe)/1024**2,2),'vpkBytes':(package/'dlt_translate_dir.vpk').stat().st_size,'credentialScan':'No sk-tp- credentials in package'}
(root/'artifacts/package-tests.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(result)
