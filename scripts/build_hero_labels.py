"""Derive display-only hero identifiers from the installed game, without cloud requests."""
import hashlib,json,os,re,struct,subprocess
from pathlib import Path
from vpk import Vpk
from term_extensions import LEGACY_NAMES
root=Path(__file__).resolve().parents[1]

def tokens(path):
    raw=path.read_bytes();text=raw.decode('utf-16' if raw.startswith((b'\xff\xfe',b'\xfe\xff')) else 'utf-8-sig')
    return dict(re.findall(r'^\s*"([^"\n]+)"\s+"([^"\n]*)"',text,re.M))

def main():
    game=Path(os.environ['DEADLOCK_TRANSLATE_GAME'])/'game/citadel'
    resource=Vpk(game/'pak01_dir.vpk').read('scripts/heroes.vdata_c')
    # Resource metadata may use newer edit-info types than the viewer understands.
    # Make a DATA-only scratch copy, preserving its original payload bytes/offsets.
    # Never patch the installed resource or attempt to interpret it as executable code.
    data=bytearray(resource);offset,count=struct.unpack_from('<II',data,8);offset+=8;blocks=[]
    if count>64 or offset+count*12>len(data):raise ValueError('Invalid hero resource table')
    for i in range(count):
        pos=offset+i*12;tag=bytes(data[pos:pos+4]);delta,size=struct.unpack_from('<II',data,pos+4)
        if tag==b'DATA':
            absolute=pos+4+delta
            if absolute+size>len(data):raise ValueError('Invalid hero DATA block')
            blocks.append((tag,absolute,size))
    if len(blocks)!=1:raise ValueError('Expected one hero DATA block')
    struct.pack_into('<I',data,12,1)
    tag,absolute,size=blocks[0];data[offset:offset+4]=tag;struct.pack_into('<II',data,offset+4,absolute-offset-4,size)
    scratch=root/'artifacts/hero-label-build';scratch.mkdir(exist_ok=True)
    compiled=scratch/'hero-data-only.vdata_c';decoded=scratch/'hero-data-only.vdata';compiled.write_bytes(data)
    viewer=os.environ.get('DLT_RESOURCE_VIEWER',str(root/'tools/vrf/Source2Viewer-CLI.exe'))
    if decoded.exists():decoded.unlink()
    result=subprocess.run([viewer,'-i',str(compiled),'-o',str(decoded),'-d'],capture_output=True,text=True)
    if result.returncode or not decoded.is_file():raise RuntimeError('Hero resource decoding failed; no stale identity map used')
    text=decoded.read_text(encoding='utf-8-sig');folder=game/'resource/localization/citadel_gc_hero_names'
    zh=tokens(folder/'citadel_gc_hero_names_schinese.txt');en=tokens(folder/'citadel_gc_hero_names_english.txt')
    # The roster labels follow the game's selected UI language, while our output
    # still follows the configured current/legacy Chinese naming preference.
    localized=[tokens(path) for path in sorted(folder.glob('citadel_gc_hero_names_*.txt'))]
    labels={};ambiguous=set();parts=re.split(r'(?m)^\t(hero_[A-Za-z0-9_]+) =\s*$',text)
    def remember(key,entry):
        if not key or key in ambiguous:return
        if key in labels and labels[key]!=entry:
            del labels[key];ambiguous.add(key)
        else:labels[key]=entry
    for i in range(1,len(parts),2):
        name,body=parts[i:i+2];match=re.search(r'(?m)^\t\tm_HeroID = (\d+)',body)
        if not match or name=='hero_base' or int(match.group(1))<=0:continue
        current=zh.get(name+':n') or en.get(name+':n');english=en.get(name+':n','')
        if not current or len(current)>80:continue
        legacy=LEGACY_NAMES.get(english,[]);entry={'name':current,'legacy':legacy[0] if legacy else current}
        for key in [name,name.removeprefix('hero_'),match.group(1),english.lower(),current.lower(),entry['legacy'].lower()]:
            remember(key,entry)
        for locale in localized:
            alias=locale.get(name+':n','').strip().lower()
            if len(alias)<=80:remember(alias,entry)
        icon=re.search(r'(?m)^\t\tm_strIconImageSmall = panorama:"([^"]+)"',body)
        if icon:
            basename=Path(icon.group(1)).stem.lower();remember(basename,entry)
    if len(labels)<80:raise ValueError('Incomplete hero identity map')
    (root/'artifacts/hero-labels.json').write_text(json.dumps(labels,ensure_ascii=False,separators=(',',':')),encoding='utf-8')
    (root/'artifacts/hero-labels-provenance.json').write_text(json.dumps({'resourceSha256':hashlib.sha256(resource).hexdigest(),'identifiers':len(labels),'locales':len(localized),'networkUsed':False},indent=2),encoding='utf-8')
    print('Generated '+str(len(labels))+' display-only hero identifiers from installed game.')

if __name__=='__main__':main()
