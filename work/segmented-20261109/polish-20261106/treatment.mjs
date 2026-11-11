/**
 * Conservative recognition of explicit process text already associated with a
 * frame. This module does not locate frames, decode CAD text, consult materials,
 * inspect quantities or read Excel. A candidate is evidence, not approval.
 *
 * Keep text/rawText unchanged; normalization is for matching only. Unknown
 * processes, negation, alternatives and local scopes block the scalar value.
 * Several distinct operations are never silently concatenated into one finish.
 */
export const TREATMENT_RULE_VERSION = 'treatment-20261109.1';

const rules = [
  ['hard_chrome', '镀硬铬', /경질\s*크롬\s*도금|\bHARD[\s-]+CHROM(?:E|IUM)[\s-]+(?:PLATING|PLATED|COATING)\b|镀硬铬|硬铬(?:电镀|镀层)/iu],
  ['alkaline_blue_white_zinc_explicit', '镀碱性蓝白锌', /(?:镀)?碱性蓝白锌/iu],
  ['white_zinc', '镀白锌', /\b(?:ZINC[\s-]+WHITE[\s-]+(?:COATING|PLATING)|WHITE[\s-]+ZINC[\s-]+(?:PLATING|COATING|COATED|PLATED))\b|백색\s*아연\s*도금|镀白锌|白色镀锌/iu],
  ['black_oxide', '发黑', /\bBLACK\s+OXIDE\b|\bBLACKEN(?:ING|ED)?\b|发黑/iu],
  ['hard_anodizing', '硬质阳极氧化', /\bHARD[\s-]+ANODI[ZS](?:E[DS]?|ING|ATION)\b|硬质阳极氧化|경질\s*(?:아노다이징|양극\s*산화)/iu],
  ['anodizing', '阳极氧化', /\bANODI[ZS](?:E[DS]?|ING|ATION)\b|\bANODIC\s+OXIDATION\b|阳极氧化|양극\s*산화|아노다이징/iu],
  ['electroless_nickel', '化学镀镍', /化学(?:镀)?镍|无电解镀镍|무전해\s*니켈\s*도금|\bELECTROLESS\s+NICKEL(?:\s+PLATING)?\b/iu],
  ['kanigen_electroless_nickel', '化学镀镍', /카니젠\s*도금|\bKANIGEN[\s-]+PLAT(?:ING|ED)\b/iu],
  ['high_frequency_quench', '高频淬火', /高频淬火/iu],
  ['high_frequency_heat_treatment', '高频热处理', /고주파\s*열처리|高频热处理/iu],
  ['induction_hardening', '感应淬火', /\bINDUCTION\s+HARDEN(?:ING|ED)?\b|感应淬火/iu],
  ['heat_treatment', '热处理', /열처리|热处理|\bHEAT[\s-]+TREAT(?:MENT|ED|ING)?\b/iu],
  ['quenching', '淬火', /淬火|\bQUENCH(?:ING|ED)?\b/iu],
  ['tempering', '回火', /回火|\bTEMPER(?:ING|ED)\b/iu],
  ['quench_temper', '调质', /调质/iu],
  ['annealing', '退火', /退火|\bANNEAL(?:ING|ED)?\b/iu],
  ['normalizing', '正火', /正火|\bNORMALI[ZS](?:ING|ED)\b/iu],
  ['carburizing', '渗碳', /渗碳|\bCARBURI[ZS](?:ING|ED)\b/iu],
  ['nitriding', '氮化', /氮化|\bNITRID(?:ING|ED)\b/iu],
  ['ral_color', null, /\bRAL\s*[-:]?\s*\d{4}(?!\d)/iu, 'color'],
];

