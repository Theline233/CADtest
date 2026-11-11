import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {predict} from '../../outputs/polish-20261106/regression-final/frozen-source/work/batch-fix-20261106/predict.mjs';

const base='D:/CADtest/outputs/polish-20261106/regression-final/samples';
const out=process.argv[2];
if(!out)throw new Error('Usage: node frame-replay.mjs <new output directory>');
await fs.mkdir(out,{recursive:false});
// Snapshot the proposed extractor and use the historical treatment dependency.
// Other agents may be editing the sibling dependency in the shared work folder.
const snapshot=path.join(out,'frame-frozen-work');
await fs.mkdir(path.join(snapshot,'polish-20261106'),{recursive:true});
const extractor=path.join(snapshot,'extract-requirement-frames.mjs');
const frozenTreatment=path.join(snapshot,'polish-20261106','treatment.mjs');
await fs.copyFile('D:/CADtest/work/accuracy-20261109/extract-requirement-frames.mjs',extractor);
await fs.copyFile('D:/CADtest/outputs/polish-20261106/regression-final/frozen-source/work/polish-20261106/treatment.mjs',frozenTreatment);
const ids=(await fs.readdir(base)).filter(s=>/^(?:S\d\d|H\d\d)$/.test(s)).sort();
assert.equal(ids.length,16);
const same=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
const hash=buffer=>crypto.createHash('sha256').update(buffer).digest('hex');
const all=[],details=[];
for(const id of ids){
  const source=path.join(base,id),dir=path.join(out,id);await fs.mkdir(dir);
  const input=await fs.readFile(path.join(source,'cad-read-report.json'));
  const native=JSON.parse(input.toString('utf8'));
  const before=JSON.parse(await fs.readFile(path.join(source,'frame-field-candidates.json'),'utf8'));
  const oldPrediction=JSON.parse(await fs.readFile(path.join(source,'predictions.json'),'utf8'));
  const frozenPrediction=predict(before);
  await fs.writeFile(path.join(dir,'cad-read-report.json'),input);
  const run=spawnSync(process.execPath,
    [extractor,dir],{encoding:'utf8'});
  assert.equal(run.status,0,`${id}: ${run.stderr}`);
  const after=JSON.parse(await fs.readFile(path.join(dir,'frame-field-candidates.json'),'utf8'));
  const newPrediction=predict(after);
  await fs.writeFile(path.join(dir,'predictions.json'),JSON.stringify(newPrediction,null,2));
  assert.equal(after.frameCount,before.frameCount,`${id}: frame count changed`);
  const frameChanges=[];
  for(let i=0;i<before.frames.length;i++){
    const old=before.frames[i],now=after.frames[i];
    assert.equal(now.index,old.index);assert.equal(now.framePath,old.framePath);
    const keys=[...new Set([...Object.keys(old),...Object.keys(now)])].filter(k=>!same(old[k],now[k]));
    if(keys.length)frameChanges.push({index:old.index,framePath:old.framePath,
      drawingBefore:old.drawingNumber,drawingAfter:now.drawingNumber,keys,
      before:Object.fromEntries(keys.map(k=>[k,old[k]??null])),after:Object.fromEntries(keys.map(k=>[k,now[k]??null]))});
  }
  const oldMap=new Map(oldPrediction.records.map(r=>[r.drawing_number,r]));
  const newMap=new Map(newPrediction.records.map(r=>[r.drawing_number,r]));
  const recordChanges=[];
  for(const number of new Set([...oldMap.keys(),...newMap.keys()])){
    const a=oldMap.get(number),b=newMap.get(number);
    if(!same(a,b)){
      const keys=[...new Set([...Object.keys(a??{}),...Object.keys(b??{})])].filter(k=>!same(a?.[k],b?.[k]));
      recordChanges.push({drawingNumber:number,keys,
        before:Object.fromEntries(keys.map(k=>[k,a?.[k]??null])),after:Object.fromEntries(keys.map(k=>[k,b?.[k]??null]))});
    }
  }
  const ordinary=native.texts.filter(t=>t.kind!=='DIMENSION_GRAPHICS');
  const issueKinds={};for(const issue of native.issues??[])issueKinds[issue.kind]=(issueKinds[issue.kind]??0)+1;
  const row={id,inputSha256:hash(input),sourceSha256:native.sourceSha256,frames:after.frameCount,
    unresolvedBefore:before.frames.filter(f=>!f.drawingNumber).length,
    unresolvedAfter:after.frames.filter(f=>!f.drawingNumber).length,
    drawingChanged:frameChanges.filter(f=>f.drawingBefore!==f.drawingAfter).length,
    changedFrames:frameChanges.length,changedRecords:recordChanges.length,
    recordCountBefore:oldPrediction.records.length,recordCountAfter:newPrediction.records.length,
    quantityChanged:recordChanges.filter(x=>x.keys.includes('quantity')).length,
    ordinaryTexts:ordinary.length,ordinaryTextsRequireReview:ordinary.filter(t=>t.textRequiresReview).length,
    dimensionGraphicsTexts:native.texts.filter(t=>t.kind==='DIMENSION_GRAPHICS').length,
    dimensions:(native.dimensions??[]).length,issueKinds,
    frozenPredictionMatchesSaved:same(frozenPrediction,oldPrediction),
    sourceReportUnchanged:hash(await fs.readFile(path.join(source,'cad-read-report.json')))===hash(input)};
  all.push(row);if(frameChanges.length||recordChanges.length)details.push({id,frameChanges,recordChanges});
  console.log(JSON.stringify(row));
}
const summary={extractorSha256:hash(await fs.readFile(extractor)),treatmentSha256:hash(await fs.readFile(frozenTreatment)),
  samples:all,allCompleted:all.length===16,sourceReportsUnchanged:all.every(x=>x.sourceReportUnchanged),
  allFrozenPredictionsMatchSaved:all.every(x=>x.frozenPredictionMatchesSaved),
  changedFrames:all.reduce((n,x)=>n+x.changedFrames,0),drawingChanged:all.reduce((n,x)=>n+x.drawingChanged,0),
  changedRecords:all.reduce((n,x)=>n+x.changedRecords,0),quantityChanged:all.reduce((n,x)=>n+x.quantityChanged,0),
  ordinaryTextsRequireReview:all.reduce((n,x)=>n+x.ordinaryTextsRequireReview,0)};
await fs.writeFile(path.join(out,'frame-replay-summary.json'),JSON.stringify(summary,null,2));
await fs.writeFile(path.join(out,'frame-replay-differences.json'),JSON.stringify(details,null,2));
console.log(JSON.stringify({summary}));
