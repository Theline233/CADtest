import assert from 'node:assert/strict';
import {predict} from './batch-fix-20261106/predict.mjs';

const frame=(index,materials,names)=>({index,drawingNumber:'TA20260606-TEST-001',
  materials:materials.map((text,i)=>({text,id:`m${index}-${i}`})),names:names.map((text,i)=>({text,id:`n${index}-${i}`})),
  quantityEvidence:[{text:'1EA',id:`q${index}`}],quantityUnit:'EA',surfaceTreatment:'镀白锌',treatmentEvidence:[]});
const record=(...frames)=>predict({frames,frameCount:frames.length}).records[0];
let r=record(frame(1,['SS41'],['PLATE']),frame(2,[],['PLATE']));
assert.equal(r.material,null,'material missing in one duplicate frame must not silently become SS41');
assert.ok(r.issues.includes('material_missing_or_conflicting'));
assert.deepEqual(r.evidence.material.map(e=>e.text),['SS41']);
r=record(frame(1,['SS41'],['PLATE']),frame(2,['SS41'],[]));
assert.equal(r.name,null,'name missing in one duplicate frame must not silently become PLATE');
assert.ok(r.issues.includes('name_missing_or_conflicting'));
r=record(frame(1,['SS41'],['PLATE']),frame(2,['SS41'],['PLATE']));
assert.equal(r.material,'SS41');assert.equal(r.name,'PLATE');
r=record(frame(1,['SS41','S45C'],['PLATE']));
assert.equal(r.material,null);assert.ok(r.issues.includes('material_missing_or_conflicting'));
r=record(frame(1,['SS41'],['PLATE']));assert.equal(r.material,'SS41');assert.equal(r.name,'PLATE');
const conflicting=frame(1,['SS41'],['PLATE']);conflicting.quantityEvidence=[{text:'1/1EA',id:'q'}];
conflicting.quantityContext=[{text:"TOTAL Q'TY: 1/1EA X 14SET = 14/14EA",id:'a'},{text:"TOTAL Q'TY: 1/1EA X 16SET = 16/16EA",id:'b'}];
r=record(conflicting);assert.equal(r.quantity,null);assert.equal(r.quantity_components,null);
assert.deepEqual(r.quantity_component_options,[{normal:14,mirror:14},{normal:16,mirror:16}]);
assert.deepEqual(r.quantity_options,[28,32]);
const suspicious=frame(1,[],['PLATE']);suspicious.fieldIssues=['material_text_requires_review'];
suspicious.fieldAssociationEvidence=[{field:'material',candidates:[{text:'SS41',id:'m'}],reviewTexts:[{text:'S45C',id:'bad',rawText:'?',kind:'MTEXT'}]}];
r=record(suspicious);assert.equal(r.material,null);assert.equal(r.field_association_evidence[0].candidates[0].text,'SS41');
assert.equal(r.field_association_evidence[0].reviewTexts[0].text,'S45C');
const child=frame('1.part1',[],['PLATE']);child.recordKind='subpart';child.parentFrameIndex=1;child.drawingNumber='TA20260606-TEST-001-1';
suspicious.subparts=[child];r=predict({frames:[suspicious]}).records.find(x=>x.drawing_number===child.drawingNumber);
assert.equal(r.material,null);assert.equal(r.field_association_evidence[0].parent_frame_index,1);
console.log(JSON.stringify({passed:true,checks:20}));
