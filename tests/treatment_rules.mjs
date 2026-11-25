import assert from 'node:assert/strict';
import {extractTreatment} from '../work/segmented-20261109/polish-20261106/treatment.mjs';
import {predict} from '../work/segmented-20261109/batch-fix-20261106/predict.mjs';
import {writeFile} from 'node:fs/promises';

// Generic process instructions transcribed from the two paired notes. No
// customer drawing, identifier, geometry or template is used in this replay.
const source = [
  '3. 질화 열처리, HrC 41~55',
  '4. 크롬 도금',
  '3. NITRIDING HEAT TREATMENT, HrC 41~55',
  '4. CHROMIUM PLATING',
];
const nitriding = '氮化热处理（HRC 41–55）';
const group = `${nitriding}；镀铬`;
let checks = 0;
function recognized(input, value) {
  const details = extractTreatment(input);
  assert.equal(details.value, value, JSON.stringify(input));
  assert.equal(details.status, 'recognized');
  assert.deepEqual(details.issues, []);
  checks++;
  return details;
}
function pending(input, issue) {
  const details = extractTreatment(input);
  assert.equal(details.value, null, JSON.stringify(input));
  assert.equal(details.status, 'needs_review');
  assert.ok(details.issues.includes(issue), details.issues);
  checks++;
  return details;
}
const details = recognized(source, group);
assert.deepEqual(details.candidates, [nitriding, '镀铬']);
assert.deepEqual(details.evidence.map(e => e.text), source);
assert.deepEqual(details.evidence.flatMap(e => e.clauses.flatMap(c => c.parameters)), ['HrC 41~55','HrC 41~55']);
assert.deepEqual(details.evidence.flatMap(e => e.clauses.flatMap(c => c.matches.map(m => m.rule))),
  ['nitriding_heat_treatment', 'chrome', 'nitriding_heat_treatment', 'chrome']);
assert.ok(details.value_includes_parameters);
recognized([...source].reverse(), group);
recognized([source[0], source[3]], group);
recognized([source[0],source[1], '3. NITRIDING HEAT TREATMENT, HRC 41.0 – 55.0', source[3]], group);
recognized(['NOTE.\n1. UNSPECIFIED DIMENSIONS TOLERANCES ARE ±0.1\n2. UNSPECIFIED CHAMFERING C0.5\n'+source.slice(2).join('\n'), ...source.slice(0,2)], group);
recognized(['8. NITRIDING HEAT TREATMENT, HRC 38~45', '9. CHROME PLATING'], '氮化热处理（HRC 38–45）；镀铬');
recognized(source[0], nitriding);
recognized(source[2], nitriding);
for (const text of ['CHROMIUM PLATING','CHROMIUM PLATING.','CHROME PLATED','크롬 도금','镀铬']) recognized(text,'镀铬');
for (const text of ['HARD CHROMIUM PLATING','HARD-CHROME PLATING','경질 크롬 도금','镀硬铬','硬质镀铬']) recognized(text,'镀硬铬');
recognized('ZINC WHITE COATING','镀白锌');
recognized('ANODIZING','阳极氧化');
recognized('硬质阳极氧化','硬质阳极氧化');
recognized('ELECTROLESS NICKEL PLATING','化学镀镍');

