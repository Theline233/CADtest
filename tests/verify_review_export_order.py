"""Independently reopen the two CFB fixture workbooks and check their audit."""
import json
import sys
import zipfile
from pathlib import Path

from openpyxl import load_workbook

if len(sys.argv)!=2: raise SystemExit('Usage: verify_review_export_order.py outputs/CASE/verify')
directory=Path(sys.argv[1])
paths=json.loads((directory/'exports.json').read_text(encoding='utf-8'))
expected='ABDECF'
qty=[4,2,4,5,0,6]
units=['EA','EA','EA','EA','EA','SET']
finish=['镀碱性蓝白锌',None,None,None,'镀碱性蓝白锌','镀白锌']
disposition=['confirmed','not_required','missing','unconfirmed','confirmed','confirmed']
core=Path(paths['core'])
styled=Path(paths['styled'])
for workbook in (core,styled):
    with zipfile.ZipFile(workbook) as package:
        assert package.testzip() is None

review=load_workbook(paths['review'],read_only=True)
core_doc=load_workbook(core,read_only=True)
style_doc=load_workbook(styled,read_only=True)
try:
    ws=review['复核明细']
    assert [ws.cell(r,22).value for r in range(8,15)]==paths['ids']
    assert ''.join(ws.cell(r,2).value[-1] for r in range(8,15))==expected+'X'
    assert ws.cell(9,5).value=='镀白锌' and ws.cell(9,13).value=='无需表面处理'
    assert all(ws.cell(r,5).value=='镀白锌' and ws.cell(r,13).value=='镀碱性蓝白锌' for r in (8,12))
    assert ws.cell(10,13).value is None and ws.cell(11,13).value=='待确认'
    s=core_doc['确认明细']
    assert ''.join(s.cell(r,2).value[-1] for r in range(8,14))==expected
    assert [s.cell(r,12).value for r in range(8,14)]==paths['ids'][:6]
    assert [s.cell(r,5).value for r in range(8,14)]==finish
    assert [s.cell(r,6).value for r in range(8,14)]==qty
    assert [s.cell(r,7).value for r in range(8,14)]==units
    assert all(s.cell(r,8).value is None and s.cell(r,9).value==f'=IF(H{r}="","",F{r}*H{r})' for r in range(8,14))
    assert s.cell(14,2).value is None
    s=style_doc['报价单样式']
    assert ''.join(s.cell(r,3).value[-1] for r in range(6,12))==expected
    assert [s.cell(r,2).value for r in range(6,12)]==list(range(1,7))
    assert [s.cell(r,6).value for r in range(6,12)]==finish
    assert [s.cell(r,7).value for r in range(6,12)]==qty
    assert all(s.cell(r,8).value is None and s.cell(r,9).value==f'=G{r}*H{r}' for r in range(6,54))
    assert all(s.cell(r,3).value is None for r in range(12,54))
    assert s.cell(54,9).value=='=SUM(I6:I53)'
finally:
    for document in (review,core_doc,style_doc):
        document.close()

audit=json.loads((core.parent/'确认结果.json').read_text(encoding='utf-8'))
assert [r['id'] for r in audit['accepted']]==paths['ids'][:6]
assert [r['treatment_disposition'] for r in audit['accepted']]==disposition
assert [r['final']['treatment'] for r in audit['accepted']]==finish
assert [r['id'] for r in audit['excluded']]==paths['ids'][6:]
assert [(r['id'],r['disposition']) for r in audit['treatment_exceptions']]==list(zip(paths['ids'][2:4],disposition[2:4]))
assert audit['blank_treatment_approved_review_sha256']==audit['review_sha256']
assert audit['accepted'][1]['original']['treatment']=='镀白锌'
assert audit['accepted'][0]['final']['quantity']==4
result={'rows':expected,'ids':paths['ids'][:6],'quantity':qty,'units':units,'treatment':finish,
        'disposition':disposition,'test_price_total':sum(q*p for p,q in enumerate(qty,1)),
        'formula_checks':'6 confirmed amounts, 48 styled amounts and styled total'}
(directory/'independent-xlsx-check.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(result,ensure_ascii=False))
