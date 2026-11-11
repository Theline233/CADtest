"""Render one development example in AutoCAD from a disposable copy, with resource limits."""
from pathlib import Path
import hashlib,json,shutil,subprocess,time,psutil
root=Path(r'D:\CADtest')
out=root/'outputs/accuracy-20261109/native-visual-s08';out.mkdir(exist_ok=False)
samples=root/'outputs/polish-20261106/regression-final'
selection=json.loads((samples/'selection.json').read_text(encoding='utf-8'))['selected']
item=next(s for s in selection if s['id']=='S08')
src=Path(item['dwg']);copy=out/'drawing-copy.dwg'
def sha(p):
    with p.open('rb') as h:return hashlib.file_digest(h,'sha256').hexdigest().upper()
before=sha(src);shutil.copy2(src,copy)
assert before==sha(copy)==item['dwg_sha256']
image=out/'title-notes.png';script=out/'render.scr'
script.write_text('\n'.join(['(setvar "FILEDIA" 0)','(setvar "GRIDMODE" 0)','(setvar "TILEMODE" 1)',
    '_.ZOOM','_W','642860,-81265','643145,-81155','_.PNGOUT',image.as_posix(),'','_.QUIT','_N','']),encoding='ascii')
started=time.monotonic();peak=0;reason=None
with (out/'stdout.bin').open('wb') as a,(out/'stderr.bin').open('wb') as b:
    proc=subprocess.Popen([r'D:\CAD\AutoCAD 2022\accoreconsole.exe','/i',str(copy),'/s',str(script)],cwd=out,stdout=a,stderr=b,creationflags=subprocess.CREATE_NO_WINDOW)
    owned=psutil.Process(proc.pid)
    while proc.poll() is None:
        time.sleep(.5)
        try:children=[owned]+owned.children(recursive=True)
        except psutil.NoSuchProcess:break
        memory=0
        for child in children:
            try:
                info=child.memory_info();memory+=getattr(info,'private',info.rss)
            except psutil.NoSuchProcess:pass
        peak=max(peak,memory)
        if time.monotonic()-started>120:reason='timeout'
        elif memory>2300*1024**2:reason='memory_limit'
        elif psutil.virtual_memory().available<1200*1024**2:reason='system_memory_limit'
        if reason:
            for child in reversed(children):
                try:child.kill()
                except psutil.NoSuchProcess:pass
            break
    proc.wait(timeout=15)
log=(out/'stdout.bin').read_bytes().decode('utf-16-le',errors='replace');(out/'console.txt').write_text(log,encoding='utf-8')
result={'status':reason or ('rendered' if image.exists() and image.stat().st_size>100 else 'no_image'),
        'native_engine':'AutoCAD 2022 Core Console','sample':'S08','drawing':src.name,
        'region':'frame 15 subpart -2 label and adjacent quantity','source_sha_before':before,'source_sha_after':sha(src),
        'elapsed_seconds':round(time.monotonic()-started,2),'peak_private_mib':round(peak/1024**2,1),
        'returncode':proc.returncode,'font_warning':'font' in log.lower() or '字体' in log}
(out/'result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=False))
