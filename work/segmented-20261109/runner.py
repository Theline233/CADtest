"""Guarded CAD runner. DWG-only selections and optional paired XLSX are supported.
Reference files are archived/hashed, never parsed for prediction.

Native binaries must implement '<dwg> <folder> --wait-for-go --recognition-mode'
and block on stdin until the exact line GO, before opening the DWG.
Exit codes: 0 all samples passed, 1 sample failures, 2 batch rejected/blocked.
"""
import argparse
from contextlib import contextmanager
import ctypes
from ctypes import wintypes
from dataclasses import asdict, dataclass
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
from workspace_paths import workspace_root, node_executable

ROOT = workspace_root()
NODE = os.environ.get('CADTEST_NODE') or shutil.which('node') or 'node'
MIB = 1024 ** 2
LOCK = Path('work/batch-validation-20261106/runner.lock')
APP = Path(__file__).resolve().parent
APP_REL = APP.relative_to(ROOT)
PREDICT = APP_REL / 'batch-fix-20261106/predict.mjs'
EXTRACTOR = APP_REL / 'extract-requirement-frames.mjs'
NODE_GATE = APP_REL / 'node-gate.mjs'
PYTHON_GATE = APP_REL / 'python-gate.py'
PREPARE_REVIEW = APP_REL / 'prepare_review.py'
INDEPENDENT_AUDIT = APP_REL / 'independent_projection_audit.py'
# The projector's title/path geometry contract was reviewed against this exact
# extractor. A rule change must be re-reviewed and differentially tested first.
EXTRACTOR_CONTRACT_SHA256 = 'B4AAF735FCB31E4B8C9F7E845B85DE93E3135092F976F053CDF75BED9834F327'
POLL_SECONDS = .05


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def save(path, value):
    path = Path(path)
    temp = path.with_name(path.name + '.tmp')
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding='utf-8')
    temp.replace(path)


def local_path(value, root):
    path = (root / value).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError(f'Path escapes workspace: {value}')
    return path


@dataclass(frozen=True)
class Limits:
    max_files: int = 20  # Selected DWGs, optionally paired with an XLSX.
    max_file_mib: float = 64
    max_batch_mib: float = 256  # DWG + XLSX bytes, including repeated selections.
    memory_mib: float = 3072
    timeout_seconds: float = 300
    cpu_percent: float = 25

    def validate(self):
        if type(self.max_files) is not int or not 1 <= self.max_files <= 10000:
            raise ValueError('max_files must be an integer in [1, 10000]')
        for name, low, high in [('max_file_mib', 1 / MIB, 4096),
                                ('max_batch_mib', 1 / MIB, 16384),
                                ('memory_mib', 32, 16384),
                                ('timeout_seconds', .05, 3600),
                                ('cpu_percent', 1, 100)]:
            value = getattr(self, name)
            if not math.isfinite(value) or not low <= value <= high:
                raise ValueError(f'{name} must be finite and in [{low}, {high}]')


def prepare_output(value, root=ROOT, *, require_new=False):
    out = local_path(value, root)
    output_root = (root / 'outputs').resolve()
    if out == output_root or not out.is_relative_to(output_root):
        raise ValueError(f'--output must be a new directory strictly below {output_root}')
    # Drawing entry must claim a brand-new directory, including when it is empty.
    # Keep pre-staged selection/reference support for existing paired callers.
    if require_new and out.exists():
        raise ValueError('Output already exists; use a new output directory')
    if out.exists() and (not out.is_dir() or any(
            p.name not in {'selection.json', 'excel-reference.json'} for p in out.iterdir())):
        raise ValueError('Output contains previous results or other files; use a new output directory')
    out.mkdir(parents=True, exist_ok=not require_new)
    with (out / 'admission.json').open('x', encoding='utf-8') as claim:
        json.dump({'status': 'checking', 'concurrency': 1}, claim)
    return out


@contextmanager
def workspace_lock(root=ROOT):
    if os.name != 'nt':
        raise RuntimeError('Windows Job Object and byte-range lock are required')
    import msvcrt
    # Exactly the old runner's path, byte and locking mode; OS releases on exit.
    lock_path = local_path(LOCK, root)
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    with lock_path.open('a+b') as stream:
        if stream.seek(0, 2) == 0:
            stream.write(b'0')
            stream.flush()
        stream.seek(0)
        try:
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as exc:
            raise RuntimeError('workspace_busy: another old/new batch runner owns runner.lock') from exc
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def input_kinds(item):
    # Presence, not truthiness: null/empty XLSX and orphaned hashes must be rejected.
    return ('dwg', 'xlsx') if 'xlsx' in item or 'xlsx_sha256' in item else ('dwg',)


