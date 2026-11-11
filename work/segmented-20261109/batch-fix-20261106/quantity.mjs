// Pure quantity grammar. Call only with quantity-region evidence, never body/scale text.
// Units are spelling-normalized only: EA, PC, PCS, 개 and 件 are distinct.
const UNIT = /^(EA|SET|PCS|PC|件|套|개)$/i;
const TOTAL = /^(?:TOTAL\s+(?:Q['’]?TY|QUANTITY)\b|总数量|总件数)\s*[:：=]?\s*/i;
const setUnits = new Set(['SET', '套']);
const pieceUnits = new Set(['EA', 'PCS', 'PC', '件', '개']);
const MAX_LENGTH = 512, MAX_TOKENS = 128, MAX_DEPTH = 16;
const unique = values => [...new Set(values)];
const textOf = value => typeof value === 'string' ? value.trim() : '';
const normalizeUnit = value => textOf(value).toUpperCase();
const fail = (result, ...issues) => ({...result, value: null,
  issues: unique([...(result.issues ?? []), ...issues])});

const sameComponents = (a, b) => a?.normal === b?.normal && a?.mirror === b?.mirror;
const partEvidence = r => ({value: r.value, unit: r.unit,
  ...(r.components ? {components: {...r.components}} : {})});

function combine(left, right, operator, explicitTotal) {
  const parts = [left, right].map(partEvidence);
  const rule = operator === '/' ? 'normal_plus_mirror' : 'multiplication';
  const units = unique(parts.map(p => p.unit).filter(Boolean));
  const result = {value: null, unit: units.length === 1 ? units[0] : null, rule, parts,
    issues: unique([...left.issues, ...right.issues])};
  if (left.value === null || right.value === null) return result;
  if (operator === '/') {
    // Only the confirmed two-component pair. No chained sums, division,
    // pairs of products, or nested pairs acquire an invented business meaning.
    if (left.rule !== 'integer' || right.rule !== 'integer') return fail(result, 'quantity_invalid_expression');
    if (units.length > 1) return fail(result, 'quantity_unit_conflict');
    const value = left.value + right.value;
    return Number.isSafeInteger(value)
      ? {...result, value, components: {normal: left.value, mirror: right.value}}
      : fail(result, 'quantity_unsafe_integer');
  }
  if ([left, right].some(p => p.rule === 'multiplication') || (left.components && right.components)) {
    return fail(result, 'quantity_invalid_expression');
  }
  const pair = parts.find(p => p.components);
  if (pair && !explicitTotal) return fail(result, 'quantity_scope_ambiguous');
  if (parts.every(p => p.unit)) {
    const sets = parts.find(p => setUnits.has(p.unit));
    const pieces = parts.find(p => pieceUnits.has(p.unit));
    if (!sets || !pieces || sets.components) return fail(result, 'quantity_unit_conflict');
    if (!explicitTotal) return fail(result, 'quantity_scope_ambiguous');
    result.unit = pieces.unit;
    result.per_set = {...pieces};
    result.set_count = {...sets};
  }
  // A unitless pair times SET is not evidence that the pair means pieces.
  if (pair && setUnits.has(result.unit)) return fail(result, 'quantity_scope_ambiguous');
  const value = left.value * right.value;
  if (!Number.isSafeInteger(value)) return fail(result, 'quantity_unsafe_integer');
  if (pair) {
    const multiplier = parts.find(p => p !== pair).value;
    result.components = {normal: pair.components.normal * multiplier, mirror: pair.components.mirror * multiplier};
    if (!Object.values(result.components).every(Number.isSafeInteger)) return fail(result, 'quantity_unsafe_integer');
  }
  return {...result, value};
}

class QuantitySyntaxError extends Error {}
const syntaxError = (issue = 'quantity_invalid_expression') => { throw new QuantitySyntaxError(issue); };

function arithmetic(text, explicitTotal) {
  try {
    // Sticky matching consumes every character. Unknown text is never skipped.
    const token = /\s+|\d+|EA|SET|PCS|PC|件|套|개|[x×*/()]/iy;
    const tokens = [];
    for (let position = 0; position < text.length;) {
      token.lastIndex = position;
      const match = token.exec(text);
      if (!match) syntaxError();
      position = token.lastIndex;
      if (!/^\s+$/.test(match[0])) tokens.push(match[0].toUpperCase());
      if (tokens.length > MAX_TOKENS) syntaxError('quantity_expression_limit');
    }
    let cursor = 0;
    const readUnit = () => UNIT.test(tokens[cursor] ?? '') ? tokens[cursor++] : null;
    function primary(depth) {
      if (tokens[cursor] === '(') {
        if (depth >= MAX_DEPTH) syntaxError('quantity_expression_limit');
        cursor++;
        let result = product(depth + 1);
        if (tokens[cursor++] !== ')') syntaxError();
        const unit = readUnit();
        if (unit) {
          result = attachUnits(result, [unit]);
          if (result.rule === 'integer' && result.value !== null) result.parts = [{value: result.value, unit: result.unit}];
        }
        return result;
      }
      const digits = tokens[cursor++];
      if (!/^\d+$/.test(digits ?? '')) syntaxError();
      const value = Number(digits), unit = readUnit();
      const result = {value, unit, rule: 'integer', parts: [{value, unit}], issues: []};
      return Number.isSafeInteger(value) ? result : fail(result, 'quantity_unsafe_integer');
    }
    function pair(depth) {
      const left = primary(depth);
      if (tokens[cursor] !== '/') return left;
      cursor++;
      return combine(left, primary(depth), '/', explicitTotal);
    }
    function product(depth) {
      const left = pair(depth);
      if (!['X', '×', '*'].includes(tokens[cursor])) return left;
      cursor++;
      return combine(left, pair(depth), '*', explicitTotal);
    }
    const result = product(0);
    if (cursor !== tokens.length) syntaxError();
    return result;
  } catch (error) {
    if (!(error instanceof QuantitySyntaxError)) throw error;
    return {value: null, unit: null, rule: 'unsupported', issues: [error.message]};
  }
}

function attachUnits(result, hints) {
  const rawUnits = hints.filter(u => u !== null && u !== undefined && u !== '');
  const units = unique([result.unit, ...rawUnits.map(normalizeUnit)].filter(Boolean));
  const next = {...result, unit: units.length === 1 ? units[0] : null, unit_options: units};
  if (rawUnits.some(u => !UNIT.test(normalizeUnit(u)))) return fail(next, 'quantity_unknown_unit');
  return units.length > 1 ? fail(next, 'quantity_unit_conflict') : next;
}

/** Returns {value, unit, rule, scope, issues, expression, ...arithmeticEvidence}.
 * Bounded grammar: integer, confirmed A/B pair, or one multiplication;
 * parentheses group these forms, never add general arithmetic or executable code.
 * A paired product needs an explicit total label and at most one scalar multiplier.
 * components retains normal/mirror counts; per_set retains the unmultiplied pair.
 * Equations and SET/piece multiplication require an anchored explicit total label.
 * A unit hint is adjacent evidence, never a default or an equivalence conversion.
 */
export function parseQuantity(raw, {unit = null} = {}) {
  const expression = typeof raw === 'number' && Number.isSafeInteger(raw) ? String(raw) : textOf(raw);
  const marker = expression.match(TOTAL), scope = marker ? 'total_explicit' : 'quantity_field';
  const body = marker ? expression.slice(marker[0].length).trim() : expression;
  const base = {value: null, unit: null, rule: 'unsupported_or_missing', scope, expression, issues: []};
  if (expression.length > MAX_LENGTH) return attachUnits(fail(base, 'quantity_expression_limit'), [unit]);
  if (!body) return attachUnits(fail(base, 'quantity_missing'), [unit]);
  const sides = body.split('=');
  if (sides.length > 2 || (sides.length === 2 && !marker)) return attachUnits(fail(base, 'quantity_invalid_expression'), [unit]);
  const left = arithmetic(sides[0], Boolean(marker));
  let result = {...base, ...left};
  if (sides.length === 2) {
    const right = arithmetic(sides[1], false);
    result = {...result, rule: 'explicit_total_equation', calculation: left, stated_total: right};
    if (left.value === null || right.value === null || !['integer', 'normal_plus_mirror'].includes(right.rule)) {
      result = fail(result, ...right.issues, 'quantity_invalid_equation');
    } else {
      if (left.value !== right.value) result = fail(result, 'quantity_arithmetic_mismatch');
      if (right.components) {
        // An aggregate on the left cannot validate a newly claimed split.
        if (!left.components) result = fail(result, 'quantity_components_unverifiable');
        else if (!sameComponents(left.components, right.components)) result = fail(result, 'quantity_component_mismatch');
      }
      if (left.unit && right.unit && left.unit !== right.unit) result = fail(result, 'quantity_unit_conflict');
      result.unit = left.unit && right.unit && left.unit !== right.unit ? null : left.unit ?? right.unit;
    }
  }
  return attachUnits(result, [unit]);
}

function evidenceText(evidence) {
  return (Array.isArray(evidence) ? evidence : []).map(e => textOf(e?.text)).filter(Boolean);
}

function parseEvidence(evidence, unit = null) {
  const texts = evidenceText(evidence), expressions = texts.filter(t => !UNIT.test(t));
  // A single expression plus adjacent unit text is order-independent. Multiple
  // fragments stay in supplied reading order; never concatenate bare digits.
  const parsed = expressions.length === 1
    ? attachUnits(parseQuantity(expressions[0]), [unit, ...texts.filter(t => UNIT.test(t))])
    : parseQuantity(texts.join(' '), {unit});
  return {...parsed, evidence: Array.isArray(evidence) ? evidence : []};
}

/** Frame input keeps quantityEvidence/quantityUnit; quantityContext is an evidence
 * array confined by the extractor to an explicit total label and its expression.
 * Output retains title_block and total independently. A bad total never falls
 * back to a plausible title quantity. No frame.quantity, specialNotes,
 * quantityRawContext or label-evidence lookup; those cannot supply quantities.
 */
export function parseFrameQuantity(frame) {
  const title = parseEvidence(frame.quantityEvidence, frame.quantityUnit);
  const contextTexts = evidenceText(frame.quantityContext);
  const hasContext = contextTexts.length > 0;
  const totalCandidates = contextTexts.length > 1 && contextTexts.every(t => TOTAL.test(t))
    ? contextTexts.map(t => parseQuantity(t)) : [];
  let total = hasContext ? parseEvidence(frame.quantityContext) : null;
  if (totalCandidates.length) {
    const first = totalCandidates[0];
    // Equal totals alone do not establish agreement when both notes explicitly
    // give incompatible per-set usage or set counts.
    const agreeOn = field => {
      const known = totalCandidates.map(t => t[field]).filter(Boolean);
      const splits = known.map(p => p.components).filter(Boolean);
      return known.every(p => p.value === known[0].value && p.unit === known[0].unit)
        && splits.every(p => sameComponents(p, splits[0]));
    };
    const agree = totalCandidates.every(t => t.value !== null && t.value === first.value && t.unit === first.unit && t.scope === first.scope
      && sameComponents(t.components, first.components)) && agreeOn('per_set') && agreeOn('set_count');
    // When a short total and a detailed equation agree, keep the equation so
    // its explicit per-set evidence is still compared with the title quantity.
    const detailed = totalCandidates.find(t => t.per_set && t.set_count) ?? first;
    const combined = agree ? detailed : fail(first, 'quantity_total_conflict');
    if (!agree) {
      const candidateUnits = unique(totalCandidates.map(t => t.unit));
      if (candidateUnits.length > 1) {
        combined.unit = null;
        combined.unit_options = candidateUnits.filter(Boolean);
        if (combined.unit_options.length > 1) combined.issues = unique([...combined.issues, 'quantity_unit_conflict']);
      }
      // A failed choice must not expose the first candidate's calculations as
      // if they were shared facts. Every original candidate remains below.
      for (const field of ['components', 'parts', 'per_set', 'set_count', 'calculation', 'stated_total']) {
        if (!totalCandidates.every(t => JSON.stringify(t[field]) === JSON.stringify(first[field]))) delete combined[field];
      }
    }
    total = {...combined,
      evidence: frame.quantityContext, candidates: totalCandidates};
  }
  let selected = total ?? title;
  if (total && total.scope !== 'total_explicit') selected = fail(selected, 'quantity_context_without_total_marker');
  if (total && total.value !== null) {
    if (title.value === null && evidenceText(frame.quantityEvidence).some(t => !UNIT.test(t))) {
      selected = fail(selected, 'quantity_title_unresolved');
    } else if (title.value !== null) {
      const usage = total.per_set;
      const comparisonUnit = usage?.unit ?? total.unit;
      if (title.unit && comparisonUnit && title.unit !== comparisonUnit) {
        selected = {...fail(selected, 'quantity_unit_conflict'), unit:null,
          unit_options:unique([title.unit, comparisonUnit])};
      }
      if (title.scope === 'total_explicit' && title.value !== total.value) selected = fail(selected, 'quantity_conflict');
      // The title may already show the production total (including a product),
      // or the per-set usage. Either is consistent with the explicit equation.
      if (title.scope !== 'total_explicit' && usage && title.value !== usage.value && title.value !== total.value) selected = fail(selected, 'quantity_per_set_conflict');
      if (title.scope !== 'total_explicit' && !usage && title.value !== total.value) {
        // A direct total does not establish whether the title describes usage,
        // a different production scope, or conflicting drawing evidence.
        selected = fail(selected, 'quantity_scope_ambiguous');
      }
      const matching = (title.scope === 'total_explicit' ? [total] : [usage, total])
        .filter(candidate => candidate && candidate.value === title.value);
      if (title.components && matching.length && matching.every(candidate => candidate.components
        && !sameComponents(title.components, candidate.components))) selected = fail(selected, 'quantity_component_mismatch');
    }
  }
  if (total && total.value !== null && selected.value === null && !totalCandidates.length) totalCandidates.push(total);
  return {...selected, title_block: title, total, total_candidates: totalCandidates};
}
