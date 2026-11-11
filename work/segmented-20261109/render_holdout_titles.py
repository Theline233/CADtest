"""Render preselected holdout title regions in AutoCAD on disposable copies.

Four sample IDs and the first frame rule are fixed before inspecting results.
The anchor is used only to locate the crop, not to supply expected values.
"""
from pathlib import Path
import hashlib,json,math,shutil,subprocess,time,psutil

root=Path(r'D:\CADtest');run=root/'outputs/accuracy-20261109/holdout-baseline'
out=root/'outputs/accuracy-20261109/holdout-native-visual';out.mkdir(exist_ok=False)
selection=json.loads((run/'selection.json').read_text(encoding='utf-8-sig'))['selected']
def sha(p):
    with p.open('rb') as h:return hashlib.file_digest(h,'sha256').hexdigest().upper()
results=[]
for sid in ('N02','N04','N06','N08'):
    folder=out/sid;folder.mkdir();sample=next(s for s in selection if s['id']==sid)
    src=Path(sample['dwg']);before=sha(src);copy=folder/'drawing-copy.dwg';shutil.copy2(src,copy)
    assert sha(copy)==before==sample['dwg_sha256']
    source=run/'samples'/sid
    fields=json.loads((source/'frame-field-candidates.json').read_text(encoding='utf-8-sig'))
    raw=json.loads((source/'cad-read-report.json').read_text(encoding='utf-8-sig'))
    if not fields['frames']:
        results.append({'sample':sid,'status':'no_first_frame','source_sha':before});continue
    frame=fields['frames'][0]
    labels=[t for t in raw['texts'] if t['layout']==frame['layout'] and t['blockPath']==frame['framePath']
            and t['text'].strip() in ('PART No.','PART NAME','MATERIAL',"Q'TY")]
    chosen={e['id'] for k in ('drawingCandidates','names','materials','quantityEvidence','quantityContext') for e in frame.get(k,[])}
    points=labels+[t for t in raw['texts'] if t['id'] in chosen]
    points=[t for t in points if all(isinstance(v,(int,float)) and math.isfinite(v) for v in t['position'][:2])]
    if len(points)<2:
        results.append({'sample':sid,'status':'crop_unavailable','source_sha':before});continue
    anchor=next((t for t in labels if t['text'].strip()=='PART No.'),points[0]);h=abs(anchor['height']) or 1
    x=[t['position'][0] for t in points];y=[t['position'][1] for t in points]
    margin=7*h; x0,x1=min(x)-margin,max(x)+margin*2;y0,y1=min(y)-margin,max(y)+margin
    if x1-x0<35*h:x1=x0+35*h
    if y1-y0<25*h:y1=y0+25*h
    if max(x1-x0,y1-y0)>max(h*400,1e5):
        results.append({'sample':sid,'status':'crop_too_large','source_sha':before});continue
    image=folder/'title.png';script=folder/'render.scr'
    script.write_text('\n'.join(['(setvar "FILEDIA" 0)','(setvar "GRIDMODE" 0)','(setvar "TILEMODE" 1)',
        '_.ZOOM','_W',f'{x0:.6f},{y0:.6f}',f'{x1:.6f},{y1:.6f}','_.PNGOUT',image.as_posix(),'','_.QUIT','_N','']),encoding='ascii')
    started=time.monotonic();peak=0;reason=None
    with (folder/'stdout.bin').open('wb') as a,(folder/'stderr.bin').open('wb') as b:
        proc=subprocess.Popen([r'D:\CAD\AutoCAD 2022\accoreconsole.exe','/i',str(copy),'/s',str(script)],cwd=folder,stdout=a,stderr=b,creationflags=subprocess.CREATE_NO_WINDOW)
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
    log=(folder/'stdout.bin').read_bytes().decode('utf-16-le',errors='replace');(folder/'console.txt').write_text(log,encoding='utf-8')
    result={'sample':sid,'frame_index':frame['index'],'status':reason or ('rendered' if image.exists() and image.stat().st_size>100 else 'no_image'),
        'source_sha_before':before,'source_sha_after':sha(src),'elapsed_seconds':round(time.monotonic()-started,2),
        'peak_private_mib':round(peak/1024**2,1),'returncode':proc.returncode,'font_warning':'font' in log.lower() or '字体' in log,
        'window':[x0,y0,x1,y1]}
    results.append(result);print(json.dumps(result,ensure_ascii=False),flush=True)
(out/'results.json').write_text(json.dumps({'preselected_ids':['N02','N04','N06','N08'],'frame_policy':'first title frame',
    'engine':'AutoCAD 2022 Core Console','results':results},ensure_ascii=False,indent=2),encoding='utf-8')
