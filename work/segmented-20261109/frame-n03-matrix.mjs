import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

const out=process.argv[2];if(!out)throw new Error('output directory required');await fs.mkdir(out,{recursive:false});
const base=JSON.parse(await fs.readFile('D:/CADtest/outputs/accuracy-20261109/holdout-optimized/samples/N03/cad-read-report.json','utf8'));
const src=process.env.CADTEST_FRAME_EXTRACTOR??'D:/CADtest/work/accuracy-20261109/extract-requirement-frames.mjs';
const pathA='*Model_Space/INSERT:43DF:sdgrENG_A4_ASSY',pathB='*Model_Space/INSERT:44FA:sdgrENG_A4_ASSY';
const first='TA20240205-007104CV-010',second='TA20240205-007104CV-014';
const byId=(r,s)=>r.texts.find(t=>t.id.endsWith(s));
const find=(r,p)=>r.frames.find(f=>f.framePath===p);
const clone=()=>structuredClone(base);
const cases=[];
cases.push({name:'dual-real',make:clone,check:r=>{assert.equal(r.frameCount,2);assert.equal(find(r,pathA).drawingNumber,first);assert.equal(find(r,pathB).drawingNumber,second);assert.ok(r.frames.every(f=>f.names.length===0&&f.materials.length===0&&f.quantityEvidence.length===0));}});
cases.push({name:'no-number-and-bom-decoy',make:()=>{const x=clone(),v=byId(x,'#43F2'),bom=byId(x,'#43D9');x.texts=x.texts.filter(t=>t.id!==v.id);x.texts.push({...v,id:'bom-decoy',position:bom.position,text:'TA20240205-BOM-099'});return x;},
  check:r=>{assert.equal(r.frameCount,2);assert.equal(find(r,pathA).drawingNumber,null);assert.equal(find(r,pathA).drawingCandidates.length,0);assert.equal(find(r,pathB).drawingNumber,second);}});
for(const [name,value] of [['duplicate-same',first],['duplicate-conflict','TA20240205-OTHER-999']])cases.push({name,make:()=>{const x=clone(),v=byId(x,'#43F2');x.texts.push({...v,id:name,text:value,position:[v.position[0]+1,v.position[1],v.position[2]]});return x;},
  check:r=>{assert.equal(find(r,pathA).drawingNumber,null);assert.equal(find(r,pathA).drawingCandidates.length,2);assert.equal(find(r,pathB).drawingNumber,second);}});
cases.push({name:'unreadable-in-number-cell',make:()=>{const x=clone(),v=byId(x,'#43F2');x.texts.push({...v,id:'review',text:'undecoded',rawText:'\\M+bad',textRequiresReview:true,position:[v.position[0]+1,v.position[1],v.position[2]]});return x;},
  check:r=>{const f=find(r,pathA);assert.equal(f.drawingNumber,null);assert.ok(f.fieldAssociationEvidence[0].candidates.some(t=>t.id==='review'&&t.textRequiresReview));}});
cases.push({name:'foreign-block-in-cell',make:()=>{const x=clone(),v=byId(x,'#43F2');x.texts.push({...v,id:'foreign',blockPath:'*Model_Space/INSERT:other:mark',text:'TA20240205-FOREIGN-001',position:[v.position[0]+3,v.position[1],v.position[2]]});return x;},
  check:r=>{const f=find(r,pathA);assert.equal(f.drawingNumber,null);assert.ok(f.fieldAssociationEvidence[0].candidates.some(t=>t.id==='foreign'));}});
cases.push({name:'missing-divider',make:()=>{const x=clone();x.geometry=x.geometry.filter(g=>!(g.blockPath===pathA&&g.id.endsWith('#736')));return x;},
  check:r=>{assert.ok(find(r,pathA));assert.equal(find(r,pathA).drawingNumber,null);assert.ok(r.splitTitleDiagnostics.some(d=>d.framePath===pathA));assert.equal(find(r,pathB).drawingNumber,second);}});