def inspect_selection(selection, limits, root=ROOT, *, verify_hashes=True):
    """Reject the whole selection, without filtering or starting any process."""
    limits.validate()
    selected = selection.get('selected') if isinstance(selection, dict) else None
    errors, sizes, seen = [], [], set()
    if not isinstance(selected, list) or not selected:
        return {'status': 'rejected', 'reasons': [{'code': 'selected_must_be_nonempty_list'}]}
    if len(selected) > limits.max_files:
        errors.append({'code': 'too_many_files', 'actual': len(selected), 'limit': limits.max_files})
    for item in selected:
        sid = item.get('id') if isinstance(item, dict) else None
        if (not isinstance(sid, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,63}', sid)
                or re.fullmatch(r'CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9]', sid, re.I)):
            errors.append({'code': 'invalid_sample_id', 'id': sid})
            continue
        if sid.casefold() in seen:
            errors.append({'code': 'duplicate_sample_id', 'id': sid})
        seen.add(sid.casefold())
        for kind in input_kinds(item):
            try:
                value = item[kind]
                if not isinstance(value, str) or not Path(value).is_absolute():
                    raise ValueError('input path must be absolute')
                path = local_path(value, root)
                if path.suffix.lower() != f'.{kind}' or not path.is_file():
                    raise ValueError(f'input must be an existing .{kind} file')
                digest = item[f'{kind}_sha256']
                if not isinstance(digest, str) or not re.fullmatch(r'[a-fA-F0-9]{64}', digest):
                    raise ValueError('expected SHA-256 must contain 64 hex characters')
                size = path.stat().st_size
                sizes.append({'id': sid, 'kind': kind, 'bytes': size})
                if size > int(limits.max_file_mib * MIB):
                    errors.append({'code': 'file_too_large', 'id': sid, 'kind': kind,
                                   'bytes': size, 'limit_bytes': int(limits.max_file_mib * MIB)})
                if size == 0:
                    errors.append({'code': 'empty_input', 'id': sid, 'kind': kind})
            except (OSError, ValueError, TypeError, KeyError) as exc:
                errors.append({'code': 'invalid_input', 'id': sid, 'kind': kind, 'detail': str(exc)})
    total = sum(s['bytes'] for s in sizes)
    if total > int(limits.max_batch_mib * MIB):
        errors.append({'code': 'batch_too_large', 'bytes': total,
                       'limit_bytes': int(limits.max_batch_mib * MIB)})
    # Do not hash inadmissibly large files. Hash every admitted input before launch.
    if not errors and verify_hashes:
        for item in selected:
            for kind in input_kinds(item):
                try:
                    if sha(item[kind]) != item[f'{kind}_sha256'].upper():
                        raise ValueError('SHA-256 differs from selection')
                except (OSError, ValueError) as exc:
                    errors.append({'code': 'input_hash_mismatch', 'id': item['id'],
                                   'kind': kind, 'detail': str(exc)})
    return {'status': 'rejected' if errors else 'admitted', 'concurrency': 1,
            'limits': asdict(limits), 'selected_count': len(selected),
            'size_scope': 'Each supplied DWG and XLSX; batch sums supplied inputs; max_files counts selected DWGs.',
            'input_sizes': sizes, 'total_input_bytes': total, 'reasons': errors}


def copy_verified(source, target):
    before = sha(source)
    target.parent.mkdir(parents=True, exist_ok=True)
    if source.resolve() != target.resolve():
        if target.exists():
            raise ValueError(f'Refusing to overwrite staged file: {target}')
        shutil.copy2(source, target)
    if sha(source) != before or sha(target) != before:
        raise ValueError(f'File changed while freezing: {source}')
    return before


