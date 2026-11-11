import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {predict} from '../../outputs/polish-20261106/regression-final/frozen-source/work/batch-fix-20261106/predict.mjs';

// Native report slice: S08 frame 15, subpart -2, whose drawing has two separate
// adjacent entities "2EA" and "X2SET".  No Excel reference is read.
const project = 'D:/CADtest';
const sample = `${project}/outputs/polish-20261106/regression-final/samples/S08`;
const out = process.argv[2];
if (!out) throw new Error('Usage: node frame-tests.mjs <new output directory>');
await fs.mkdir(out, {recursive: false});
const [native, baseline] = await Promise.all([
  fs.readFile(`${sample}/cad-read-report.json`, 'utf8').then(JSON.parse),
  fs.readFile(`${sample}/frame-field-candidates.json`, 'utf8').then(JSON.parse),
]);
const frame = baseline.frames[14];
const inside = p => p && frame.bounds && p[0] >= frame.bounds[0] - 0.001 &&
  p[0] <= frame.bounds[2] + 0.001 && p[1] >= frame.bounds[1] - 0.001 &&
  p[1] <= frame.bounds[3] + 0.001;
const fixture = {sourceFile: native.sourceFile, sourceSha256: native.sourceSha256,
  texts: native.texts.filter(t => t.layout === frame.layout && inside(t.position) &&
    (t.text.trim() !== 'PART No.' || t.blockPath === frame.framePath)),
  geometry: native.geometry.filter(g => g.layout === frame.layout &&
    (g.blockPath === frame.framePath || g.blockPath.startsWith(frame.framePath + '/')))};
await fs.writeFile(path.join(out, 'cad-read-report.json'), JSON.stringify(fixture));
const result = spawnSync(process.execPath,
  [process.env.CADTEST_FRAME_EXTRACTOR ?? `${project}/work/accuracy-20261109/extract-requirement-frames.mjs`, out],
  {encoding: 'utf8'});
assert.equal(result.status, 0, result.stderr);
const extracted = JSON.parse(await fs.readFile(path.join(out, 'frame-field-candidates.json'), 'utf8'));
const sub = extracted.frames.flatMap(f => f.subparts)
  .find(s => s.drawingNumber === 'TA20260606-PROTL105-007-2');
const predicted = predict(extracted).records.find(r => r.drawing_number === sub?.drawingNumber);
const actual = {quantityEvidence: sub?.quantityEvidence, quantity: predicted?.quantity,
  issues: predicted?.issues, fieldIssues: sub?.fieldIssues};
let passed = false, failure = null;
try {
  assert.deepEqual(sub?.quantityEvidence.map(e => e.text), ['2EA', 'X2SET']);
  assert.equal(predicted?.quantity, null, 'a split piece/set product must not become a certain 2EA');
  passed = true;
} catch (error) { failure = error.message; }
await fs.writeFile(path.join(out, 'frame-test-result.json'), JSON.stringify({passed, actual, failure}, null, 2));
console.log(JSON.stringify({passed, actual, failure}, null, 2));
if (!passed) process.exitCode = 1;
