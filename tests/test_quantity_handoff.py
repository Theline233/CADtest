"""Create a frozen synthetic review and verify both real desktop exports."""
import json
import shutil
import subprocess
import sys
from pathlib import Path

from openpyxl import load_workbook

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / 'work/segmented-20261109'))
import runner

out = runner.prepare_output(sys.argv[1], runner.ROOT, require_new=True)
record = out / 'recognition'
record.mkdir(parents=True)
old_reviewer = Path(sys.argv[2]).resolve() if len(sys.argv) == 3 else None
with runner.workspace_lock():
    if old_reviewer is None:
        frozen, sources = runner.freeze_sources(record)
    else:
        frozen = record / 'frozen-source'
        shutil.copytree(old_reviewer, frozen / 'review-native')
        sources = {str(p.relative_to(frozen)): {'source': str(p), 'sha256': runner.sha(p)}
                   for p in (frozen / 'review-native').iterdir() if p.is_file()}
        runner.save(frozen/'manifest.json', sources)
    protocol = record/'protocol.json'
    runner.save(protocol, {'frozen_manifest_sha256': runner.sha(frozen/'manifest.json'), 'scope':'SYNTHETIC ONLY'})
    drawing = out/'synthetic.dwg'
    drawing.write_bytes(b'SYNTHETIC ONLY; NO REAL DRAWING')
    df = runner.sha(drawing)
    data = {'schema_version':1,'task_id':'SYNTHETIC-QUANTITY-'+out.name,'title':'SYNTHETIC QUANTITY ONLY',
            'sources': [{'path':str(p), 'sha256':runner.sha(p)} for p in
                        [drawing,protocol,frozen/'manifest.json',*(frozen/p for p in sources)]],
            'files':[{'id':'F1','name':drawing.name,'path':str(drawing),'sha256':df,
                      'diagnostics':'本次未发现需要文件级确认的诊断','requires_acknowledgement':False}],
            'items':[]}
    for i,qty,unit,warning in [('MISSING',None,'EA','数量尚未确定'),('BOTH',None,None,'数量尚未确定；单位缺失或冲突'),
        ('ZERO',0,'EA',''),('UNIT',7,None,'单位缺失或冲突'),('CROSSED',None,None,'')]:
        row={'id':i,'file_id':'F1','cad':{'drawing_number':f'SYNTHETIC-{i}','name':'PART',
              'material':'SS41','treatment':'发黑','quantity':qty,'unit':unit},'warnings':warning,
              'requires_review':bool(warning),'kind':'part_title','parents':[],'quantity_evidence':'',
              'treatment_evidence':'','reference_info':''}
        if i=='CROSSED':row['default_decision']={'mode':'不纳入','note':'图面整框打X（合成测试标记）'}
        data['items'].append(row)
    manifest=out/'dataset.json'
    runner.save(manifest,data)
    review=out/'review'
    result=runner.command([frozen/'review-native/CadReviewWorkflow.exe','prepare',
        '--input',manifest,'--output',review,'--wait-for-go'],out,'prepare',
        runner.Limits(memory_mib=1024,cpu_percent=25),wait_for_go=True)
    assert result['status']=='passed',result
ui=out/'ui'
app=REPO/'CadReviewUI.exe'
if not app.is_file():app=REPO/'ui/bin/Release/net10.0-windows/CadReviewUI.exe'
completed=subprocess.run([app,'--quantity-handoff-test',ui,review],cwd=REPO,timeout=320)
print((ui/'checks.txt').read_text(encoding='utf-8'))
assert completed.returncode==0,completed.returncode
paths=json.loads((ui/'exports.json').read_text(encoding='utf-8'))
expected=['MISSING','BOTH','ZERO','UNIT']
numbers=['SYNTHETIC-'+r for r in expected]
for key,sheet,first,number_col,quantity_col in [('core','确认明细',8,2,6),('quote','报价单样式',6,3,7)]:
    book=load_workbook(paths[key],read_only=True,data_only=False)
    try:
        ws=book[sheet]
        rows=range(first,first+4)
        assert [ws.cell(r,number_col).value for r in rows]==numbers
        assert [ws.cell(r,quantity_col).value for r in rows]==[None,None,0,7]
        assert all(ws.cell(r,8).value is None and ws.cell(r,9).data_type=='f' for r in rows)
        if key=='core':
            assert [ws.cell(r,7).value for r in rows]==['EA',None,'EA',None]
            assert [ws.cell(r,12).value for r in rows]==expected
        assert ws.cell(first+4,number_col).value is None
    finally:
        book.close()
audit=json.loads((Path(paths['core']).parent/'确认结果.json').read_text(encoding='utf-8'))
assert audit['blank_quantity_approved_review_sha256']==audit['review_sha256']
assert [(x['id'],x['quantity_blank'],x['unit_blank']) for x in audit['quantity_handoffs']]==[
    ('MISSING',True,False),('BOTH',True,True),('UNIT',False,True)]
assert [x['id'] for x in audit['excluded']]==['CROSSED']
print('PASS independent reopen: two genuine blanks, numeric zero and numeric 7; units, formulas, audit preserved')