def freeze_sources(out, root=ROOT):
    frozen = out / 'frozen-source'
    frozen.mkdir()
    if sha(root / EXTRACTOR) != EXTRACTOR_CONTRACT_SHA256:
        raise ValueError('Extractor changed; review and retest projection contract before running')
    native = root / APP_REL / 'native'
    native_source = root / APP_REL / 'native-source'
    if not (native / 'CADtest.exe').is_file() or not (native / 'CADtest.dll').is_file():
        raise ValueError('Backend programs missing. Run Build.ps1 first (native must support --wait-for-go).')
    sources = {p: Path('src') / p.name for p in native_source.glob('*.cs')}
    sources.update({p: Path('src') / p.name for p in native_source.glob('*.csproj')})
    if not list(native_source.glob('*.cs')) or not list(native_source.glob('*.csproj')):
        raise ValueError('Missing C# sources/project')
    sources.update({p: Path('native') / p.relative_to(native) for p in native.rglob('*') if p.is_file()})
    lockfile = native_source / 'packages.lock.json'
    if lockfile.exists():
        sources[lockfile] = Path('src/packages.lock.json')
    # Keep relative directories so predict's quantity imports still resolve.
    pending = [root / EXTRACTOR, root / PREDICT, root / NODE_GATE]
    imports = re.compile(r'''(?:\bfrom\s*|\bimport\s*(?:\(\s*)?|\brequire\s*\(\s*)['"]([^'"]+)['"]''')
    while pending:
        path = local_path(pending.pop(), root)
        if path in sources:
            continue
        sources[path] = path.relative_to(root)
        for specifier in imports.findall(path.read_text(encoding='utf-8-sig')):
            if specifier.startswith('.'):
                pending.append(local_path(path.parent / specifier, root))
            elif not specifier.startswith('node:'):
                raise ValueError(f'Unfrozen module dependency {specifier} in {path.name}')
    # Freeze every executable, transitive managed dependency and review script
    # used during the run; the frozen copies are the ones actually executed.
    for directory, dest in [('projector/bin/Release/net10.0', 'projector'),
                            ('review-native', 'review-native'),
                            ('projector', 'projector-source'),
                            ('review-source', 'review-source')]:
        base = root / APP_REL / directory
        files = list(base.rglob('*')) if directory in ('projector/bin/Release/net10.0', 'review-native') else list(base.glob('*'))
        for p in files:
            if p.is_file(): sources[p] = Path(dest) / p.relative_to(base)
    for p in (root / PYTHON_GATE, root / PREPARE_REVIEW, root / INDEPENDENT_AUDIT,
              root / APP_REL / 'runner.py', root / APP_REL / 'parallel_runner.py',
              root / APP_REL / 'workspace_paths.py'):
        sources[p] = p.relative_to(root)
    if not (root / APP_REL / 'projector/bin/Release/net10.0/Projector.exe').is_file():
        raise ValueError('Projector executable missing')
    if not (root / APP_REL / 'review-native/CadReviewWorkflow.exe').is_file():
        raise ValueError('Review executable missing')
    manifest = {}
    for source, relative in sorted(sources.items()):
        local_path(source, root)  # Reject symlinks/junctions escaping the workspace.
        digest = copy_verified(source, frozen / relative)
        manifest[str(relative)] = {'source': str(source), 'sha256': digest}
    # A simultaneous rebuild/edit must not silently mix versions in the snapshot.
    for entry in manifest.values():
        if sha(entry['source']) != entry['sha256']:
            raise ValueError(f'Source changed during snapshot: {entry["source"]}')
    save(frozen / 'manifest.json', manifest)
    return frozen, manifest


def verify_frozen(frozen, manifest):
    for relative, entry in manifest.items():
        if sha(frozen / relative) != entry['sha256']:
            raise ValueError(f'Frozen runtime changed: {relative}')


