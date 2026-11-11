"""Bounded independent reopen of the two-large-drawing review workbook."""
from pathlib import Path
import argparse, json
import runner

ap=argparse.ArgumentParser()
ap.add_argument('--base',default=str(runner.ROOT/'outputs/segmented-20261109/large-e2e-01'))
base=runner.local_path(ap.parse_args().base,runner.ROOT)
if not base.is_relative_to(runner.ROOT/'outputs/segmented-20261109'):raise ValueError('Expected isolated output directory')
folder=base/'verification'/'review-reopen'
folder.mkdir(parents=True,exist_ok=False)
python=Path(r'C:\Program Files\WindowsApps\OpenAI.CodexPrimaryRuntime.v26-1104-2876-0_26.1104.2876.0_x64__3k8sg7r9htsxt\dependencies\python\python.exe')
verifier=runner.APP/'verify_review.py'
gate=runner.APP/'python-gate.py'
book=base/'review'/'图纸复核.xlsx'
before={str(p):runner.sha(p) for p in (python,verifier,gate,book)}
with runner.workspace_lock():
    result=runner.command([python,'-B',gate,verifier,'--review-folder',base/'review'],folder,'review-audit',runner.Limits(),wait_for_go=True)
after={str(p):runner.sha(p) for p in (python,verifier,gate,book)}
result.update(input_sha256_before=before,input_sha256_after=after)
if before!=after:result.update(status='failed',blocked_reason='audit_input_changed',returncode=1)
runner.save(folder/'run.json',result)
print(json.dumps({'status':result['status'],'seconds':result['elapsed_seconds'],'peak_commit_mib':result['peak_commit_mib']},ensure_ascii=False))
raise SystemExit(0 if result['status']=='passed' else 1)
