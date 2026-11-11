import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {spawnSync} from 'node:child_process';
const out=process.argv[2]??'D:/CADtest/outputs/batch-fix-20261106/frame-tests';
await fs.mkdir(out,{recursive:true});
function fixture(rotation=0,sx=1,sy=1) {
  const c=Math.cos(rotation),s=Math.sin(rotation),pos=(x,y)=>[100+sx*c*x-sy*s*y,200+sx*s*x+sy*c*y,0];
  let id=0;
  const text=(value,x,y,blockPath='frame',layout='Model')=>({id:String(++id),kind:'TEXT',text:value,layout,blockPath,position:pos(x,y),height:Math.abs(sy),rotation,axisX:[sx*c,sx*s,0],axisY:[-sy*s,sy*c,0],invisible:false});
  return {sourceFile:'fixture',texts:[text('PART No.',60,10),text('MATERIAL',60,25),text("Q'TY",80,25),text('PART NAME',20,25),text('TA20260606-X-001B',61,8,'values'),text('S45C',61,23,'values'),text('2EA',81,23,'values'),text('F',89,23),text('BRACKET',21,23,'values'),text('TA20260606-WRONG-000',10,65,'values'),text('TA20260606-WRONG-001',61,8,'values','Layout1')],geometry:[{layout:'Model',blockPath:'frame',points:[[0,0],[100,0],[100,80],[0,80]].map(p=>pos(...p))}]};
}
async function extract(report,id){const dir=`${out}/${id}`;await fs.mkdir(dir,{recursive:true});await fs.writeFile(`${dir}/cad-read-report.json`,JSON.stringify(report));const p=spawnSync(process.execPath,['D:/CADtest/outputs/accuracy-20261109/frames/n03-replay-24/frame-frozen-work/extract-requirement-frames.mjs',dir],{encoding:'utf8'});assert.equal(p.status,0,p.stderr);return JSON.parse(await fs.readFile(`${dir}/frame-field-candidates.json`,'utf8'));}
let checks=0;
for(const [i,args] of [[0,1,1],[Math.PI/2,1,1],[Math.PI,-2,3],[.7,-1,2]].entries()){
  const f=(await extract(fixture(...args),`synthetic-${i}`)).frames[0];
  assert.equal(f.drawingNumber,'TA20260606-X-001B');assert.equal(f.materials[0].text,'S45C');assert.deepEqual(f.quantityEvidence.map(q=>q.text),['2EA']);assert.equal(f.names[0].text,'BRACKET');checks+=4;
}
const baseline=JSON.parse(await fs.readFile('D:/CADtest/outputs/quotation-pilot-20261105/raw/cad-read-report.json','utf8'));
const spaced=fixture();spaced.texts[4].text='TA20260606-X- 001B';
const spacedFrame=(await extract(spaced,'spaced-identifier')).frames[0];assert.equal(spacedFrame.drawingNumber,'TA20260606-X-001B');assert.equal(spacedFrame.drawingCandidates[0].text,'TA20260606-X- 001B');checks+=2;
const doubleId=fixture();doubleId.texts[4].text='TA20260606-X-001 TA20260606-X-002';
assert.equal((await extract(doubleId,'two-identifiers')).frames[0].drawingNumber,null);checks++;
const control=await extract(baseline,'control');assert.equal(control.frameCount,16);assert.equal(new Set(control.frames.map(f=>f.drawingNumber)).size,14);assert.ok(control.frames.every(f=>f.drawingNumber));checks+=3;
const result={passed:true,checks,controlFrames:16,controlUnique:14};await fs.writeFile(`${out}/result.json`,JSON.stringify(result,null,2));console.log(JSON.stringify(result));
