import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {createReadStream} from 'node:fs';
import {createHash} from 'node:crypto';

// Independently trace every changed frame's evidence to native text identity,
// decoded/raw text and same-layout frame bounds. This is not visual or human
// validation of the drawing, or a claim that the frame association is correct.
const out='D:/CADtest/outputs/accuracy-20261109/treatment';
const replay=process.argv[2];
if(!replay || path.dirname(path.resolve(replay)).toLowerCase()!==path.resolve(out).toLowerCase())throw Error('Pass a replay directory directly under the treatment output.');
const report=JSON.parse(await fs.readFile(path.join(replay,'treatment-replay-results.json'),'utf8'));
const changes=JSON.parse(await fs.readFile(path.join(replay,'treatment-replay-changes.json'),'utf8'));
assert.equal(report.passed,true);
let checks=0;const failures=[],evidence=[],seen={},counts={changedFrames:0,checkedEvidence:0};
function test(name,fn){try{fn();checks++;}catch(e){failures.push({name,error:e.message});}}
async function sha(file){const h=createHash('sha256');for await(const c of createReadStream(file))h.update(c);return h.digest('hex');}
for(const entry of [...report.codeHashes,...report.sourceHashes]){
  const actual=await sha(entry.file);test(`source hash ${entry.file}`,()=>assert.equal(actual,entry.sha256));
}
const csvCell=value=>'"'+String(value??'').replaceAll('"','""')+'"';
const numberList=[];
for(const group of report.groups){
  const root=`D:/CADtest/outputs/polish-20261106/regression-final/samples/${group.sample}`;
  const original=JSON.parse(await fs.readFile(path.join(root,'frame-field-candidates.json'),'utf8'));
  const updated=JSON.parse(await fs.readFile(path.join(replay,group.sample,'frame-field-candidates.json'),'utf8'));
  const native=JSON.parse(await fs.readFile(path.join(root,'cad-read-report.json'),'utf8'));
  const nativeById=new Map(native.texts.map(t=>[t.id,t]));
  const groupChanges=changes.filter(x=>x.sample===group.sample);
  const beforeValues=original.frames.filter(f=>f.surfaceTreatment!==null).length;
  const afterValues=updated.frames.filter(f=>f.surfaceTreatment!==null).length;
  test(`${group.sample} counts`,()=>{assert.equal(original.frames.length,updated.frames.length);assert.equal(groupChanges.length,group.changedFrames);assert.equal(beforeValues,group.before.values);assert.equal(afterValues,group.after.values);});
  for(const change of groupChanges){
    counts.changedFrames++;
    const before=original.frames.find(f=>f.index===change.frame),after=updated.frames.find(f=>f.index===change.frame);
    test(`${group.sample}/${change.frame} summary`,()=>{
      assert.ok(before&&after);assert.equal(after.drawingNumber,change.drawingNumber);
      assert.deepEqual(before.treatmentCandidates,change.before.candidates);assert.deepEqual(after.treatmentCandidates,change.after.candidates);
      assert.equal(before.surfaceTreatment,change.before.value);assert.equal(after.surfaceTreatment,change.after.value);
      assert.deepEqual(after.treatmentIssues,change.after.issues);
    });
    for(const e of after.treatmentDetails.evidence){
      const n=nativeById.get(e.id),b=after.bounds,p=n?.position;
      test(`${group.sample}/${change.frame}/${e.id} native association`,()=>{
        assert.ok(n);assert.equal(e.text,n.text);assert.equal(e.rawText,n.rawText);assert.equal(e.layout,n.layout);
        assert.deepEqual(e.position,n.position);assert.equal(after.layout,n.layout);assert.ok(b&&p);
        assert.ok(p[0]>=b[0]-0.001&&p[0]<=b[2]+0.001&&p[1]>=b[1]-0.001&&p[1]<=b[3]+0.001);
      });
      counts.checkedEvidence++;
      const params=(e.clauses??[]).flatMap(c=>c.parameters??[]);
      const matches=(e.clauses??[]).flatMap(c=>(c.matches??[]).map(m=>({rule:m.rule,value:m.value,text:m.matched_text})));
      const item={sample:group.sample,frame:change.frame,drawingNumber:after.drawingNumber,sourceId:e.id,
        text:e.text,rawText:e.rawText,position:e.position,layout:e.layout,parameters:params,matches,
        beforeCandidates:change.before.candidates,afterCandidates:change.after.candidates,issues:after.treatmentIssues};
      evidence.push(item);
      for(const match of matches){seen[match.rule]??=new Set();seen[match.rule].add(`${group.sample}|${change.frame}|${e.id}`);}
      if(group.sample==='S08'&&matches.some(m=>m.rule==='high_frequency_quench'))numberList.push(e.id);
    }
  }
}
test('all reported changed frames were inspected',()=>assert.equal(counts.changedFrames,report.totals.changedFrames));
test('S08 high-frequency source count and independent native ids',()=>{assert.equal(numberList.length,75);assert.equal(new Set(numberList).size,75);});
test('H04 Kanigen candidate source occurrences',()=>assert.equal([...seen.kanigen_electroless_nickel].filter(x=>x.startsWith('H04|')).length,9));
const s04=evidence.find(e=>e.sample==='S04'&&e.frame===11&&e.text==='열처리 부');
test('S04 local-area text remains a scoped issue',()=>{assert.ok(s04);assert.ok(s04.issues.includes('scope_requires_review'));});
const issuesOut={passed:failures.length===0,checks,failures,counts,distinctS08NativeTextIds:new Set(numberList).size,
  rules:Object.fromEntries(Object.entries(seen).map(([rule,ids])=>[rule,ids.size])),
  boundary:'Native-record identity, literal contents, decoded/raw text and bounding-box membership only; not a visual inspection or independently verified semantic ground truth.'};
await fs.writeFile(path.join(out,'treatment-independent-verification.json'),JSON.stringify(issuesOut,null,2));
await fs.writeFile(path.join(out,'treatment-candidate-evidence.json'),JSON.stringify(evidence,null,2));
const headers=['sample','frame','drawingNumber','sourceId','text','rawText','parameters','matches','beforeCandidates','afterCandidates','issues'];
const csv=[headers.map(csvCell).join(','),...evidence.map(e=>headers.map(key=>csvCell(Array.isArray(e[key])?JSON.stringify(e[key]):e[key])).join(','))].join('\r\n');
await fs.writeFile(path.join(out,'treatment-candidate-evidence.csv'),'\ufeff'+csv+'\r\n');
console.log(JSON.stringify(issuesOut));
if(!issuesOut.passed)process.exitCode=1;
