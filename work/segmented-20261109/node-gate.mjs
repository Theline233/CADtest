// Do not open any input until the parent has installed and verified its Job.
import {pathToFileURL} from 'node:url';
let line='';
for await (const part of process.stdin) {
  line += part.toString('utf8');
  if (line.length > 32) throw new Error('invalid_gate');
  if (line.includes('\n')) break;
}
if (line !== 'GO\n') throw new Error('invalid_gate');
const target=process.argv[2];
if (!target) throw new Error('missing_script');
process.argv=[process.argv[0],target,...process.argv.slice(3)];
await import(pathToFileURL(target).href);
