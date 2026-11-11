"""Independent bounded Python comparison of source vs projected array elements."""
import argparse
import hashlib
import io
import json
from itertools import zip_longest
from pathlib import Path
import sys

MAX_BUFFER = 16 * 1024 * 1024
MISSING = object()

def sha(path):
    with open(path, 'rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest().upper()

def load(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))

def elements(path, stat):
    """Read a single top-level array incrementally, materializing one object."""
    decoder = json.JSONDecoder()
    with open(path, 'rb') as raw:
        raw.seek(stat['start'])
        text = io.TextIOWrapper(raw, encoding='utf-8', newline='')
        data, pos, eof = '', 0, False
        def fill():
            nonlocal data, pos, eof
            if pos: data, pos = data[pos:], 0
            chunk = text.read(65536)
            if not chunk: eof = True
            data += chunk
            if len(data) > MAX_BUFFER: raise ValueError('audit_record_exceeds_16MiB')
        def space():
            nonlocal pos
            while True:
                while pos < len(data) and data[pos] in ' \t\r\n': pos += 1
                if pos < len(data) or eof: return
                fill()
        fill(); space()
        if pos >= len(data) or data[pos] != '[': raise ValueError('invalid_array_start')
        pos += 1
        first = True
        while True:
            space()
            if pos >= len(data): raise ValueError('incomplete_array')
            if data[pos] == ']':
                pos += 1; break
            if not first:
                if data[pos] != ',': raise ValueError('array_comma_missing')
                pos += 1; space()
                if pos < len(data) and data[pos] == ']': raise ValueError('array_trailing_comma')
            while True:
                try:
                    obj, end = decoder.raw_decode(data, pos)
                    pos = end
                    break
                except json.JSONDecodeError:
                    if eof: raise
                    fill()
            first = False
            yield obj

def js_trim(s):
    return s.strip(' \t\n\r\v\f\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff')

def meta(path, field):
    with open(path, 'rb') as f:
        f.seek(field['start'])
        data = f.read(field['bytes'])
    return json.loads(data.decode('utf-8'))

def main():
    ap = argparse.ArgumentParser()
    for arg in ('source', 'profile', 'projected', 'projected-profile', 'output'):
        ap.add_argument('--' + arg, required=True)
    ap.add_argument('--wait-for-go', action='store_true')
    a = ap.parse_args()
    if not a.wait_for_go or sys.stdin.readline() != 'GO\n': raise ValueError('invalid_gate')
    source, projected = Path(a.source), Path(a.projected)
    source_profile, projected_profile = load(a.profile), load(a.projected_profile)
    source_hash, projected_hash = sha(source), sha(projected)
    if source_hash != source_profile['sourceHash'] or projected_hash != projected_profile['sourceHash']:
        raise ValueError('report_hash_changed')
    sf, pf = source_profile['fields'], projected_profile['fields']
    for name in ('sourceFile', 'sourceSha256', 'success', 'summary', 'numericSanitization'):
        if name in sf and meta(source, sf[name]) != meta(projected, pf[name]): raise ValueError(name + '_metadata_mismatch')
    anchors, counts = {}, {}
    for name in ('texts', 'issues'):
        n = 0
        for left, right in zip_longest(elements(source, sf[name]), elements(projected, pf[name]), fillvalue=MISSING):
            if left is MISSING or right is MISSING or left != right: raise ValueError(f'{name}_mismatch_at_{n}')
            if name == 'texts':
                v = js_trim(left['text'])
                if v == 'PART No.' or v.upper() == 'NUMBER':
                    anchors.setdefault(left['layout'], set()).add(left['blockPath'])
            n += 1
        if n != sf[name]['items'] or n != pf[name]['items']: raise ValueError(name + '_count_mismatch')
        counts[name] = n
    def matches(obj):
        candidates = anchors.get(obj['layout'], ())
        path = obj['blockPath']
        if path in candidates: return True
        return any(path[:i] in candidates for i, char in enumerate(path) if char == '/')
    projected_geometry = iter(elements(projected, pf['geometry']))
    total = retained = 0
    for obj in elements(source, sf['geometry']):
        total += 1
        if matches(obj):
            other = next(projected_geometry, MISSING)
            if other is MISSING or obj != other: raise ValueError(f'geometry_mismatch_at_source_{total}')
            retained += 1
    if next(projected_geometry, MISSING) is not MISSING: raise ValueError('unexpected_projected_geometry')
    if total != sf['geometry']['items'] or retained != pf['geometry']['items']:
        raise ValueError('geometry_count_mismatch')
    if sha(source) != source_hash or sha(projected) != projected_hash: raise ValueError('reports_changed_during_audit')
    result = {'status': 'passed', 'method': 'independent Python streaming JSON object equality and independent title-path selection',
              'source_sha256': source_hash, 'projection_sha256': projected_hash,
              'all_text_objects_equal': counts['texts'], 'all_issue_objects_equal': counts['issues'],
              'original_geometry_objects': total, 'matching_geometry_objects_equal': retained,
              'anchor_paths': sum(map(len, anchors.values())), 'metadata_equal': ['sourceFile','sourceSha256','success','summary','numericSanitization'],
              'source_and_projection_unchanged_during_audit': True}
    Path(a.output).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False))

if __name__ == '__main__': main()
