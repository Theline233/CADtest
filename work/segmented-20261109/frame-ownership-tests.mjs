import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

const out = process.argv[2];
if (!out) throw new Error('Usage: node frame-ownership-tests.mjs <new output directory>');
await fs.mkdir(out, {recursive: false});
const text = (id, value, x, y) => ({id, text:value, kind:'TEXT', layout:'Model',
  blockPath:'*Model_Space', position:[x,y,0], height:1, rotation:0,
  axisX:[1,0,0], axisY:[0,1,0], invisible:false, textRequiresReview:false});
// Two title labels in the same model-space path see the same text through
// overlapping search regions. Position alone cannot assign its ownership.
const fixture = {sourceFile:'synthetic-ambiguous-ownership',
  texts:[text('label-a','PART No.',0,0), text('label-b','PART No.',20,0),
    text('number','TA20260606-X-001',21,-2)],
  geometry:[{layout:'Model',blockPath:'*Model_Space',points:[[-5,-10],[60,-10],[60,30],[-5,30]]}]};
await fs.writeFile(path.join(out,'cad-read-report.json'),JSON.stringify(fixture));
const run = spawnSync(process.execPath,
  [process.env.CADTEST_FRAME_EXTRACTOR ?? 'D:/CADtest/work/accuracy-20261109/extract-requirement-frames.mjs',out],{encoding:'utf8'});
assert.equal(run.status,0,run.stderr);
const extracted = JSON.parse(await fs.readFile(path.join(out,'frame-field-candidates.json'),'utf8'));
const actual = extracted.frames.map(f=>({drawingNumber:f.drawingNumber,
  candidates:f.drawingCandidates, issues:f.issues}));
let passed=false,failure=null;
try {
  assert.equal(actual.length,2);
  assert.deepEqual(actual.map(f=>f.drawingNumber),[null,null]);
  assert.deepEqual(actual.map(f=>f.candidates.map(c=>c.id)),[['number'],['number']]);
  assert.ok(actual.every(f=>f.issues.includes('图号候选跨图框归属不明')));
  passed=true;
} catch(error) { failure=error.message; }
await fs.writeFile(path.join(out,'frame-test-result.json'),JSON.stringify({passed,actual,failure},null,2));
console.log(JSON.stringify({passed,actual,failure},null,2));
if(!passed)process.exitCode=1;