class MemoryCounters(ctypes.Structure):
    _fields_ = [('cb', wintypes.DWORD), ('PageFaultCount', wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in ('PeakWorkingSetSize', 'WorkingSetSize',
        'QuotaPeakPagedPoolUsage', 'QuotaPagedPoolUsage', 'QuotaPeakNonPagedPoolUsage',
        'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage')]


class BasicLimits(ctypes.Structure):
    _fields_ = [('PerProcessUserTimeLimit', ctypes.c_longlong), ('PerJobUserTimeLimit', ctypes.c_longlong),
               ('LimitFlags', wintypes.DWORD), ('MinimumWorkingSetSize', ctypes.c_size_t),
               ('MaximumWorkingSetSize', ctypes.c_size_t), ('ActiveProcessLimit', wintypes.DWORD),
               ('Affinity', ctypes.c_size_t), ('PriorityClass', wintypes.DWORD), ('SchedulingClass', wintypes.DWORD)]


class IoCounters(ctypes.Structure):
    _fields_ = [(name, ctypes.c_ulonglong) for name in ('ReadOperationCount', 'WriteOperationCount',
                'OtherOperationCount', 'ReadTransferCount', 'WriteTransferCount', 'OtherTransferCount')]


class ExtendedLimits(ctypes.Structure):
    _fields_ = [('BasicLimitInformation', BasicLimits), ('IoInfo', IoCounters)] + [
        (name, ctypes.c_size_t) for name in ('ProcessMemoryLimit', 'JobMemoryLimit',
                                          'PeakProcessMemoryUsed', 'PeakJobMemoryUsed')]


class CompletionPort(ctypes.Structure):
    _fields_ = [('CompletionKey', ctypes.c_void_p), ('CompletionPort', wintypes.HANDLE)]


class CpuRateLimits(ctypes.Structure):
    _fields_ = [('ControlFlags', wintypes.DWORD), ('CpuRate', wintypes.DWORD)]


def check(ok):
    if not ok:
        raise ctypes.WinError(ctypes.get_last_error())


class WindowsJob:
    """No worker threads: poll memory and Job Object limit notifications together."""
    def __init__(self, limit_bytes, cpu_percent=25):
        if os.name != 'nt':
            raise RuntimeError('Windows resource guards unavailable')
        self.kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        self.psapi = ctypes.WinDLL('psapi', use_last_error=True)
        signatures = {
            'CreateJobObjectW': ([ctypes.c_void_p, wintypes.LPCWSTR], wintypes.HANDLE),
            'CreateIoCompletionPort': ([wintypes.HANDLE, wintypes.HANDLE, ctypes.c_size_t, wintypes.DWORD], wintypes.HANDLE),
            'SetInformationJobObject': ([wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD], wintypes.BOOL),
            'QueryInformationJobObject': ([wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p], wintypes.BOOL),
            'AssignProcessToJobObject': ([wintypes.HANDLE, wintypes.HANDLE], wintypes.BOOL),
            'TerminateJobObject': ([wintypes.HANDLE, wintypes.UINT], wintypes.BOOL),
            'CloseHandle': ([wintypes.HANDLE], wintypes.BOOL),
            'GetQueuedCompletionStatus': ([wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD),
                ctypes.POINTER(ctypes.c_size_t), ctypes.POINTER(ctypes.c_void_p), wintypes.DWORD], wintypes.BOOL)}
        for name, (args, result) in signatures.items():
            fn = getattr(self.kernel, name)
            fn.argtypes, fn.restype = args, result
        self.psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(MemoryCounters), wintypes.DWORD]
        self.psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
        self.handle = self.port = None
        try:
            self.handle = self.kernel.CreateJobObjectW(None, None)
            check(self.handle)
            self.port = self.kernel.CreateIoCompletionPort(wintypes.HANDLE(-1), None, 0, 1)
            check(self.port)
            port = CompletionPort(1, self.port)
            check(self.kernel.SetInformationJobObject(self.handle, 7, ctypes.byref(port), ctypes.sizeof(port)))
            limits = ExtendedLimits()
            limits.BasicLimitInformation.LimitFlags = 0x100 | 0x200 | 0x2000
            limits.ProcessMemoryLimit = limits.JobMemoryLimit = limit_bytes
            check(self.kernel.SetInformationJobObject(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)))
            verified = self.limits()
            if (verified.ProcessMemoryLimit != limit_bytes or verified.JobMemoryLimit != limit_bytes
                    or verified.BasicLimitInformation.LimitFlags & 0x2300 != 0x2300):
                raise RuntimeError('Job Object limit verification failed')
            # Percentage of total machine CPU across the whole Job, not one core.
            self.set_cpu_percent(cpu_percent)
        except BaseException:
            self.close()
            raise

    def limits(self):
        value = ExtendedLimits()
        check(self.kernel.QueryInformationJobObject(self.handle, 9, ctypes.byref(value), ctypes.sizeof(value), None))
        return value

    def set_cpu_percent(self, cpu_percent):
        cpu = CpuRateLimits(0x1 | 0x4, round(cpu_percent * 100))
        check(self.kernel.SetInformationJobObject(self.handle, 15, ctypes.byref(cpu), ctypes.sizeof(cpu)))
        actual_cpu = CpuRateLimits()
        check(self.kernel.QueryInformationJobObject(self.handle, 15, ctypes.byref(actual_cpu), ctypes.sizeof(actual_cpu), None))
        if actual_cpu.ControlFlags != cpu.ControlFlags or actual_cpu.CpuRate != cpu.CpuRate:
            raise RuntimeError('Job Object CPU limit verification failed')

    def assign(self, process):
        check(self.kernel.AssignProcessToJobObject(self.handle, wintypes.HANDLE(int(process._handle))))

    def memory(self, process):
        value = MemoryCounters()
        value.cb = ctypes.sizeof(value)
        check(self.psapi.GetProcessMemoryInfo(wintypes.HANDLE(int(process._handle)), ctypes.byref(value), value.cb))
        return value

    def memory_limit_hit(self):
        hit = False
        while True:
            message, key, overlapped = wintypes.DWORD(), ctypes.c_size_t(), ctypes.c_void_p()
            if not self.kernel.GetQueuedCompletionStatus(self.port, ctypes.byref(message), ctypes.byref(key), ctypes.byref(overlapped), 0):
                if ctypes.get_last_error() == 258:  # WAIT_TIMEOUT: no queued event.
                    return hit
                check(False)
            hit |= message.value in (9, 10)  # PROCESS_MEMORY_LIMIT / JOB_MEMORY_LIMIT

    def terminate(self):
        check(self.kernel.TerminateJobObject(self.handle, 137))

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)  # KILL_ON_JOB_CLOSE includes descendants.
            self.handle = None
        if self.port:
            self.kernel.CloseHandle(self.port)
            self.port = None


def skipped(reason):
    return {'status': 'blocked', 'blocked_reason': reason, 'returncode': None,
            'elapsed_seconds': 0, 'peak_rss_mib': 0, 'timeout': False, 'memory_limit_exceeded': False}


