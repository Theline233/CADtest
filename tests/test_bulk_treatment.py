"""CFB-03: exercise the WPF replacement and independently reopen both exports.

Run from the workspace after Build.ps1:
    python -B tests/test_bulk_treatment.py outputs/cfb03-new
Requires openpyxl. No genuine drawing, task, or template is edited.
"""
import json
from pathlib import Path
import subprocess
import sys
from zipfile import ZipFile

from openpyxl import load_workbook

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / 'work/segmented-20261109'))
import runner

ORIGINAL = '镀白锌'
REPLACEMENT = '镀碱性蓝白锌'
EVIDENCE = 'SYNTHETIC CAD: ZINC WHITE COATING'


def prepare_task(directory, main):
    recognition = directory / 'recognition'
    recognition.mkdir(parents=True)
    frozen, manifest = runner.freeze_sources(recognition)
    protocol = recognition / 'protocol.json'
    runner.save(protocol, {'frozen_manifest_sha256': runner.sha(frozen / 'manifest.json'),
                           'scope': 'SYNTHETIC CFB-03 ONLY; no CAD recognition was performed'})
    sources = [protocol, frozen / 'manifest.json', *(frozen / name for name in manifest)]
    files = []
    for file_id in (('F1', 'F2') if main else ('F1',)):
        drawing = directory / f'synthetic-cfb03-{file_id}.dwg'
        drawing.write_bytes(b'NOT A DWG - SYNTHETIC CFB-03 TEST')
        sources.append(drawing)
        files.append({'id': file_id, 'name': drawing.name, 'path': str(drawing), 'sha256': runner.sha(drawing),
                      'diagnostics': '本次未发现需要文件级确认的诊断', 'requires_acknowledgement': False})
    items = []
    for i in (range(1, 13) if main else (1,)):
        treatment = {8: '镀白锌（加厚）', 11: '喷漆'}.get(i, ORIGINAL)
        evidence = EVIDENCE if treatment == ORIGINAL else 'SYNTHETIC CAD: different treatment'
        row = {'id': f'ITEM-{i}', 'file_id': 'F2' if i == 12 else 'F1',
               'cad': {'drawing_number': f'SYNTHETIC-{i:02d}', 'name': 'Synthetic part',
                       'material': 'TEST', 'treatment': treatment, 'quantity': i, 'unit': 'EA'},
               'warnings': '', 'requires_review': False, 'kind': 'part_title', 'parents': [],
               'quantity_evidence': f'Synthetic {i} EA', 'treatment_evidence': evidence, 'reference_info': ''}
        if i == 10:
            row['default_decision'] = {'mode': '不纳入', 'note': '图面整框打X（合成测试标记）'}
        items.append(row)
    dataset = directory / 'dataset.json'
    runner.save(dataset, {'schema_version': 1, 'task_id': f'SYNTHETIC-CFB03-{directory.parent.name}-{directory.name}',
                          'title': 'Synthetic CFB-03 only', 'files': files, 'items': items,
                          'sources': [{'path': str(path), 'sha256': runner.sha(path)} for path in sources]})
    review = directory / 'review'
    result = runner.command([frozen / 'review-native/CadReviewWorkflow.exe', 'prepare', '--input', dataset,
                             '--output', review, '--wait-for-go'], directory, 'prepare',
                            runner.Limits(memory_mib=1024, cpu_percent=25), wait_for_go=True)
    runner.save(directory / 'prepare-result.json', result)
    if result['status'] != 'passed':
        raise RuntimeError(f'prepare failed: {directory}')
    return review


