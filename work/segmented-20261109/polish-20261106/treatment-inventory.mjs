// Inventory only the saved CAD/native and frame JSON from the 16-group run.
// No DWG reader, workbook, Excel-derived comparison, or mutation of inputs.
import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {extractTreatment} from './treatment.mjs';

const root = 'D:/CADtest/outputs/polish-20261106/regression-final/samples';
const out = 'D:/CADtest/outputs/accuracy-20261109/treatment';
const ids = [...Array.from({length:12}, (_, i) => `S${String(i+1).padStart(2,'0')}`),
  ...Array.from({length:4}, (_, i) => `H${String(i+1).padStart(2,'0')}`)];
const hint = /도금|도장|표면|후처리|열처리|열\s*처리|경도|경질|고주파|질화|담금질|소둔|소입|경화|연마|코팅|산화|아연|크롬|니켈|침탄|방청|두께|마스킹|HRC|HV\b|PLAT|COAT|TREAT|HARDEN|ANODI|OXIDE|RAL|镀|热处理|淬火|厚度|硬度/iu;
const finitePoint = p => Array.isArray(p) && p.length >= 2 && p.every(v => typeof v === 'number' && Number.isFinite(v));
const inside = (p,b) => p[0] >= b[0]-0.001 && p[0] <= b[2]+0.001 && p[1] >= b[1]-0.001 && p[1] <= b[3]+0.001;
const entries = new Map(), sources = [], groups = [];
await fs.mkdir(out, {recursive:true});
for (const id of ids) {
  const base = path.join(root,id);
  const nativeFile = path.join(base,'cad-read-report.json');
  const frameFile = path.join(base,'frame-field-candidates.json');
  const nativeBytes = await fs.readFile(nativeFile), frameBytes = await fs.readFile(frameFile);
  sources.push(...[[nativeFile,nativeBytes],[frameFile,frameBytes]].map(([file,bytes]) => ({file,sha256:createHash('sha256').update(bytes).digest('hex')})));
  const native = JSON.parse(nativeBytes), saved = JSON.parse(frameBytes);
  let visited = 0, matchingSaved = 0;
  for (const frame of saved.frames) {
    const within = frame.bounds ? native.texts.filter(t => t.kind !== 'DIMENSION_GRAPHICS' && !t.invisible && finitePoint(t.position) && t.layout === frame.layout && inside(t.position,frame.bounds)) : [];
    const result = extractTreatment(within);
    if (JSON.stringify(result) === JSON.stringify(frame.treatmentDetails)) matchingSaved++;
    for (const t of within) {
      if (!hint.test(t.text)) continue;
      visited++;
      const e = entries.get(t.text) ?? {text:t.text,occurrences:0,examples:[],individual:extractTreatment([t])};
      e.occurrences++;
      if (e.examples.length < 8) e.examples.push({sample:id,frame:frame.index,drawingNumber:frame.drawingNumber,id:t.id,rawText:t.rawText,position:t.position,layout:t.layout,textRequiresReview:t.textRequiresReview});
      entries.set(t.text,e);
    }
  }
  groups.push({sample:id,frames:saved.frames.length,framesMatchingSavedBaseline:matchingSaved,hintTextOccurrences:visited});
}
const result = {method:'Saved native text within saved same-layout frame bounds; duplicates across frames counted separately. Keyword inventory is exploratory, not a gold standard.',sources,groups,
  entries:[...entries.values()].sort((a,b)=>b.occurrences-a.occurrences || a.text.localeCompare(b.text))};
await fs.writeFile(path.join(out,'treatment-baseline-inventory.json'),JSON.stringify(result,null,2));
console.log(JSON.stringify({groups,uniquePhrases:entries.size,output:path.join(out,'treatment-baseline-inventory.json')}));