def command(args, folder, name, limits, *, wait_for_go=False, cwd=ROOT, control=None):
    limits.validate()
    start, process, job = time.perf_counter(), None, None
    result = {**skipped('not_started'), 'command': [str(a) for a in args],
              'guards_installed': False, 'input_gate_released': False, 'peak_commit_mib': 0}
    try:
        job = WindowsJob(int(limits.memory_mib * MIB), limits.cpu_percent)
        if control is not None:
            control.register(job, name, result)
            if control.cancelled():
                result.update(status='failed', returncode=1, blocked_reason='cancelled')
                return result
        env = os.environ.copy()
        env.update({'DOTNET_DbgEnableMiniDump': '0', 'COMPlus_DbgEnableMiniDump': '0',
                    'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'PYTHONDONTWRITEBYTECODE': '1',
                    'PYTHONUTF8': '1', 'PYTHONIOENCODING': 'utf-8',
                    'CADTEST_ROOT': str(Path(cwd).resolve())})
        with (folder / f'{name}.stdout.log').open('wb') as stdout, (folder / f'{name}.stderr.log').open('wb') as stderr:
            process = subprocess.Popen(result['command'], cwd=cwd, env=env, stdin=subprocess.PIPE if wait_for_go else subprocess.DEVNULL,
                                       stdout=stdout, stderr=stderr, creationflags=subprocess.CREATE_NO_WINDOW | subprocess.BELOW_NORMAL_PRIORITY_CLASS)
            result['child_pid'] = process.pid
            job.assign(process)
            sample = job.memory(process)  # Fail closed before releasing native gate.
            result['guards_installed'] = True
            result['blocked_reason'] = None
            pre_go_failed = False
            if wait_for_go:
                # A queued limit event or an expired deadline must be observed
                # before releasing the worker to open a drawing or workbook.
                if control is not None and control.cancelled():
                    result.update(blocked_reason='cancelled')
                    pre_go_failed = True
                elif job.memory_limit_hit() or sample.PeakWorkingSetSize > limits.memory_mib * MIB:
                    result.update(memory_limit_exceeded=True, blocked_reason='memory_limit_exceeded')
                    pre_go_failed = True
                elif time.perf_counter() - start >= limits.timeout_seconds:
                    result.update(timeout=True, blocked_reason='timeout')
                    pre_go_failed = True
                if pre_go_failed:
                    job.terminate()
                else:
                    process.stdin.write(b'GO\n')
                    process.stdin.flush()
                    process.stdin.close()
                    result['input_gate_released'] = True
            while True:
                result['peak_rss_mib'] = max(result['peak_rss_mib'], sample.PeakWorkingSetSize / MIB)
                result['peak_commit_mib'] = max(result['peak_commit_mib'], sample.PeakPagefileUsage / MIB)
                if pre_go_failed:
                    break
                if control is not None and control.cancelled():
                    result.update(blocked_reason='cancelled')
                    job.terminate()
                    break
                if job.memory_limit_hit() or sample.PeakWorkingSetSize > limits.memory_mib * MIB:
                    result.update(memory_limit_exceeded=True, blocked_reason='memory_limit_exceeded')
                    job.terminate()
                    break
                if process.poll() is not None:
                    break
                if time.perf_counter() - start >= limits.timeout_seconds:
                    result.update(timeout=True, blocked_reason='timeout')
                    job.terminate()
                    break
                time.sleep(POLL_SECONDS)
                try:
                    sample = job.memory(process)
                except OSError:
                    if process.poll() is None:
                        raise
            process.wait(timeout=5)
            result['peak_commit_mib'] = max(result['peak_commit_mib'], job.limits().PeakJobMemoryUsed / MIB)
            if job.memory_limit_hit():
                result.update(memory_limit_exceeded=True, blocked_reason='memory_limit_exceeded')
            result['process_returncode'] = process.returncode
            result['returncode'] = process.returncode or (1 if result['blocked_reason'] else 0)
            result['status'] = 'passed' if result['returncode'] == 0 else 'failed'
            if result['returncode'] and not result['blocked_reason']:
                result['blocked_reason'] = 'process_exit_nonzero'
    except Exception as exc:
        result.update(status='blocked', returncode=1, blocked_reason='guard_or_launch_error', error=str(exc))
    finally:
        if control is not None and job:
            control.unregister(job)
        if job:
            job.close()
        if process:
            if process.stdin and not process.stdin.closed:
                try:
                    process.stdin.close()
                except OSError:
                    pass  # A child that exits before GO must still be reaped.
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
            result.setdefault('process_returncode', process.returncode)
        result['elapsed_seconds'] = round(time.perf_counter() - start, 3)
        result['peak_rss_mib'] = round(result['peak_rss_mib'], 3)
        result['peak_commit_mib'] = round(result['peak_commit_mib'], 3)
    return result


