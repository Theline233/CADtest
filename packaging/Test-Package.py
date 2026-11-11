"""Verify a fresh extracted package; retain all synthetic test evidence.

Usage: python packaging/Test-Package.py BUILD-RESULT.json NEW-TEST-DIRECTORY [DWG ...]
Requires Windows and the developer Python used to run the test driver. The app
and child engines must use only their own runtimes. No installer is run here.
"""
from pathlib import Path,PurePosixPath
import hashlib,json,os,shutil,subprocess,sys,zipfile,xml.etree.ElementTree as ET

if len(sys.argv)<3:raise SystemExit(__doc__)
repo=Path(__file__).resolve().parents[1]
build_file=Path(sys.argv[1]).resolve()
build=json.loads(build_file.read_text(encoding='utf-8-sig'))
test=Path(sys.argv[2]).resolve();test.mkdir(parents=True,exist_ok=False)
def digest(path):
    with Path(path).open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest().upper()
assert digest(build['zip'])==build['zip_sha256']
manifest=json.loads((build_file.parent/'payload-manifest.json').read_text(encoding='utf-8-sig'))
with zipfile.ZipFile(build['zip']) as archive:
    names=set();payload={}
    for entry in archive.infolist():
        name=entry.filename.replace('\\','/')
        assert name.startswith('CadReview/') and '..' not in PurePosixPath(name).parts and ':' not in name,name
        assert name.casefold() not in names,name
        names.add(name.casefold())
        assert (entry.external_attr>>16)&0xF000!=0xA000,name
        if not entry.is_dir():payload[name[len('CadReview/'):]]=hashlib.sha256(archive.read(entry)).hexdigest().upper()
    assert payload==manifest,'ZIP differs from staged payload'
    archive.extractall(test)
root=test/'CadReview'
for relative,expected in manifest.items():assert digest(root/relative)==expected,relative
base=os.environ.copy()
for key in list(os.environ):
    if key.startswith(('CADTEST_','DOTNET_','PYTHON')):os.environ.pop(key,None)
os.environ['PATH']=str(Path(os.environ['SystemRoot'])/'System32')
os.environ['PYTHONUTF8']='1'
os.environ['CADTEST_POWERSHELL']='__NO_EXTERNAL_POWERSHELL__'
os.environ['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=str(test/'bundle-cache')
sys.path.insert(0,str(root/'work/segmented-20261109'))
import runner
assert runner.ROOT==root
checks=[]
def command(label,args,dotnet=False,timeout=360):
    logs=test/label;logs.mkdir()
    old={key:os.environ.get(key) for key in ('DOTNET_ROOT','DOTNET_ROOT_X64')}
    if dotnet:
        os.environ['DOTNET_ROOT']=os.environ['DOTNET_ROOT_X64']=str(root/'runtime/dotnet')
    try: result=runner.command(args,logs,label,runner.Limits(memory_mib=16384,cpu_percent=75,timeout_seconds=timeout),cwd=root)
    finally:
        for key,value in old.items():
            if value is None:os.environ.pop(key,None)
            else:os.environ[key]=value
    runner.save(logs/'run.json',result)
    if result['status']!='passed':raise RuntimeError(label+' failed; see '+str(logs))
    checks.append(label);print('PASS '+label,flush=True)
    return logs
app=root/'CadReviewUI.exe';python=root/'runtime/python/python.exe'
support=root/'tests';support.mkdir()
shutil.copy2(repo/'tests/make_ui_fixtures.py',support/'make_ui_fixtures.py')
command('synthetic-fixtures',[python,'-B','-X','utf8',support/'make_ui_fixtures.py','outputs/package-fixtures'],dotnet=True)
reviews=json.loads((root/'outputs/package-fixtures/ui-fixtures.json').read_text(encoding='utf8'))['reviews']
command('desktop-integration',[app,'--frontend-integration-test',root/'outputs/package-ui',*reviews])
ui=json.loads((root/'outputs/package-ui/result.json').read_text(encoding='utf8'))
assert ui['checks']>=33
for file in ui['exported']:
    with zipfile.ZipFile(file) as z:
        sheet=ET.fromstring(z.read('xl/worksheets/sheet1.xml'))
        ns={'s':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
        cells={c.attrib['r']:c for c in sheet.findall('.//s:c',ns)}
        for row in (6,7):
            assert cells[f'G{row}'].find('s:v',ns).text==str(row-5)
            assert f'H{row}' not in cells or cells[f'H{row}'].find('s:v',ns) is None
            assert cells[f'I{row}'].find('s:f',ns) is not None
        assert not any(n.startswith(('docProps/','xl/printerSettings/')) for n in z.namelist())
checks.append('independent-styled-xlsx-reopen')
command('interaction',[app,'--interaction-test',root/'outputs/package-interaction',*reviews])

# Verify frozen helper resolution under the actual embedded Python _pth behavior.
frozen=Path(reviews[0]).parent/'recognition/frozen-source'
engine=frozen/'work/segmented-20261109'
probe=engine/'test-import.py'
probe.write_text('import workspace_paths\nprint(workspace_paths.__file__)\n',encoding='utf8')
logs=test/'frozen-import';logs.mkdir()
result=runner.command([python,'-B',engine/'python-gate.py',probe],logs,'probe',runner.Limits(memory_mib=512,cpu_percent=25),wait_for_go=True,cwd=root)
assert result['status']=='passed',result
actual=Path((logs/'probe.stdout.log').read_text(encoding='utf8').strip()).resolve()
assert actual==(engine/'workspace_paths.py').resolve(),actual
checks.append('embedded-python-imports-frozen-helper')

drawings=[]
originals={}
inputs=root/'ui-inputs/package-tests';inputs.mkdir(parents=True)
for number,value in enumerate(sys.argv[3:],1):
    source=Path(value).resolve();originals[str(source)]=digest(source)
    target=inputs/f'sample-{number}.dwg';shutil.copy2(source,target);drawings.append(target)
parallel=None
if drawings:
    command('real-single',[app,'--recognize-test',test/'real-single.log',root/'outputs/real-single',drawings[0]])
    if len(drawings)>1:
        batch={'tasks':[{'id':f'T{i}','drawing':str(path),'output':str(root/f'outputs/parallel-{i}')} for i,path in enumerate(drawings,1)]}
        runner.save(root/'package-batch.json',batch)
        command('real-parallel',[python,'-B','-X','utf8',root/'work/segmented-20261109/runner.py','--parallel-batch',root/'package-batch.json','--output',root/'outputs/parallel-batch','--cpu-percent','75','--max-parallel','2'],dotnet=True)
        one=json.loads((root/'outputs/real-single/review/source.json').read_text(encoding='utf8'))
        many=json.loads((root/'outputs/parallel-1/review/source.json').read_text(encoding='utf8'))
        assert one['items']==many['items']
        parallel=json.loads((root/'outputs/parallel-batch/batch-status.json').read_text(encoding='utf8'))
        checks.append('single-parallel-fields-identical')
for path,expected in originals.items():assert digest(path)==expected
for relative,expected in manifest.items():assert digest(root/relative)==expected,relative
result={'status':'passed','source_commit':build['source_commit'],'checks':checks,'ui_checks':ui['checks'],
        'payload_files_unchanged':len(manifest),'real_drawings':len(drawings),'parallel_status':parallel.get('status') if parallel else None,
        'limits':'Fresh package copy, not a clean OS or installer test. Dialogs use synthetic answers. No real records were approved.'}
runner.save(test/'acceptance.json',result)
print(json.dumps(result,ensure_ascii=False),flush=True)
