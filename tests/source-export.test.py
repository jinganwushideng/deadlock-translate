import importlib.util
from pathlib import Path
import tempfile

root = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('export_source', root/'scripts/export_source.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
files = module.public_files()
names = {p.relative_to(root).as_posix() for p in files}
assert not names & module.GENERATED
assert not any(n.startswith(('artifacts/', 'dist/', 'tools/')) for n in names)
assert all(not p.suffix.lower() in ('.key', '.dpapi', '.exe', '.dll', '.vpk', '.zip') for p in files)
assert {'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md', '.github/workflows/ci.yml'} <= names
for file in files:
    assert not module.findings(file), file.relative_to(root)
with tempfile.TemporaryDirectory() as folder:
    fixture = Path(folder)/'fixture.txt'
    fixture.write_text('sk-'+'tp-'+'A'*30, encoding='utf-8')
    assert module.findings(fixture)[0]['kind'] == 'API credential'
    fixture.write_text('C:'+chr(92)+'Users'+chr(92)+'example_person'+chr(92)+'state', encoding='utf-8')
    assert module.findings(fixture)[0]['kind'] == 'personal Windows path'
    fixture.write_text('sk-test and https://api.example.com/v1', encoding='utf-8')
    assert not module.findings(fixture)
print('PASS: export boundary, required notices, source scan and credential/path detection')
