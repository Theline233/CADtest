"""Render two N03 title areas with AutoCAD Core Console on a disposable copy."""
from pathlib import Path
import hashlib, json, shutil, subprocess, time

root = Path(r'D:\CADtest')
native = json.loads((root/'outputs/accuracy-20261109/holdout-optimized/samples/N03/cad-read-report.json').read_text(encoding='utf-8'))
src = root/'1.一次发货'/native['sourceFile']
out = root/'outputs/accuracy-20261109/frames/n03-native-visual'
out.mkdir(exist_ok=False)
def sha(p):
    with p.open('rb') as stream: return hashlib.file_digest(stream,'sha256').hexdigest().upper()
before=sha(src); assert before==native['sourceSha256']
copy=out/'drawing-copy.dwg'; shutil.copyfile(src,copy); assert sha(copy)==before
labels=[t for t in native['texts'] if t['text'].strip()=='NUMBER']
commands=['(setvar "FILEDIA" 0)','(setvar "GRIDMODE" 0)','(setvar "TILEMODE" 1)']
windows=[]
for i,label in enumerate(labels,1):
    x,y=label['position'][:2]
    window=[x-230,y-30,x+320,y+205]; windows.append(window)
    commands+=['_.ZOOM','_W',f'{window[0]:.6f},{window[1]:.6f}',f'{window[2]:.6f},{window[3]:.6f}',
      '_.PNGOUT',(out/f'title-{i}.png').as_posix(),'']
commands+=['_.QUIT','_Y','']
script=out/'frame-render.scr';script.write_text('\n'.join(commands),encoding='ascii')
start=time.monotonic()
proc=subprocess.run([r'D:\CAD\AutoCAD 2022\accoreconsole.exe','/i',str(copy),'/s',str(script)],
    cwd=out,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=120,creationflags=subprocess.CREATE_NO_WINDOW)
log=proc.stdout.decode('utf-16-le',errors='replace');(out/'frame-console.txt').write_text(log,encoding='utf-8')
result={'sourceShaBefore':before,'sourceShaAfter':sha(src),'copyShaBefore':before,'returnCode':proc.returncode,
        'elapsedSeconds':round(time.monotonic()-start,2),'windows':windows,
        'images':[{'path':f'title-{i}.png','exists':(out/f'title-{i}.png').exists(),
                   'bytes':(out/f'title-{i}.png').stat().st_size if (out/f'title-{i}.png').exists() else 0}
                  for i in range(1,len(labels)+1)],'fontWarning':'font' in log.lower() or '字体' in log}
(out/'frame-visual-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=False))
