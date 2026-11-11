import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {predict} from '../../outputs/polish-20261106/regression-final/frozen-source/work/batch-fix-20261106/predict.mjs';

const out=process.argv[2];
if(!out)throw new Error('Usage: node frame-duplicate-review-tests.mjs <new output directory>');
await fs.mkdir(out,{recursive:false});
const text=(id,value,x,y,blockPath,extra={})=>({id,text:value,rawText:value,kind:'TEXT',layout:'Model',
  blockPath,position:[x,y,0],height:1,rotation:0,axisX:[1,0,0],axisY:[0,1,0],
  invisible:false,textRequiresReview:false,...extra});
const texts=[],geometry=[];
for(let i=0;i<2;i++){
  const dx=i*200,frame=`frame-${i}`,values=`values-${i}`;
  texts.push(text(`title-${i}`,'PART No.',60+dx,10,frame),text(`material-label-${i}`,'MATERIAL',60+dx,25,frame),
    text(`number-${i}`,'TA20260606-X-001',61+dx,8,values),text(`material-${i}`,'S45C',61+dx,23,values));
  geometry.push({layout:'Model',blockPath:frame,points:[[dx,0],[dx+100,0],[dx+100,80],[dx,80]]});
}
texts.push(text('review','undecoded',62,23,'values-0',{textRequiresReview:true,rawText:'\\M+bad'}));
await fs.writeFile(path.join(out,'cad-read-report.json'),JSON.stringify({sourceFile:'synthetic-duplicate-review',texts,geometry}));
const proc=spawnSync(process.execPath,
  [process.env.CADTEST_FRAME_EXTRACTOR ?? 'D:/CADtest/work/accuracy-20261109/extract-requirement-frames.mjs',out],{encoding:'utf8'});
assert.equal(proc.status,0,proc.stderr);
const extracted=JSON.parse(await fs.readFile(path.join(out,'frame-field-candidates.json'),'utf8'));
const record=predict(extracted).records[0];
const actual={frames:extracted.frames.map(f=>({material:f.materials,fieldIssues:f.fieldIssues,
  association:f.fieldAssociationEvidence})),record:{number:record.drawing_number,material:record.material,issues:record.issues}};
let passed=false,failure=null;
try{
  assert.equal(record.drawing_number,'TA20260606-X-001');
  assert.equal(record.material,null);
  assert.ok(record.issues.includes('frame_field_issues'));
  assert.ok(actual.frames.every(f=>f.material.length===0));
  assert.ok(actual.frames[1].association.some(e=>e.candidates.some(c=>c.id==='material-1')));
  passed=true;
}catch(error){failure=error.message;}
await fs.writeFile(path.join(out,'frame-test-result.json'),JSON.stringify({passed,actual,failure},null,2));
console.log(JSON.stringify({passed,actual,failure},null,2));
if(!passed)process.exitCode=1;
