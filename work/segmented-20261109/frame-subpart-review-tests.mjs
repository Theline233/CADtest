import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {predict} from '../../outputs/polish-20261106/regression-final/frozen-source/work/batch-fix-20261106/predict.mjs';
const out=process.argv[2];if(!out)throw new Error('output directory required');await fs.mkdir(out,{recursive:false});
const t=(id,text,x,y,extra={})=>({id,text,rawText:text,kind:'TEXT',layout:'Model',blockPath:'frame',
  position:[x,y,0],height:1,axisX:[1,0,0],axisY:[0,1,0],rotation:0,invisible:false,textRequiresReview:false,...extra});
const fixture={sourceFile:'synthetic-subpart-review',texts:[t('anchor','PART No.',60,10),
  t('parent','TA20260606-X-001',61,8,{blockPath:'values'}),
  t('sub','TA20260606-X-001-1',20,60,{blockPath:'values'}),t('qty','2EA',21,58,{blockPath:'values'}),
  t('review','bad',22,58,{blockPath:'values',rawText:'\\M+bad',textRequiresReview:true})],
  geometry:[{layout:'Model',blockPath:'frame',points:[[0,0],[100,0],[100,80],[0,80]]}]};
await fs.writeFile(path.join(out,'cad-read-report.json'),JSON.stringify(fixture));
const proc=spawnSync(process.execPath,[process.env.CADTEST_FRAME_EXTRACTOR ?? 'D:/CADtest/work/accuracy-20261109/extract-requirement-frames.mjs',out],{encoding:'utf8'});
assert.equal(proc.status,0,proc.stderr);
const frames=JSON.parse(await fs.readFile(path.join(out,'frame-field-candidates.json'),'utf8'));
const sub=frames.frames[0].subparts[0],record=predict(frames).records.find(r=>r.drawing_number===sub?.drawingNumber);
const actual={sub,quantity:record?.quantity,issues:record?.issues};let passed=false,failure=null;
try{assert.equal(record.quantity,null);assert.ok(sub.fieldIssues.includes('subpart_quantity_text_requires_review'));
  assert.equal(sub.fieldAssociationEvidence[0].reviewTexts[0].id,'review');passed=true;}catch(error){failure=error.message;}
await fs.writeFile(path.join(out,'frame-test-result.json'),JSON.stringify({passed,actual,failure},null,2));
console.log(JSON.stringify({passed,actual,failure},null,2));if(!passed)process.exitCode=1;
