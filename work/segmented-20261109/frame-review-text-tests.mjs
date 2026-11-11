import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

const out=process.argv[2];
if(!out)throw new Error('Usage: node frame-review-text-tests.mjs <new output directory>');
await fs.mkdir(out,{recursive:false});
const text=(id,value,x,y,extra={})=>({id,text:value,rawText:value,kind:'TEXT',layout:'Model',
  blockPath:'frame',position:[x,y,0],height:1,rotation:0,axisX:[1,0,0],axisY:[0,1,0],
  invisible:false,textRequiresReview:false,...extra});
async function run(name, kind) {
  const dir=path.join(out,name);await fs.mkdir(dir);
  const fixture={sourceFile:'synthetic-review-text',texts:[text('label','PART No.',60,10),
    text('valid','TA20260606-X-001',61,8,{blockPath:'values'}),
    text('questionable','undecoded',62,8,{blockPath:'values',kind,textRequiresReview:true,rawText:'\\M+bad'})],
    geometry:[{layout:'Model',blockPath:'frame',points:[[0,0],[100,0],[100,80],[0,80]]}]};
  await fs.writeFile(path.join(dir,'cad-read-report.json'),JSON.stringify(fixture));
  const proc=spawnSync(process.execPath,
    [process.env.CADTEST_FRAME_EXTRACTOR ?? 'D:/CADtest/work/accuracy-20261109/extract-requirement-frames.mjs',dir],{encoding:'utf8'});
  assert.equal(proc.status,0,proc.stderr);
  return JSON.parse(await fs.readFile(path.join(dir,'frame-field-candidates.json'),'utf8')).frames[0];
}
const field=await run('ordinary-text','TEXT');
const dimension=await run('dimension-graphics','DIMENSION_GRAPHICS');
const actual={field:{number:field.drawingNumber,candidates:field.drawingCandidates,
    issues:field.issues,fieldIssues:field.fieldIssues,association:field.fieldAssociationEvidence},
  dimension:{number:dimension.drawingNumber,issues:dimension.issues}};
let passed=false,failure=null;
try {
  assert.equal(field.drawingNumber,null);
  assert.deepEqual(field.drawingCandidates.map(x=>x.id),['valid']);
  assert.ok(field.issues.includes('图号字段含需复核文字'));
  assert.ok(field.fieldIssues.includes('drawing_number_text_requires_review'));
  const evidence=field.fieldAssociationEvidence.find(e=>e.field==='drawingNumber');
  assert.equal(evidence.reviewTexts[0].id,'questionable');
  assert.equal(evidence.reviewTexts[0].rawText,'\\M+bad');
  assert.equal(dimension.drawingNumber,'TA20260606-X-001');
  passed=true;
}catch(error){failure=error.message;}
await fs.writeFile(path.join(out,'frame-test-result.json'),JSON.stringify({passed,actual,failure},null,2));
console.log(JSON.stringify({passed,actual,failure},null,2));
if(!passed)process.exitCode=1;
