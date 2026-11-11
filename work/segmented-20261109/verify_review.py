"""Independently reopen the generated review file and verify all original values."""
from pathlib import Path
import argparse,hashlib,json
from collections import Counter
import openpyxl
root=Path(r'D:\CADtest');ap=argparse.ArgumentParser();ap.add_argument('--review-folder',required=True);args=ap.parse_args()
folder=Path(args.review_folder).resolve()
if not folder.is_relative_to(root/'outputs/segmented-20261109'):raise ValueError('Expected isolated output directory')
source=folder/'source.json';bookpath=folder/'图纸复核.xlsx';resultpath=folder/'independent-review.json'
if resultpath.exists():raise ValueError('Do not overwrite prior evidence')
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
before=sha(bookpath);data=json.loads(source.read_text(encoding='utf-8-sig'));b=openpyxl.load_workbook(bookpath,read_only=True,data_only=False)
sheet=b['复核明细'];file_sheet=b['文件诊断'];items={i['id']:i for i in data['items']};files={i['id']:i for i in data['files']};errors=[];checks=Counter();seen=set();seen_files=set()
def check(ok,label):
    checks['total']+=1
    if not ok:errors.append(label)
def norm(value):return '' if value is None else str(value).strip()
for row in sheet.iter_rows(min_row=8,max_row=sheet.max_row,values_only=True):
    key=row[21];check(key in items and key not in seen,f'unknown/duplicate row {key}')
    if key not in items:continue
    seen.add(key);item=items[key];cad=item['cad'];f=files[item['file_id']]
    original=[item['file_id'],cad['drawing_number'],cad['name'],cad['material'],cad['treatment'],cad['quantity'],cad['unit'],item['warnings'],
              item['kind'],'；'.join(item['parents']),item['quantity_evidence'],item['treatment_evidence'],f['name'],key,item['reference_info']]
    actual=list(row[:8])+list(row[16:23])
    for j,(a,e) in enumerate(zip(actual,original)):
        check(a==e if j==5 and e is not None else norm(a)==norm(e),f'{key} original-{j} differs')
    check(row[8]=='待复核',f'{key} not pending')
    for idx in range(9,16):check(row[idx] is None or row[idx]=='',f'{key} confirmation-{idx} not blank')
check(len(seen)==len(items),'item count')
for row in file_sheet.iter_rows(min_row=8,max_row=file_sheet.max_row,values_only=True):
    key=row[0];check(key in files and key not in seen_files,f'bad file {key}')
    if key not in files:continue
    seen_files.add(key);f=files[key]
    for col,val in [(1,f['name']),(2,f['diagnostics']),(3,'需确认' if f['requires_acknowledgement'] else '仅信息'),(7,f['sha256'])]:
        check(norm(row[col])==norm(val),f'{key} original file field {col}')
    empty=not any(i['file_id']==key for i in data['items'])
    check(row[4]==('待核查' if f['requires_acknowledgement'] or empty else '无需确认'),f'{key} file state')
    check(all(row[c] is None or row[c]=='' for c in (5,6)),f'{key} file confirmation not blank')
check(len(seen_files)==len(files),'file count')
for src in data['sources']:
    p=Path(src['path']);check(p.is_file() and sha(p)==src['sha256'].upper(),f'source hash {p}')
meta=b['绑定信息'];check(meta['B2'].value==data['task_id'],'task ID');check(meta['B3'].value==sha(source),'manifest SHA')
b.close();check(sha(bookpath)==before,'read-only check changed workbook')
result={'status':'passed' if not errors else 'failed','rows':len(seen),'files':len(seen_files),'checks':checks['total'],'errors':errors,
        'source_count':len(data['sources']),'workbook_sha256':before,'input_values_were_not_confirmed':True}
resultpath.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf8')
print(json.dumps({k:result[k] for k in ('status','rows','files','checks','errors')},ensure_ascii=False))
if errors:raise SystemExit(1)
