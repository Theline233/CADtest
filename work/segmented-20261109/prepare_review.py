"""Parameter-driven review dataset. CAD values only; reference differences remain separate notes."""
import argparse,hashlib,json,math
from pathlib import Path
from workspace_paths import workspace_root

ROOT=workspace_root()
def sha(path):
    with Path(path).open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()

def duplicate_from_record(row):
    """Copy optional predictor evidence without treating historical rows as new results."""
    raw=row.get('duplicate_occurrences')
    raw=raw if isinstance(raw,list) else []
    count=row.get('duplicate_count')
    count=count if type(count) is int and count>=0 else None
    if count is None and len(raw)>1:count=len(raw)
    if count is None or count<2:return None

    def number(value):
        if type(value) is int:return value if abs(value)<=9007199254740991 else None
        return value if type(value) is float and math.isfinite(value) and abs(value)<=9007199254740991 else None
    def string(value):
        return value if isinstance(value,str) else None
    def boolean(value):
        return value if type(value) is bool else None
    def frame_index(value):
        if type(value) in (int,str):return value
        return value if type(value) is float and math.isfinite(value) else None
    occurrences=[]
    for entry in raw:
        if not isinstance(entry,dict):continue
        index=entry.get('frame_index')
        occurrences.append({'frame_index':frame_index(index),
            'value':number(entry.get('value')),'unit':string(entry.get('unit')),
            'scope':string(entry.get('scope')),'expression':string(entry.get('expression')),
            'crossed_out':boolean(entry.get('crossed_out')),'included_in_sum':boolean(entry.get('included_in_sum'))})
    active=row.get('duplicate_active_count',row.get('active_count'))
    if type(active) is not int or active<0:
        active=sum(o['included_in_sum'] is True for o in occurrences) if occurrences and all(o['included_in_sum'] is not None for o in occurrences) else None
    mode=row.get('quantity_mode')
    return {'count':count,'active_count':active,'occurrences':occurrences,
        'sum':number(row.get('quantity_sum')),'single':number(row.get('quantity_single')),
        'sum_candidate':number(row.get('quantity_sum_candidate')),
        'unit':string(row.get('unit')),'mode':mode if mode in ('sum','single') else None}

def duplicate_warning(duplicate):
    def value_text(value):return '数量未确定' if value is None else str(value)
    occurrences=[]
    for occurrence in duplicate['occurrences']:
        index=occurrence['frame_index']
        text=(f'图框{index}：' if index is not None else '图框序号未确定：')+value_text(occurrence['value'])+(occurrence['unit'] or '')
        if occurrence['crossed_out'] is True:text+='（打X，不计入）'
        elif occurrence['included_in_sum'] is False:text+='（未计入）'
        occurrences.append(text)
    note=f"同图号出现{duplicate['count']}处"
    if occurrences:note+='（'+' / '.join(occurrences)+'）'
    if duplicate['active_count']==0:note+='，全部打X图框默认不纳入'
    elif duplicate['mode']=='sum' and duplicate['sum'] is not None:
        note+=f"，默认累加为{value_text(duplicate['sum'])}{duplicate['unit'] or ''}；如是重复绘制，可改为按一处计"
    elif duplicate['active_count']==1:note+='，仅一处参与数量核对，其余图框未计入'
    elif duplicate['mode'] is None and duplicate['occurrences']:
        note+='，数量待核，不自动累加'
        if duplicate['sum_candidate'] is not None:
            note+=f"；各处数值相加候选{value_text(duplicate['sum_candidate'])}仅供核对，单位或范围须确认"
    else:note+='，数量及各处情况请核对'
    return note

