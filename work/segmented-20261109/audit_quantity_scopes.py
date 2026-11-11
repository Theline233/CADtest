"""Find independent title and stated totals whose relation is not explicit."""
from pathlib import Path
import json
root=Path(r'D:\CADtest');samples=root/'outputs/polish-20261106/regression-final/samples'
cases=[]
for folder in sorted(samples.iterdir()):
    p=json.loads((folder/'predictions.json').read_text(encoding='utf-8'))
    for r in p['records']:
        for d in r['quantity_details']:
            title=d.get('title_block') or {};total=d.get('total') or {}
            if title.get('value') is not None and total.get('value') is not None and title['value']!=total['value'] and not total.get('per_set'):
                cases.append({'sample':folder.name,'drawing':r['drawing_number'],'output':r['quantity'],'unit':r['unit'],
                    'title':title.get('expression'),'title_value':title['value'],'total':total.get('expression'),'total_value':total['value'],
                    'scope':d.get('scope'),'issues':d.get('issues'),'total_details':total})
out=root/'outputs/accuracy-20261109/scope-audit.json';out.write_text(json.dumps(cases,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'count':len(cases),'examples':cases[:4]},ensure_ascii=False))
