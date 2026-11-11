"""Diagnostic screenshots using only disposable in-memory style/color substitutions."""
from pathlib import Path
import hashlib,json,shutil,subprocess,time
root=Path(r'D:\CADtest');raw=json.loads((root/'outputs/accuracy-20261109/holdout-optimized/samples/N03/cad-read-report.json').read_text(encoding='utf-8'))
source=root/'1.一次发货'/raw['sourceFile'];out=root/'outputs/accuracy-20261109/frames/n03-native-visibility-attempt-2';out.mkdir(exist_ok=False)
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
before=sha(source);assert before==raw['sourceSha256'];copy=out/'drawing-copy.dwg';shutil.copyfile(source,copy);assert sha(copy)==before
commands=['(setvar "FILEDIA" 0)','(setvar "GRIDMODE" 0)','(setvar "TILEMODE" 1)',
 '(progn (princ "\\nCADTEST_RAW_ENTITY:") (princ (entget (handent "43F2"))) (princ))',
 '(progn (setq st (entget (tblobjname "STYLE" "Standard"))) (entmod (subst (cons 3 "C:/Windows/Fonts/malgun.ttf") (assoc 3 st) st)) (princ))',
 '(defun cadfixtext (e / d) (setq d (entget e)) (if (member (cdr (assoc 0 d)) (list "TEXT" "MTEXT")) (progn (if (assoc 420 d) (setq d (vl-remove (assoc 420 d) d))) (if (assoc 430 d) (setq d (vl-remove (assoc 430 d) d))) (if (assoc 62 d) (setq d (subst (cons 62 7) (assoc 62 d) d)) (setq d (append d (list (cons 62 7))))) (if (assoc 7 d) (setq d (subst (cons 7 "Standard") (assoc 7 d) d))) (entmod d))) (princ))',
 '(progn (setq bb (tblnext "BLOCK" T)) (while bb (setq ee (entnext (tblobjname "BLOCK" (cdr (assoc 2 bb))))) (while ee (setq dd (entget ee)) (if (= (cdr (assoc 0 dd)) "ENDBLK") (setq ee nil) (progn (cadfixtext ee) (setq ee (entnext ee))))) (setq bb (tblnext "BLOCK"))) (princ))',
 '(progn (setq ss (ssget "_X" (list (cons 0 "TEXT,MTEXT") (cons 410 "Model")))) (if ss (progn (princ (strcat "\\nCADTEST_MODEL_TEXT_COUNT:" (itoa (sslength ss)))) (setq ii 0) (repeat (sslength ss) (cadfixtext (ssname ss ii)) (setq ii (1+ ii))))) (princ "\\nCADTEST_AFTER_ENTITY:") (princ (entget (handent "43F2"))) (princ))',
 '_.REGENALL']
labels=[t for t in raw['texts'] if t['text'].strip()=='NUMBER'];windows=[]
for i,t in enumerate(labels,1):
 x,y=t['position'][:2];w=[x-230,y-30,x+320,y+205];windows.append(w)
 commands+=['_.ZOOM','_W',f'{w[0]:.6f},{w[1]:.6f}',f'{w[2]:.6f},{w[3]:.6f}','_.PNGOUT',(out/f'title-{i}.png').as_posix(),'']
commands+=['_.QUIT','_Y',''];script=out/'frame-visibility.scr';script.write_text('\n'.join(commands),encoding='ascii')
start=time.monotonic();p=subprocess.run([r'D:\CAD\AutoCAD 2022\accoreconsole.exe','/i',str(copy),'/s',str(script)],cwd=out,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=120,creationflags=subprocess.CREATE_NO_WINDOW)
log=p.stdout.decode('utf-16-le',errors='replace');(out/'frame-console.txt').write_text(log,encoding='utf-8')
result={'sourceShaBefore':before,'sourceShaAfter':sha(source),'returnCode':p.returncode,'elapsedSeconds':round(time.monotonic()-start,2),
 'inMemoryOnly':'force text style Standard/malgun and color 7 on a drawing copy; no text content or position changes',
 'images':[{'path':f'title-{i}.png','exists':(out/f'title-{i}.png').exists(),'bytes':(out/f'title-{i}.png').stat().st_size if (out/f'title-{i}.png').exists() else 0} for i in range(1,len(labels)+1)]}
(out/'frame-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(result,ensure_ascii=False))
