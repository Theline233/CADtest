import fs from 'node:fs/promises';
const dir='D:/CADtest/outputs/polish-20261106/regression-final/samples/S07';
const out=process.argv[2];if(!out)throw new Error('output file required');
const r=JSON.parse(await fs.readFile(`${dir}/cad-read-report.json`,'utf8'));
const f=JSON.parse(await fs.readFile(`${dir}/frame-field-candidates.json`,'utf8'));
const ordinary=r.texts.filter(t=>t.kind!=='DIMENSION_GRAPHICS');
const inside=(p,b)=>Array.isArray(p)&&p.length>=2&&p.every(Number.isFinite)&&
  p[0]>=b[0]-.001&&p[0]<=b[2]+.001&&p[1]>=b[1]-.001&&p[1]<=b[3]+.001;
const unresolved=f.frames.filter(x=>!x.drawingNumber).map(frame=>{
  const texts=ordinary.filter(t=>t.layout===frame.layout&&inside(t.position,frame.bounds));
  const anchor=texts.find(t=>t.blockPath===frame.framePath&&t.text.trim()==='PART No.');
  const external=texts.filter(t=>t.blockPath!==frame.framePath&&!t.blockPath.startsWith(frame.framePath+'/'));
  return {index:frame.index,path:frame.framePath,bounds:frame.bounds,anchor:{id:anchor?.id,position:anchor?.position,axisX:anchor?.axisX,axisY:anchor?.axisY},
    textsInBounds:texts.length,externalTexts:external.map(t=>({id:t.id,text:t.text,kind:t.kind,position:t.position,invisible:t.invisible,textRequiresReview:t.textRequiresReview})),
    drawingLikeTexts:texts.filter(t=>/TA\s*\d{6,}/i.test(t.text)).map(t=>({id:t.id,text:t.text,position:t.position})),
    textsRequireReview:texts.filter(t=>t.textRequiresReview).map(t=>({id:t.id,text:t.text,rawText:t.rawText}))};
});
const dimensionIds=new Set(r.dimensions.map(x=>x.id));
const warnings=r.issues.filter(i=>i.kind==='MTEXT_FORMAT_REVIEW');
const caches=warnings.filter(i=>i.message.includes('/CACHE:'));
const result={sourceSha256:r.sourceSha256,frameCount:f.frameCount,unresolvedCount:unresolved.length,
  ordinaryTextCount:ordinary.length,ordinaryTextRequiresReview:ordinary.filter(t=>t.textRequiresReview).length,
  formatWarnings:warnings.length,cacheWarnings:caches.length,
  warningsLinkedToDimensions:caches.filter(i=>dimensionIds.has(i.message.split('/CACHE:')[0])).length,
  limitations:r.limitations,unresolved};
await fs.writeFile(out,JSON.stringify(result,null,2));
console.log(JSON.stringify({unresolved:unresolved.length,hasDrawingLike:unresolved.filter(x=>x.drawingLikeTexts.length).length,
  ordinaryTextRequiresReview:result.ordinaryTextRequiresReview,formatWarnings:result.formatWarnings,
  warningsLinkedToDimensions:result.warningsLinkedToDimensions}));
