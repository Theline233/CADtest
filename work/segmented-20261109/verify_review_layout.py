"""Check workbook controls and original/confirmation styling independently."""
from pathlib import Path
import argparse,hashlib,json
import openpyxl

ap=argparse.ArgumentParser();ap.add_argument('--review-folder',required=True);a=ap.parse_args()
folder=Path(a.review_folder).resolve();root=Path(r'D:\CADtest\outputs\segmented-20261109')
if not folder.is_relative_to(root):raise ValueError('Expected isolated output directory')
bookpath=folder/'图纸复核.xlsx';manifest=folder/'source.json';out=folder/'independent-layout.json'
if out.exists():raise FileExistsError(out)
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
before=sha(bookpath);data=json.loads(manifest.read_text(encoding='utf-8-sig'))
book=openpyxl.load_workbook(bookpath,read_only=False,data_only=False)
checks=0
def check(condition,label):
 global checks
 checks+=1
 if not condition:raise AssertionError(label)
check(book.sheetnames==['复核明细','文件诊断','绑定信息'],'sheet names')
check(book['绑定信息'].sheet_state=='veryHidden','binding metadata visibility')
ws=book['复核明细'];files=book['文件诊断'];last=7+len(data['items']);flast=7+len(data['files'])
for sheet,end,col in ((ws,last,'W'),(files,flast,'H')):
 check(sheet.freeze_panes=='C8',sheet.title+' freeze')
 check(sheet.auto_filter.ref==f'A7:{col}{end}',sheet.title+' filter')
 check(all(sheet.row_dimensions[r].height and 38<=sheet.row_dimensions[r].height<=409 for r in range(8,end+1)),sheet.title+' row heights')
check(any('I8:I' in str(v.sqref) for v in ws.data_validations.dataValidation),'row decisions validation')
check(any('E8:E' in str(v.sqref) for v in files.data_validations.dataValidation),'file decisions validation')
for r in range(8,last+1):
 check(ws.cell(r,9).value=='待复核',f'row {r} pending')
 for c in range(9,17):
  check(ws.cell(r,c).fill.fgColor.rgb[-6:]=='FFF6D9',f'row {r} confirmation fill')
 for c in range(1,24):
  check(ws.cell(r,c).data_type!='f',f'row {r} unexpected formula')
for r in range(8,flast+1):
 check(files.cell(r,4).value=='需确认' and files.cell(r,5).value=='待核查',f'file {r} requires review')
book.close();check(sha(bookpath)==before,'workbook changed during read-only check')
out.write_text(json.dumps({'status':'passed','checks':checks,'rows':len(data['items']),'files':len(data['files']),'workbook_sha256':before},ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'status':'passed','checks':checks,'rows':len(data['items']),'files':len(data['files'])},ensure_ascii=False))
