"""Prove audit failure and intermediate tampering stop the real serial loop."""
from pathlib import Path
from unittest.mock import patch
import json,sys
import runner

out=runner.ROOT/'outputs/segmented-20261109/integration-guard-01'
out.mkdir(parents=True,exist_ok=False)
names=('native','profile','projection','projection_profile','projection_audit','fields','prediction')
results=[]
with runner.workspace_lock():
 for scenario in ('audit_failure','audit_input_mutated'):
  base=out/scenario;base.mkdir()
  seed=base/'synthetic.dwg';seed.write_bytes(b'synthetic fixture: not a drawing')
  frozen=base/'frozen';frozen.mkdir();run=base/'run';run.mkdir()
  item={'id':'case','dwg':str(seed),'dwg_sha256':runner.sha(seed)}
  def commands(frozen_arg,item_arg,folder,node,recognition_mode):
   previous=seed;stages=[]
   for name in names:
    target=folder/(name+'.bin')
    code=("import sys\nfrom pathlib import Path\n"
          "if sys.stdin.readline() != 'GO\\n': raise SystemExit(31)\n"
          "src=Path(sys.argv[1]); out=Path(sys.argv[2]); out.write_bytes(src.read_bytes()+b'!')\n")
    if name=='projection_audit' and scenario=='audit_failure':code+="raise SystemExit(47)\n"
    if name=='projection_audit' and scenario=='audit_input_mutated':code+="src.write_bytes(src.read_bytes()+b'changed')\n"
    stages.append((name,[sys.executable,'-B','-c',code,str(previous),str(target)],[previous],[target]))
    previous=target
   return stages
  with patch.object(runner,'stage_commands',commands):
   got=runner.run_samples([item],run,frozen,{},runner.Limits(memory_mib=128,timeout_seconds=10),root=runner.ROOT)[0]
  sample=run/'samples/case'
  assert got['status']=='failed' and got['projection_audit']['status']=='failed'
  assert got['fields']['returncode'] is None and got['prediction']['returncode'] is None
  assert not (sample/'fields.bin').exists() and not (sample/'prediction.bin').exists()
  assert (sample/'projection_audit.bin.failed').exists()
  if scenario=='audit_failure':assert got['projection_audit']['process_returncode']==47
  else:
   assert got['projection_audit']['blocked_reason']=='stage_input_changed'
   assert got['projection_audit']['input_sha256_before']!=got['projection_audit']['input_sha256_after']
  assert runner.sha(seed)==item['dwg_sha256']
  results.append({'scenario':scenario,'status':'passed','blocked_reason':got['blocked_reason']})
runner.save(out/'results.json',{'status':'passed','cases':results})
print(json.dumps({'status':'passed','cases':len(results)},ensure_ascii=False))
