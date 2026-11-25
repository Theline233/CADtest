import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {execFileSync} from 'node:child_process';
import {predict} from './batch-fix-20261106/predict.mjs';

const engine=path.dirname(fileURLToPath(import.meta.url));
const outputs=path.resolve(engine,'../../outputs');
await fs.mkdir(outputs,{recursive:true});
const out=await fs.mkdtemp(path.join(outputs,'split-title-'));
const report={texts:[],geometry:[],issues:[]};
const add=(stem,number,options={})=>{
  const dx=120*report.texts.length, layout='Model';
  const addText=(blockPath,text,x,y,textRequiresReview=false)=>report.texts.push({
    layout,blockPath,id:'t'+report.texts.length,text,textRequiresReview,height:1,position:[x+dx,y,0]});
  const line=(blockPath,x1,y1,x2,y2)=>report.geometry.push({
    layout,blockPath,id:'g'+report.geometry.length,kind:'Line',points:[[x1+dx,y1,0],[x2+dx,y2,0]]});
  const border=`*Model_Space/INSERT:${dx+1}:${stem}$0$Borders 텔스타 A3`;
  const title=`*Model_Space/INSERT:${dx+2}:${options.foreign?'Other':stem}$0$Title Blocks 텔스타 A3`;
  addText(border,'DRAWING',0,2); addText(border,'NUMBER',0,0);
  line(border,-5,-2,54,-2); line(border,-5,5,54,5);
  for(const x of [-5,7,54]) line(border,x,-2,x,85);
  addText(title,number,15,0,!!options.unreadable);
  if(options.second) addText(title,'TL20261231-333333-002',27,0);

  const bom=`*Model_Space/INSERT:${dx+3}:${stem}$0$부품 리스트`;
  const headings=['NO.','PART NAME STANDARD',"MAT'L","Q'TY","T'Q'TY",'REMARKS'];
  const xs=[-3,5,29,36,41,48], walls=[-5,0,28,35,39,45,54];
  headings.forEach((t,i)=>addText(bom,t,xs[i],24));
  for(const y of [23,26,29,...(options.multiple?[32]:[])]) line(bom,-5,y,54,y);
  for(const x of walls) line(bom,x,23,x,options.multiple?32:29);
  const qty=options.qty??'2', total=options.total??'4';
  ['03',options.row??stem,'SS41',qty,total].forEach((t,i)=>{
    if(t!=='')addText(bom,t,xs[i],27);
  });
  if(options.unreadableTotal)addText(bom,'',xs[4],27,true);
  if(options.foreignTotal)addText('*Model_Space','4',xs[4],27);
  if(options.multiple) addText(bom,'SECOND PART',5,30);
  if(options.foreignMaterial) addText('*Model_Space', 'AL6061',30,27);
};
add('TA20261231-123000-001 TEST PLATE','TL20261231-123000-001');
add('대기 STOPPER 10','대기 STOPPER-10',{row:'대기 STOPPER 10 BRACKET'});
add('TA20261231-123000-003 ITEM','TA20261231-123000-003',{foreign:true});
add('TA20261231-123000-004 ITEM','TL20261231-123000-004',{second:true});
add('TA20261231-123000-005 ITEM','TL20261231-123000-005',{unreadable:true});
add('TA20261231-123000-006 ASSEMBLY','TA20261231-123000-006',{multiple:true});
add('TA20261231-123000-007 ITEM','TL20261231-123000-007',{row:'TA20261231-123000-017 OTHER'});
add('TA20261231-123000-008 ITEM','TL20261231-123000-008',{foreignMaterial:true});
add('TL20261231-123000-009 ITEM','TL20261231-123000-009',{qty:'5',total:''});
add('TL20261231-123000-010 ITEM','TL20261231-123000-010',{qty:'2',total:'2'});
add('TL20261231-123000-011 ITEM','TL20261231-123000-011',{qty:'1/1',total:''});
add('TL20261231-123000-012 ITEM','TL20261231-123000-012',{total:'',unreadableTotal:true});
add('TL20261231-123000-013 ITEM','TL20261231-123000-013',{total:'',foreignTotal:true});
add('TL20261231-123000-014 ITEM','TL20261231-123000-014',{qty:'',total:'9'});
add('TL20261231-123000-015 ITEM','TL20261231-123000-015',{qty:'0',total:''});
add('TL20261231-123000-016 ITEM','TL20261231-123000-016',{qty:'5',total:''});
add('TL20261231-123000-016 ITEM','TL20261231-123000-016',{qty:'5',total:''});
await fs.writeFile(path.join(out,'cad-read-report.json'),JSON.stringify(report));
execFileSync(process.execPath,[path.join(engine,'extract-requirement-frames.mjs'),out]);
const result=JSON.parse(await fs.readFile(path.join(out,'frame-field-candidates.json'),'utf8'));
const {records,unresolvedFrames}=predict(result);
assert.deepEqual(records.map(r=>[r.drawing_number,r.name,r.material,r.quantity,r.unit]),[
  ['TL20261231-123000-001','TEST PLATE','SS41',null,null],
  ['대기 STOPPER-10','BRACKET','SS41',null,null],
  ['TA20261231-123000-006',null,null,null,null],
  ['TL20261231-123000-007',null,null,null,null],
  ['TL20261231-123000-008','ITEM',null,null,null],
  ['TL20261231-123000-009','ITEM','SS41',5,null],
  ['TL20261231-123000-010','ITEM','SS41',2,null],
  ['TL20261231-123000-011','ITEM','SS41',null,null],
  ['TL20261231-123000-012','ITEM','SS41',null,null],
  ['TL20261231-123000-013','ITEM','SS41',null,null],
  ['TL20261231-123000-014','ITEM','SS41',null,null],
  ['TL20261231-123000-015','ITEM','SS41',0,null],
  ['TL20261231-123000-016','ITEM','SS41',null,null],
]);
assert.deepEqual(unresolvedFrames.map(f=>f.index),[3,4,5]);
assert.deepEqual(records[0].field_association_evidence.find(e=>e.field==='quantity').candidates.map(c=>c.text),
  ["Q'TY: 2","T'Q'TY: 4"]);
assert.ok(records.slice(2,4).every(r=>!r.field_association_evidence.some(e=>e.field==='quantity')));
assert.ok(records[5].field_association_evidence.some(e=>e.style==='same_sheet_bom_quantity'));
assert.ok(records[12].field_association_evidence.every(e=>e.style!=='same_sheet_bom_quantity'));
console.log('Paired titles, distinct drawing formats, owned fields, quantity scope and ambiguity verified.');
