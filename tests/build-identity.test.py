import importlib.util
import json
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch

root=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(root/'scripts'))
import build_identity

checks=0
def check(value,name):
    global checks
    assert value,name
    checks+=1
    print('PASS '+name)

actual=build_identity.stamp(check=True)
check(actual==build_identity.identity(),'generated identities are current and stable')
check(actual['buildId'] in (root/'src/Bridge/BuildInfo.Generated.cs').read_text(encoding='utf-8'),'backend identity matches')
check(actual['buildId'] in (root/'mod/panorama/scripts/dlt_chat.js').read_text(encoding='utf-8'),'game identity matches')
with tempfile.TemporaryDirectory() as tmp:
    fixture=Path(tmp)
    (fixture/'version.json').write_text('{"version":"0.6.23"}',encoding='utf-8')
    (fixture/'source.cs').write_bytes(b'class Example {}\r\n')
    (fixture/'build-info.json').write_text('{}',encoding='utf-8')
    with patch.object(build_identity,'public_files',lambda _:sorted(fixture.iterdir())):
        before=build_identity.identity(fixture)
        (fixture/'source.cs').write_bytes(b'class Example {}\n')
        check(before==build_identity.identity(fixture),'Windows line endings do not invent another revision')
        for name in ('layout.xml','launch.cmd','app.manifest','ci.yml','terms.tsv'):
            file=fixture/name;file.write_bytes(b'fixture\r\nsecond line\r\n');with_windows=build_identity.identity(fixture)
            file.write_bytes(b'fixture\nsecond line\n')
            check(with_windows==build_identity.identity(fixture),'Git line-ending normalization preserves identity for '+name)
        before=build_identity.identity(fixture)
        (fixture/'build-info.json').write_text('{"buildId":"arbitrary-generated-value"}',encoding='utf-8')
        check(before==build_identity.identity(fixture),'generated identity cannot recursively change its own hash')
        (fixture/'source.cs').write_text('class Example { int changed; }',encoding='utf-8')
        check(before!=build_identity.identity(fixture),'changed source produces another build identity')
print(json.dumps({'passed':checks,'formalBuild':False}))
