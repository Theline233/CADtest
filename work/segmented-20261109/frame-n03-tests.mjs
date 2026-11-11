import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

const out=process.argv[2];
if(!out)throw new Error('Usage: node frame-n03-tests.mjs <new output directory>');
await fs.mkdir(out,{recursive:false});
const sample='D:/CADtest/outputs/accuracy-20261109/holdout-optimized/samples/N03/cad-read-report.json';
const native=JSON.parse(await fs.readFile(sample,'utf8'));
await fs.writeFile(path.join(out,'cad-read-report.json'),JSON.stringify(native));
const script=process.env.CADTEST_FRAME_EXTRACTOR??'D:/CADtest/work/accuracy-20261109/extract-requirement-frames.mjs';
const run=spawnSync(process.execPath,[script,out],{encoding:'utf8'});
assert.equal(run.status,0,run.stderr);
const output=JSON.parse(await fs.readFile(path.join(out,'frame-field-candidates.json'),'utf8'));
const actual={frameCount:output.frameCount,frames:output.frames.map(f=>({index:f.index,
  framePath:f.framePath,drawingNumber:f.drawingNumber,drawingCandidates:f.drawingCandidates,
  names:f.names,materials:f.materials,quantityEvidence:f.quantityEvidence}))};
let passed=false,failure=null;
try{
  assert.equal(output.frameCount,2);
  assert.deepEqual(output.frames.map(f=>f.drawingNumber).sort(),
    ['TA20240205-007104CV-010','TA20240205-007104CV-014']);
  assert.ok(output.frames.every(f=>f.names.length===0&&f.materials.length===0&&f.quantityEvidence.length===0),
    'BOM rows are not main-drawing fields');
  passed=true;
}catch(error){failure=error.message;}
await fs.writeFile(path.join(out,'frame-test-result.json'),JSON.stringify({passed,actual,failure},null,2));
console.log(JSON.stringify({passed,actual,failure},null,2));
if(!passed)process.exitCode=1;
