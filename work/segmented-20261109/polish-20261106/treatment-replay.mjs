import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {createReadStream} from 'node:fs';
import {createHash} from 'node:crypto';
import {spawnSync} from 'node:child_process';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {predict} from '../../../outputs/polish-20261106/regression-final/frozen-source/work/batch-fix-20261106/predict.mjs';
import {TREATMENT_RULE_VERSION} from './treatment.mjs';

// Runs the actual copied frame extractor on copied native JSON (not DWG or
// Excel). Baseline is the saved 16-group frame JSON. Original artifacts are
// hashed and verified again after the replay.
const oldRoot='D:/CADtest/outputs/polish-20261106/regression-final/samples';
const outputRoot='D:/CADtest/outputs/accuracy-20261109/treatment';
const oldExtractor='D:/CADtest/outputs/polish-20261106/regression-final/frozen-source/work/extract-requirement-frames.mjs';
const oldTreatment='D:/CADtest/outputs/polish-20261106/regression-final/frozen-source/work/polish-20261106/treatment.mjs';
const newTreatment='D:/CADtest/work/accuracy-20261109/polish-20261106/treatment.mjs';
const ids=[...Array.from({length:12},(_,i)=>`S${String(i+1).padStart(2,'0')}`),...Array.from({length:4},(_,i)=>`H${String(i+1).padStart(2,'0')}`)];
async function hash(file){const h=createHash('sha256'); for await(const c of createReadStream(file))h.update(c);return h.digest('hex');}
const hashes=new Map();
async function track(file){const value=await hash(file);hashes.set(file,value);return value;}
const treatmentKeys=new Set(['surfaceTreatment','treatmentEvidence','treatmentCandidates','treatmentDetails','treatmentIssues','treatmentRuleVersion']);
const stripFrame=v=>Array.isArray(v)?v.map(stripFrame):v&&typeof v==='object'
  ?Object.fromEntries(Object.entries(v).filter(([k])=>!treatmentKeys.has(k)).map(([k,x])=>[k,stripFrame(x)])):v;
function stripPrediction(p){
  return {...p,unresolvedFrames:stripFrame(p.unresolvedFrames),records:p.records.map(record=>{
    const r={...record}; delete r.treatment; delete r.treatment_candidates; delete r.treatment_details; delete r.issues;
    if(r.evidence){r.evidence={...r.evidence};delete r.evidence.treatment;}return r;
  })};
}
const params=f=>(f.treatmentDetails?.evidence??[]).flatMap(e=>(e.clauses??[]).flatMap(c=>c.parameters??[]));
const semantic=f=>({value:f.surfaceTreatment??null,candidates:f.treatmentCandidates??[],issues:f.treatmentIssues??[],
  status:f.treatmentDetails?.status??null,evidence:(f.treatmentDetails?.evidence??[]).map(e=>({id:e.id,text:e.text,rawText:e.rawText,
    issues:e.issues,clauses:e.clauses}))});
