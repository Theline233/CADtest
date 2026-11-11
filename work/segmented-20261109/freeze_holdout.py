"""Freeze a new, filename/size-stratified holdout before reading its CAD outputs."""
from pathlib import Path
import hashlib,json,math

root=Path(r'D:\CADtest')
out=root/'outputs/accuracy-20261109/holdout-plan'
inventory=json.loads((root/'outputs/batch-validation-20261106/inventory.json').read_text(encoding='utf-8-sig'))
known=json.loads((root/'outputs/polish-20261106/regression-final/selection.json').read_text(encoding='utf-8-sig'))['selected']
excluded={r['dwg_sha256'].upper() for r in known}|{r['sha256'].upper() for r in inventory['excluded_tested_inputs']}
files={r['path']:r for r in inventory['files']}
pool=[]
for pair in inventory['pair_candidates']:
    d=files[pair['dwg']];x=files[pair['xlsx']]
    if pair['match_level']!='strong_filename' or pair['version_review_required'] or d['sha256'].upper() in excluded:continue
    if d['size_bytes']>64*1024*1024 or x['size_bytes']>64*1024*1024:continue
    if d['strong_candidate_count']!=1:continue
    pool.append((d,x,pair))
selected=[];used_d=set();used_x=set()
for batch in ('1.一次发货','2.二次发货'):
    group=sorted((p for p in pool if p[0]['batch']==batch),key=lambda p:(p[0]['size_bytes'],p[0]['path'].casefold()))
    for percentile in (.10,.35,.65,.90):
        candidates=sorted(group,key=lambda p:(abs(group.index(p)/(len(group)-1)-percentile),p[0]['path'].casefold()))
        d,x,pair=next(p for p in candidates if p[0]['sha256'] not in used_d and p[1]['sha256'] not in used_x)
        used_d.add(d['sha256']);used_x.add(x['sha256'])
        selected.append({'id':f'N{len(selected)+1:02d}','batch':batch,'dwg':d['path'],'xlsx':x['path'],
                         'dwg_sha256':d['sha256'],'xlsx_sha256':x['sha256'],
                         'dwg_size_bytes':d['size_bytes'],'selection_size_percentile':percentile,
                         'pairing_status':'strong_filename_business_unconfirmed','business_pair_confirmed':False})
assert len(selected)==8 and sum(x['dwg_size_bytes']+files[x['xlsx']]['size_bytes'] for x in selected)<256*1024*1024
out.mkdir(parents=True,exist_ok=False)
data={'method':'4 size-stratified strong-filename pairs per batch; no DWG results or Excel field values inspected for selection; old known SHA excluded',
      'selected':selected}
(out/'selection.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
for item in selected:
    for typ in ('dwg','xlsx'):
        digest=hashlib.file_digest(open(item[typ],'rb'),'sha256').hexdigest().upper()
        if digest!=item[typ+'_sha256'].upper():raise ValueError(f'{typ} input changed: {item["id"]}')
print(json.dumps({'count':len(selected),'sizes_mib':[round(s['dwg_size_bytes']/2**20,3) for s in selected],
                  'selection_sha256':hashlib.sha256((out/'selection.json').read_bytes()).hexdigest().upper()},ensure_ascii=False))