// Broad hints retain unsupported requests for review, rather than inventing a
// process (e.g. unspecified nickel plating is NOT electroless nickel).
const processHint = /\b(?:COAT(?:ING|ED)?|PLAT(?:ING|ED)|ANODI[ZS]\w*|BLACKEN\w*|OXIDE|PAINT\w*|GALVAN\w*|RAL|SURFACE\s+(?:TREATMENT|FINISH)|HEAT[\s-]+TREAT\w*|QUENCH\w*|TEMPER(?:ING|ED)|ANNEAL\w*|NORMALI[ZS]\w*|CARBURI[ZS]\w*|NITRID\w*|UNPLATED|UNCOATED|UNTREATED|UNANODI[ZS]ED)\b|도금|도장|표면\s*처리|열처리|고주파\s*소입|아노다이징|양극\s*산화|镀|氧化|发黑|喷砂|喷塑|喷漆|涂装|表面处理|热处理|淬火|回火|调质|退火|正火|渗碳|氮化/iu;
const negation = /\b(?:NO|NOT|NEVER|WITHOUT|EXCEPT|EXCLUDING|OMIT|AVOID|PROHIBITED|FORBIDDEN|UNPLATED|UNCOATED|UNTREATED|UNANODI[ZS]ED|DELETE[DS]?|CANCEL(?:LED|ED)?|REMOVE[DS]?)\b|\bNON[\s-]*(?:ANODI[ZS]ED|PLATED|COATED|HEAT[\s-]*TREATED)\b|\bDON['’]?T\b|禁止|严禁|不得|不可|不允许|无需|无须|不需|不要|不进行|不做|不镀|不喷|不涂|不发黑|不氧化|不淬火|不回火|不热处理|免镀|免热处理|无镀层|无涂层|无热处理|无表面处理|取消|删除|作废|未镀|除外|除.+外|하지\s*(?:말|않)|하지마|금지|생략|불필요|제외|삭제|취소|미\s*도금|무\s*도금|비\s*도금|안\s*(?:함|하|할|도금|열처리)|도금\s*(?:없|불가)|열처리\s*없|없음/iu;
const uncertainty = /[?？]|待定|待确认|待核|暂定|任选|可选|按需|或者?|另见|参见|见图|\b(?:TBD|TBC|OPTIONAL|MAY|OR|IF|UNSPECIFIED|AS\s+REQUIRED|REFER\s+TO|SEE\s+(?:NOTE|DRAWING))\b|추후|미정|선택|또는|혹은|필요\s*시|도면\s*참조/iu;
const partialScope = /열처리\s*부(?![가-힣])|표시부|지시부|부분|해당부|일부|부위|내측|외측|내부|외부|한쪽|양측|양쪽|상부|하부|용접부|\b(?:ONLY|LOCAL(?:LY)?|MARKED|INDICATED|PARTIAL|AREA|REGION|ZONE|BORE|HOLES?|THREADS?|INSIDE|OUTSIDE|INNER|OUTER|SURFACE\s+[A-Z]|SIDE\s+[A-Z])\b|局部|部分|标示|标记|标注部|指定区域|孔内|内孔|螺纹|外表面|内表面|内侧|外侧|[A-Z]面|仅|只限/iu;
const stock = /도금\s*판|열처리\s*(?:연마\s*)?봉|(?:镀锌|镀镍|镀铬|镀层)板|热处理(?:研磨棒|磨光棒|棒料|棒材)|\b(?:PRE[\s-]?(?:PLATED|COATED)|(?:PLATED|COATED)\s+(?:SHEET|STOCK|PLATE)|HEAT[\s-]+TREATED\s+(?:GROUND\s+)?(?:BAR|STOCK|ROD))\b/iu;
// Kanigen is a named process; retain the original name and require review
// rather than silently substituting an unspecified generic nickel finish.
const variant = /\b(?:PTFE|TEFLON|COMPOSITE)\b|复合镀|복합\s*도금|카니젠\s*도금|\bKANIGEN[\s-]+PLAT(?:ING|ED)\b/iu;
const genericLabels = /\bSURFACE\s+(?:TREATMENT|FINISH)\s*:?[\s]*|(?:表面处理|工艺|표면\s*처리|후처리)\s*[:：]?/giu;
const paintWords = /\b(?:PAINT(?:ED|ING)?|COAT(?:ING|ED)?)\b|도장|涂装|喷漆|喷塑/giu;
// Include compact labels such as HRC45 and the original lower/upper-bound
// wording; dropping 이상/以上 would turn a minimum into an exact value.
const parameter = /(?:\b(?:HRC|HV|HB)(?![A-Z])|\bTHICKNESS\b|厚度|膜厚|두께)\s*[:=]?\s*(?:>=|<=|[<>≤≥])?\s*\d+(?:\.\d+)?(?:\s*[-~～]\s*\d+(?:\.\d+)?)?(?:\s*(?:μm|um|mm|微米))?(?:\s*(?:이상|이하|초과|미만|以上|以下|以内|以內))?|\d+(?:\.\d+)?\s*(?:μm|um|microns?|微米)\b/giu;

function parameterKey(raw) {
  const type = /^(HRC|HV|HB)/iu.exec(raw)?.[0].toUpperCase() ?? 'thickness';
  const value = raw.replace(/^(?:HRC|HV|HB|THICKNESS|厚度|膜厚|두께)\s*[:=]?/iu, '')
    .replace(/\s+/gu, '').toUpperCase().replace(/Μ/gu, 'U');
  return {type, value};
}

function matchesIn(text) {
  const all = rules.flatMap(([rule, value, regex, kind = 'process']) =>
    [...text.matchAll(new RegExp(regex.source, 'giu'))].map(m => ({
      rule, value: rule === 'ral_color' ? `RAL${m[0].match(/\d{4}/u)[0]}` : value,
      kind, matched_text: m[0], start: m.index, end: m.index + m[0].length,
    })));
  // In the observed combined Korean expression, 소입 and 소려 are distinct
  // operations. Preserve both spans; do not publish just one of them as a finish.
  for (const m of text.matchAll(/고주파\s*소입\s*소려(?![가-힣])/giu)) {
    const quenchEnd = m.index + m[0].indexOf('소입') + '소입'.length;
    const temperStart = m.index + m[0].lastIndexOf('소려');
    all.push({rule:'high_frequency_quench',value:'高频淬火',kind:'process',
      matched_text:text.slice(m.index,quenchEnd),start:m.index,end:quenchEnd});
    all.push({rule:'tempering',value:'回火',kind:'process',
      matched_text:text.slice(temperStart,m.index+m[0].length),start:temperStart,end:m.index+m[0].length});
  }
  // Prefer the complete explicit phrase over contained generic words. This is
  // lexical overlap, not a choice between conflicting separate instructions.
  all.sort((a, b) => a.start - b.start || (b.end - b.start) - (a.end - a.start));
  const selected = [];
  for (const match of all) if (!selected.some(x => match.start < x.end && match.end > x.start)) selected.push(match);
  return selected;
}

/**
 * @param {string|object|Array<string|object>} input Native text records (or text).
 * @returns {{value: string|null, candidates: string[], evidence: object[], issues: string[], rule_version: string, status: string}}
 * candidates include mentioned, negated and scoped processes; ONLY value is a
 * resolved scalar. A nonempty issues list always makes value null.
 */
export function extractTreatment(input) {
  const records = Array.isArray(input) ? input : input == null ? [] : [input];
  const candidates = new Set(), issues = new Set(), evidence = [], constraints = new Map(), scopes = new Set();
  for (const item of records) {
    const record = typeof item === 'string' ? {text: item} : item;
    if (!record || typeof record.text !== 'string') continue;
    const normalized = record.text.normalize('NFKC');
    if (!processHint.test(normalized) && !matchesIn(normalized).length) continue;
    const localIssues = new Set(), clauses = [];
    if (record.textRequiresReview) localIssues.add('source_text_requires_review');
    if (negation.test(normalized)) localIssues.add('negation_or_exclusion');
    if (uncertainty.test(normalized)) localIssues.add('uncertain_instruction');
    if (stock.test(normalized)) localIssues.add('pretreated_stock_not_part_finish');
    if (variant.test(normalized)) localIssues.add('treatment_variant_requires_review');
    if (record.scope != null) {
      scopes.add(String(record.scope));
      if (!['frame_notes', 'whole_part', 'entire_part', 'all', 'unspecified'].includes(String(record.scope))) localIssues.add('scope_requires_review');
    }
    // Do not split decimal thickness values or numbered note prefixes at '.'.
    for (const clause of normalized.split(/\r?\n|[;；。]/u)) {
      const matches = matchesIn(clause);
      const parameters = [...clause.matchAll(new RegExp(parameter.source, parameter.flags))].map(m => m[0]);
      if (!matches.length && !processHint.test(clause)) {
        // Preserve a separate parameter in this process-bearing source record,
        // but do not invent which process or material it belongs to.
        if (parameters.length) {
          localIssues.add('parameter_scope_requires_review');
          clauses.push({normalized_text:clause,matches,parameters,scope_requires_review:partialScope.test(clause)});
        }
        continue;
      }
      if (partialScope.test(clause)) localIssues.add('scope_requires_review');
      const parameterTypes = new Map();
      for (const raw of parameters) {
        const {type, value} = parameterKey(raw), values = parameterTypes.get(type) ?? new Set();
        values.add(value); parameterTypes.set(type, values);
      }
      if ([...parameterTypes.values()].some(values => values.size > 1)) localIssues.add('parameters_require_review');
      let unmatched = clause;
      for (const match of [...matches].reverse()) unmatched = unmatched.slice(0, match.start) + ' '.repeat(match.end - match.start) + unmatched.slice(match.end);
      unmatched = unmatched.replace(genericLabels, '');
      // RAL remains a color specification, never an inferred paint process.
      if (matches.some(m => m.kind === 'color')) unmatched = unmatched.replace(paintWords, '');
      if (!matches.length || processHint.test(unmatched)) localIssues.add('unrecognized_treatment');
      for (const match of matches) {
        candidates.add(match.value);
        if (parameters.length) {
          const values = constraints.get(match.value) ?? new Set();
          values.add(parameters.map(p => { const v = parameterKey(p); return `${v.type}:${v.value}`; }).sort().join('|'));
          constraints.set(match.value, values);
        }
      }
      clauses.push({normalized_text: clause, matches, parameters,
        scope_requires_review: partialScope.test(clause)});
    }
    for (const issue of localIssues) issues.add(issue);
    const original = {};
    for (const key of ['id', 'text', 'rawText', 'layout', 'blockPath', 'handle', 'position', 'scope', 'textRequiresReview']) {
      if (Object.hasOwn(record, key)) original[key] = structuredClone(record[key]);
    }
    evidence.push({...original, normalized_text: normalized, clauses, issues: [...localIssues]});
  }
  if (candidates.size > 1) issues.add('multiple_treatments_require_review');
  if (scopes.size > 1) issues.add('scope_requires_review');
  if ([...constraints.values()].some(values => values.size > 1)) issues.add('parameters_require_review');
  const value = candidates.size === 1 && !issues.size ? [...candidates][0] : null;
  return {value, candidates: [...candidates], evidence, issues: [...issues], rule_version: TREATMENT_RULE_VERSION,
    status: value !== null ? 'recognized' : evidence.length ? 'needs_review' : 'not_found'};
}
