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
