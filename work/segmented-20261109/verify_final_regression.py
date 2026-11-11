"""Compare all final field and prediction outputs with retained CAD-only runs."""
from pathlib import Path
import hashlib,json
root=Path(r'D:\CADtest')
run=root/'outputs/segmented-20261109/final-e2e/recognition'
baselines={
 'D001':root/'outputs/expanded-20261109/large-fix/E025-final/samples/E025',
 'D002':root/'outputs/accuracy-20261109/development-final/samples/S07',
 'D003':root/'outputs/expanded-20261109/batch-03/samples/E034',
 'D004':root/'outputs/expanded-20261109/batch-03/samples/E012',
 'D005':root/'outputs/expanded-20261109/batch-03/samples/E037',
 'D006':root/'outputs/accuracy-20261109/development8-final/samples/N03',
 'D007':root/'outputs/accuracy-20261109/development-final/samples/S10',
 'D008':root/'outputs/accuracy-20261109/entry-e2e/recognition/samples/D001',
}
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
rows=[]
for sid,old in baselines.items():
 current=run/'samples'/sid
 row={'sample':sid,'baseline':str(old),'outputs':{}}
 for name in ('frame-field-candidates.json','predictions.json'):
  left,right=old/name,current/name
  same=json.loads(left.read_text(encoding='utf-8'))==json.loads(right.read_text(encoding='utf-8'))
  row['outputs'][name]={'equal_json':same,'baseline_sha256':sha(left),'current_sha256':sha(right)}
  if not same:raise AssertionError(f'{sid}: {name} differs')
 rows.append(row)
out=run.parent/'regression-comparison.json'
if out.exists():raise FileExistsError(out)
out.write_text(json.dumps({'status':'passed','scope':'8 previously seen CAD drawings, including prior E025 recovery; no Excel backfill','samples':rows},ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'status':'passed','samples':len(rows),'outputs':len(rows)*2}))
