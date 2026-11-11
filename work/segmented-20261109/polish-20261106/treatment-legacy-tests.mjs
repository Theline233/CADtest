// Reuse the preceding round's unit cases without letting their old harness
// write into the historical output directory or execute its old replay loop.
import fs from 'node:fs/promises';
import {spawnSync} from 'node:child_process';
import {pathToFileURL} from 'node:url';
import {createHash} from 'node:crypto';

const original='D:/CADtest/work/polish-20261106/treatment-tests.mjs';
const modulePath='D:/CADtest/work/accuracy-20261109/polish-20261106/treatment.mjs';
const out='D:/CADtest/outputs/accuracy-20261109/treatment';
const source=await fs.readFile(original,'utf8');
const marker="const extractor = path.join(project, 'work/extract-requirement-frames.mjs');";
const end=source.indexOf(marker);
if(end<0) throw new Error('Legacy test boundary changed; refusing to run altered harness.');
let prefix=source.slice(0,end);
const oldRoot="const evidenceRoot = path.join(project, 'outputs/polish-20261106/treatment');";
if(!prefix.includes(oldRoot) || !prefix.includes("await import('./treatment.mjs')")) throw new Error('Legacy test harness layout changed.');
prefix=prefix.replace(oldRoot,`const evidenceRoot = ${JSON.stringify(out)};`)
  .replace("await import('./treatment.mjs')",`await import(${JSON.stringify(pathToFileURL(modulePath).href)})`);
prefix+=`\nconst report = {legacySource:${JSON.stringify(original)}, sourceSha256:${JSON.stringify(createHash('sha256').update(source).digest('hex'))}, ruleVersion:TREATMENT_RULE_VERSION,total:tests.length,passed:tests.filter(t=>t.passed).length,failures:tests.filter(t=>!t.passed),cases:tests};\n`+
  `await fs.writeFile(path.join(output,'treatment-legacy-results.json'),JSON.stringify(report,null,2));\n`+
  `console.log(JSON.stringify({output,total:report.total,passed:report.passed,failures:report.failures}));\n`+
  `if(report.failures.length) process.exitCode=1;\n`;
await fs.mkdir(out,{recursive:true});
const generated=`${out}/treatment-legacy-generated.mjs`;
await fs.writeFile(generated,prefix);
const child=spawnSync(process.execPath,[generated],{encoding:'utf8',timeout:60000});
if(child.stdout) process.stdout.write(child.stdout);
if(child.stderr) process.stderr.write(child.stderr);
if(child.error || child.status!==0) { if(child.error) console.error(child.error); process.exitCode=1; }
