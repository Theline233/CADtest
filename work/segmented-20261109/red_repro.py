"""Replay the exact old Node read on the retained >string-limit fixture."""
from pathlib import Path
import json, shutil
import runner

base = Path(r'D:\CADtest\outputs\segmented-20261109\red-repro')
base.mkdir(parents=True, exist_ok=False)
source = Path(r'D:\CADtest\outputs\expanded-20261109\large-fix\red-synthetic\cad-read-report.json')
copy = base / 'cad-read-report.json'
shutil.copyfile(source, copy)
with runner.workspace_lock():
    result = runner.command([runner.NODE, Path(r'D:\CADtest\work\accuracy-20261109\extract-requirement-frames.mjs'), base], base, 'fields', runner.Limits(memory_mib=3072, timeout_seconds=120))
error = (base / 'fields.stderr.log').read_text(encoding='utf-8')
passed = result['status'] == 'failed' and 'Invalid string length' in error and runner.sha(source) == runner.sha(copy)
(base / 'result.json').write_text(json.dumps({'status': 'reproduced' if passed else 'unexpected', 'stage': result, 'source_sha256': runner.sha(source)}, indent=2), encoding='utf-8')
print(json.dumps({'exact_failure_reproduced': passed, 'returncode': result['returncode'], 'error': 'Invalid string length' if 'Invalid string length' in error else error[-400:]}, ensure_ascii=False))
raise SystemExit(0 if passed else 1)
