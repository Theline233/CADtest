import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawnSync} from 'node:child_process';
import {predict} from '../../outputs/polish-20261106/regression-final/frozen-source/work/batch-fix-20261106/predict.mjs';

const out=process.argv[2];
if(!out)throw new Error('Usage: node frame-review-fields-tests.mjs <new output directory>');
await fs.mkdir(out,{recursive:false});
const text=(id,value,x,y,extra={})=>({id,text:value,rawText:value,kind:'TEXT',layout:'Model',
  blockPath:'frame',position:[x,y,0],height:1,rotation:0,axisX:[1,0,0],axisY:[0,1,0],
  invisible:false,textRequiresReview:false,...extra});
const configs={material:[62,23],quantity:[82,23],name:[22,23]};
const actual={};
for(const [field,[x,y]] of Object.entries(configs)) {
  const dir=path.join(out,field);await fs.mkdir(dir);
  const fixture={sourceFile:'synthetic-review-field',texts:[text('number-label','PART No.',60,10),
    text('material-label','MATERIAL',60,25),text('quantity-label',"Q'TY",80,25),
    text('name-label','PART NAME',20,25),text('number','TA20260606-X-001',61,8,{blockPath:'values'}),
    text('material','S45C',61,23,{blockPath:'values'}),text('quantity','2EA',81,23,{blockPath:'values'}),
    text('name','BRACKET',21,23,{blockPath:'values'}),text('total',"TOTAL Q'TY 2EA",5,60,{blockPath:'values'}),
    text(`review-${field}`,'unreadable',x,y,{blockPath:'values',rawText:'\\M+bad',textRequiresReview:true})],
    geometry:[{layout:'Model',blockPath:'frame',points:[[0,0],[100,0],[100,80],[0,80]]}]};
  await fs.writeFile(path.join(dir,'cad-read-report.json'),JSON.stringify(fixture));
  const proc=spawnSync(process.execPath,
    [process.env.CADTEST_FRAME_EXTRACTOR ?? 'D:/CADtest/work/accuracy-20261109/extract-requirement-frames.mjs',dir],{encoding:'utf8'});
  assert.equal(proc.status,0,proc.stderr);
  const extracted=JSON.parse(await fs.readFile(path.join(dir,'frame-field-candidates.json'),'utf8'));
  const frame=extracted.frames[0],record=predict(extracted).records[0];
  actual[field]={frame:{drawingNumber:frame.drawingNumber,materials:frame.materials,names:frame.names,
    quantityEvidence:frame.quantityEvidence,quantityContext:frame.quantityContext,
    fieldIssues:frame.fieldIssues,association:frame.fieldAssociationEvidence},
    record:{material:record.material,name:record.name,quantity:record.quantity,issues:record.issues}};
}
let passed=false,failure=null;
try {
  for(const field of Object.keys(configs)){
    const {frame,record}=actual[field];
    assert.equal(frame.drawingNumber,'TA20260606-X-001');
    assert.ok(frame.fieldIssues.includes(`${field}_text_requires_review`));
    assert.ok(frame.association.some(e=>e.field===field&&e.reviewTexts.some(t=>t.id===`review-${field}`)));
    assert.equal(record[field],null);
    assert.ok(record.issues.includes('frame_field_issues'));
  }
  assert.deepEqual(actual.quantity.frame.quantityContext,[]);
  passed=true;
}catch(error){failure=error.message;}
await fs.writeFile(path.join(out,'frame-test-result.json'),JSON.stringify({passed,actual,failure},null,2));
console.log(JSON.stringify({passed,actual,failure},null,2));
if(!passed)process.exitCode=1;
