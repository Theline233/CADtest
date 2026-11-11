import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {predict} from '../../outputs/polish-20261106/regression-final/frozen-source/work/batch-fix-20261106/predict.mjs';

const out=process.argv[2], base='D:/CADtest/outputs/polish-20261106/regression-final/samples/S08';
if(!out)throw new Error('Usage: node frame-inline-expression-tests.mjs <new output directory>');
await fs.mkdir(out,{recursive:false});
const native=JSON.parse(await fs.readFile(`${base}/cad-read-report.json`,'utf8'));
const baseline=JSON.parse(await fs.readFile(`${base}/frame-field-candidates.json`,'utf8'));
const frame=baseline.frames[21];
const inside=p=>p&&p[0]>=frame.bounds[0]-.001&&p[0]<=frame.bounds[2]+.001&&
  p[1]>=frame.bounds[1]-.001&&p[1]<=frame.bounds[3]+.001;
const fixture={sourceFile:native.sourceFile,sourceSha256:native.sourceSha256,
  texts:native.texts.filter(t=>t.layout===frame.layout&&inside(t.position)&&
    (t.text.trim()!=='PART No.'||t.blockPath===frame.framePath)),
  geometry:native.geometry.filter(g=>g.layout===frame.layout&&
    (g.blockPath===frame.framePath||g.blockPath.startsWith(frame.framePath+'/')))};
await fs.writeFile(path.join(out,'cad-read-report.json'),JSON.stringify(fixture));
const run=spawnSync(process.execPath,
  [process.env.CADTEST_FRAME_EXTRACTOR ?? 'D:/CADtest/work/accuracy-20261109/extract-requirement-frames.mjs',out],{encoding:'utf8'});
assert.equal(run.status,0,run.stderr);
const extracted=JSON.parse(await fs.readFile(path.join(out,'frame-field-candidates.json'),'utf8'));
const sub=extracted.frames.flatMap(f=>f.subparts).find(s=>s.drawingNumber==='TA20260606-PROTL109-008-2');
const record=predict(extracted).records.find(r=>r.drawing_number===sub?.drawingNumber);
const actual={evidence:sub?.quantityEvidence,fieldIssues:sub?.fieldIssues,
  quantity:record?.quantity,issues:record?.issues};
let passed=false,failure=null;
try {
  assert.deepEqual(sub?.quantityEvidence.map(e=>e.text),['16EAx7SET']);
  assert.equal(record?.quantity,null);
  assert.ok(record.issues.includes('quantity_scope_ambiguous'));
  assert.ok(sub.fieldIssues.includes('subpart_quantity_missing_or_ambiguous'));
  passed=true;
}catch(error){failure=error.message;}
await fs.writeFile(path.join(out,'frame-test-result.json'),JSON.stringify({passed,actual,failure},null,2));
console.log(JSON.stringify({passed,actual,failure},null,2));
if(!passed)process.exitCode=1;