pending([source[0], source[2].replace('41','45')], 'parameters_require_review');
pending([source[0], '3. NITRIDING HEAT TREATMENT'], 'parameters_require_review');
pending([...source, '5. HRC 45~55'], 'parameter_scope_requires_review');
pending(['3. NITRIDING HEAT TREATMENT, HRC 55~41', source[3]], 'multiple_treatments_require_review');
pending(['3. NITRIDING HEAT TREATMENT, HRC ≥41', source[3]], 'multiple_treatments_require_review');
pending(['3. NITRIDING HEAT TREATMENT, HRC 41~55 이상', source[3]], 'multiple_treatments_require_review');
pending(['NITRIDING HEAT TREATMENT, HRC 41~55', 'CHROMIUM PLATING'], 'multiple_treatments_require_review');
pending(['3. NITRIDING HEAT TREATMENT, HRC 41~55; 3. CHROMIUM PLATING'], 'multiple_treatments_require_review');
pending(['3. NITRIDING HEAT TREATMENT, HRC 41~55', '4. HARD CHROMIUM PLATING'], 'multiple_treatments_require_review');
pending(['3. BLACK OXIDE','4. ZINC WHITE COATING'], 'multiple_treatments_require_review');
pending('고주파 소입 소려', 'multiple_treatments_require_review');
pending(['CHROMIUM PLATING','HARD CHROMIUM PLATING'], 'multiple_treatments_require_review');
pending('CHROMIUM PLATING (HARD)', 'treatment_variant_requires_review');
pending('크롬 도금 (경질)', 'treatment_variant_requires_review');
pending('NON HARD CHROMIUM PLATING', 'negation_or_exclusion');
pending('HARD\nCHROMIUM PLATING', 'treatment_variant_requires_review');
pending('CHROMIUM PLATED STOCK', 'pretreated_stock_not_part_finish');
pending('니켈 도금','unrecognized_treatment');
pending('카니젠 도금','treatment_variant_requires_review');
pending([source[0],'4. NO CHROMIUM PLATING'], 'negation_or_exclusion');
pending([source[0],'4. 크롬 도금 금지'], 'negation_or_exclusion');
pending([source[0],'4. CHROMIUM PLATING ON BORE ONLY'], 'scope_requires_review');
pending([source[0],'4. 국부 크롬 도금'], 'scope_requires_review');
pending([source[0],'4. CHROMIUM PLATING\nBORE ONLY'], 'scope_requires_review');
pending([source[0],'4. CHROMIUM PLATING OR BLACK OXIDE'], 'uncertain_instruction');
pending([{text:source[0],scope:'shared_parent_frame'},source[1]],'scope_requires_review');
pending([{text:source[0],textRequiresReview:true},source[1]], 'source_text_requires_review');

function frame(index, notes, options={}) {
  const framePath = `*Model_Space/INSERT:${index}`;
  const d = extractTreatment(notes.map((n,j)=>({text:n,id:`note-${index}-${j}`,layout:'Model',blockPath:framePath,position:[10,10]})));
  return {index, drawingNumber:'TA20000101-TEST-001', bounds:[0,0,20,20], layout:'Model', framePath, recordKind:'part_title',
    materials:[{text:'STEEL'}], names:[{text:'TEST'}], quantityEvidence:[{text:'2 EA'}], quantityUnit:'EA',
    surfaceTreatment:d.value, treatmentDetails:d, treatmentCandidates:d.candidates, treatmentIssues:d.issues, ...options};
}
const resolve = (...frames) => predict({frames}).records[0];
assert.equal(resolve(frame(1,source)).treatment,group);
assert.ok(resolve(frame(1,source)).issues.includes('treatment_parameters_require_confirmation'));
assert.equal(resolve(frame(1,['HEAT TREATMENT, HV 350'])).treatment,null);
assert.equal(resolve(frame(1,source), frame(2,source,{bounds:[30,0,50,20],layout:'Other'})).treatment,null);
const overlap = resolve(frame(1,source), frame(2,[],{drawingNumber:'TA20000101-TEST-002'}));
assert.equal(overlap.treatment,null);
assert.ok(overlap.issues.includes('treatment_ownership_requires_review'));
assert.deepEqual(overlap.treatment_candidates,[nitriding,'镀铬']);
const assembly = resolve(frame(1,source,{recordKind:'assembly_title'}));
assert.equal(assembly.treatment,null);
assert.ok(assembly.issues.includes('treatment_ownership_requires_review'));
// Across independent non-overlapping frames of one drawing, neither contrary
// hardness nor partial operation lists can be merged into a better result.
const second = frame(2,source.map(s=>s.replace('41~55','45~55')),{bounds:[30,0,50,20]});
second.treatmentDetails.evidence.forEach(e=>e.position=[40,10]);
assert.ok(resolve(frame(1,source), second).issues.includes('treatment_conflict'));
assert.equal(resolve(frame(1,source),second).treatment,null);
const chrome = frame(2,[source[3]],{bounds:[30,0,50,20]});
chrome.treatmentDetails.evidence[0].position=[40,10];
assert.equal(resolve(frame(1,[source[0]]), chrome).treatment,null);
assert.equal(resolve(frame(1,source), {...second,crossedOut:true}).treatment,group);

if (process.argv[2]) await writeFile(process.argv[2], source.join('\n')+'\n', {flag:'wx'});
console.log(`Treatment regression: ${checks} recognition/review cases and predictor/evidence assertions passed.`);
