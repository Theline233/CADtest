import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {extractTreatment, TREATMENT_RULE_VERSION} from './treatment.mjs';
import {predict} from '../batch-fix-20261106/predict.mjs';

// The cases are specified before changing the recognizer, including negative
// controls. No workbook, material-to-process table, or CAD reader is consulted.
const cases = [];
function check(name, fn) {
  try { fn(); cases.push({name,passed:true}); }
  catch (error) { cases.push({name,passed:false,error:error.message}); }
}
const result = text => extractTreatment([{id:'note',text,rawText:text}]);
const parameters = r => r.evidence.flatMap(e => e.clauses.flatMap(c => c.parameters));
const issuesInclude = (r, issue) => assert.ok(r.issues.includes(issue), `${issue} missing in ${JSON.stringify(r.issues)}`);

for (const text of ['고주파 소입소려 (HRC45이상)', '고주파 소입 소려 (HRC45이상)', '고주파  소입   소려 (HRC45이상)']) {
  check(`two explicit operations and full Korean hardness limit: ${text}`, () => {
    const r=result(text);
    assert.equal(r.value,null); assert.deepEqual(r.candidates,['高频淬火','回火']);
    issuesInclude(r,'multiple_treatments_require_review');
    assert.deepEqual(parameters(r),['HRC45이상']); assert.equal(r.evidence[0].rawText,text);
  });
}
for (const text of ['고주파 소입소려 금지 (HRC45이상)', '고주파 소입소려 하지 말 것 (HRC45이상)', '고주파 소입소려 없음 (HRC45이상)']) {
  check(`negative quench and temper: ${text}`, () => {
    const r=result(text); assert.equal(r.value,null); issuesInclude(r,'negation_or_exclusion');
    assert.deepEqual(r.candidates,['高频淬火','回火']); assert.deepEqual(parameters(r),['HRC45이상']);
  });
}
for (const text of ['표시부 고주파 소입소려 (HRC45이상)', '일부 고주파 소입소려 (HRC45이상)', '고주파 소입소려 ONLY ON HOLES (HRC45이상)']) {
  check(`local quench and temper: ${text}`, () => {
    const r=result(text); assert.equal(r.value,null); issuesInclude(r,'scope_requires_review');
    assert.deepEqual(r.candidates,['高频淬火','回火']); assert.deepEqual(parameters(r),['HRC45이상']);
  });
}
for (const text of ['고주파 소입소려?', '고주파 소입소려 미정 (HRC45이상)']) {
  check(`uncertain quench and temper: ${text}`, () => {
    const r=result(text); assert.equal(r.value,null); issuesInclude(r,'uncertain_instruction');
  });
}
for (const text of ['고주파 소입소려품', '고주파 소입물', 'SS41', 'S45C HRC45', 'HRC45이상', 'HRC 42以上', 'HV200', '카니젠 회사']) {
  check(`no false scalar or invented process: ${text}`, () => {
    const r=result(text); assert.equal(r.value,null); assert.deepEqual(r.candidates,[]);
  });
}
for (const text of ['4. 카니젠 도금 처리 할 것.', '카니젠도금', 'KANIGEN PLATING']) {
  check(`Kanigen candidate retains source and brand review: ${text}`, () => {
    const r=result(text); assert.equal(r.value,null); assert.deepEqual(r.candidates,['化学镀镍']);
    issuesInclude(r,'treatment_variant_requires_review'); assert.equal(r.evidence[0].text,text);
  });
}
for (const text of ['카니젠 도금 금지', '카니젠 도금 하지 말 것', 'NO KANIGEN PLATING']) {
  check(`negative Kanigen: ${text}`, () => {
    const r=result(text); assert.equal(r.value,null); assert.deepEqual(r.candidates,['化学镀镍']);
    issuesInclude(r,'negation_or_exclusion'); issuesInclude(r,'treatment_variant_requires_review');
  });
}
for (const text of ['카니젠 도금판 사용', 'KANIGEN PLATED SHEET']) {
  check(`Kanigen stock cannot become part finish: ${text}`, () => {
    const r=result(text); assert.equal(r.value,null); issuesInclude(r,'pretreated_stock_not_part_finish');
  });
}
check('different unfamiliar nickel coatings never become electroless nickel', () => {
  for (const text of ['니켈 도금', 'CHROME PLATING', '카니젠회사', 'KANIGEN COMPANY']) {
    const r=result(text); assert.equal(r.value,null); assert.ok(!r.candidates.includes('化学镀镍'));
  }
});
for (const text of ['열처리 부', '열처리부', '열처리 부 (HRC45이상)']) {
  check(`heat-treated area is not a whole-part instruction: ${text}`, () => {
    const r=result(text); assert.equal(r.value,null); assert.deepEqual(r.candidates,['热处理']);
    issuesInclude(r,'scope_requires_review'); assert.equal(r.evidence[0].text,text);
  });
}
check('부품 is not mistaken for the standalone local-area marker 부', () => {
  const r=result('열처리 부품'); assert.ok(!r.issues.includes('scope_requires_review'));
});
const parameterCases = [
  ['HEAT TREATMENT HRC45','HRC45'], ['HEAT TREATMENT HRC 45이상','HRC 45이상'],
  ['HEAT TREATMENT HRC45이상','HRC45이상'], ['HEAT TREATMENT HRC45이하','HRC45이하'],
  ['HEAT TREATMENT HRC 42以上','HRC 42以上'], ['HEAT TREATMENT HRC≥45','HRC≥45'],
  ['HEAT TREATMENT HRC>=45','HRC>=45'], ['HEAT TREATMENT HRC45~50','HRC45~50'],
  ['热处理 HV200','HV200'], ['镀硬铬 厚度≥10μm','厚度≥10μm'],
];
for (const [text, expected] of parameterCases) check(`parameter retained: ${text}`, () => {
  const r=result(text); assert.deepEqual(parameters(r),[expected]);
});
check('different limits for the same process are not silently equated', () => {
  const r=extractTreatment(['HEAT TREATMENT HRC45이상','HEAT TREATMENT HRC45이하']);
  assert.equal(r.value,null); issuesInclude(r,'parameters_require_review');
});
for (const text of ['HEAT TREATMENT; HRC45이상', 'HARD CHROME PLATING; HRC45이상; BLACK OXIDE']) {
  check(`unassociated parameter in the same record is retained for review: ${text}`, () => {
    const r=result(text); assert.equal(r.value,null); assert.deepEqual(parameters(r),['HRC45이상']);
    issuesInclude(r,'parameter_scope_requires_review');
  });
}
check('HRC-only material text is not attached to another text record as a process parameter', () => {
  const r=extractTreatment(['SS41 HRC45이상','ZINC WHITE COATING']);
  assert.equal(r.value,'镀白锌'); assert.deepEqual(parameters(r),[]); assert.equal(r.evidence.length,1);
});
check('known source decoder warning is preserved', () => {
  const r=extractTreatment([{text:'고주파 소입소려 (HRC45이상)',rawText:'encoded',textRequiresReview:true}]);
  assert.equal(r.value,null); issuesInclude(r,'source_text_requires_review'); assert.equal(r.evidence[0].rawText,'encoded');
});
check('downstream predictor holds compact-HRC process for parameter confirmation', () => {
  const t=result('HARD CHROME PLATING HRC45');
  const frame={index:1,drawingNumber:'TA20260606-X-001',quantityEvidence:[{text:'1EA',id:'q'}],quantityUnit:'EA',
    materials:[{text:'SS41'}],names:[{text:'PLATE'}],surfaceTreatment:t.value,treatmentDetails:t,
    treatmentIssues:t.issues,treatmentCandidates:t.candidates,treatmentEvidence:t.evidence};
  const p=predict({frames:[frame]}).records[0]; assert.equal(p.treatment,null);
  assert.ok(p.issues.includes('treatment_parameters_require_confirmation'));
});
const tag=process.argv[2] || 'post';
assert.match(tag,/^[a-z0-9-]+$/u);
const out='D:/CADtest/outputs/accuracy-20261109/treatment';
await fs.mkdir(out,{recursive:true});
const report={ruleVersion:TREATMENT_RULE_VERSION,tag,total:cases.length,passed:cases.filter(x=>x.passed).length,
  failures:cases.filter(x=>!x.passed),cases};
await fs.writeFile(`${out}/treatment-tests-${tag}.json`,JSON.stringify(report,null,2));
console.log(JSON.stringify({tag,total:report.total,passed:report.passed,failed:report.failures.length,failures:report.failures}));
if (report.failures.length) process.exitCode=1;
