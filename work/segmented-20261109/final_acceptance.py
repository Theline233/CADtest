"""Record independent final integration and source-integrity checks."""
from pathlib import Path
import hashlib,json
root=Path(r'D:\CADtest');out=root/'outputs/accuracy-20261109';work=root/'work/accuracy-20261109'
def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def sha(p):
    with Path(p).open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
checks=[]
def check(name,ok,details=None):checks.append({'name':name,'passed':bool(ok),'details':details})
lock=read(out/'code-lock-v2.json')
for path,digest in lock['source_sha256'].items():check('frozen source '+path,sha(work/path)==digest)
check('second holdout selection unchanged',sha(out/'holdout2-plan/selection.json')==lock['holdout2_selection_sha256'])
for name,expected in [('development-final',16),('development8-final',8),('holdout2-baseline',6),('holdout2-optimized',6)]:
    status=read(out/name/'batch-status.json');runs=read(out/name/'runs.json');integrity=read(out/name/'source-integrity.json') if (out/name/'source-integrity.json').exists() else None
    check(name+' completion',status['status']=='passed' and status['passed_count']==expected and len(runs)==expected)
    check(name+' input hashes',all(r['inputs_unchanged'] for r in runs) and (integrity is None or integrity['passed']))
diff=read(out/'development-final/version-delta.json');d8=read(out/'development8-final/version-delta.json');h=read(out/'holdout2-optimized/version-delta.json')
check('old development preserves candidate identities',len(diff['added'])==len(diff['removed'])==0 and diff['common_candidates']==621)
check('subpart safety changed 21 quantities to pending',diff['field_change_counts'].get('quantity')==21 and all(x['fields'].get('quantity',{}).get('after') is None for x in diff['field_changes'] if 'quantity' in x['fields']))
check('new title format yields two N03 candidates',len(d8['added'])==2 and {v['sample'] for v in d8['added']}=={'N03'})
check('holdout candidate and core values unchanged',h['common_candidates']==38 and not h['added'] and not h['removed'] and not h['field_change_counts'])
records=read(out/'development-final/samples/S08/predictions.json')['records']
r=next(x for x in records if x['drawing_number']=='TA20260606-PROTL105-007-2')
check('S08 uncertain subpart quantity is not selected',r['quantity'] is None and {'2EA','X2SET'}.issubset({e['text'] for e in r['evidence']['quantity']}))
r=next(x for x in read(out/'development-final/samples/S10/predictions.json')['records'] if x['drawing_number']=='TA20260606-T1F70180-012')
check('S10 alternatives and splits preserved',r['quantity'] is None and set(r['quantity_options'])=={28,32} and r['quantity_components'] is None and {tuple(x.values()) for x in r['quantity_component_options']}=={(14,14),(16,16)})
n03=read(out/'development8-final/samples/N03/predictions.json')['records']
check('N03 new-number-only records',len(n03)==2 and all(r['material'] is None and r['quantity'] is None and r['name'] is None for r in n03))
spot=read(out/'holdout-native-visual/spotcheck.json');check('four native-frame spot checks',spot['status']=='passed' and len(spot['results'])==4)
review=read(out/'combined-review/independent-review.json');check('review independent check',review['status']=='passed' and review['rows']==643 and review['files']==24)
preview=read(out/'combined-preview/excel-check.json');check('WPS preview read-only',preview['readOnly'] and preview['unchanged'] and preview['sourceSha256']==review['workbook_sha256'])
pending=read(out/'combined-pending-export/validation.json');check('unconfirmed real review blocked',pending['status']=='blocked' and pending['pending']==643 and not pending['accepted'] and not (out/'combined-pending-export/确认明细.xlsx').exists())
synthetic=read(out/'synthetic-confirmed-export/independent-check.json');check('synthetic confirmation and zero quantity',synthetic['passed'])
check('isolated recognition entry',read(out/'entry-e2e/recognition/batch-status.json')['passed_count']==1 and read(out/'entry-e2e/review/independent-review.json')['rows']==14)
old=read(root/'outputs/polish-20261106/regression-final/protocol.json')['code_sha256']
for p in ['src/SafeCadText.cs','work/extract-requirement-frames.mjs','work/batch-fix-20261106/predict.mjs','work/batch-fix-20261106/quantity.mjs','work/polish-20261106/treatment.mjs']:
    absolute=str(root/p);check('previous production-path source unchanged '+p,sha(absolute)==old[absolute])
unicode=read(out/'unicode/green2-test-results.json');check('Unicode regression',unicode['passed']==79 and unicode['failed']==0)
newframes=read(out/'frames/n03-final-matrix/frame-matrix-result.json');check('new title fixture matrix',newframes['passed'] and len(newframes['results'])==11)
testreport=read(out/'review-data-test/result.json');check('review evidence propagation',testreport['status']=='passed')
failures=[c for c in checks if not c['passed']]
result={'status':'passed' if not failures else 'failed','check_count':len(checks),'failures':failures,
        'development':diff['after'],'previous_development':diff['before'],'new_title_development':d8['after'],
        'independent_holdout':h['after'],'holdout_comparison':read(out/'holdout2-optimized/comparison.json')['summary'],
        'review':{'rows':review['rows'],'files':review['files'],'checks':review['checks'],'workbook_sha256':review['workbook_sha256']},
        'checks':checks,'limitations':['First 24 samples are development data.','Six later samples are small and not representative of all formats.',
        'Field coverage and Excel agreement are not drawing accuracy.','No customer confirmations were applied.']}
target=out/'RESULTS.json'
with target.open('x',encoding='utf8') as f:json.dump(result,f,ensure_ascii=False,indent=2)
print(json.dumps({'status':result['status'],'checks':len(checks),'failures':failures},ensure_ascii=False))
if failures:raise SystemExit(1)
