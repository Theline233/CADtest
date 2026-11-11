"""Freeze final candidate runtime before reading the second holdout outputs."""
from pathlib import Path
import hashlib,json
root=Path(r'D:\CADtest');work=root/'work/accuracy-20261109';out=root/'outputs/accuracy-20261109'
files=['runner.py','RunAccuracy.ps1','extract-requirement-frames.mjs','batch-fix-20261106/predict.mjs',
       'batch-fix-20261106/quantity.mjs','polish-20261106/treatment.mjs','prepare_review.py']
files += [p.relative_to(work).as_posix() for part in ('native','native-source','review-native')
          for p in (work/part).rglob('*') if p.is_file() and not any(x in p.parts for x in ('obj','bin'))]
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
result={'holdout2_selection_sha256':sha(out/'holdout2-plan/selection.json'),
        'previous_evaluated_selection_sha256':sha(out/'holdout-plan/selection.json'),
        'source_sha256':{f:sha(work/f) for f in sorted(files)},
        'note':'First 8 samples, including N03, became development data after format investigation. Six new samples frozen independently before their outcomes were inspected.'}
with (out/'code-lock-v2.json').open('x',encoding='utf8') as f:json.dump(result,f,ensure_ascii=False,indent=2)
print(json.dumps({'locked_files':len(files),'holdout2_selection_sha256':result['holdout2_selection_sha256']},ensure_ascii=False))
