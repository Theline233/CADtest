"""Profile a saved projection and independently check it, under the CAD Job."""
import argparse, json, sys
from pathlib import Path
import runner

ap=argparse.ArgumentParser()
ap.add_argument('--sample',required=True);ap.add_argument('--output',required=True)
a=ap.parse_args()
sample=runner.local_path(a.sample,runner.ROOT)
out=runner.prepare_output(a.output,runner.ROOT,require_new=True)
frozen=sample.parent.parent/'frozen-source'
projector=frozen/'projector/Projector.exe'
source=sample/'cad-read-report.json';profile=sample/'profile.json'
projected=sample/'cad-read-projection.json';p_profile=out/'projection-profile.json'
auditor=runner.APP/'independent_projection_audit.py'
inputs=(projector,source,profile,projected,auditor,Path(sys.executable))
with runner.workspace_lock():
    before={str(p):runner.sha(p) for p in inputs}
    first=runner.command([projector,'profile',projected,p_profile,out/'unused.json','--wait-for-go'],out,'projection-profile',runner.Limits(),wait_for_go=True)
    runner.save(out/'projection-profile.run.json',first)
    if first['status']!='passed':raise SystemExit('Projection profile failed')
    second=runner.command([sys.executable,'-B',auditor,'--source',source,'--profile',profile,
        '--projected',projected,'--projected-profile',p_profile,'--output',out/'audit.json','--wait-for-go'],out,'audit',runner.Limits(),wait_for_go=True)
    after={str(p):runner.sha(p) for p in inputs}
    second.update(input_sha256_before=before,input_sha256_after=after)
    if before!=after:second.update(status='failed',returncode=1,blocked_reason='audit_input_changed')
    runner.save(out/'audit.run.json',second)
    print(json.dumps({'status':second['status'],'seconds':second['elapsed_seconds'],'peak_commit_mib':second['peak_commit_mib']},ensure_ascii=False))
    raise SystemExit(0 if second['status']=='passed' else 1)
