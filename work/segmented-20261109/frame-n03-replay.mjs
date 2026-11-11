import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {predict} from '../../outputs/accuracy-20261109/holdout-optimized/frozen-source/work/accuracy-20261109/batch-fix-20261106/predict.mjs';

const out=process.argv[2];if(!out)throw new Error('new output directory required');await fs.mkdir(out,{recursive:false});
const locked='D:/CADtest/outputs/accuracy-20261109/holdout-optimized/frozen-source/work/accuracy-20261109';
const snap=path.join(out,'frame-frozen-work');await fs.mkdir(path.join(snap,'polish-20261106'),{recursive:true});
const extractor=path.join(snap,'extract-requirement-frames.mjs');
await fs.copyFile('D:/CADtest/work/accuracy-20261109/extract-requirement-frames.mjs',extractor);
await fs.copyFile(`${locked}/polish-20261106/treatment.mjs`,path.join(snap,'polish-20261106/treatment.mjs'));
const inputs=[{group:'development',root:'D:/CADtest/outputs/accuracy-20261109/development-replay/samples',ids:['H01','H02','H03','H04',...Array.from({length:12},(_,i)=>`S${String(i+1).padStart(2,'0')}`)]},
  {group:'current8',root:'D:/CADtest/outputs/accuracy-20261109/holdout-optimized/samples',ids:Array.from({length:8},(_,i)=>`N${String(i+1).padStart(2,'0')}`)}];
const hash=x=>crypto.createHash('sha256').update(x).digest('hex').toUpperCase(),same=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
const rows=[],changes=[];
for(const set of inputs){const groupDir=path.join(out,set.group);await fs.mkdir(groupDir);
  for(const id of set.ids){const original=path.join(set.root,id),dir=path.join(groupDir,id);await fs.mkdir(dir);
    const raw=await fs.readFile(path.join(original,'cad-read-report.json'));
    const before=JSON.parse(await fs.readFile(path.join(original,'frame-field-candidates.json'),'utf8'));
    const old=JSON.parse(await fs.readFile(path.join(original,'predictions.json'),'utf8'));
    const baselineMatches=same(predict(before),old);
    await fs.writeFile(path.join(dir,'cad-read-report.json'),raw);
    const run=spawnSync(process.execPath,[extractor,dir],{encoding:'utf8'});assert.equal(run.status,0,`${id}: ${run.stderr}`);
    const after=JSON.parse(await fs.readFile(path.join(dir,'frame-field-candidates.json'),'utf8'));
    const now=predict(after);await fs.writeFile(path.join(dir,'predictions.json'),JSON.stringify(now,null,2));
    const oldFrames=new Map(before.frames.map(f=>[JSON.stringify([f.index,f.layout,f.framePath]),f]));
    const newFrames=new Map(after.frames.map(f=>[JSON.stringify([f.index,f.layout,f.framePath]),f]));
    const added=[...newFrames].filter(([k])=>!oldFrames.has(k)).map(([,f])=>f);
    const removed=[...oldFrames].filter(([k])=>!newFrames.has(k)).map(([,f])=>f);
    const changed=[...oldFrames].filter(([k,v])=>newFrames.has(k)&&!same(v,newFrames.get(k))).map(([k,v])=>({before:v,after:newFrames.get(k)}));
    const oldRecords=new Map(old.records.map(r=>[r.drawing_number,r])),newRecords=new Map(now.records.map(r=>[r.drawing_number,r]));
    const addedRecords=[...newRecords].filter(([k])=>!oldRecords.has(k)).map(([,v])=>v);
    const removedRecords=[...oldRecords].filter(([k])=>!newRecords.has(k)).map(([,v])=>v);
    const changedRecords=[...oldRecords].filter(([k,v])=>newRecords.has(k)&&!same(v,newRecords.get(k))).map(([k,v])=>({before:v,after:newRecords.get(k)}));
    const row={group:set.group,id,inputSha256:hash(raw),framesBefore:before.frameCount,framesAfter:after.frameCount,
      unresolvedBefore:before.frames.filter(f=>!f.drawingNumber).length,unresolvedAfter:after.frames.filter(f=>!f.drawingNumber).length,
      addedFrames:added.length,removedFrames:removed.length,changedExistingFrames:changed.length,
      recordsBefore:old.records.length,recordsAfter:now.records.length,addedRecords:addedRecords.length,removedRecords:removedRecords.length,
      changedExistingRecords:changedRecords.length,baselinePredictorMatchesSaved:baselineMatches,
      sourceReportUnchanged:hash(await fs.readFile(path.join(original,'cad-read-report.json')))===hash(raw)};
    rows.push(row);if(added.length||removed.length||changed.length||addedRecords.length||removedRecords.length||changedRecords.length)
      changes.push({group:set.group,id,addedFrames:added,removedFrames:removed,changedFrames:changed,addedRecords,removedRecords,changedRecords});
    console.log(JSON.stringify(row));
  }
}
const result={extractorSha256:hash(await fs.readFile(extractor)),frozenPredictorSha256:hash(await fs.readFile(`${locked}/batch-fix-20261106/predict.mjs`)),
  frozenQuantitySha256:hash(await fs.readFile(`${locked}/batch-fix-20261106/quantity.mjs`)),
  frozenTreatmentSha256:hash(await fs.readFile(`${locked}/polish-20261106/treatment.mjs`)),rows,
  completed:rows.length===24,baselinePredictionsMatch:rows.every(r=>r.baselinePredictorMatchesSaved),
  sourceReportsUnchanged:rows.every(r=>r.sourceReportUnchanged)};
await fs.writeFile(path.join(out,'frame-replay-summary.json'),JSON.stringify(result,null,2));
await fs.writeFile(path.join(out,'frame-replay-differences.json'),JSON.stringify(changes,null,2));
console.log(JSON.stringify({completed:result.completed,baselinePredictionsMatch:result.baselinePredictionsMatch,sourceReportsUnchanged:result.sourceReportsUnchanged}));