def stage_commands(frozen, item, folder, node, recognition_mode=True):
    native = [frozen / 'native/CADtest.exe', item['dwg'], folder, '--wait-for-go']
    if recognition_mode:
        native.append('--recognition-mode')
    raw = folder / 'cad-read-report.json'
    profile = folder / 'profile.json'
    projected = folder / 'cad-read-projection.json'
    projection_profile = folder / 'projection-profile.json'
    audit = folder / 'projection-audit.json'
    fields = folder / 'frame-field-candidates.json'
    predictions = folder / 'predictions.json'
    projector = frozen / 'projector/Projector.exe'
    return [
        ('native', native, [Path(item['dwg'])], [raw]),
        ('profile', [projector, 'profile', raw, profile, folder / 'unused.json', '--wait-for-go'], [raw], [profile]),
        ('projection', [projector, 'project', raw, profile, projected, '--wait-for-go'], [raw, profile],
         [projected, folder / 'cad-read-projection.json.projection.json']),
        ('projection_profile', [projector, 'profile', projected, projection_profile, folder / 'unused.json', '--wait-for-go'],
         [projected], [projection_profile]),
        ('projection_audit', [sys.executable, '-B', frozen / INDEPENDENT_AUDIT, '--source', raw, '--profile', profile,
         '--projected', projected, '--projected-profile', projection_profile, '--output', audit, '--wait-for-go'],
         [raw, profile, projected, projection_profile], [audit]),
        ('fields', [node, frozen / NODE_GATE, frozen / EXTRACTOR, folder, projected], [projected], [fields]),
        ('prediction', [node, frozen / NODE_GATE, frozen / PREDICT, fields, predictions], [fields], [predictions]),
    ]


def input_hashes(item):
    values, errors = {}, []
    for kind in input_kinds(item):
        try:
            values[kind] = sha(item[kind])
        except (OSError, ValueError, TypeError, KeyError) as exc:
            values[kind] = None
            errors.append(f'{kind}: {exc}')
    return values, errors


def run_samples(selected, out, frozen, manifest, limits, node=NODE, recognition_mode=True, root=ROOT, control=None):
    results = []
    (out / 'samples').mkdir()
    save(out / 'runs.json', results)
    for item in selected:
        folder = out / 'samples' / item['id']
        folder.mkdir()  # Never reopen an existing sample result.
        run = {'id': item['id'], 'status': 'blocked', 'blocked_reason': 'not_started',
               **{stage: skipped('upstream_not_completed') for stage in ('native', 'profile', 'projection', 'projection_profile', 'projection_audit', 'fields', 'prediction')}}
        expected = {kind: item[f'{kind}_sha256'].upper() for kind in input_kinds(item)}
        interrupted = False
        try:
            run['before_sha256'], errors = input_hashes(item)
            if errors or run['before_sha256'] != expected:
                raise ValueError('input_changed_before_sample')
            print(f"{item['id']}: started (serial, guarded)", flush=True)
            known = {str(Path(item['dwg'])): expected['dwg']}
            for name, args, inputs, artifacts in stage_commands(frozen, item, folder, node, recognition_mode):
                if control is not None and control.cancelled():
                    run.update(status='failed', blocked_reason='cancelled')
                    break
                verify_frozen(frozen, manifest)
                before = {str(p): sha(p) for p in inputs}
                if any(known.get(path) != digest for path, digest in before.items()):
                    raise ValueError(f'{name}: input differs from preceding stage')
                kwargs = {'wait_for_go': True, 'cwd': root}
                if control is not None:
                    kwargs['control'] = control
                stage = command(args, folder, name, limits, **kwargs)
                stage['input_sha256_before'] = before
                stage['input_sha256_after'] = {str(p): sha(p) for p in inputs}
                if stage['input_sha256_after'] != before:
                    stage.update(status='failed', returncode=1, blocked_reason='stage_input_changed')
                verify_frozen(frozen, manifest)
                run[name] = stage
                if stage['returncode'] == 0 and any(not p.is_file() for p in artifacts):
                    stage.update(status='failed', returncode=1, blocked_reason='expected_output_missing')
                if stage['status'] == 'passed':
                    stage['output_sha256'] = {str(p): sha(p) for p in artifacts}
                    known.update(stage['output_sha256'])
                save(folder / 'run.json', run)
                if stage['status'] != 'passed':
                    for artifact in artifacts:
                        if not artifact.exists(): continue
                        # A failed serializer may leave truncated JSON. Preserve it
                        # outside the names old compare.py treats as valid output.
                        artifact.rename(artifact.with_name(artifact.name + '.failed'))
                    run.update(status='failed', blocked_reason=f"{name}: {stage['blocked_reason']}")
                    break
            else:
                run.update(status='passed', blocked_reason=None)
        except KeyboardInterrupt:
            run.update(status='blocked', blocked_reason='interrupted')
            interrupted = True
        except Exception as exc:
            run.update(status='blocked', blocked_reason=str(exc))
        finally:
            run['after_sha256'], errors = input_hashes(item)
            run['inputs_unchanged'] = not errors and run.get('before_sha256') == run['after_sha256'] == expected
            if not run['inputs_unchanged']:
                run.update(status='blocked', blocked_reason='input_integrity_failure', input_errors=errors)
                # Preserve untrusted evidence, but old compare.py must not score it.
                if (folder / 'predictions.json').exists():
                    (folder / 'predictions.json').rename(folder / 'predictions.untrusted.json')
                    run['prediction'].update(status='blocked', returncode=1, blocked_reason='input_integrity_failure')
            for stage in ('native', 'profile', 'projection', 'projection_profile', 'projection_audit', 'fields', 'prediction'):
                if run[stage]['returncode'] is None:
                    run[stage]['blocked_reason'] = run['blocked_reason']
            save(folder / 'run.json', run)
            results.append(run)
            save(out / 'runs.json', results)
        print(f"{item['id']}: {run['status']} ({run['blocked_reason'] or 'all stages completed'})", flush=True)
        if interrupted:
            raise KeyboardInterrupt
    return results


