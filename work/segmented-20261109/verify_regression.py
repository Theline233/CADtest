"""Independent exact output comparison against prior retained recognition runs."""
from pathlib import Path
import hashlib, json

root=Path(r'D:\CADtest')
run=root/'outputs/segmented-20261109/regression-01/recognition'
baselines={
    'D001':root/'outputs/expanded-20261109/batch-03/samples/E034',
    'D002':root/'outputs/expanded-20261109/batch-03/samples/E012',
    'D003':root/'outputs/expanded-20261109/batch-03/samples/E037',
    'D004':root/'outputs/accuracy-20261109/development8-final/samples/N03',
    'D005':root/'outputs/accuracy-20261109/development-final/samples/S10',
    'D006':root/'outputs/accuracy-20261109/entry-e2e/recognition/samples/D001',
}
def digest(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
results=[]
for sid,old in baselines.items():
    current=run/'samples'/sid
    row={'sample':sid,'baseline':str(old),'files':{}}
    for name in ('frame-field-candidates.json','predictions.json'):
        a,b=old/name,current/name
        same=json.loads(a.read_text(encoding='utf-8'))==json.loads(b.read_text(encoding='utf-8'))
        row['files'][name]={'equal_json':same,'baseline_sha256':digest(a),'current_sha256':digest(b)}
        if not same:raise AssertionError(f'{sid}: {name} differs')
    results.append(row)
path=run.parent/'regression-comparison.json'
if path.exists():raise FileExistsError(path)
path.write_text(json.dumps({'status':'passed','scope':'same six previously seen drawings; exact parsed field and prediction JSON','samples':results},ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'status':'passed','samples':len(results),'compared_json_files':len(results)*2},ensure_ascii=False))
