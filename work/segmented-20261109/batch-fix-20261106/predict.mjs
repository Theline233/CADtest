import fs from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {parseFrameQuantity} from './quantity.mjs';
export {parseQuantity} from './quantity.mjs';

const unique = values => [...new Set(values)];
const present = values => unique(values.filter(v => v !== null && v !== undefined && v !== ''));
const single = values => values.length === 1 ? values[0] : null;
const array = value => Array.isArray(value) ? value : [];
const fieldText = (frames, field) => present(frames.flatMap(f => array(f[field]).map(e => e.text?.trim())));

/** Compatible with batch-validation/predict.mjs and compare.py:
 * all old record fields retain their names/types; quantity_options remains number[].
 * Additions: quantity_scope ('quantity_field'|'total_explicit'|null),
 * quantity_details (one entry per frame; value/unit/scope/issues/title_block/total
 * and their raw evidence), quantity_unit_options (including null),
 * field_issues (verbatim extractor issues by frame), evidence.quantity_context,
 * frame_diagnostics (verbatim quantityRawContext/quantityLabelEvidence/labels;
 * these fields never participate in quantity parsing or deduplication).
 * Duplicate physical occurrences are retained; compatible active quantities are summed.
 */
export function predict(native) {
  const groups = new Map(), unresolvedFrames = [];
  const expandedFrames = array(native.frames).flatMap(f => [f, ...array(f.subparts)]);
  for (const frame of expandedFrames) {
    if (!frame.drawingNumber) { unresolvedFrames.push(frame); continue; }
    if (!groups.has(frame.drawingNumber)) groups.set(frame.drawingNumber, []);
    groups.get(frame.drawingNumber).push(frame);
  }
  const records = [...groups].map(([drawingNumber, allFrames]) => {
    // A frame struck through corner-to-corner is a cancelled sheet. When the
    // same number also has an active frame, only active frames define the
    // values; crossed frames stay as evidence. A number seen only on crossed
    // frames is kept as a record so the reviewer sees and can override it.
    const activeFrames = allFrames.filter(f => !f.crossedOut);
    const crossedFrames = allFrames.filter(f => f.crossedOut);
    const frames = activeFrames.length ? activeFrames : allFrames;
    const crossedOut = activeFrames.length === 0;
    const details = frames.map(f => ({frame_index: f.index, ...parseFrameQuantity(f)}));
    const detailByFrame = new Map(frames.map((f, i) => [f, details[i]]));
    const quantities = present(details.map(d => d.value));
    const units = unique(details.map(d => d.unit)); // Missingness is significant.
    const scopes = unique(details.map(d => d.scope));
    const componentOptions = unique(details.filter(d=>d.components).map(d=>JSON.stringify(d.components))).map(s=>JSON.parse(s));
    // Candidate alternatives are evidence, not a selected quantity. Include
    // the alternatives from conflicting totals even when no top-level split exists.
    const candidateComponents = unique(details.flatMap(d => [d, ...array(d.total_candidates)])
      .filter(d => d.value !== null && d.components).map(d => JSON.stringify(d.components))).map(s => JSON.parse(s));
    const candidateUnits = present(details.flatMap(d => [d.unit, ...array(d.unit_options),
      ...array(d.total_candidates).map(c => c.unit)]));
    const legacyQuantity = details.every(d => d.value !== null) && quantities.length === 1 && units.length === 1 && scopes.length === 1 && componentOptions.length<=1
      ? quantities[0] : null;
    const duplicate = allFrames.length > 1;
    const activeDuplicate = duplicate && activeFrames.length > 1;
    // Missing units can agree with each other under the existing unit-warning
    // policy; a missing unit must never agree with a known unit. Never silently
    // add incomplete quantities, mismatched scopes or unsafe values.
    const candidateSum = activeDuplicate && details.every(d => Number.isSafeInteger(d.value))
      ? details.reduce((total, d) => total + BigInt(d.value), 0n) : null;
    const safeSum = candidateSum !== null && candidateSum <= BigInt(Number.MAX_SAFE_INTEGER)
      && candidateSum >= BigInt(Number.MIN_SAFE_INTEGER) ? Number(candidateSum) : null;
    const canSum = activeDuplicate && safeSum !== null && units.length === 1
      && scopes.length === 1 && scopes[0] != null;
    const quantity = activeDuplicate ? (canSum ? safeSum : null) : legacyQuantity;
    const duplicateMode = !duplicate || crossedOut || quantity === null ? null : (activeDuplicate ? 'sum' : 'single');
    const sumComponents = canSum && details.every(d => d.components)
      ? details.reduce((result, d) => ({normal: result.normal + d.components.normal,
          mirror: result.mirror + d.components.mirror}), {normal: 0, mirror: 0}) : null;
    const partialComponents = canSum && details.some(d => d.components) && !details.every(d => d.components);
    const quantityComponents = activeDuplicate ? sumComponents : (quantity===null?null:(componentOptions[0]??null));
    const materials = fieldText(frames, 'materials'), names = fieldText(frames, 'names');
    // Missing data in a duplicate frame is meaningful: preserve all evidence,
    // but do not make another frame's value look confirmed for every frame.
    const completeMaterial = frames.every(f => fieldText([f], 'materials').length === 1);
    const completeName = frames.every(f => fieldText([f], 'names').length === 1);
    const treatments = present(frames.map(f => f.surfaceTreatment));
    const treatmentDetails = frames.map(f => ({frame_index:f.index,...(f.treatmentDetails??{value:f.surfaceTreatment??null,issues:[],rule_version:'legacy'})}));
    const treatmentIssues = unique(frames.flatMap(f => array(f.treatmentIssues)));
    const allTreatments = unique(frames.map(f => f.surfaceTreatment??null));
    const hasTreatmentParameters=frames.some(f=>array(f.treatmentDetails?.evidence).some(e=>array(e.clauses).some(c=>array(c.parameters).length)));
    const treatment = treatmentIssues.length===0 && allTreatments.length===1 && !hasTreatmentParameters ? allTreatments[0] : null;
    const issues = [];
    if (materials.length !== 1 || !completeMaterial) issues.push('material_missing_or_conflicting');
    if (names.length !== 1 || !completeName) issues.push('name_missing_or_conflicting');
    if (quantity === null) issues.push(quantities.length > 1 || units.length > 1 || scopes.length > 1 ? 'quantity_conflict' : 'quantity_unresolved');
    if(componentOptions.length>1 && !canSum)issues.push('quantity_component_conflict');
    if (units.length !== 1 || units[0] === null) issues.push('unit_missing_or_conflicting');
    if (units.length > 1) issues.push('quantity_unit_conflict');
    if (scopes.length > 1) issues.push('quantity_scope_conflict');
    if (treatments.length !== 1) issues.push(treatments.length > 1 ? 'treatment_conflict' : 'treatment_not_recognized');
    if(allTreatments.length>1)issues.push('treatment_missing_or_conflicting_across_frames');
    if(treatmentIssues.length)issues.push('treatment_requires_review',...treatmentIssues);
    if(hasTreatmentParameters)issues.push('treatment_parameters_require_confirmation');
    if (canSum) issues.push('duplicate_drawing_summed');
    if (activeDuplicate && quantities.length > 1) issues.push('duplicate_quantities_differ');
    if (partialComponents) issues.push('duplicate_quantity_components_incomplete');
    if (candidateSum !== null && safeSum === null) issues.push('duplicate_quantity_sum_unsafe');
    const fieldIssues = frames.filter(f => f.fieldIssues && (!Array.isArray(f.fieldIssues) || f.fieldIssues.length))
      .map(f => ({frame_index: f.index, issues: f.fieldIssues}));
    const associationEvidence = frames.flatMap(f => {
      const direct = array(f.fieldAssociationEvidence).map(e => ({frame_index:f.index,...e}));
      const parent = f.recordKind === 'subpart' ? array(native.frames).find(p => p.index === f.parentFrameIndex) : null;
      const inherited = array(parent?.fieldAssociationEvidence).filter(e => e.field === 'material' || e.field === 'name')
        .map(e => ({frame_index:f.index,parent_frame_index:parent.index,...e}));
      return [...direct,...inherited];
    });
    if (fieldIssues.length) issues.push('frame_field_issues');
    if (crossedOut) issues.unshift('crossed_out_drawing');
    else if (crossedFrames.length) issues.push('crossed_out_duplicate_frames_ignored');
    return {
      crossed_out: crossedOut,
      crossed_out_frame_indices: crossedFrames.map(f => f.index),
      crossed_out_marks: crossedFrames.map(f => ({frame_index: f.index, inherited_from_frame: f.crossedOut.inheritedFromFrame ?? null,
        rule: f.crossedOut.rule, segments: array(f.crossedOut.segments).map(s => s.id)})),
      drawing_number: drawingNumber, name: completeName ? single(names) : null, material: completeMaterial ? single(materials) : null, quantity,
      record_kinds: present(frames.map(f => f.recordKind ?? 'part_title')),
      parent_drawing_numbers: present(frames.map(f => f.parentDrawingNumber)),
      field_scopes: frames.map((f,i) => ({frame_index:f.index, ...(f.fieldScopes ?? {drawingNumber:'title_cell',name:'title_cell',material:'title_cell',treatment:'frame_notes'}),
        quantity:details[i].scope==='total_explicit'?'explicit_total_note':(f.fieldScopes?.quantity??'quantity_cell'),
        quantity_resolution:details[i].value===null?'unresolved':'resolved'})),
      unit: single(units), treatment, quantity_options: present([...quantities,...details.flatMap(d=>array(d.total_candidates).map(c=>c.value)),...(canSum?[safeSum]:[])]),
      treatment_details:treatmentDetails,treatment_candidates:present(frames.flatMap(f=>array(f.treatmentCandidates))),
      quantity_expressions: details.map(d => d.expression), quantity_rules: present(details.map(d => d.rule)),
      frame_indices: frames.map(f => f.index), duplicate_frames: frames.length > 1,
      issues: unique([...issues, ...details.flatMap(d => d.issues)]),
      quantity_scope: quantity === null ? null : scopes[0], quantity_unit_options: units,
      quantity_components:quantityComponents,quantity_component_options:candidateComponents,
      quantity_candidate_unit_options:candidateUnits,
      quantity_details: details, field_issues: fieldIssues, field_association_evidence: associationEvidence,
      frame_diagnostics: frames.map(f => ({
        frame_index: f.index, quantityRawContext: array(f.quantityRawContext),
        quantityLabelEvidence: array(f.quantityLabelEvidence), labels: f.labels ?? null,
      })),
      evidence: {
        drawing_number: frames.flatMap(f => array(f.drawingCandidates)),
        name: frames.flatMap(f => array(f.names)), material: frames.flatMap(f => array(f.materials)),
        quantity: frames.flatMap(f => array(f.quantityEvidence)),
        quantity_context: frames.flatMap(f => array(f.quantityContext)),
        treatment: frames.flatMap(f => array(f.treatmentEvidence)),
      },
      ...(duplicate ? {
        duplicate_count: allFrames.length,
        duplicate_active_count: activeFrames.length,
        duplicate_occurrences: allFrames.map(f => {
          const d = detailByFrame.get(f) ?? parseFrameQuantity(f);
          return {frame_index: f.index, value: d.value, unit: d.unit, scope: d.scope,
            expression: d.expression, crossed_out: Boolean(f.crossedOut),
            included_in_sum: !f.crossedOut};
        }),
        quantity_mode: duplicateMode,
        quantity_sum: canSum ? safeSum : null,
        quantity_single: duplicateMode === 'single' ? quantity
          : (canSum && quantities.length === 1 ? quantities[0] : null),
        quantity_sum_candidate: activeDuplicate ? safeSum : null,
      } : {}),
    };
  });
  return {
    method: records.some(r => r.duplicate_count)
      ? 'CAD quantity-region evidence only. Strict integer/unit grammar; explicit labelled totals; compatible active duplicate frames are summed. No Excel lookup, sample override, material substitution or treatment inheritance.'
      : 'CAD quantity-region evidence only. Strict integer/unit grammar; explicit labelled totals; conservative value/unit/scope deduplication. No Excel lookup, sample override, material substitution or treatment inheritance.',
    sourceFile: native.sourceFile,sourceSha256:native.sourceSha256??null, frameCount: native.frameCount ?? array(native.frames).length,
    crossedOutFrameCount: array(native.frames).filter(f => f.crossedOut).length, records, unresolvedFrames,
  };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [input, output] = process.argv.slice(2);
  if (!input || !output) throw new Error('Usage: predict.mjs frame-field-candidates.json predictions.json');
  const result = predict(JSON.parse((await fs.readFile(input, 'utf8')).replace(/^\uFEFF/, '')));
  await fs.writeFile(output, JSON.stringify(result, null, 2));
  console.log(JSON.stringify({frames: result.frameCount, records: result.records.length, unresolvedFrames: result.unresolvedFrames.length}));
}
