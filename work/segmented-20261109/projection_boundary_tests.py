"""Exercise the current projector's guarded, fail-closed boundaries."""
from pathlib import Path
import json, shutil
import runner

out=runner.ROOT/'outputs/segmented-20261109/projection-boundaries-01'
out.mkdir(parents=True,exist_ok=False)
exe=runner.APP/'projector/bin/Release/net10.0/Projector.exe'
original=runner.ROOT/'outputs/segmented-20261109/smoke-02/recognition/samples/D001/cad-read-report.json'
limits=runner.Limits()
results=[]
def stage(case,name,args,expected,contains=None):
    folder=out/case;folder.mkdir(exist_ok=True)
    result=runner.command([exe,*args,'--wait-for-go'],folder,name,limits,wait_for_go=True)
    stderr=(folder/f'{name}.stderr.log').read_text(encoding='utf-8',errors='replace')
    assert result['status']==expected,(case,name,result,stderr[-1000:])
    if contains:assert contains in stderr,(case,name,stderr[-1000:])
    runner.save(folder/f'{name}.run.json',result)
    results.append({'case':case,'stage':name,'status':result['status'],'expected':expected,'seconds':result['elapsed_seconds'],'peak_commit_mib':result['peak_commit_mib']})

with runner.workspace_lock():
    source=out/'original-copy.json';shutil.copyfile(original,source)
    source_sha=runner.sha(source)
    profile=out/'valid-profile.json';projected=out/'valid-projection.json'
    stage('valid','profile',['profile',source,profile,out/'unused.json'],'passed')
    stage('valid','project',['project',source,profile,projected],'passed')
    a=json.loads(source.read_text(encoding='utf-8'));b=json.loads(projected.read_text(encoding='utf-8'))
    assert a['notifications']==b['notifications'] and len(a['notifications'])>0
    assert b['projectionMetadata']['allNotificationsRetained'] is True
    for mutation in ('removed','added','changed_layout'):
        p=json.loads(profile.read_text(encoding='utf-8'))
        assert p['anchors']
        if mutation=='removed':p['anchors'].pop()
        elif mutation=='added':p['anchors'].append({'layout':'synthetic','blockPath':'unrelated'})
        else:p['anchors'][0]['layout']+='-tampered'
        bad=out/(mutation+'-profile.json');bad.write_text(json.dumps(p,ensure_ascii=False),encoding='utf-8')
        destination=out/(mutation+'-projection.json')
        stage(mutation,'project',['project',source,bad,destination],'failed','profile_anchors_or_text_count_changed')
        assert not destination.exists() and Path(str(destination)+'.partial').exists() and runner.sha(source)==source_sha
    changed=out/'changed-source.json';shutil.copyfile(source,changed)
    with changed.open('ab') as f:f.write(b' ')
    destination=out/'changed-projection.json'
    stage('changed','project',['project',changed,profile,destination],'failed','source_changed_since_profile')
    assert not destination.exists() and runner.sha(source)==source_sha
    duplicate=out/'duplicate.json';duplicate.write_bytes(b'{"sourceFile":"a","sourceFile":"b"}')
    stage('duplicate','profile',['profile',duplicate,out/'duplicate-profile.json',out/'unused.json'],'failed','duplicate_root_property')
    malformed=out/'malformed.json';malformed.write_bytes(b'{"sourceFile":"unclosed')
    stage('malformed','profile',['profile',malformed,out/'malformed-profile.json',out/'unused.json'],'failed')
    oversized=out/'oversize.json';a['texts'][0]['text']='X'*(17*1024*1024)
    oversized.write_text(json.dumps(a,ensure_ascii=False),encoding='utf-8')
    stage('oversize','profile',['profile',oversized,out/'oversize-profile.json',out/'unused.json'],'failed','single_token_or_record_exceeds_16777216_bytes')
    # Exceed the actual 256 MiB projection ceiling with bounded individual records.
    big=out/'projection-overflow.json'
    entry=json.dumps({'kind':'Warning','message':'x'*4096,'exceptionType':None},separators=(',',':')).encode()
    with big.open('wb') as f:
        f.write(b'{"sourceFile":"synthetic","sourceSha256":"synthetic","success":true,"summary":{},"numericSanitization":{},"issues":[],"texts":[],"geometry":[],"notifications":[')
        for i in range(68000):
            if i:f.write(b',')
            f.write(entry)
        f.write(b']}')
    assert big.stat().st_size>256*1024*1024
    big_profile=out/'overflow-profile.json';big_out=out/'overflow-projection.json'
    stage('overflow','profile',['profile',big,big_profile,out/'unused.json'],'passed')
    stage('overflow','project',['project',big,big_profile,big_out],'failed','projection_exceeds_256MiB')
    assert not big_out.exists() and Path(str(big_out)+'.partial').exists()

runner.save(out/'results.json',{'status':'passed','cases':results,'notifications_preserved':len(a['notifications']),'source_unchanged':runner.sha(source)==source_sha})
print(json.dumps({'status':'passed','stages':len(results),'notifications_preserved':len(a['notifications'])},ensure_ascii=False))