const equal=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
await fs.mkdir(outputRoot,{recursive:true});
const output=await fs.mkdtemp(path.join(outputRoot,'replay-'));
const files=[oldExtractor,oldTreatment,newTreatment];
for(const f of files)await track(f);
// Other agents may be modifying the shared copied extractor. Generate a
// dedicated copy of the frozen extractor that differs only in module import.
const extractor=path.join(output,'treatment-extractor-replay.mjs');
const originalExtractor=await fs.readFile(oldExtractor,'utf8');
const originalImport="import {extractTreatment} from './polish-20261106/treatment.mjs';";
assert.equal(originalExtractor.split(originalImport).length,2,'Frozen extractor import did not have the expected unique form.');
const replayImport=`import {extractTreatment} from ${JSON.stringify(pathToFileURL(newTreatment).href)};`;
await fs.writeFile(extractor,originalExtractor.replace(originalImport,replayImport));
const groups=[],changes=[],failures=[];
for(const id of ids){
  try{
    const baselineNative=path.join(oldRoot,id,'cad-read-report.json');
    const baselineFrame=path.join(oldRoot,id,'frame-field-candidates.json');
    await track(baselineNative);await track(baselineFrame);
    const before=JSON.parse(await fs.readFile(baselineFrame,'utf8'));
    const dir=path.join(output,id);await fs.mkdir(dir);
    const copiedNative=path.join(dir,'cad-read-report.json');await fs.copyFile(baselineNative,copiedNative);
    assert.equal(await hash(copiedNative),hashes.get(baselineNative),'Native copy hash mismatch.');
    const run=spawnSync(process.execPath,['--max-old-space-size=2048',extractor,dir],{encoding:'utf8',timeout:120000});
    await fs.writeFile(path.join(dir,'treatment-extractor-run.json'),JSON.stringify({status:run.status,stdout:run.stdout,stderr:run.stderr,error:run.error?.message??null},null,2));
    assert.equal(run.status,0,run.stderr||run.error?.message||'extractor failed');
    const after=JSON.parse(await fs.readFile(path.join(dir,'frame-field-candidates.json'),'utf8'));
    assert.deepEqual(stripFrame(after),stripFrame(before),'Non-treatment frame fields changed.');
    const predBefore=predict(before),predAfter=predict(after);
    assert.deepEqual(stripPrediction(predAfter),stripPrediction(predBefore),'Non-treatment prediction fields changed.');
    const summary={sample:id,frames:after.frames.length,records:predAfter.records.length,
      before:{values:0,candidates:0,evidence:0,parameters:0},after:{values:0,candidates:0,evidence:0,parameters:0},
      changedFrames:0,changedValues:0,changedCandidates:0,changedParameters:0,changedPredictionValues:0,
      nonTreatmentFramesUnchanged:true,nonTreatmentPredictionsUnchanged:true};
    for(let i=0;i<after.frames.length;i++){
      const b=before.frames[i],a=after.frames[i];
      assert.equal(a.treatmentRuleVersion,TREATMENT_RULE_VERSION);
      assert.equal(a.surfaceTreatment,a.treatmentDetails.value);
      assert.deepEqual(a.treatmentCandidates,a.treatmentDetails.candidates);
      if(a.treatmentIssues.length)assert.equal(a.surfaceTreatment,null);
      for(const part of a.subparts??[]){assert.equal(part.surfaceTreatment,a.surfaceTreatment);assert.deepEqual(part.treatmentCandidates,a.treatmentCandidates);assert.equal(part.fieldScopes.treatment,'shared_parent_frame');}
      for(const [f,side] of [[b,'before'],[a,'after']]){
        summary[side].values+=Number(f.surfaceTreatment!==null);
        summary[side].candidates+=Number((f.treatmentCandidates??[]).length>0);
        summary[side].evidence+=Number((f.treatmentDetails?.evidence??[]).length>0);
        summary[side].parameters+=Number(params(f).length>0);
      }
      const sb=semantic(b),sa=semantic(a);
      if(!equal(sb,sa)){
        summary.changedFrames++;
        summary.changedValues+=Number(b.surfaceTreatment!==a.surfaceTreatment);
        summary.changedCandidates+=Number(!equal(b.treatmentCandidates,a.treatmentCandidates));
        summary.changedParameters+=Number(!equal(params(b),params(a)));
        changes.push({sample:id,frame:a.index,drawingNumber:a.drawingNumber,before:sb,after:sa});
      }
    }
    for(let i=0;i<predAfter.records.length;i++)summary.changedPredictionValues+=Number(predBefore.records[i].treatment!==predAfter.records[i].treatment);
    groups.push(summary);
    console.log(JSON.stringify({sample:id,frames:summary.frames,changedFrames:summary.changedFrames,changedValues:summary.changedValues}));
  }catch(error){failures.push({sample:id,error:error.stack});console.error(JSON.stringify(failures.at(-1)));}
}
for(const [file,expected] of hashes)if(await hash(file)!==expected)failures.push({file,error:'Source hash changed during replay.'});
const totals=groups.reduce((sum,g)=>{
  for(const key of ['frames','records','changedFrames','changedValues','changedCandidates','changedParameters','changedPredictionValues'])sum[key]=(sum[key]??0)+g[key];
  for(const side of ['before','after'])for(const key of ['values','candidates','evidence','parameters'])sum[side][key]=(sum[side][key]??0)+g[side][key];
  return sum;
},{before:{},after:{}});
const report={passed:failures.length===0&&groups.length===16,ruleVersion:TREATMENT_RULE_VERSION,output,
  method:'16 saved CAD native JSON replays through the frozen frame extractor with only the treatment import redirected, and frozen prediction code. CAD evidence only; no DWG or Excel read. Frame counts are not accuracy.',
  codeHashes:files.map(file=>({file,sha256:hashes.get(file)})),sourceHashes:[...hashes].filter(([file])=>!files.includes(file)).map(([file,sha256])=>({file,sha256})),totals,groups,failures};
await fs.writeFile(path.join(output,'treatment-replay-results.json'),JSON.stringify(report,null,2));
await fs.writeFile(path.join(output,'treatment-replay-changes.json'),JSON.stringify(changes,null,2));
console.log(JSON.stringify({output,passed:report.passed,totals,failures}));
if(!report.passed)process.exitCode=1;