def prepare_review(out, frozen, manifest, limits, root, control=None):
    """Keep the shared lock and per-stage Job while creating the review workbook."""
    review = out.parent / 'review'
    logs = out.parent / 'review-logs'
    review.mkdir()
    logs.mkdir()
    dataset = review / 'input-dataset.json'
    workbook = review / '图纸复核.xlsx'
    status = {'status': 'failed', 'stages': {}}
    status_file = out.parent / 'review-status.json'
    try:
        verify_frozen(frozen, manifest)
        kwargs = {'wait_for_go': True, 'cwd': root}
        if control is not None:
            kwargs['control'] = control
        stage = command([sys.executable, '-B', frozen / PYTHON_GATE, frozen / PREPARE_REVIEW,
                         '--run', out, '--output', dataset], logs, 'dataset', limits, **kwargs)
        status['stages']['dataset'] = stage
        if stage['status'] != 'passed' or not dataset.is_file():
            status['blocked_reason'] = 'dataset: ' + str(stage['blocked_reason'] or 'expected_output_missing')
            return False
        dataset_hash = sha(dataset)
        stage['output_sha256'] = {str(dataset): dataset_hash}
        verify_frozen(frozen, manifest)
        stage = command([frozen / 'review-native/CadReviewWorkflow.exe', 'prepare', '--input', dataset,
                         '--output', review, '--wait-for-go'], logs, 'workbook', limits, **kwargs)
        status['stages']['workbook'] = stage
        if sha(dataset) != dataset_hash:
            stage.update(status='failed', returncode=1, blocked_reason='dataset_changed_during_prepare')
        verify_frozen(frozen, manifest)
        if stage['status'] != 'passed' or not workbook.is_file() or not (review / 'source.json').is_file():
            status['blocked_reason'] = 'workbook: ' + str(stage['blocked_reason'] or 'expected_output_missing')
            return False
        stage['input_sha256_before'] = stage['input_sha256_after'] = {str(dataset): dataset_hash}
        stage['output_sha256'] = {str(p): sha(p) for p in (workbook, review / 'source.json')}
        status.update(status='passed', blocked_reason=None)
        print('复核表：' + str(workbook), flush=True)
        return True
    finally:
        save(status_file, status)


def parser():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument('--output', required=True, help='Fresh outputs/ directory; no prior results permitted')
    inputs = cli.add_mutually_exclusive_group()
    inputs.add_argument('--selection', help='Selection JSON; defaults to output/selection.json')
    inputs.add_argument('--drawing', nargs='+', help='Workspace DWG paths (absolute or relative to workspace); generate selection.json in a new output directory')
    inputs.add_argument('--parallel-batch', help='JSON manifest for independent drawing tasks under a single workspace lock')
    cli.add_argument('--reference', help='Archive as excel-reference.json without parsing its contents')
    cli.add_argument('--node', help='Node.js executable; otherwise CADTEST_NODE, bundled runtime, then PATH')
    cli.add_argument('--concurrency', type=int, choices=[1], default=1)
    cli.add_argument('--max-files', type=int, default=20, help='Maximum number of selected DWGs')
    cli.add_argument('--max-file-mib', type=float, default=64)
    cli.add_argument('--max-batch-mib', type=float, default=256)
    cli.add_argument('--memory-mib', type=float, default=3072)
    cli.add_argument('--timeout', '--timeout-seconds', dest='timeout_seconds', type=float, default=300)
    cli.add_argument('--cpu-percent', type=float, default=25)
    cli.add_argument('--max-parallel', type=int, default=0, help='Parallel task limit: 0 for automatic, otherwise 1..6')
    cli.add_argument('--cancel-file', help='Cancellation marker inside the fresh batch output directory')
    cli.add_argument('--prepare-review', action='store_true', help='Prepare the review workbook under the same lock and guard')
    cli.add_argument('--recognition-mode', action='store_true', default=True,
                     help='Always enabled for this runner; skips native diagnostic display/tessellation')
    return cli


