import fs from 'node:fs/promises';
import {extractTreatment} from './polish-20261106/treatment.mjs';
const root=process.argv[2]??'D:/CADtest/outputs/format-check-20261105';
// The original native report stays intact.  The runner supplies a bounded,
// provenance-checked projection; omitting this argument preserves test replays.
const report=JSON.parse(await fs.readFile(process.argv[3]??`${root}/cad-read-report.json`,'utf8'));
const finitePoint=p=>Array.isArray(p)&&p.length>=2&&p.every(v=>typeof v==='number'&&Number.isFinite(v));
const texts=report.texts.filter(t=>t.kind!=='DIMENSION_GRAPHICS'&&!t.invisible&&finitePoint(t.position));
const titleLabels=texts.filter(t=>t.text.trim()==='PART No.');
const inside=(p,b)=>p[0]>=b[0]-0.001&&p[0]<=b[2]+0.001&&p[1]>=b[1]-0.001&&p[1]<=b[3]+0.001;
const isLabel=text=>/^(PART NO\.?|PART NAME|MATERIAL|Q'TY|SCALE|SIZE|UNIT|PALLET|REV\.?)$/i.test(text.trim());
const drawingKey=text=>text.normalize('NFKC').replace(/\s+/g,'').toUpperCase();
const isDrawingText=text=>/^TA\d{8}-[A-Z0-9-]+$/.test(drawingKey(text))&&(drawingKey(text).match(/TA\d{8}-/g)??[]).length===1;
function relative(point,label) {
  const h=label.height,r=label.rotation??0;
  const x=finitePoint(label.axisX)?label.axisX:[Math.cos(r)*h,Math.sin(r)*h];
  const y=finitePoint(label.axisY)?label.axisY:[-Math.sin(r)*h,Math.cos(r)*h];
  const det=x[0]*y[1]-x[1]*y[0];
  if(!Number.isFinite(det)||Math.abs(det)<1e-12)return null;
  const dx=point[0]-label.position[0],dy=point[1]-label.position[1];
  return [(dx*y[1]-dy*y[0])/det,(x[0]*dy-x[1]*dx)/det];
}
const frameLabelSelections=[],frameReviewSelections=[];
const frames=titleLabels.map((anchor,index)=>{
  const framePath=anchor.blockPath;
  const bounds=[Infinity,Infinity,-Infinity,-Infinity];
  for(const g of report.geometry)if(g.layout===anchor.layout&&(g.blockPath===framePath||g.blockPath.startsWith(framePath+'/')))
    for(const p of g.points)if(finitePoint(p)){bounds[0]=Math.min(bounds[0],p[0]);bounds[1]=Math.min(bounds[1],p[1]);bounds[2]=Math.max(bounds[2],p[0]);bounds[3]=Math.max(bounds[3],p[1]);}
  const within=texts.filter(t=>t.layout===anchor.layout&&inside(t.position,bounds));
  const labels=texts.filter(t=>t.layout===anchor.layout&&t.blockPath===framePath).sort((a,b)=>Math.hypot(a.position[0]-anchor.position[0],a.position[1]-anchor.position[1])-Math.hypot(b.position[0]-anchor.position[0],b.position[1]-anchor.position[1]));
  const materialLabel=labels.find(t=>t.text.trim()==='MATERIAL');
  const quantityLabel=labels.find(t=>t.text.trim()==="Q'TY");
  const nameLabel=labels.find(t=>t.text.trim()==='PART NAME');
  frameLabelSelections[index]={material:materialLabel,quantity:quantityLabel,name:nameLabel};
  const nearBelow=(label,dx0,dx1,dy0=-5,dy1=-0.15)=>label?within.filter(t=>{if(t.id===label.id||isLabel(t.text)||t.textRequiresReview)return false;const p=relative(t.position,label);return p&&inside(p,[dx0,dy0,dx1,dy1]);}):[];
  const reviewBelow=(label,dx0,dx1,dy0=-5,dy1=-0.15)=>label?within.filter(t=>{
    if(t.id===label.id||!t.textRequiresReview)return false;
    const p=relative(t.position,label);return p&&inside(p,[dx0,dy0,dx1,dy1]);
  }):[];
  const numberCandidates=nearBelow(anchor,-1.5,36,-6,-0.05).filter(t=>isDrawingText(t.text));
  const drawingReviewTexts=[...(anchor.textRequiresReview?[anchor]:[]),...reviewBelow(anchor,-1.5,36,-6,-0.05)];
  const materials=nearBelow(materialLabel,-1.5,9).filter(t=>!/^\d+[/.]\d/.test(t.text));
  const qtyContents=nearBelow(quantityLabel,-1.5,10).filter(t=>!(t.blockPath===framePath&&/^[A-F]$/.test(t.text.trim()))).sort((a,b)=>relative(a.position,quantityLabel)[0]-relative(b.position,quantityLabel)[0]);
  const qtyNumbers=qtyContents.filter(t=>/^\d+$/.test(t.text.trim()));
  const units=qtyContents.filter(t=>/^(EA|SET|PCS?|개|套|件)$/i.test(t.text.trim()));
  const names=nearBelow(nameLabel,-1.5,23).filter(t=>!/^TA\d/.test(t.text)&&!/^PALLET|UNIT$/i.test(t.text));
  const labelAndReview=(label,dx0,dx1)=>label?[...(label.textRequiresReview?[label]:[]),...reviewBelow(label,dx0,dx1)]:[];
  frameReviewSelections[index]={material:labelAndReview(materialLabel,-1.5,9),
    quantity:labelAndReview(quantityLabel,-1.5,10),name:labelAndReview(nameLabel,-1.5,23)};
  const treatmentDetails=extractTreatment(within);
  const treatment=treatmentDetails.evidence;
  const quantityContext=within.filter(t=>!t.textRequiresReview&&/(?:TOTAL\s*Q\s*['’]?\s*TY|TOTAL\s*QUANTITY|总数量|总计数量)/i.test(t.text));
  const treatmentCandidates=treatmentDetails.candidates;
  const specialNotes=within.filter(t=>/SYMMETR|대칭|镜像|对称/i.test(t.text));
  const quantity=qtyNumbers.length===1?Number(qtyNumbers[0].text):null;
  const parentNumber=numberCandidates.length===1&&!drawingReviewTexts.length?drawingKey(numberCandidates[0].text):null;
  const subparts=parentNumber?within.filter(t=>!t.textRequiresReview&&isDrawingText(t.text)&&drawingKey(t.text).startsWith(parentNumber+'-')&&/^\d+$/.test(drawingKey(t.text).slice(parentNumber.length+1))).map((t,i)=>{
    // A subpart must have its own complete identifier. Only a nearby quantity
    // carrying an explicit unit can supply its quantity; never divide parent total.
    const inlinePieceSet=/^\d+\s*(?:EA|PCS|件)\s*[x×*]\s*\d+\s*(?:SET|套)$/i;
    const quantities=nearBelow(t,-1.5,24,-6,-.05).filter(q=>/^\d+(?:\s*[x×*/]\s*\d+)?\s*(EA|SET|PCS|件|套)$/i.test(q.text.trim())||inlinePieceSet.test(q.text.trim()));
    // CAD may store the set multiplier as a separate text entity immediately
    // after the piece quantity. Keep both original entities in reading order:
    // dropping the multiplier would incorrectly promote a per-set value to a
    // certain total. The existing quantity parser will leave its scope unresolved.
    const quantityEvidence=quantities.flatMap(q=>{
      const modifiers=within.filter(m=>{
        if(m.id===q.id||m.blockPath!==q.blockPath||m.textRequiresReview||
           !/^[x×*]\s*\d+\s*(?:SET|套)$/i.test(m.text.trim()))return false;
        const p=relative(m.position,q);
        return p&&p[0]>0&&p[0]<=6&&Math.abs(p[1])<=0.6;
      });
      return [q,...modifiers];
    });
    const subpartReviewTexts=reviewBelow(t,-1.5,24,-6,-.05);
    return {index:`${index+1}.part${i+1}`,framePath,layout:anchor.layout,bounds,
      drawingNumber:drawingKey(t.text),drawingCandidates:[{text:t.text,id:t.id}],
      names:names.map(n=>({text:n.text,id:n.id})),materials:materials.map(m=>({text:m.text,id:m.id})),
      quantityEvidence:subpartReviewTexts.length?[]:quantityEvidence.map(q=>({text:q.text,id:q.id})),quantityUnit:'',quantityContext:[],
      surfaceTreatment:treatmentDetails.value,treatmentCandidates,
      treatmentDetails,treatmentIssues:treatmentDetails.issues,treatmentRuleVersion:treatmentDetails.rule_version,
      treatmentEvidence:treatment.map(n=>({text:n.text,id:n.id})),
      recordKind:'subpart',parentDrawingNumber:parentNumber,parentFrameIndex:index+1,
      fieldScopes:{drawingNumber:'subpart_label',quantity:'adjacent_subpart_label_with_unit',name:'shared_parent_title',material:'shared_parent_title',treatment:'shared_parent_frame'},
      fieldIssues:[...(quantities.length!==1||quantityEvidence.length!==quantities.length||quantities.some(q=>inlinePieceSet.test(q.text.trim()))?['subpart_quantity_missing_or_ambiguous']:[]),
        ...(subpartReviewTexts.length?['subpart_quantity_text_requires_review']:[])],
      ...(subpartReviewTexts.length?{fieldAssociationEvidence:[{field:'quantity',
        candidates:quantityEvidence.map(q=>({text:q.text,id:q.id})),
        reviewTexts:subpartReviewTexts.map(q=>({text:q.text,id:q.id,rawText:q.rawText,kind:q.kind}))}]}:{})};
  }):[];
  return {index:index+1,framePath,layout:anchor.layout,bounds:bounds.every(Number.isFinite)?bounds:null,
    drawingNumber:parentNumber,
    drawingCandidates:numberCandidates.map(t=>({text:t.text,id:t.id})),
    names:names.map(t=>({text:t.text,id:t.id})),
    materials:materials.map(t=>({text:t.text,id:t.id})),
    quantity,quantityUnit:units.map(t=>t.text).join(' '),quantityEvidence:qtyContents.map(t=>({text:t.text,id:t.id})),
    quantityContext:quantityContext.map(t=>({text:t.text,id:t.id})),quantityLabelEvidence:quantityLabel?[{text:quantityLabel.text,id:quantityLabel.id}]:[],
    surfaceTreatment:treatmentDetails.value,treatmentCandidates,
    treatmentDetails,treatmentIssues:treatmentDetails.issues,treatmentRuleVersion:treatmentDetails.rule_version,
    treatmentEvidence:treatment.map(t=>({text:t.text,id:t.id})),
    specialNotes:specialNotes.map(t=>({text:t.text,id:t.id})),
    subparts,recordKind:subparts.length?'assembly_title':'part_title',
    ...(drawingReviewTexts.length?{fieldIssues:['drawing_number_text_requires_review'],
      fieldAssociationEvidence:[{field:'drawingNumber',candidates:numberCandidates.map(t=>({text:t.text,id:t.id})),
        reviewTexts:drawingReviewTexts.map(t=>({text:t.text,id:t.id,rawText:t.rawText,kind:t.kind}))}]}:{}),
    issues:[...(numberCandidates.length!==1||drawingReviewTexts.length?['图号非唯一']:[]),
      ...(drawingReviewTexts.length?['图号字段含需复核文字']:[]),
      ...(qtyNumbers.length!==1?['数量非唯一或缺失']:[]),...(materials.length!==1?['材质非唯一或缺失']:[])]};
});
// A second title layout places DRAWING/NUMBER in a small label cell, with the
// drawing identifier in the adjacent cell to its right. Accept this layout
// only when the native block geometry proves the complete pair of cells.
// A coordinate window alone could otherwise promote a nearby BOM row.
function splitDrawingCell(anchor,drawingLabel){
  const tolerance=0.25;
  const labelPosition=relative(drawingLabel.position,anchor);
  if(!labelPosition)return null;
  const badGeometry=new Set((report.issues??[]).filter(i=>i.kind==='PREVIEW_POLYLINE_BULGE_APPROXIMATION')
    .map(i=>String(i.message??'').split(' | ')[0]));
  const horizontal=[],vertical=[];
  for(const g of report.geometry){
    if(g.layout!==anchor.layout||g.dimensionGraphics||
       !(g.blockPath===anchor.blockPath||g.blockPath.startsWith(anchor.blockPath+'/'))||
       !['Line','LwPolyline'].includes(g.kind)||badGeometry.has(g.id)||!Array.isArray(g.points))continue;
    const points=g.points.filter(finitePoint);
    if(points.length!==g.points.length)continue;
    const pairs=[];for(let i=1;i<points.length;i++)pairs.push([points[i-1],points[i]]);
    if(g.closed&&points.length>2)pairs.push([points.at(-1),points[0]]);
    for(const [p,q] of pairs){
      const a=relative(p,anchor),b=relative(q,anchor);if(!a||!b)continue;
      const dx=Math.abs(a[0]-b[0]),dy=Math.abs(a[1]-b[1]);
      if(dy<=0.03&&dx>tolerance)horizontal.push({id:g.id,at:(a[1]+b[1])/2,lo:Math.min(a[0],b[0]),hi:Math.max(a[0],b[0])});
      else if(dx<=0.03&&dy>tolerance)vertical.push({id:g.id,at:(a[0]+b[0])/2,lo:Math.min(a[1],b[1]),hi:Math.max(a[1],b[1])});
    }
  }
  const minLabelX=Math.min(0,labelPosition[0]),maxLabelX=Math.max(0,labelPosition[0]);
  const minLabelY=Math.min(0,labelPosition[1]),maxLabelY=Math.max(0,labelPosition[1]);
  const crossesLabel=h=>h.lo<=minLabelX+tolerance&&h.hi>=maxLabelX-tolerance;
  const bottom=horizontal.filter(h=>crossesLabel(h)&&h.at<minLabelY-tolerance).sort((a,b)=>b.at-a.at)[0];
  const top=horizontal.filter(h=>crossesLabel(h)&&h.at>maxLabelY+tolerance).sort((a,b)=>a.at-b.at)[0];
  if(!bottom||!top)return null;
  const walls=vertical.filter(v=>v.lo<=bottom.at+tolerance&&v.hi>=top.at-tolerance);
  const left=walls.filter(v=>v.at<minLabelX-tolerance).sort((a,b)=>b.at-a.at)[0];
  const divider=walls.filter(v=>v.at>maxLabelX+tolerance).sort((a,b)=>a.at-b.at)[0];
  if(!left||!divider)return null;
  const right=walls.filter(v=>v.at>divider.at+tolerance).sort((a,b)=>a.at-b.at)[0];
  if(!right||right.at-divider.at<=2*tolerance||top.at-bottom.at<=2*tolerance)return null;
  if([top,bottom].some(h=>h.lo>left.at+tolerance||h.hi<right.at-tolerance))return null;
  return {bounds:[divider.at,bottom.at,right.at,top.at],tolerance,
    borders:{top:top.id,bottom:bottom.id,left:left.id,divider:divider.id,right:right.id}};
}
const splitDiagnostics=[];
for(const anchor of texts.filter(t=>t.text.trim().toUpperCase()==='NUMBER')){
  // Root model-space text is not an individual frame. Without an insert
  // boundary the same grid could belong to a parts table or another drawing.
  if(!anchor.blockPath.includes('/INSERT:'))continue;
  const samePath=texts.filter(t=>t.layout===anchor.layout&&t.blockPath===anchor.blockPath);
  const paired=samePath.filter(t=>t.text.trim().toUpperCase()==='DRAWING').filter(t=>{
    const p=relative(t.position,anchor);return p&&p[0]>=-1.5&&p[0]<=1.5&&p[1]>=0.5&&p[1]<=3;
  });
  if(!paired.length)continue;
  const cell=splitDrawingCell(anchor,paired[0]);
  if(!cell){
    splitDiagnostics.push({layout:anchor.layout,framePath:anchor.blockPath,
      numberLabelId:anchor.id,issue:'drawing_number_cell_geometry_unproven'});
    const existing=frames.find(f=>f.layout===anchor.layout&&f.framePath===anchor.blockPath);
    const labelEvidence={field:'drawingNumber',style:'DRAWING_NUMBER',
      numberLabel:{id:anchor.id,text:anchor.text},drawingLabels:paired.map(t=>({id:t.id,text:t.text})),cell:null,candidates:[]};
    if(existing){
      existing.drawingNumber=null;
      if(existing.subparts.length){existing.unresolvedSubparts=existing.subparts;existing.subparts=[];existing.recordKind='part_title';}
      if(!existing.issues.includes('图号非唯一'))existing.issues.unshift('图号非唯一');
      existing.issues.push('图号标题单元格边界待核');
      existing.fieldIssues=[...(existing.fieldIssues??[]),'drawing_number_cell_geometry_unproven'];
      existing.fieldAssociationEvidence=[...(existing.fieldAssociationEvidence??[]),labelEvidence];
    }else{
      const treatmentDetails={...extractTreatment([]),status:'not_assessed',issues:['alternative_title_fields_unresolved']};
      frames.push({index:frames.length+1,framePath:anchor.blockPath,layout:anchor.layout,bounds:null,
        drawingNumber:null,drawingCandidates:[],names:[],materials:[],quantity:null,quantityUnit:'',
        quantityEvidence:[],quantityContext:[],quantityLabelEvidence:[],surfaceTreatment:null,treatmentCandidates:[],
        treatmentDetails,treatmentIssues:treatmentDetails.issues,treatmentRuleVersion:treatmentDetails.rule_version,
        treatmentEvidence:[],specialNotes:[],subparts:[],recordKind:'unresolved_title_candidate',
        fieldScopes:{drawingNumber:'unresolved',name:'unresolved',material:'unresolved',quantity:'unresolved',treatment:'unresolved'},
        fieldIssues:['drawing_number_cell_geometry_unproven','alternative_title_fields_unresolved'],
        fieldAssociationEvidence:[labelEvidence],issues:['图号标题单元格边界待核']});
      frameLabelSelections.push({material:null,quantity:null,name:null});
      frameReviewSelections.push({material:[],quantity:[],name:[]});
    }
    continue;
  }
  const parentPath=anchor.blockPath.slice(0,anchor.blockPath.lastIndexOf('/INSERT:'));
  const ownedText=t=>t.blockPath===parentPath||t.blockPath===anchor.blockPath||t.blockPath.startsWith(anchor.blockPath+'/');
  const inCell=t=>{
    if(t.layout!==anchor.layout)return false;
    const p=relative(t.position,anchor);if(!p)return false;
    return p[0]>=cell.bounds[0]-cell.tolerance&&p[0]<=cell.bounds[2]+cell.tolerance&&
      p[1]>=cell.bounds[1]-cell.tolerance&&p[1]<=cell.bounds[3]+cell.tolerance;
  };
  const cellTexts=texts.filter(t=>inCell(t)&&t.text.trim());
  const drawingCandidates=cellTexts.filter(t=>!t.textRequiresReview&&isDrawingText(t.text));
  const insideCell=t=>{const p=relative(t.position,anchor);return p&&p[0]>cell.bounds[0]+cell.tolerance&&
    p[0]<cell.bounds[2]-cell.tolerance&&p[1]>cell.bounds[1]+cell.tolerance&&p[1]<cell.bounds[3]-cell.tolerance;};
  const duplicateLabels=samePath.filter(t=>t.text.trim().toUpperCase()==='NUMBER').length!==1||paired.length!==1;
  const reviewTexts=[...(anchor.textRequiresReview?[anchor]:[]),...paired.filter(t=>t.textRequiresReview),...cellTexts.filter(t=>t.textRequiresReview)];
  const foreignTexts=cellTexts.filter(t=>!ownedText(t));
  const resolved=!duplicateLabels&&!reviewTexts.length&&!foreignTexts.length&&cellTexts.length===1&&drawingCandidates.length===1&&insideCell(drawingCandidates[0]);
  const evidence=cellTexts.map(t=>({text:t.text,id:t.id,rawText:t.rawText,kind:t.kind,blockPath:t.blockPath,
    foreignBlock:!ownedText(t),textRequiresReview:!!t.textRequiresReview}));
  const legacy=frames.find(f=>f.layout===anchor.layout&&f.framePath===anchor.blockPath);
  if(legacy){
    // Two competing title schemes inside one block do not define two frames or
    // establish which title owns the drawing. Retain both kinds of evidence.
    legacy.drawingNumber=null;
    if(legacy.subparts.length){legacy.unresolvedSubparts=legacy.subparts;legacy.subparts=[];legacy.recordKind='part_title';}
    if(!legacy.issues.includes('图号非唯一'))legacy.issues.unshift('图号非唯一');
    legacy.issues.push('同一图框存在多种图号标题');
    legacy.fieldIssues=[...(legacy.fieldIssues??[]),'multiple_drawing_title_schemes'];
    legacy.drawingAssociationEvidence=[...(legacy.drawingAssociationEvidence??[]),{style:'DRAWING_NUMBER',
      numberLabel:{id:anchor.id,text:anchor.text},drawingLabel:{id:paired[0].id,text:paired[0].text},
      cell,candidates:evidence}];
    continue;
  }
  const bounds=[Infinity,Infinity,-Infinity,-Infinity];
  for(const g of report.geometry)if(g.layout===anchor.layout&&(g.blockPath===anchor.blockPath||g.blockPath.startsWith(anchor.blockPath+'/')))
    for(const p of g.points)if(finitePoint(p)){bounds[0]=Math.min(bounds[0],p[0]);bounds[1]=Math.min(bounds[1],p[1]);bounds[2]=Math.max(bounds[2],p[0]);bounds[3]=Math.max(bounds[3],p[1]);}
  const treatmentDetails={...extractTreatment([]),status:'not_assessed',issues:['alternative_title_fields_unresolved']};
  const issueCodes=['alternative_title_fields_unresolved',...(duplicateLabels?['drawing_title_labels_ambiguous']:[]),
    ...(foreignTexts.length?['drawing_number_cell_foreign_block']:[]),
    ...(!resolved?['drawing_number_cell_unresolved']:[])];
  frames.push({index:frames.length+1,framePath:anchor.blockPath,layout:anchor.layout,bounds:bounds.every(Number.isFinite)?bounds:null,
    drawingNumber:resolved?drawingKey(drawingCandidates[0].text):null,
    drawingCandidates:drawingCandidates.map(t=>({text:t.text,id:t.id})),names:[],materials:[],
    quantity:null,quantityUnit:'',quantityEvidence:[],quantityContext:[],quantityLabelEvidence:[],
    surfaceTreatment:null,treatmentCandidates:[],treatmentDetails,treatmentIssues:treatmentDetails.issues,
    treatmentRuleVersion:treatmentDetails.rule_version,treatmentEvidence:[],specialNotes:[],subparts:[],recordKind:'part_title',
    fieldScopes:{drawingNumber:'drawing_number_cell',name:'unresolved',material:'unresolved',quantity:'unresolved',treatment:'unresolved'},
    fieldIssues:issueCodes,fieldAssociationEvidence:[{field:'drawingNumber',style:'DRAWING_NUMBER',
      numberLabel:{id:anchor.id,text:anchor.text},drawingLabels:paired.map(t=>({id:t.id,text:t.text})),cell,candidates:evidence}],
    issues:[...(!resolved?['图号非唯一或缺失']:[]),'名称、材质、数量及工艺范围待核']});
  frameLabelSelections.push({material:null,quantity:null,name:null});
  frameReviewSelections.push({material:[],quantity:[],name:[]});
}
// A second, undecodable ordinary text in a title field can hide a conflict.
// Preserve it and the readable candidate, but do not promote the field to a
// value. DIMENSION_GRAPHICS and texts without finite positions were excluded
// before the local title-cell checks above.
for(const frame of frames)for(const [field,reviewTexts] of Object.entries(frameReviewSelections[frame.index-1])){
  if(!reviewTexts.length)continue;
  const original=field==='material'?frame.materials:field==='name'?frame.names:frame.quantityEvidence;
  const evidence={field,candidates:original,reviewTexts:reviewTexts.map(t=>({text:t.text,id:t.id,rawText:t.rawText,kind:t.kind}))};
  if(field==='quantity')evidence.quantityContext=frame.quantityContext;
  frame.fieldAssociationEvidence=[...(frame.fieldAssociationEvidence??[]),evidence];
  const issue=`${field}_text_requires_review`;
  frame.fieldIssues=[...(frame.fieldIssues??[]),issue];
  if(field==='material'){
    frame.materials=[];
    if(!frame.issues.includes('材质非唯一或缺失'))frame.issues.push('材质非唯一或缺失');
  } else if(field==='name')frame.names=[];
  else {
    frame.quantity=null;frame.quantityEvidence=[];frame.quantityUnit='';frame.quantityContext=[];
    if(!frame.issues.includes('数量非唯一或缺失'))frame.issues.push('数量非唯一或缺失');
  }
  if(field==='material'||field==='name')for(const subpart of frame.subparts){
    if(field==='material')subpart.materials=[];else subpart.names=[];
    subpart.fieldIssues=[...(subpart.fieldIssues??[]),issue];
  }
}
// Reusing one title-label entity for multiple frames can silently copy a
// neighbouring field into both. Keep the rejected evidence for review, while
// withholding the field until ownership can be established.
const labelClaims=new Map();
for(const frame of frames)for(const [field,label] of Object.entries(frameLabelSelections[frame.index-1])){
  if(typeof label?.id!=='string'||!label.id)continue;
  const key=JSON.stringify([frame.layout,field,label.id]);
  if(!labelClaims.has(key))labelClaims.set(key,[]);
  labelClaims.get(key).push(frame.index);
}
for(const frame of frames)for(const [field,label] of Object.entries(frameLabelSelections[frame.index-1])){
  if(!label)continue;
  const frameIndices=labelClaims.get(JSON.stringify([frame.layout,field,label.id]))??[];
  if(frameIndices.length<2)continue;
  const original=field==='material'?frame.materials:field==='name'?frame.names:frame.quantityEvidence;
  const evidence={field,label:{text:label.text,id:label.id},frameIndices,candidates:original};
  if(field==='quantity')evidence.quantityContext=frame.quantityContext;
  frame.fieldAssociationEvidence=[...(frame.fieldAssociationEvidence??[]),evidence];
  const issue=`${field}_label_shared_across_frames`;
  frame.fieldIssues=[...(frame.fieldIssues??[]),issue];
  if(field==='material'){
    frame.materials=[];
    if(!frame.issues.includes('材质非唯一或缺失'))frame.issues.push('材质非唯一或缺失');
  } else if(field==='name')frame.names=[];
  else {
    frame.quantity=null;frame.quantityEvidence=[];frame.quantityUnit='';frame.quantityContext=[];
    if(!frame.issues.includes('数量非唯一或缺失'))frame.issues.push('数量非唯一或缺失');
  }
  if(field==='material'||field==='name')for(const subpart of frame.subparts){
    if(field==='material')subpart.materials=[];else subpart.names=[];
    subpart.fieldIssues=[...(subpart.fieldIssues??[]),issue];
  }
}
// A single native text entity claimed by several title cells has no proven
// owner. Never turn that shared evidence into multiple certain drawing numbers.
const drawingClaims=new Map();
for(const frame of frames)for(const candidate of frame.drawingCandidates){
  if(typeof candidate.id!=='string'||!candidate.id)continue;
  const key=JSON.stringify([frame.layout,candidate.id]);
  if(!drawingClaims.has(key))drawingClaims.set(key,[]);
  drawingClaims.get(key).push(frame.index);
}
for(const frame of frames){
  const shared=frame.drawingCandidates.flatMap(candidate=>{
    const indices=drawingClaims.get(JSON.stringify([frame.layout,candidate.id]))??[];
    return indices.length>1?[{...candidate,frameIndices:indices}]:[];
  });
  if(!shared.length)continue;
  frame.drawingNumber=null;
  if(frame.subparts.length){frame.unresolvedSubparts=frame.subparts;frame.subparts=[];frame.recordKind='part_title';}
  if(!frame.issues.includes('图号非唯一'))frame.issues.unshift('图号非唯一');
  frame.issues.push('图号候选跨图框归属不明');
  frame.fieldIssues=[...(frame.fieldIssues??[]),'drawing_candidate_shared_across_frames'];
  frame.drawingAssociationEvidence=shared;
}
// A whole frame struck through by two straight corner-to-corner segments is a
// drawing-level cancellation mark. Only segments that the projector retains
// under the same rule (open 2-point Line/Polyline, not dimension graphics,
// length >= 100, neither axis < 1/4 of the other) are considered, so the full
// report and the projection give the same answer. Inflated or missing bounds
// simply leave the frame unmarked; nothing is deleted here.
const crossMarkSegment=g=>{
  if(g.dimensionGraphics||g.closed||!Array.isArray(g.points)||g.points.length!==2)return null;
  if(g.kind!=='Line'&&!String(g.kind).includes('Polyline'))return null;
  const [a,b]=g.points;
  if(!finitePoint(a)||!finitePoint(b))return null;
  const dx=Math.abs(b[0]-a[0]),dy=Math.abs(b[1]-a[1]);
  if(dx*dx+dy*dy<10000||dy<0.25*dx||dx<0.25*dy)return null;
  return {g,a,b};
};
const crossSegments=report.geometry.map(crossMarkSegment).filter(Boolean);
let crossedFrameCount=0;
for(const frame of frames){
  if(!Array.isArray(frame.bounds)||!frame.bounds.every(Number.isFinite))continue;
  const [x0,y0,x1,y1]=frame.bounds,diagonal=Math.hypot(x1-x0,y1-y0);
  if(!(x1>x0&&y1>y0&&diagonal>=100))continue;
  const tolerance=0.05*diagonal;
  const near=(p,q)=>Math.hypot(p[0]-q[0],p[1]-q[1])<=tolerance;
  const joins=(s,p,q)=>(near(s.a,p)&&near(s.b,q))||(near(s.a,q)&&near(s.b,p));
  const local=crossSegments.filter(s=>s.g.layout===frame.layout);
  const main=local.filter(s=>joins(s,[x0,y0],[x1,y1])),anti=local.filter(s=>joins(s,[x0,y1],[x1,y0]));
  if(!main.length||!anti.length)continue;
  crossedFrameCount++;
  const mark={rule:'whole_frame_corner_to_corner_x_v1',tolerance,frameIndex:frame.index,
    segments:[...main,...anti].map(s=>({id:s.g.id,layer:s.g.layer,blockPath:s.g.blockPath,points:[s.a.slice(0,2),s.b.slice(0,2)]}))};
  frame.crossedOut=mark;
  frame.issues=[...(frame.issues??[]),'图框整框打X'];
  for(const sub of [...(frame.subparts??[]),...(frame.unresolvedSubparts??[])])sub.crossedOut={...mark,inheritedFromFrame:frame.index};
}
// The frozen predictor merges duplicate drawing numbers and historically treats
// one nonempty name/material as enough. Propagate an unresolved title field to
// every duplicate in that group so the conflict cannot disappear at that seam.
// Crossed-out frames do not take part when the same number has an active frame,
// matching the predictor, which then ignores them.
const duplicateGroups=new Map();
for(const record of frames.flatMap(frame=>[frame,...frame.subparts])){
  if(!record.drawingNumber)continue;
  if(!duplicateGroups.has(record.drawingNumber))duplicateGroups.set(record.drawingNumber,[]);
  duplicateGroups.get(record.drawingNumber).push(record);
}
for(const allRecords of duplicateGroups.values())for(const field of ['material','name']){
  const active=allRecords.filter(record=>!record.crossedOut),group=active.length?active:allRecords;
  const causes=[`${field}_text_requires_review`,`${field}_label_shared_across_frames`];
  if(group.length<2||!group.some(record=>(record.fieldIssues??[]).some(issue=>causes.includes(issue))))continue;
  for(const record of group){
    const key=field==='material'?'materials':'names';
    if(!record[key].length)continue;
    record.fieldAssociationEvidence=[...(record.fieldAssociationEvidence??[]),
      {field,reason:'unresolved_in_duplicate_frame',candidates:record[key]}];
    record[key]=[];
    record.fieldIssues=[...(record.fieldIssues??[]),`${field}_unresolved_in_duplicate_frame`];
    if(field==='material'&&record.issues&&!record.issues.includes('材质非唯一或缺失'))record.issues.push('材质非唯一或缺失');
  }
}
await fs.writeFile(`${root}/frame-field-candidates.json`,JSON.stringify({sourceFile:report.sourceFile,sourceSha256:report.sourceSha256,method:'Same-layout title labels; legacy label-relative fields or geometrically bounded DRAWING/NUMBER cells; original evidence retained. No Excel input.',frameCount:frames.length,crossedOutFrameCount:crossedFrameCount,crossMarkRule:'whole_frame_corner_to_corner_x_v1',frames,...(splitDiagnostics.length?{splitTitleDiagnostics:splitDiagnostics}:{})},null,2));
console.log(JSON.stringify({frameCount:frames.length,identifiedFrames:frames.filter(f=>f.drawingNumber).length,unresolvedFrames:frames.filter(f=>!f.drawingNumber).length,crossedOutFrames:crossedFrameCount}));