def main():
    cli=argparse.ArgumentParser();cli.add_argument('--run',nargs='+',required=True);cli.add_argument('--output',required=True);args=cli.parse_args()
    sources={};items=[];files=[];seen=set()
    def read(path):
        path=Path(path).resolve()
        if not path.is_relative_to(ROOT):raise ValueError('Input must stay in workspace')
        if path.stat().st_size>256*1024*1024:raise ValueError('Review JSON exceeds 256 MiB; retain evidence and review manually')
        payload=path.read_bytes()
        sources[str(path)]=hashlib.sha256(payload).hexdigest().upper()
        return json.loads(payload.decode('utf-8-sig'))
    messages={'quantity_conflict':'同图号数量/单位/范围存在差异','quantity_total_conflict':'同图存在不同总量','quantity_unresolved':'数量尚未确定',
        'quantity_invalid_expression':'数量表达式未支持','quantity_invalid_equation':'数量等式未通过','quantity_component_mismatch':'正反件分量不一致',
        'quantity_unit_conflict':'数量单位不一致','unit_missing_or_conflicting':'单位缺失或冲突','material_missing_or_conflicting':'材质缺失或冲突',
        'name_missing_or_conflicting':'名称缺失或冲突','treatment_not_recognized':'工艺尚未确定','treatment_requires_review':'工艺需要确认',
        'scope_requires_review':'工艺范围需核对','multiple_treatments_require_review':'存在多个工艺要求','negation_or_exclusion':'工艺包含否定/排除',
        'unrecognized_treatment':'有工艺原文但未识别','source_text_requires_review':'工艺原文格式待核','frame_field_issues':'标注关联待核',
        'quantity_component_conflict':'总量相同但正反件拆分不同','treatment_parameters_require_confirmation':'工艺含硬度/厚度等参数，请确认完整要求',
        'quantity_scope_ambiguous':'数量适用范围不明确，需核对单套与总量',
        'quantity_title_unresolved':'标题数量未能可靠读取，需核对图纸',
        'treatment_missing_or_conflicting_across_frames':'重复图框工艺缺失或冲突','pretreated_stock_not_part_finish':'原材料已处理，不等于零件工艺',
        'parameter_scope_requires_review':'硬度或厚度参数的对应工艺不明确','treatment_variant_requires_review':'工艺品牌或型号须核实，保留原文',
        'crossed_out_drawing':'图面整框打X（程序判断为作废），默认不纳入；如需报价请改为采用图纸或人工修订并写明原因',
        'crossed_out_duplicate_frames_ignored':'同图号另有图框整框打X，已不计入数量和字段，请核对',
        'duplicate_drawing_summed':'同图号多处数量已按有效图框累加，请核对',
        'duplicate_quantities_differ':'同图号各处数量不同，请核对',
        'duplicate_quantity_components_incomplete':'部分图框缺少正反件拆分，合计未保留拆分，请核对',
        'duplicate_quantity_sum_unsafe':'重复数量合计超出安全范围，未自动累加',
        'quantity_scope_conflict':'同图号数量适用范围不一致'}
    crossed_note='图面整框打X，程序判断为作废，默认不纳入（可改回）'
    for run_arg in args.run:
        run=Path(run_arg).resolve();selection=read(run/'selection.json')['selected'];runs={x['id']:x for x in read(run/'runs.json')}
        protocol=read(run/'protocol.json');manifest=read(run/'frozen-source'/'manifest.json')
        if sha(run/'frozen-source'/'manifest.json')!=protocol.get('frozen_manifest_sha256'):
            raise ValueError('Frozen manifest differs from runtime protocol')
        for relative,entry in manifest.items():
            frozen=(run/'frozen-source'/relative).resolve()
            if not frozen.is_relative_to((run/'frozen-source').resolve()):raise ValueError('Invalid frozen source path')
            sources[str(frozen)]=entry['sha256']
        # This independent entry is DWG-only. Never auto-import an optional
        # comparison file: business/version differences are a separate review.
        comparison=None
        comparison_rows={}
        for selected in selection:
            sid=selected['id']
            if sid in seen:raise ValueError('Repeated file ID across runs; prepare separate tasks or use distinct IDs')
            seen.add(sid)
            if runs[sid].get('status')!='passed' or not runs[sid].get('inputs_unchanged'):raise ValueError(f'{sid}: incomplete or changed-input run cannot prepare review')
            path=Path(selected['dwg']).resolve();sources[str(path)]=selected['dwg_sha256'].upper()
            if sha(path)!=sources[str(path)]:raise ValueError(f'{sid}: original drawing changed')
            folder=run/'samples'/sid
            recorded=read(folder/'run.json')
            if recorded!=runs[sid]:raise ValueError(f'{sid}: stage records do not match')
            # Bind both the full native evidence and every intermediate output to
            # the hashes captured immediately at each guarded stage boundary.
            for stage in ('native','profile','projection','projection_profile','projection_audit','fields','prediction'):
                entry=recorded.get(stage,{})
                if entry.get('status')!='passed' or not entry.get('output_sha256'):
                    raise ValueError(f'{sid}: {stage} missing stage provenance')
                if entry.get('input_sha256_before')!=entry.get('input_sha256_after'):
                    raise ValueError(f'{sid}: {stage} input changed')
                for name,digest in entry['output_sha256'].items():
                    artifact=Path(name).resolve()
                    if not artifact.is_relative_to(folder.resolve()) or not artifact.is_file() or sha(artifact)!=digest:
                        raise ValueError(f'{sid}: {stage} output changed')
                    sources[str(artifact)]=digest
            p=read(folder/'predictions.json')
            raw=read(folder/'cad-read-projection.json')
            profile=read(folder/'profile.json')
            audit=read(folder/'projection-audit.json')
            full_report=folder/'cad-read-report.json'
            full_hash=sources[str(full_report.resolve())]
            metadata=raw.get('projectionMetadata',{})
            if (profile.get('sourceHash')!=full_hash or metadata.get('originalReportSha256')!=full_hash
                or metadata.get('fullNativeReport') is not False or metadata.get('allTextAndIssuesRetained') is not True
                or metadata.get('allNotificationsRetained') is not True or audit.get('status')!='passed'
                or audit.get('source_sha256')!=full_hash or audit.get('projection_sha256')!=sources[str((folder/'cad-read-projection.json').resolve())]):
                raise ValueError(f'{sid}: projection is not bound to the full original report')
            if raw.get('success') is not True or raw.get('sourceFile')!=path.name or p.get('sourceFile')!=path.name or str(raw.get('sourceSha256','')).upper()!=selected['dwg_sha256'].upper() or str(p.get('sourceSha256','')).upper()!=selected['dwg_sha256'].upper():
                raise ValueError(f'{sid}: result is not bound to this drawing/version; rerun with the current recognizer')
            warnings=[];blocking=['图框范围与字段归属由路径几何推断，须对照原图核查']
            if not p.get('records'):blocking.append('本文件未提取到图号明细，必须核查或明确排除')
            if p.get('unresolvedFrames'):
                crossed_unresolved=sum(bool(f.get('crossedOut')) for f in p['unresolvedFrames'])
                blocking.append(f"{len(p['unresolvedFrames'])}个图框未确定唯一图号"+(f"（其中{crossed_unresolved}个整框打X）" if crossed_unresolved else ''))
            replaced=raw.get('numericSanitization',{}).get('replacedCount',0)
            if replaced:blocking.append(f'{replaced}个非有限数字已留空')
            actual_issues=[x for x in raw.get('issues',[]) if not x['kind'].startswith('PREVIEW_') and x['kind'] not in ('MTEXT_FORMAT_REVIEW','TEXT_ENCODING_REVIEW')]
            if actual_issues:blocking.append(f'{len(actual_issues)}条原生提取问题')
            reviews=sum(x['kind'] in ('MTEXT_FORMAT_REVIEW','TEXT_ENCODING_REVIEW') for x in raw.get('issues',[]))
            if reviews:warnings.append(f'{reviews}条文字格式/编码警告保留在诊断记录')
            crossed_frames=p.get('crossedOutFrameCount',0)
            if crossed_frames:warnings.append(f'{crossed_frames}个图框整框打X，只出现在这些图框的图号默认不纳入（可改回）')
            duplicates=[duplicate_from_record(row) for row in p['records']]
            duplicate_groups=sum(d is not None for d in duplicates)
            if duplicate_groups:warnings.append(f'本文件有{duplicate_groups}组重复图号，数量请核对')
            notices=raw.get('notifications',[])
            if notices:
                blocking.append(f'{len(notices)}条原生读取器通知需核查，完整内容保留在原报告及投影的notifications中')
            if comparison:
                sample_comparison=next((s for s in comparison.get('samples',[]) if s['id']==sid),None)
                if sample_comparison and sample_comparison['expected_unique_drawings'] and sample_comparison['raw_text_reference_overlap']==0:
                    blocking.append('CAD与候选对照表图号无重叠，核对文件配对/版本；不自动认定错配')
                elif sample_comparison:
                    cad_numbers={r['drawing_number'] for r in p['records']}
                    unmatched=sorted(number for sample,number in comparison_rows if sample==sid and number not in cad_numbers)
                    if unmatched:blocking.append(f"候选Excel另有{len(unmatched)}个图号未关联本次CAD候选，需核对版本/报价范围（不自动认定漏读）："+'、'.join(unmatched))
            files.append({'id':sid,'name':path.name,'path':str(path),'sha256':selected['dwg_sha256'].upper(),
                'diagnostics':'；'.join(blocking+warnings) or '本次未发现需要文件级确认的诊断','requires_acknowledgement':bool(blocking)})
            for row,duplicate in zip(p['records'],duplicates):
                notes=[]
                issues=set(row.get('issues',[]))
                treatment_details={'scope_requires_review','multiple_treatments_require_review','negation_or_exclusion','unrecognized_treatment',
                    'source_text_requires_review','treatment_parameters_require_confirmation','treatment_missing_or_conflicting_across_frames','pretreated_stock_not_part_finish',
                    'parameter_scope_requires_review','treatment_variant_requires_review'}
                if issues & treatment_details:issues-={'treatment_not_recognized','treatment_requires_review'}
                if any(i.startswith('quantity_') and i!='quantity_unresolved' for i in issues):issues.discard('quantity_unresolved')
                for issue in dict.fromkeys(i for i in row.get('issues',[]) if i in issues):
                    text=messages.get(issue,issue.replace('_',' '))
                    if text not in notes:notes.append(text)
                if duplicate is not None:notes.append(duplicate_warning(duplicate))
                kinds=row.get('record_kinds',[])
                if 'assembly_title' in kinds:notes.append('父图/装配项：确认是否纳入报价，避免与子件重复')
                if 'subpart' in kinds:notes.append('子件的名称/材质/工艺共用父图标题，确认适用范围')
                if row.get('quantity') is None and row.get('quantity_options'):notes.append('已读出的数量候选：'+ ' / '.join(map(str,row['quantity_options'])))
                if len(row.get('quantity_component_options',[]))>1:notes.append('正反件拆分候选：'+'；'.join(f"{c['normal']}/{c['mirror']}" for c in row['quantity_component_options']))
                if len(row.get('quantity_candidate_unit_options',[]))>1:notes.append('单位候选：'+' / '.join(row['quantity_candidate_unit_options']))
                if row.get('treatment') is None and row.get('treatment_candidates'):notes.append('已读出的工艺候选（范围仍待核）：'+' / '.join(row['treatment_candidates']))
                association=row.get('field_association_evidence',[])
                def association_values(entry):
                    evidence=entry.get('candidates',[])+entry.get('reviewTexts',[])+entry.get('quantityContext',[])
                    parts=[]
                    for x in evidence:
                        if x.get('text'):parts.append(x['text'])
                        if x.get('rawText') and x['rawText']!=x.get('text'):parts.append('原始格式：'+x['rawText'])
                    return list(dict.fromkeys(parts))
                for field,label in [('material','材质'),('name','名称')]:
                    rejected=list(dict.fromkeys(v for a in association if a.get('field')==field for v in association_values(a)))
                    if rejected:notes.append(label+'待核原文：'+' / '.join(rejected))
                c=comparison_rows.get((sid,row['drawing_number']));reference_info='未提供对照表，仅以CAD读取值准备复核'
                if comparison:
                    if c is None:notes.append('候选Excel未列此图号，确认本次报价范围');reference_info='CAD额外编号，不能自动判为错误'
                    else:
                        reference_info='；'.join(f"{label}：{c['reference'].get(field) if c['reference'].get(field) is not None else '原表未提供'}" for field,label in [('quantity','数量'),('material','材质'),('treatment','工艺')])
                        if any(v=='mismatch' for v in c['fields'].values()):notes.append('与候选Excel有资料差异，不预判哪方错误')
                        if any(v in ('ambiguous_reference','reference_semantics_uncertain') for v in c['fields'].values()):notes.append('对照表列含义/重复行需确认，不反填CAD')
                ev=row.get('evidence',{});key=sid+'|'+row['drawing_number'];identity=hashlib.sha256((sources[str(path)]+'|'+key).encode()).hexdigest()[:24]
                items.append({'id':identity,'file_id':sid,'cad':{'drawing_number':row['drawing_number'],**{f:row.get(f) for f in ('name','material','treatment','quantity','unit')}},
                    'warnings':'；'.join(notes),'requires_review':bool(notes),'kind':'；'.join(kinds),'parents':row.get('parent_drawing_numbers',[]),
                    'quantity_evidence':'；'.join(dict.fromkeys([x['text'] for k in ('quantity','quantity_context') for x in ev.get(k,[])]+
                        [v for a in association if a.get('field')=='quantity' for v in association_values(a)])),
                    'treatment_evidence':'；'.join(dict.fromkeys(x['text'] for x in ev.get('treatment',[]))), 'reference_info':reference_info,
                    **({'duplicate':duplicate} if duplicate is not None else {}),
                    **({'default_decision':{'mode':'不纳入','note':crossed_note}} if row.get('crossed_out') is True else {})})
    source_list=[{'path':p,'sha256':h} for p,h in sorted(sources.items())]
    for p,h in sources.items():
        if sha(p)!=h:raise ValueError('Source changed while preparing dataset: '+p)
    task='CAD-'+hashlib.sha256(json.dumps(source_list,sort_keys=True).encode()).hexdigest()[:16]
    dataset={'schema_version':1,'task_id':task,'title':'图纸明细复核','sources':source_list,'files':files,'items':items}
    target=Path(args.output).resolve()
    if not target.is_relative_to(ROOT/'outputs') or target.exists():raise ValueError('Use a new output JSON below outputs')
    target.parent.mkdir(parents=True,exist_ok=True);target.write_text(json.dumps(dataset,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'task_id':task,'files':len(files),'items':len(items),'output':str(target)},ensure_ascii=False))
if __name__=='__main__':main()
