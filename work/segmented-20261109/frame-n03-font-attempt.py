"""Substitute a local font only in an unsaved disposable AutoCAD drawing."""
from pathlib import Path
import hashlib, json, shutil, subprocess, time
root=Path(r'D:\CADtest')
raw=json.loads((root/'outputs/accuracy-20261109/holdout-optimized/samples/N03/cad-read-report.json').read_text(encoding='utf-8'))
src=root/'1.一次发货'/raw['sourceFile'];out=root/'outputs/accuracy-20261109/frames/n03-native-font-attempt';out.mkdir(exist_ok=False)
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
before=sha(src);assert before==raw['sourceSha256'];copy=out/'drawing-copy.dwg';shutil.copyfile(src,copy);assert sha(copy)==before
commands=['(setvar "FILEDIA" 0)','(setvar "GRIDMODE" 0)','(setvar "TILEMODE" 1)',
    '(progn (setq fsx (tblnext "STYLE" T)) (while fsx (princ (strcat "\\nCADTEST_STYLE:" (cdr (assoc 2 fsx)) "|" (if (assoc 3 fsx) (cdr (assoc 3 fsx)) ""))) (setq fsx (tblnext "STYLE"))) (princ))',
    '(progn (setq fsx (tblnext "STYLE" T)) (while fsx (if (assoc 3 fsx) (progn (setq frec (entget (tblobjname "STYLE" (cdr (assoc 2 fsx))))) (if (assoc 3 frec) (entmod (subst (cons 3 "C:/Windows/Fonts/malgun.ttf") (assoc 3 frec) frec))))) (setq fsx (tblnext "STYLE"))) (princ))',
    '_.REGENALL']
labels=[t for t in raw['texts'] if t['text'].strip()=='NUMBER'];windows=[]
for i,t in enumerate(labels,1):
    x,y=t['position'][:2];win=[x-230,y-30,x+320,y+205];windows.append(win)
    commands+=['_.ZOOM','_W',f'{win[0]:.6f},{win[1]:.6f}',f'{win[2]:.6f},{win[3]:.6f}',
               '_.PNGOUT',(out/f'title-{i}.png').as_posix(),'']
commands+=['_.QUIT','_Y','']
script=out/'frame-font-attempt.scr';script.write_text('\n'.join(commands),encoding='ascii');start=time.monotonic()
proc=subprocess.run([r'D:\CAD\AutoCAD 2022\accoreconsole.exe','/i',str(copy),'/s',str(script)],cwd=out,
    stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=120,creationflags=subprocess.CREATE_NO_WINDOW)
log=proc.stdout.decode('utf-16-le',errors='replace');(out/'frame-console.txt').write_text(log,encoding='utf-8')
result={'sourceShaBefore':before,'sourceShaAfter':sha(src),'returnCode':proc.returncode,'fontSubstitution':'malgun.ttf on disposable copy',
    'elapsedSeconds':round(time.monotonic()-start,2),'windows':windows,'images':[{'path':f'title-{i}.png',
    'exists':(out/f'title-{i}.png').exists(),'bytes':(out/f'title-{i}.png').stat().st_size if (out/f'title-{i}.png').exists() else 0} for i in range(1,len(labels)+1)]}
(out/'frame-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(result,ensure_ascii=False))
