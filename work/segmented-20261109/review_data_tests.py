"""Synthetic end-to-end check: uncertain CAD evidence reaches the review dataset."""
from pathlib import Path
import hashlib,json,subprocess,sys
root=Path(r'D:\CADtest');out=root/'outputs/accuracy-20261109/review-data-test';out.mkdir(exist_ok=False)
run=out/'run';sample=run/'samples/F01';sample.mkdir(parents=True)
dwg=out/'synthetic.dwg';dwg.write_bytes(b'NOT_A_REAL_DRAWING_SYNTHETIC_TEST')
sha=hashlib.sha256(dwg.read_bytes()).hexdigest().upper()
def write(path,value):path.write_text(json.dumps(value,ensure_ascii=False),encoding='utf8')
write(run/'selection.json',{'selected':[{'id':'F01','dwg':str(dwg),'dwg_sha256':sha}]})
write(run/'runs.json',[{'id':'F01','status':'passed','inputs_unchanged':True}])
write(run/'protocol.json',{'scope':'synthetic test'})
write(sample/'cad-read-report.json',{'success':True,'sourceFile':dwg.name,'sourceSha256':sha,'issues':[]})
record={'drawing_number':'TA20260606-TEST-001','name':None,'material':None,'quantity':None,'unit':None,'treatment':None,
        'issues':['frame_field_issues','quantity_scope_ambiguous'],'record_kinds':['subpart'],'parent_drawing_numbers':['TA20260606-TEST'],
        'quantity_options':[4,8],'quantity_component_options':[{'normal':2,'mirror':2},{'normal':4,'mirror':4}],
        'quantity_candidate_unit_options':['EA','SET'],'treatment_candidates':['化学镀镍'],'evidence':{},
        'field_association_evidence':[{'field':'material','candidates':[{'text':'SS41'}],
             'reviewTexts':[{'text':'?','rawText':r'\M+bad'}]},
             {'field':'quantity','candidates':[{'text':'2EA'}],'reviewTexts':[{'text':'X2SET','rawText':r'X\M+bad'}]}]}
write(sample/'predictions.json',{'sourceFile':dwg.name,'sourceSha256':sha,'records':[record]})
target=out/'dataset.json'
proc=subprocess.run([sys.executable,'-B','-X','utf8',str(root/'work/accuracy-20261109/prepare_review.py'),
                     '--run',str(run),'--output',str(target)],capture_output=True,text=True,encoding='utf8')
if proc.returncode:raise RuntimeError(proc.stderr)
d=json.loads(target.read_text(encoding='utf8'));r=d['items'][0]
checks={'quantity_unselected':r['cad']['quantity'] is None,'material_unselected':r['cad']['material'] is None,
        'original_quantity':all(s in r['quantity_evidence'] for s in ('2EA','X2SET',r'X\M+bad')),
        'material_evidence':all(s in r['warnings'] for s in ('SS41',r'\M+bad')),
        'candidate_components':all(s in r['warnings'] for s in ('2/2','4/4')),
        'candidate_units':all(s in r['warnings'] for s in ('EA','SET')),
        'scope_explained':'数量适用范围不明确' in r['warnings'],'review_required':r['requires_review']}
write(out/'result.json',{'status':'passed' if all(checks.values()) else 'failed','checks':checks})
print(json.dumps(checks,ensure_ascii=False))
if not all(checks.values()):raise SystemExit(1)
