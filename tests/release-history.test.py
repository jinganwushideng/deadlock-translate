import copy
import json
from pathlib import Path
import sys

root=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(root/'scripts'))
from release_history import check,validate

entries=check();version=json.loads((root/'version.json').read_text())['version']
doc=json.loads((root/'docs/UPDATE-HISTORY.json').read_text(encoding='utf-8'));changelog=(root/'CHANGELOG.md').read_text(encoding='utf-8')
passed=1
assert {f'0.6.{i}' for i in range(21)}|{'0.5.0','0.4.0'} <= {e['version'] for e in entries}
passed+=1
for name,mutate in [
    ('missing history',lambda d:d['entries'].pop()),
    ('duplicate version',lambda d:d['entries'].append(copy.deepcopy(d['entries'][-1]))),
    ('wrong order',lambda d:d['entries'].reverse()),
    ('empty changes',lambda d:d['entries'][0].update(changes=[])),
    ('invalid date',lambda d:d['entries'][0].update(date='2026-99-99')),
    ('oversized summary',lambda d:d['entries'][0].update(changes=['x'*241])),
]:
    bad=copy.deepcopy(doc);mutate(bad)
    try:validate(bad,version,changelog)
    except ValueError:passed+=1
    else:raise AssertionError(name+' was accepted')
try:validate(doc,'9.9.9',changelog)
except ValueError:passed+=1
else:raise AssertionError('new package without new version entry accepted')
result=dict(passed=passed,versions=len(entries),oldest=entries[-1]['version'],networkUsed=False)
bad=copy.deepcopy(doc);bad['entries'][0]['version']='0.6.23.1.9'
try:validate(bad,'0.6.23.1.9',changelog)
except ValueError:passed+=1
else:raise AssertionError('five-component version accepted')
result['passed']=passed
(root/'artifacts').mkdir(exist_ok=True)
(root/'artifacts/release-history-tests.json').write_text(json.dumps(result,indent=2))
print(json.dumps(result))
