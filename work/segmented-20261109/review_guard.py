"""Run validation/export with the task's frozen review engine and shared guard."""
import argparse, json, sys, uuid
from datetime import datetime, timezone
from pathlib import Path
import runner

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument('mode', choices=('validate','export'))
    ap.add_argument('--manifest',required=True); ap.add_argument('--review',required=True)
    ap.add_argument('--output'); ap.add_argument('--reviewer')
    ap.add_argument('--memory-mib',type=float,default=3072)
    ap.add_argument('--timeout',type=float,default=300)
    ap.add_argument('--cpu-percent',type=float,default=25)
    a=ap.parse_args()
    limits=runner.Limits(memory_mib=a.memory_mib,timeout_seconds=a.timeout,cpu_percent=a.cpu_percent)
    limits.validate()
    source=runner.local_path(a.manifest,runner.ROOT)
    review=runner.local_path(a.review,runner.ROOT)
    if source.name!='source.json' or source.parent.name!='review':raise ValueError('Manifest must be the task review/source.json')
    recognition=source.parent.parent/'recognition'
    protocol=json.loads((recognition/'protocol.json').read_text(encoding='utf-8'))
    frozen=recognition/'frozen-source'
    manifest_path=frozen/'manifest.json'
    if runner.sha(manifest_path)!=protocol['frozen_manifest_sha256']:raise ValueError('Frozen manifest does not match task protocol')
    frozen_manifest=json.loads(manifest_path.read_text(encoding='utf-8'))
    runner.verify_frozen(frozen,frozen_manifest)
    program=frozen/'review-native/CadReviewWorkflow.exe'
    args=[program,a.mode,'--manifest',source,'--review',review]
    if a.mode=='export':
        if not a.output or not a.reviewer:raise ValueError('Export requires output and reviewer')
        args+=['--output',a.output,'--reviewer',a.reviewer]
    args+=['--wait-for-go']
    logs=runner.ROOT/'outputs/segmented-review-operations'/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S')+'-'+uuid.uuid4().hex[:12])
    logs.mkdir(parents=True,exist_ok=False)
    with runner.workspace_lock():
        runner.verify_frozen(frozen,frozen_manifest)
        before={str(p):runner.sha(p) for p in (source,review)}
        result=runner.command(args,logs,a.mode,limits,wait_for_go=True)
        after={str(p):runner.sha(p) for p in (source,review)}
        result['input_sha256_before']=before;result['input_sha256_after']=after
        if after!=before:result.update(status='failed',returncode=1,blocked_reason='review_or_manifest_changed')
        runner.verify_frozen(frozen,frozen_manifest)
        if runner.sha(manifest_path)!=protocol['frozen_manifest_sha256']:
            result.update(status='failed',returncode=1,blocked_reason='frozen_manifest_changed')
    runner.save(logs/'result.json',result)
    print((logs/(a.mode+'.stdout.log')).read_text(encoding='utf-8',errors='replace'),end='')
    error=(logs/(a.mode+'.stderr.log')).read_text(encoding='utf-8',errors='replace')
    if error:print(error,file=sys.stderr,end='')
    print('复核操作记录：'+str(logs))
    return result['returncode']

if __name__=='__main__':
    try:raise SystemExit(main())
    except (Exception,KeyboardInterrupt) as exc:
        print(f'Review guard blocked: {exc}',file=sys.stderr)
        raise SystemExit(1)
