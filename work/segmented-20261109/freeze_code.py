"""Lock isolated runtime source hashes before inspecting the holdout outputs."""
from pathlib import Path
import hashlib,json
root=Path(r'D:\CADtest');work=root/'work/accuracy-20261109';out=root/'outputs/accuracy-20261109'
files=['runner.py','extract-requirement-frames.mjs','batch-fix-20261106/predict.mjs',
       'batch-fix-20261106/quantity.mjs','polish-20261106/treatment.mjs',
       'native/CADtest.dll','native/CADtest.exe','prepare_review.py']
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
record={'selection_sha256':sha(out/'holdout-plan/selection.json'),
        'source_sha256':{f:sha(work/f) for f in files},
        'note':'Frozen before inspecting holdout CAD outputs; 16 prior samples remain development data.'}
target=out/'code-lock.json'
with target.open('x',encoding='utf-8') as f:json.dump(record,f,ensure_ascii=False,indent=2)
print(json.dumps({'files':len(files),'selection_sha256':record['selection_sha256']},ensure_ascii=False))
