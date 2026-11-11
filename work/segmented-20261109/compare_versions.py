"""Compare candidate outputs; coverage and agreement are never accuracy estimates."""
from pathlib import Path
from collections import Counter
import argparse,json,hashlib

root=Path(r'D:\CADtest')
ap=argparse.ArgumentParser();ap.add_argument('--before',required=True);ap.add_argument('--after',required=True);args=ap.parse_args()
before=Path(args.before).resolve();after=Path(args.after).resolve()
assert before.is_relative_to(root/'outputs') and after.is_relative_to(root/'outputs/accuracy-20261109')
fields=('name','material','quantity','unit','treatment')
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def collect(folder):
    rows={};frames=0;unresolved=0;file_counts={}
    for sample in sorted((folder/'samples').iterdir()):
        p=read(sample/'predictions.json');frames+=p['frameCount'];unresolved+=len(p['unresolvedFrames'])
        file_counts[sample.name]={'frames':p['frameCount'],'records':len(p['records']),'unresolved_frames':len(p['unresolvedFrames'])}
        for record in p['records']:
            key=(sample.name,record['drawing_number'])
            if key in rows:raise ValueError(f'duplicate candidate key {key}')
            rows[key]=record
    return rows,frames,unresolved,file_counts
a,af,au,ap=collect(before);b,bf,bu,bp=collect(after)
coverage=lambda rows:{field:sum(r.get(field) is not None for r in rows.values()) for field in fields}
changes=[];counts=Counter()
for key in sorted(a.keys()&b.keys()):
    diffs={f:{'before':a[key].get(f),'after':b[key].get(f)} for f in fields if a[key].get(f)!=b[key].get(f)}
    for f in diffs:counts[f]+=1
    if diffs:changes.append({'sample':key[0],'drawing_number':key[1],'fields':diffs,
                             'before_issues':a[key].get('issues',[]),'after_issues':b[key].get('issues',[])})
target=after/'version-delta.json'
if target.exists():raise ValueError('Refusing to overwrite version delta')
result={'method':'Compare extracted candidate values only; no claim about correctness.',
        'before':{'directory':str(before),'records':len(a),'frames':af,'unresolved_frames':au,'coverage':coverage(a),'files':ap},
        'after':{'directory':str(after),'records':len(b),'frames':bf,'unresolved_frames':bu,'coverage':coverage(b),'files':bp},
        'common_candidates':len(a.keys()&b.keys()),'added':[{'sample':k[0],'drawing_number':k[1]} for k in sorted(b.keys()-a.keys())],
        'removed':[{'sample':k[0],'drawing_number':k[1]} for k in sorted(a.keys()-b.keys())],
        'field_change_counts':counts,'field_changes':changes}
target.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:result[k] for k in ('before','after','common_candidates','field_change_counts')},ensure_ascii=False))
