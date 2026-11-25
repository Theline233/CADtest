"""CFB-02: process notes in a synthetic DWG through guarded recognition, WPF review, and XLSX.
Run Build.ps1 -IncludeTestTools first. Uses openpyxl for an independent workbook read.
"""
import json
import os
import subprocess
import sys
import uuid
from pathlib import Path

from openpyxl import load_workbook

ROOT = Path(__file__).resolve().parents[1]
ENGINE = ROOT / 'work/segmented-20261109'
COMBINED = '氮化热处理（HRC 41–55）；镀铬'


def main():
    out = ROOT / 'outputs' / ('treatment-' + uuid.uuid4().hex[:12])
    out.mkdir(parents=True)
    print(out, flush=True)
    env = {**os.environ, 'CADTEST_ROOT': str(ROOT), 'PYTHONUTF8': '1'}

    def run(label, *args):
        result = subprocess.run(args, cwd=ROOT, env=env, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, timeout=180)
        (out / f'{label}.log').write_bytes(result.stdout)
        if result.returncode:
            raise RuntimeError(f'{label} failed: exit {result.returncode}; see {out / (label + ".log")}')

    def read(path):
        return json.loads(path.read_text(encoding='utf-8-sig'))

    def row_id(n):
        return f'TA20000101-TEST-{n:03}'

    notes, drawing = out / 'process-notes.txt', out / 'SYNTHETIC-CFB02.dwg'
    run('rules', 'node', ROOT / 'tests/treatment_rules.mjs', notes)
    run('create-dwg', ENGINE / 'fixture-tests/bin/Release/net10.0/FixtureTests.exe',
        'treatment-dwg', drawing, notes)
    task = out / 'task'
    recognition = task / 'recognition'
    run('recognize', sys.executable, '-B', ENGINE / 'runner.py', '--drawing', drawing,
        '--output', recognition, '--prepare-review')
    sample = recognition / 'samples/D001'
    run_record = read(sample / 'run.json')
    for stage in ('native', 'profile', 'projection', 'projection_profile', 'projection_audit', 'fields', 'prediction'):
        assert run_record[stage]['status'] == 'passed' and run_record[stage]['guards_installed'], stage
    assert run_record['inputs_unchanged']
    assert read(task / 'review-status.json')['status'] == 'passed'

    # The process text and frame geometry must yield identical field evidence
    # with and without retention filtering in the production projector.
    raw_replay = out / 'raw-replay'
    raw_replay.mkdir()
    run('unprojected-fields', 'node', ENGINE / 'extract-requirement-frames.mjs', raw_replay,
        sample / 'cad-read-report.json')
    fields = read(sample / 'frame-field-candidates.json')
    assert fields == read(raw_replay / 'frame-field-candidates.json')
    assert fields['frameCount'] == 8 and fields['frames'][0]['surfaceTreatment'] == COMBINED
    evidence = notes.read_text(encoding='utf8').splitlines()
    assert [v['text'] for v in fields['frames'][0]['treatmentDetails']['evidence']] == evidence
    assert all(v['rawText'] and v['position'] and v['handle'] for v in fields['frames'][0]['treatmentDetails']['evidence'])

    records = {r['drawing_number']: r for r in read(sample / 'predictions.json')['records']}
    assert len(records) == 8
    for n, treatment in [(1, COMBINED), (2, '镀硬铬'), (3, '镀白锌')]:
        assert records[row_id(n)]['treatment'] == treatment
    recognized = records[row_id(1)]
    assert recognized['issues'] == ['treatment_parameters_require_confirmation']
    assert recognized['treatment_candidates'] == ['氮化热处理（HRC 41–55）', '镀铬']
    assert [v['text'] for v in recognized['evidence']['treatment']] == evidence
    issues = dict(enumerate(['scope_requires_review', 'negation_or_exclusion', 'parameters_require_review',
                            'multiple_treatments_require_review', 'treatment_ownership_requires_review'], start=4))
    for n, issue in issues.items():
        assert records[row_id(n)]['treatment'] is None and issue in records[row_id(n)]['issues'], (n, records[row_id(n)]['issues'])
    assert '氮化热处理（HRC 45–55）' in records[row_id(6)]['treatment_candidates']

    review = task / 'review'
    dataset = {i['cad']['drawing_number']: i for i in read(review / 'input-dataset.json')['items']}
    prepared = dataset[row_id(1)]
    assert prepared['cad']['treatment'] == COMBINED
    assert prepared['treatment_evidence'] == '；'.join(evidence)
    assert prepared['requires_review'] is True
    assert '工艺含硬度' in prepared['warnings'] and '工艺尚未确定' not in prepared['warnings']
    for n in issues:
        entry = dataset[row_id(n)]
        assert entry['requires_review'] is True and entry['cad']['treatment'] is None
        assert '已读出的工艺候选' in entry['warnings']
    assert '工艺标注归属不明' in dataset[row_id(8)]['warnings']

    book = load_workbook(review / '图纸复核.xlsx', read_only=True)
    try:
        rows = {r[1]: r for r in book['复核明细'].iter_rows(min_row=8, values_only=True)}
        row = rows[row_id(1)]
        assert row[4:7] == (COMBINED, 2, 'EA')
        assert row[7] == prepared['warnings'] and row[8] in (None, '', '待复核')
        assert row[19] == '；'.join(evidence)
        for n in issues:
            assert rows[row_id(n)][4] in (None, '') and rows[row_id(n)][19]
    finally:
        book.close()

    # Real TaskSession and workbook round trip; only synthetic review choices.
    ui_out = out / 'ui-selftest'
    run('ui', ROOT / 'ui/bin/Release/net10.0-windows/CadReviewUI.exe',
        '--selftest', review, ui_out)
    exported = ui_out / 'confirmed'
    audit = read(exported / '确认结果.json')
    book = load_workbook(exported / '确认明细.xlsx', read_only=True)
    try:
        rows = {r[1]: r for r in book['确认明细'].iter_rows(min_row=8, values_only=True)}
        row = rows[row_id(1)]
        assert row[4:8] == (COMBINED, 2, 'EA', None)
        assert row[8] == '=IF(H8="","",F8*H8)'
        assert row[11] == prepared['id']
        assert {n: rows[row_id(n)][4] for n in [2, 3]} == {2:'镀硬铬', 3:'镀白锌'}
        assert row_id(8) not in rows and row_id(6) not in rows
    finally:
        book.close()
    accepted = next(r for r in audit['accepted'] if r['id'] == prepared['id'])
    assert accepted['original']['treatment'] == accepted['final']['treatment'] == COMBINED
    summary = {'status': 'passed', 'frames': 8, 'treatment': COMBINED,
               'original_notes': evidence, 'review_cases': issues,
               'synthetic_only': True}
    (out / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf8')
    print('Synthetic DWG → guarded recognition → review/workbook → WPF save/reopen → independent confirmed XLSX: passed.')


if __name__ == '__main__':
    main()
