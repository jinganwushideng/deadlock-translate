import sys,pathlib
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'scripts'))
from merge_term_defaults import merge
old=[dict(source='urn',target='旧名'),dict(source='deleted',target='默认'),dict(source='changed',target='原译'),dict(source='obsolete',target='废弃')]
new=[dict(source='urn',target='灵瓮'),dict(source='deleted',target='新默认'),dict(source='changed',target='新译'),dict(source='QSR',target='魔力装填')]
user=[dict(source='urn',target='旧名'),dict(source='changed',target='我自己的译名'),dict(source='custom',target='自定义'),dict(source='obsolete',target='废弃')]
result={t['source']:t['target'] for t in merge(old,new,user)}
assert result=={'urn':'灵瓮','changed':'我自己的译名','custom':'自定义','QSR':'魔力装填'}
assert merge(new,new,list(merge(old,new,user)))==merge(old,new,user)
print('PASS: new defaults, corrected defaults, edits, deletions, obsolete defaults and idempotent re-upgrade')

legacy_old=[dict(source='Lash',target='劳什',legacyTarget='神鞭'),dict(source='item',target='当前名')]
legacy_new=[dict(source='Lash',target='劳什',legacyTarget='新默认旧名'),dict(source='item',target='修正当前名',legacyTarget='旧称')]
legacy_user=[dict(source='Lash',target='劳什',legacyTarget='我的习惯旧名'),dict(source='item',target='当前名',legacyTarget='')]
legacy_result={t['source']:t for t in merge(legacy_old,legacy_new,legacy_user)}
assert legacy_result['Lash']['legacyTarget']=='我的习惯旧名'
assert legacy_result['item']['target']=='修正当前名' and legacy_result['item']['legacyTarget']=='旧称'
assert merge(legacy_new,legacy_new,list(legacy_result.values()))==list(legacy_result.values())
print('PASS: legacy display edits survive, and absent/empty optional fields do not count as custom edits')

# Nickname additions must migrate into saved glossaries without replacing player edits.
import json
root=pathlib.Path(__file__).resolve().parents[1]
defaults_file=root/'src/Bridge/data/terms.json'
if not defaults_file.is_file():
    print('SKIP: actual generated nickname glossary merge requires build_terms.py with a game installation')
    sys.exit(0)
new_defaults=json.loads(defaults_file.read_text(encoding='utf-8'))
aliases={'蓝牛','藍牛','狼女','希尔芙','wolf girl','blue cow','волчица'}
old_defaults=[t for t in new_defaults if t['source'] not in aliases]
saved=[dict(t) for t in old_defaults]
saved.append(dict(source='蓝牛',target='我自定义的蓝牛',english='Abrams',kind='name',ambiguous=False))
merged=merge(old_defaults,new_defaults,saved)
by_source={t['source']:t for t in merged}
assert by_source['蓝牛']['target']=='我自定义的蓝牛'
assert by_source['狼女']['english']=='Silver'
assert all(alias in by_source for alias in aliases)
assert merge(new_defaults,new_defaults,merged)==merged
print('PASS: nickname defaults merge into a saved glossary, preserving edits and repeat-upgrade idempotence')
