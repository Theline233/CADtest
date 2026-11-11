"""Compare four transcriptions from native AutoCAD images with first-frame outputs.

Transcriptions are local visual spot checks, not a representative accuracy set.
Aggregated conflicting duplicates remain pending even when this one frame is legible.
"""
from pathlib import Path
import json,hashlib
root=Path(r'D:\CADtest\outputs\accuracy-20261109');run=root/'holdout-optimized'
cases=[
    {'id':'N02','number':'TA20260606-C1650700-041','name':'DRIVE SHAFT','material':'S45C','title_quantity':'1EA','total':75,'unit':'EA','record_value':None,'record_alternatives':[75,10]},
    {'id':'N04','number':'TA20260606-L1F65030-020','name':'OIL PAN','material':'SS41','title_quantity':'1EA','total':17,'unit':'EA','record_value':17},
    {'id':'N06','number':'TA20260606-T1F70180-026','name':'ADJUST HOUSING','material':'SS41','title_quantity':'1EA','total':16,'unit':'EA','record_value':None,'record_alternatives':[16,14]},
    {'id':'N08','number':'TA20260606-T1F70180-034','name':'TURN PLATE','material':'SS41','title_quantity':'1EA','total':16,'unit':'EA','record_value':16},
]
failures=[];results=[]
for c in cases:
    folder=run/'samples'/c['id'];p=json.loads((folder/'predictions.json').read_text(encoding='utf8'))
    f=json.loads((folder/'frame-field-candidates.json').read_text(encoding='utf8'))['frames'][0]
    r=next((r for r in p['records'] if r['drawing_number']==c['number']),None)
    detail=next((d for d in (r or {}).get('quantity_details',[]) if d['frame_index']==f['index']),{})
    actual={'number':f['drawingNumber'],'name':f['names'][0]['text'] if len(f['names'])==1 else None,
            'material':f['materials'][0]['text'] if len(f['materials'])==1 else None,
            'title_quantity':''.join(e['text'] for e in f['quantityEvidence']),
            'total':detail.get('value'),'unit':detail.get('unit'),
            'record_value':r.get('quantity') if r else None}
    checks={k:actual[k]==c[k] for k in actual}
    if 'record_alternatives' in c:checks['record_alternatives']=set(r.get('quantity_options',[]))==set(c['record_alternatives'])
    failures.extend(f"{c['id']}:{k}" for k,v in checks.items() if not v)
    img=root/'holdout-native-visual'/c['id']/'title.png'
    results.append({'sample':c['id'],'frame_index':f['index'],'image_sha256':hashlib.sha256(img.read_bytes()).hexdigest().upper(),
                    'visual_transcription':c,'prediction':actual,'checks':checks})
out=root/'holdout-native-visual'/'spotcheck.json'
with out.open('x',encoding='utf8') as h:json.dump({'status':'passed' if not failures else 'failed',
 'scope':'Four preselected frame crops visually transcribed from native renderer; no claim about remaining frames or overall accuracy.',
 'results':results,'failures':failures},h,ensure_ascii=False,indent=2)
print(json.dumps({'status':'passed' if not failures else 'failed','frames':len(cases),'failures':failures},ensure_ascii=False))
if failures:raise SystemExit(1)
