"""Create clearly synthetic UI tasks with the actual frozen review engine.

No customer DWG is read, and these fixtures are not recognition accuracy evidence.
Usage: python -B tests/make_ui_fixtures.py outputs/new-ui-fixtures
"""
from pathlib import Path
import hashlib,json,sys

REPO=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(REPO/'work/segmented-20261109'))
import runner

if len(sys.argv)!=2:raise SystemExit(__doc__)
out=runner.prepare_output(sys.argv[1],runner.ROOT,require_new=True)
reviews=[]
with runner.workspace_lock():
    for number in range(1,4):
        task=out/f'synthetic-{number}';recognition=task/'recognition';recognition.mkdir(parents=True)
        frozen,manifest=runner.freeze_sources(recognition)
        protocol=recognition/'protocol.json'
        runner.save(protocol,{'frozen_manifest_sha256':runner.sha(frozen/'manifest.json'),'scope':'SYNTHETIC UI TEST ONLY; no CAD recognition was performed'})
        drawing=task/'synthetic.dwg';drawing.write_bytes(b'NOT A REAL DWG - SYNTHETIC UI TEST')
        sources=[drawing,protocol,frozen/'manifest.json']+[frozen/name for name in manifest]
        data={'schema_version':1,'task_id':f'SYNTHETIC-UI-{out.name}-{number}','title':'Synthetic UI integration test',
              'sources':[{'path':str(p),'sha256':runner.sha(p)} for p in sources],
              'files':[{'id':'F1','name':drawing.name,'path':str(drawing),'sha256':runner.sha(drawing),
                        'diagnostics':'Synthetic UI fixture; not a real drawing','requires_acknowledgement':True}],
              'items':[{'id':f'ITEM-{i}','file_id':'F1','cad':{'drawing_number':f'SYNTHETIC-{i}','name':'Synthetic part',
                        'material':'TEST','treatment':'TEST','quantity':i,'unit':'EA'},'warnings':'','requires_review':False,
                        'kind':'part_title','parents':[],'quantity_evidence':'Synthetic','treatment_evidence':'Synthetic','reference_info':'',
                        **({'default_decision':{'mode':'不纳入','note':'图面整框打X（合成测试标记）'}} if i==1 else {})} for i in (1,2)]}
        dataset=task/'dataset.json';runner.save(dataset,data)
        review=task/'review'
        result=runner.command([frozen/'review-native/CadReviewWorkflow.exe','prepare','--input',dataset,'--output',review,'--wait-for-go'],task,'prepare',runner.Limits(memory_mib=1024,cpu_percent=25),wait_for_go=True)
        runner.save(task/'prepare-result.json',result)
        if result['status']!='passed':raise RuntimeError(str(task)+' prepare failed')
        reviews.append(str(review))
runner.save(out/'ui-fixtures.json',{'scope':'Synthetic only','reviews':reviews})
print(json.dumps({'output':str(out),'reviews':reviews},ensure_ascii=False))
