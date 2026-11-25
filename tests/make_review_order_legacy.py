"""Make a synthetic, no-geometry task with an actual legacy frozen reviewer."""
from pathlib import Path
import json
import shutil
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'work/segmented-20261109'))
import runner

if len(sys.argv)!=3: raise SystemExit('Usage: make_review_order_legacy.py outputs/NEW outputs/BASELINE/review-native')
out=runner.prepare_output(sys.argv[1],runner.ROOT,require_new=True)
review_native=runner.local_path(sys.argv[2],runner.ROOT)
frozen=out/'recognition/frozen-source'
shutil.copytree(review_native,frozen/'review-native')
manifest={str(p.relative_to(frozen)):{'source':str(review_native/p.name),'sha256':runner.sha(p)}
          for p in (frozen/'review-native').iterdir() if p.is_file()}
runner.save(frozen/'manifest.json',manifest)
protocol=out/'recognition/protocol.json'
runner.save(protocol,{'frozen_manifest_sha256':runner.sha(frozen/'manifest.json'),'scope':'SYNTHETIC LEGACY TEST ONLY'})
drawing=out/'legacy-layout.dwg'
drawing.write_bytes(b'SYNTHETIC LEGACY INPUT (not a DWG)')
paths=[drawing,protocol,frozen/'manifest.json',*(frozen/key for key in manifest)]
task_id='SYNTHETIC-LEGACY-'+out.name
dataset={
    'schema_version':1,'task_id':task_id,'title':'SYNTHETIC LEGACY TEST',
    'sources':[{'path':str(p),'sha256':runner.sha(p)} for p in paths],
    'files':[{'id':'D001','name':drawing.name,'path':str(drawing),'sha256':runner.sha(drawing),
              'diagnostics':'本次未发现需要文件级确认的诊断','requires_acknowledgement':False}],
    'items':[
        {'id':f'LEGACY-{i}','file_id':'D001','cad':{'drawing_number':f'SYNTHETIC-LEGACY-{i}',
         'name':'Legacy fixture','material':'SS41','quantity':i,'unit':'EA','treatment':'镀白锌'},
         'warnings':'','requires_review':False,'kind':'subpart' if i==2 else 'assembly_title',
         'parents':['SYNTHETIC-LEGACY-1'] if i==2 else [],'quantity_evidence':'Synthetic',
         'treatment_evidence':'Synthetic','reference_info':''} for i in (1,2)]
}
runner.save(out/'input.json',dataset)
review=out/'review'
with runner.workspace_lock():
    result=runner.command([frozen/'review-native/CadReviewWorkflow.exe','prepare','--input',out/'input.json',
        '--output',review,'--wait-for-go'],out,'legacy-prepare',runner.Limits(memory_mib=1024,cpu_percent=25),wait_for_go=True)
runner.save(out/'legacy-prepare-result.json',result)
if result['status']!='passed':raise SystemExit('Legacy fixture prepare failed')
archive=out/'ui-decisions.json'
runner.save(archive,{
    'Schema':1,'TaskId':task_id,'ManifestSha256':runner.sha(review/'source.json'),
    'Items':{'LEGACY-1':{'Mode':'采用图纸'}},'Files':{'D001':{'State':'无需确认'}}
})
print(json.dumps({'review':str(review),'legacy_archive':str(archive)},ensure_ascii=False))
