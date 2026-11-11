import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {predict} from '../../outputs/polish-20261106/regression-final/frozen-source/work/batch-fix-20261106/predict.mjs';

const out=process.argv[2];
if(!out)throw new Error('Usage: node frame-title-label-tests.mjs <new output directory>');
await fs.mkdir(out,{recursive:false});
const text=(id,value,x,y)=>({id,text:value,kind:'TEXT',layout:'Model',blockPath:'*Model_Space',
  position:[x,y,0],height:1,rotation:0,axisX:[1,0,0],axisY:[0,1,0],
  invisible:false,textRequiresReview:false});
// Shared model-space block path, two distinct title numbers, one MATERIAL
// label. A nearest-label search assigns the same label and value to both.
const fixture={sourceFile:'synthetic-shared-title-label',texts:[
  text('title-a','PART No.',0,0),text('title-b','PART No.',50,0),
  text('number-a','TA20260606-X-001',1,-2),text('number-b','TA20260606-X-002',51,-2),
  text('material-label','MATERIAL',0,20),text('material-value','S45C',1,18)],
  geometry:[{layout:'Model',blockPath:'*Model_Space',points:[[-10,-10],[80,-10],[80,30],[-10,30]]}]};
await fs.writeFile(path.join(out,'cad-read-report.json'),JSON.stringify(fixture));
const run=spawnSync(process.execPath,
  [process.env.CADTEST_FRAME_EXTRACTOR ?? 'D:/CADtest/work/accuracy-20261109/extract-requirement-frames.mjs',out],{encoding:'utf8'});
assert.equal(run.status,0,run.stderr);
const extracted=JSON.parse(await fs.readFile(path.join(out,'frame-field-candidates.json'),'utf8'));
const records=predict(extracted).records;
const actual=extracted.frames.map(f=>({drawingNumber:f.drawingNumber,materials:f.materials,
  fieldIssues:f.fieldIssues,association:f.fieldAssociationEvidence}));
let passed=false,failure=null;
try {
  assert.deepEqual(actual.map(f=>f.drawingNumber),['TA20260606-X-001','TA20260606-X-002']);
  assert.deepEqual(actual.map(f=>f.materials),[[],[]]);
  assert.ok(actual.every(f=>f.fieldIssues.includes('material_label_shared_across_frames')));
  assert.ok(actual.every(f=>f.association.some(e=>e.label.id==='material-label')));
  assert.ok(records.every(r=>r.material===null&&r.issues.includes('frame_field_issues')));
  passed=true;
}catch(error){failure=error.message;}
await fs.writeFile(path.join(out,'frame-test-result.json'),JSON.stringify({passed,actual,failure},null,2));
console.log(JSON.stringify({passed,actual,failure},null,2));
if(!passed)process.exitCode=1;
