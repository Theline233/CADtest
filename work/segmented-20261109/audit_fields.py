"""Summarize development-set field evidence without reading new holdout results."""
from pathlib import Path
from collections import Counter,defaultdict
import json
root=Path(r'D:\CADtest');sample_root=root/'outputs/polish-20261106/regression-final/samples'
counts=Counter(); values=Counter(); incidents=[]
for folder in sorted(sample_root.iterdir()):
    p=json.loads((folder/'predictions.json').read_text(encoding='utf-8'))
    fr=json.loads((folder/'frame-field-candidates.json').read_text(encoding='utf-8'))
    frames=[f for f in fr['frames'] for f in [f,*f.get('subparts',[])]]
    for r in p['records']:
        counts['records']+=1
        for f in ('material','name','quantity','treatment'):
            counts[f+'_non_null']+=r.get(f) is not None
        counts.update(r['issues'])
        if r.get('material') is not None:values[r['material']]+=1
        matches=[f for f in frames if f.get('drawingNumber')==r['drawing_number']]
        if len(matches)>1:
            mats=[[v.get('text') for v in f.get('materials',[])] for f in matches]
            names=[[v.get('text') for v in f.get('names',[])] for f in matches]
            if r.get('material') and any(not v for v in mats):
                incidents.append({'sample':folder.name,'drawing':r['drawing_number'],'output':r['material'],'frame_materials':mats,'frame_names':names})
out=root/'outputs/accuracy-20261109/field-audit.json'
out.write_text(json.dumps({'counts':counts,'material_values':values,'duplicate_frames_with_missing_material':incidents},ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'counts':counts,'material_values':values.most_common(35),'duplicate_frames_with_missing_material_count':len(incidents),'examples':incidents[:8]},ensure_ascii=False))