cases.push({name:'overlapping-frame-claims',make:()=>{const x=clone(),a=x.texts.find(t=>t.blockPath===pathA&&t.text.trim()==='NUMBER'),b=x.texts.find(t=>t.blockPath===pathB&&t.text.trim()==='NUMBER'),dx=a.position[0]-b.position[0],dy=a.position[1]-b.position[1];
  for(const t of x.texts)if(t.blockPath===pathB||t.blockPath.startsWith(pathB+'/'))t.position=[t.position[0]+dx,t.position[1]+dy,t.position[2]];
  for(const g of x.geometry)if(g.blockPath===pathB||g.blockPath.startsWith(pathB+'/'))g.points=g.points.map(p=>[p[0]+dx,p[1]+dy,p[2]]);return x;},
  check:r=>{assert.equal(find(r,pathA).drawingNumber,null);assert.equal(find(r,pathB).drawingNumber,null);assert.ok(find(r,pathA).issues.includes('图号候选跨图框归属不明'));}});
cases.push({name:'rotation-and-mirror',make:()=>{const x=clone(),a=.7,c=Math.cos(a),s=Math.sin(a);const tr=p=>p&&p.length>=2&&p.slice(0,2).every(Number.isFinite)?[-c*p[0]-s*p[1],-s*p[0]+c*p[1],p[2]??0]:p;
  for(const t of x.texts){t.position=tr(t.position);t.axisX=tr(t.axisX);t.axisY=tr(t.axisY);}for(const g of x.geometry)g.points=g.points.map(tr);return x;},
  check:r=>{assert.equal(r.frameCount,2);assert.equal(find(r,pathA).drawingNumber,first);assert.equal(find(r,pathB).drawingNumber,second);}});
cases.push({name:'mixed-separate-frames',make:()=>{const x=clone(),fp='*Model_Space/INSERT:synthetic:legacy',tp='*Model_Space',t=(id,text,x,y,b)=>({id,text,rawText:text,kind:'TEXT',layout:'Model',blockPath:b,position:[x,y,0],height:1,rotation:0,axisX:[1,0,0],axisY:[0,1,0],invisible:false,textRequiresReview:false});
  x.texts.push(t('legacy-label','PART No.',60,10,fp),t('legacy-material-label','MATERIAL',60,25,fp),t('legacy-number','TA20260606-OLD-001',61,8,tp),t('legacy-material','S45C',61,23,tp));
  x.geometry.push({id:'legacy-border',layout:'Model',blockPath:fp,kind:'LwPolyline',points:[[0,0],[100,0],[100,80],[0,80],[0,0]],closed:false});return x;},
  check:r=>{assert.equal(r.frameCount,3);assert.equal(find(r,'*Model_Space/INSERT:synthetic:legacy').drawingNumber,'TA20260606-OLD-001');assert.equal(find(r,pathA).drawingNumber,first);assert.equal(find(r,pathB).drawingNumber,second);}});
cases.push({name:'mixed-same-frame-conflict',make:()=>{const x=clone(),v=byId(x,'#43F2'),anchor=x.texts.find(t=>t.blockPath===pathA&&t.text.trim()==='NUMBER');x.texts.push({...anchor,id:'mixed-label',text:'PART No.',position:[v.position[0]-anchor.axisX[0],v.position[1]+2*anchor.axisY[1],0]});return x;},
  check:r=>{assert.equal(r.frameCount,2);assert.equal(find(r,pathA).drawingNumber,null);assert.ok(find(r,pathA).fieldIssues.includes('multiple_drawing_title_schemes'));assert.equal(find(r,pathB).drawingNumber,second);}});

const results=[];
for(const test of cases){const dir=path.join(out,test.name);await fs.mkdir(dir);await fs.writeFile(path.join(dir,'cad-read-report.json'),JSON.stringify(test.make()));
  const run=spawnSync(process.execPath,[src,dir],{encoding:'utf8'});let result={name:test.name,passed:false,exitCode:run.status};
  try{assert.equal(run.status,0,run.stderr);const output=JSON.parse(await fs.readFile(path.join(dir,'frame-field-candidates.json'),'utf8'));test.check(output);result.passed=true;}
  catch(error){result.failure=error.message;}results.push(result);}
const summary={passed:results.every(x=>x.passed),results};await fs.writeFile(path.join(out,'frame-matrix-result.json'),JSON.stringify(summary,null,2));
console.log(JSON.stringify(summary,null,2));if(!summary.passed)process.exitCode=1;
