"""Bounded read-only layout verification of the final review workbook."""
import json
from pathlib import Path
import runner

base=runner.ROOT/'outputs/segmented-20261109/final-e2e'
folder=base/'verification/layout'
folder.mkdir(parents=True,exist_ok=False)
python=Path(r'C:\Program Files\WindowsApps\OpenAI.CodexPrimaryRuntime.v26-1104-2876-0_26.1104.2876.0_x64__3k8sg7r9htsxt\dependencies\python\python.exe')
gate=runner.APP/'python-gate.py';verifier=runner.APP/'verify_review_layout.py';book=base/'review/图纸复核.xlsx'
before={str(p):runner.sha(p) for p in (python,gate,verifier,book)}
with runner.workspace_lock():
 result=runner.command([python,'-B',gate,verifier,'--review-folder',base/'review'],folder,'layout',runner.Limits(),wait_for_go=True)
after={str(p):runner.sha(p) for p in (python,gate,verifier,book)}
result.update(input_sha256_before=before,input_sha256_after=after)
if before!=after:result.update(status='failed',returncode=1,blocked_reason='audit_input_changed')
runner.save(folder/'run.json',result)
print(json.dumps({'status':result['status'],'seconds':result['elapsed_seconds'],'peak_commit_mib':result['peak_commit_mib']},ensure_ascii=False))
raise SystemExit(0 if result['status']=='passed' else 1)
