"""Read-only independent verification of the deliberately synthetic export."""
from pathlib import Path
import hashlib, json
import openpyxl

base=Path(r'D:\CADtest\outputs\segmented-20261109\synthetic-export-test\模拟确认明细-禁止报价')
bookpath=base/'确认明细.xlsx'
record=json.loads((base/'确认结果.json').read_text(encoding='utf-8-sig'))
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
before=sha(bookpath)
assert before==record['output_sha256'] and '合成测试' in record['reviewer']
book=openpyxl.load_workbook(bookpath,read_only=False,data_only=False)
assert book.sheetnames==['确认明细']
ws=book['确认明细']; checks=0
def verify(condition,message):
    global checks
    checks+=1
    if not condition:raise AssertionError(message)
verify(ws.max_row==9,'row count')
verify(ws.freeze_panes=='C8','freeze panes')
verify(bool(ws.auto_filter.ref),'filter')
verify([ws.cell(7,c).value for c in range(1,13)]==['样本','图号','部品名','材质','表面处理','数量','单位','单价','金额','复核说明','原图文件','记录ID'],'headers')
for row in (8,9):
    item=record['accepted'][row-8]; final=item['final']
    for col,key in [(2,'drawing_number'),(3,'name'),(4,'material'),(5,'treatment'),(6,'quantity'),(7,'unit')]:
        verify(ws.cell(row,col).value==final[key],f'row {row}, {key}')
    verify(ws.cell(row,2).value==f'SYNTHETIC-TEST-{row-7:03d}','synthetic number')
    verify(ws.cell(row,8).value is None,'unit price must be blank')
    verify(ws.cell(row,9).value==f'=IF(H{row}="","",F{row}*H{row})','formula')
    verify(ws.cell(row,12).value==item['id'],'record ID')
    verify('合成测试' in ws.cell(row,10).value,'test-only note')
    verify(item['original']['drawing_number']!=final['drawing_number'],'original evidence retained')
    verify(ws.cell(row,8).fill.fgColor.rgb[-6:]=='FFF6D9','input highlight')
verify(ws.cell(8,6).value==2 and ws.cell(9,6).value==0,'quantity including zero')
book.close()
verify(sha(bookpath)==before,'read-only inspection modified output')
result={'status':'passed','checks':checks,'rows':2,'workbook_sha256':before,'scope':'synthetic test only; no user approval'}
path=base/'independent-check.json'
if path.exists():raise FileExistsError(path)
path.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=False))