def check_books(output, task_review):
    source_book = task_review / '图纸复核.xlsx'
    accepted = [*range(1, 9), 11]
    expectations = {i: REPLACEMENT if i <= 6 else '镀白锌（加厚）' if i == 8 else ORIGINAL for i in accepted}
    ui = output / 'ui'
    reviewed, = (ui / 'sessions').glob('*/snapshots/*/已复核.xlsx')
    with ZipFile(reviewed) as archive:
        assert archive.testzip() is None
    review_book = load_workbook(reviewed, read_only=True)
    orig_book = load_workbook(source_book, read_only=True)
    try:
        rows = list(review_book['复核明细'].iter_rows(min_row=8, values_only=True))
        originals = list(orig_book['复核明细'].iter_rows(min_row=8, values_only=True))
        assert len(rows) == len(originals) == 12
        assert all((row[:8], row[16:]) == (original[:8], original[16:]) for row, original in zip(rows, originals))
        for i, row in enumerate(rows, 1):
            assert row[21] == f'ITEM-{i}'
            if i <= 6:
                assert (row[4], row[8], row[12], row[19]) == (ORIGINAL, '人工修订', REPLACEMENT, EVIDENCE)
    finally:
        review_book.close()
        orig_book.close()

    confirmed = ui / 'export/复核记录/确认明细.xlsx'
    styled = ui / '报价样式.xlsx'
    for path, sheet_name, start, drawing, treatment, quantity, unit in (
            (confirmed, '确认明细', 8, 2, 5, 6, 7), (styled, '报价单样式', 6, 3, 6, 7, None)):
        with ZipFile(path) as archive:
            assert archive.testzip() is None
        book = load_workbook(path, read_only=True, data_only=False)
        try:
            sheet = book[sheet_name]
            found = []
            for row in range(start, sheet.max_row + 1):
                number = sheet.cell(row, drawing).value
                if not isinstance(number, str) or not number.startswith('SYNTHETIC-'):
                    continue
                i = int(number.rsplit('-', 1)[1])
                found.append(i)
                assert sheet.cell(row, treatment).value == expectations[i]
                assert sheet.cell(row, quantity).value == i
                assert unit is None or sheet.cell(row, unit).value == 'EA'
                assert sheet.cell(row, 8).value is None and sheet.cell(row, 9).data_type == 'f'
                if path == confirmed:
                    assert sheet.cell(row, 12).value == f'ITEM-{i}'
            assert found == accepted, found
        finally:
            book.close()
    result = json.loads((ui / 'export/复核记录/确认结果.json').read_text(encoding='utf-8-sig'))
    validation = json.loads((ui / 'export/复核记录/validation.json').read_text(encoding='utf-8-sig'))
    assert validation['status'] == 'ready'
    assert [row['id'] for row in result['accepted']] == [f'ITEM-{i}' for i in accepted]
    assert {row['id'] for row in result['excluded']} == {'ITEM-9', 'ITEM-10', 'ITEM-12'}
    assert all(row['original']['treatment'] == ORIGINAL and row['final']['treatment'] == REPLACEMENT
               for row in result['accepted'][:6])

    # Change only the original treatment in a disposable copy of the saved review.
    # The existing guarded validator must reject it, not use it as a confirmation.
    tampered = output / 'changed-original-negative.xlsx'
    editable = load_workbook(reviewed)
    try:
        editable['复核明细']['E8'] = 'SYNTHETIC original changed'
        editable.save(tampered)
    finally:
        editable.close()
    rejected = subprocess.run([sys.executable, '-B', '-X', 'utf8', runner.APP / 'review_guard.py', 'validate',
                               '--manifest', task_review / 'source.json', '--review', tampered],
                              cwd=REPO, capture_output=True, encoding='utf-8', timeout=60)
    (output / 'original-protection-negative.txt').write_text(rejected.stdout + rejected.stderr, encoding='utf-8')
    assert rejected.returncode != 0 and 'Source value changed at E8' in rejected.stderr
    runner.save(output / 'workbook-check.json',
                {'status': 'passed', 'reviewed': str(reviewed), 'confirmed': str(confirmed), 'styled': str(styled),
                 'accepted_ids_in_both_workbooks': [f'ITEM-{i}' for i in accepted],
                 'exported_treatments': expectations, 'six_revised_rows': 6,
                 'original_columns_and_evidence_preserved': True, 'altered_original_rejected': True,
                 'unit_prices_empty_and_amounts_formulas': True})


def main():
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    output = runner.prepare_output(sys.argv[1], runner.ROOT, require_new=True)
    with runner.workspace_lock():
        review = prepare_task(output / 'primary', main=True)
        other = prepare_task(output / 'other', main=False)
    protected = [p for folder in (review, other) for p in folder.iterdir() if p.suffix in {'.json', '.xlsx'}]
    before = {path: path.read_bytes() for path in protected}
    executable = REPO / 'ui/bin/Release/net10.0-windows/CadReviewUI.exe'
    subprocess.run([executable, '--bulk-treatment-test', output / 'ui', review, other],
                   cwd=REPO, check=True, timeout=150)
    assert all(p.read_bytes() == value for p, value in before.items())
    check_books(output, review)
    print(output / 'workbook-check.json')


if __name__ == '__main__':
    main()