def main(argv=None, root=ROOT):
    args = parser().parse_args(argv)
    if args.parallel_batch:
        import parallel_runner
        return parallel_runner.main(args, root)
    try:
        out = prepare_output(args.output, root, require_new=args.drawing is not None)
    except (OSError, ValueError) as exc:
        print(f'Batch rejected: {exc}', flush=True)
        return 2
    admission = {'status': 'blocked', 'concurrency': 1, 'reasons': []}
    try:
        with workspace_lock(root):
            limits = Limits(args.max_files, args.max_file_mib, args.max_batch_mib, args.memory_mib, args.timeout_seconds, args.cpu_percent)
            limits.validate()
            if args.drawing is not None:
                # Cheap path/count/size checks precede hashing. Placeholder hashes
                # exist only in memory; the saved selection always has real SHAs.
                selection = {'selected': [
                    {'id': f'D{index:03d}', 'dwg': str(local_path(value, root)), 'dwg_sha256': '0' * 64}
                    for index, value in enumerate(args.drawing, start=1)]}
                preflight = inspect_selection(selection, limits, root, verify_hashes=False)
                if preflight['status'] != 'admitted':
                    admission = preflight
                    save(out / 'admission.json', admission)
                    print('Batch rejected; see admission.json. No sample was started.', flush=True)
                    return 2
                for item in selection['selected']:
                    item['dwg'] = str(local_path(item['dwg'], root))
                    item['dwg_sha256'] = sha(item['dwg'])
                save(out / 'selection.json', selection)
            source = local_path(args.selection or out / 'selection.json', root)
            selection_sha = copy_verified(source, out / 'selection.json')
            # Only the selection JSON is parsed. Reference JSON is opaque archival data.
            selection = json.loads((out / 'selection.json').read_text(encoding='utf-8-sig'))
            if sha(out / 'selection.json') != selection_sha:
                raise ValueError('Selection changed while reading')
            reference_sha = None
            if args.reference or (out / 'excel-reference.json').exists():
                reference = local_path(args.reference or out / 'excel-reference.json', root)
                if reference.suffix.lower() != '.json':
                    raise ValueError('--reference must be a JSON file')
                reference_sha = copy_verified(reference, out / 'excel-reference.json')
            admission = inspect_selection(selection, limits, root)
            save(out / 'admission.json', admission)
            if admission['status'] != 'admitted':
                print('Batch rejected; see admission.json. No sample was started.', flush=True)
                return 2
            node = node_executable(args.node, root)
            frozen, manifest = freeze_sources(out, root)
            frozen_manifest_sha = sha(frozen / 'manifest.json')
            harness_sha = sha(__file__)
            python_sha = sha(sys.executable)
            node_sha = sha(node)
            save(out / 'protocol.json', {'concurrency': 1, 'limits': asdict(limits),
                'selection_sha256': selection_sha, 'reference_sha256': reference_sha,
                'native_dll_sha256': sha(frozen / 'native/CADtest.dll'),
                'code_sha256': {v['source']: v['sha256'] for v in manifest.values()},
                'frozen_manifest_sha256': frozen_manifest_sha,
                'extractor_projection_contract_sha256': EXTRACTOR_CONTRACT_SHA256,
                'harness_sha256': harness_sha, 'python': sys.executable, 'python_sha256': python_sha,
                'node': str(node), 'node_sha256': node_sha,
                'recognition_mode': args.recognition_mode,
                'memory_monitor': 'Windows Job Object process + total job commit caps, KILL_ON_JOB_CLOSE; working-set polling',
                'poll_interval_seconds': POLL_SECONDS, 'stage_gate': 'stdin GO after job assignment and memory/CPU limit verification',
                'comparison_reads_excel_only_after_predictions': True,
                'reference_policy': 'Only copy and SHA-256; reference contents are never passed to prediction',
                'scope': 'Recognition only; no quotation workbook export or automatic comparison'})
            runs = run_samples(selection['selected'], out, frozen, manifest, limits, node, args.recognition_mode, root)
            passed = sum(r['status'] == 'passed' for r in runs)
            save(out / 'batch-status.json', {'status': 'passed' if passed == len(runs) else 'failed',
                'selected_count': len(runs), 'passed_count': passed, 'failed_or_blocked_count': len(runs) - passed})
            if (sha(__file__) != harness_sha or sha(sys.executable) != python_sha or sha(node) != node_sha
                    or sha(frozen / 'manifest.json') != frozen_manifest_sha):
                raise ValueError('runtime_or_manifest_changed_during_recognition')
            verify_frozen(frozen, manifest)
            if passed != len(runs): return 1
            if args.prepare_review:
                if not prepare_review(out, frozen, manifest, limits, root): return 1
            return 0
    except (Exception, KeyboardInterrupt) as exc:
        reason = 'interrupted' if isinstance(exc, KeyboardInterrupt) else str(exc)
        if admission['status'] != 'admitted':
            admission.update(status='blocked', reasons=[{'code': 'batch_preflight_error', 'detail': reason}])
            save(out / 'admission.json', admission)
        save(out / 'batch-status.json', {'status': 'blocked', 'blocked_reason': reason})
        print(f'Batch blocked: {reason}', flush=True)
        return 130 if isinstance(exc, KeyboardInterrupt) else 2


if __name__ == '__main__':
    raise SystemExit(main())
