"""Replay the 16 previously seen native CAD reports using this isolated version."""
from pathlib import Path
import json,sys
import runner

root=Path(r'D:\CADtest');baseline=root/'outputs/polish-20261106/regression-final'
out=root/'outputs/accuracy-20261109/development-replay'
out.mkdir(exist_ok=False)
limits=runner.Limits()
with runner.workspace_lock(root):
    frozen,manifest=runner.freeze_sources(out,root)
    for name in ('selection.json','excel-reference.json'):
        runner.copy_verified(baseline/name,out/name)
    oldruns=json.loads((baseline/'runs.json').read_text(encoding='utf-8-sig'))
    (out/'samples').mkdir();runs=[]
    for old in oldruns:
        sid=old['id'];folder=out/'samples'/sid;folder.mkdir()
        raw=baseline/'samples'/sid/'cad-read-report.json'
        digest=runner.copy_verified(raw,folder/'cad-read-report.json')
        run=dict(old);run['native_replayed_from']={'path':str(raw),'sha256':digest}
        commands=[('fields',[runner.NODE,frozen/runner.EXTRACTOR,folder],'frame-field-candidates.json'),
                  ('prediction',[runner.NODE,frozen/runner.PREDICT,folder/'frame-field-candidates.json',folder/'predictions.json'],'predictions.json')]
        for name,command,artifact in commands:
            result=runner.command(command,folder,name,limits,cwd=root)
            run[name]=result
            if result['status']!='passed' or not (folder/artifact).is_file():
                run['status']='failed';run['blocked_reason']=name+' replay failed';break
        else:run['status']='passed';run['blocked_reason']=None
        runs.append(run);runner.save(folder/'run.json',run);runner.save(out/'runs.json',runs)
        print(sid+': '+run['status'],flush=True)
    runner.save(out/'protocol.json',{'mode':'replay previously completed CAD native reports; native stage is not rerun',
        'source_run':str(baseline),'source_selection_sha256':runner.sha(baseline/'selection.json'),
        'source_native_report_sha256':{r['id']:r['native_replayed_from']['sha256'] for r in runs},
        'code_sha256':{v['source']:v['sha256'] for v in manifest.values()},'concurrency':1,'limits':runner.asdict(limits)})
    passed=sum(r['status']=='passed' for r in runs)
    runner.save(out/'batch-status.json',{'status':'passed' if passed==len(runs) else 'failed','selected_count':len(runs),'passed_count':passed})
    if passed!=len(runs):sys.exit(1)
