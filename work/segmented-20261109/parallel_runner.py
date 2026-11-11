"""Run independent CAD tasks under one shared lock and bounded Windows Jobs.

The public entry is runner.py --parallel-batch. No drawing or review is ever
processed by this coordinator itself: the existing guarded stages are reused.
"""
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
import ctypes
from ctypes import wintypes
from dataclasses import asdict
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import re
import shutil
import signal
import sys
import threading
import time

import runner

MIB = runner.MIB
RESERVE_MIB = 3072
MAX_TASKS = 6
IDLE_WAIT_SECONDS = 15
POLL_SECONDS = 0.25


def timestamp():
    return datetime.now(timezone.utc).isoformat(timespec='milliseconds')


class MemoryStatus(ctypes.Structure):
    _fields_ = [('length', wintypes.DWORD), ('load', wintypes.DWORD)] + [
        (name, ctypes.c_ulonglong) for name in ('total_phys', 'avail_phys', 'total_pagefile',
            'avail_pagefile', 'total_virtual', 'avail_virtual', 'avail_extended_virtual')]


def memory_snapshot():
    info = MemoryStatus()
    info.length = ctypes.sizeof(info)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.GlobalMemoryStatusEx.argtypes = [ctypes.POINTER(MemoryStatus)]
    kernel.GlobalMemoryStatusEx.restype = wintypes.BOOL
    runner.check(kernel.GlobalMemoryStatusEx(ctypes.byref(info)))
    return {'available_physical_mib': info.avail_phys / MIB,
            'available_commit_mib': info.avail_pagefile / MIB,
            'total_physical_mib': info.total_phys / MIB}


def disk_reservation(drawing):
    return max(1024 * MIB, drawing.stat().st_size * 50)


def safe_parallel_capacity(cores, cpu_percent, maximum):
    # Do not silently exceed a user-specified total CPU budget. If one logical
    # core cannot fit, reject before any worker starts rather than violating it.
    cpu_budget = cpu_percent
    if cpu_budget + 1e-9 < 100.0 / max(1, cores):
        raise ValueError('CPU budget is below the share of one logical core')
    # Windows Job hard caps are expressed in whole hundredths of a percent,
    # and this runner's validated minimum is 1%; account for both that floor
    # and the one-logical-core share on machines with more than 100 CPUs.
    cpu_slots = max(1, min(math.floor(cores * cpu_budget / 100.0 + 1e-9),
                           math.floor(cpu_budget)))
    return min(MAX_TASKS, maximum or MAX_TASKS, max(1, cores // 2), cpu_slots), cpu_budget


class BatchState:
    def __init__(self, directory, tasks):
        self.path = directory / 'batch-status.json'
        self.lock = threading.RLock()
        self.data = {'status': 'checking', 'started_at': timestamp(), 'tasks': tasks,
                     'admissions': [], 'cpu_events': []}
        self.write()

    def write(self):
        with self.lock:
            runner.save(self.path, self.data)

    def update(self, **values):
        with self.lock:
            self.data.update(values)
            self.write()

    def task(self, task_id, **values):
        with self.lock:
            item = next(t for t in self.data['tasks'] if t['id'] == task_id)
            item.update(values)
            self.write()

    def event(self, name, value):
        with self.lock:
            self.data[name].append(value)
            self.write()


class Controls:
    """Keep all running Jobs' hard CPU limits within one batch budget."""
    def __init__(self, state, marker, cores, budget):
        self.state, self.marker, self.cores, self.budget = state, marker, cores, budget
        self.lock = threading.RLock()
        self.active = 0
        self.jobs = {}
        self.stop = threading.Event()
        self.error = None

    def cancelled(self):
        return self.stop.is_set() or self.marker.is_file()

    def share(self):
        count = max(1, self.active)
        # Round down so the aggregate of Job hard caps never exceeds the budget.
        return max(1.0, math.floor((self.budget / count) * 100) / 100)

    def set_active(self, count):
        with self.lock:
            self.active = count
            share = self.share()
            try:
                for job in self.jobs:
                    job.set_cpu_percent(share)
            except Exception as exc:
                self.error = 'CPU guard update failed: ' + str(exc)
                self.stop.set()
                raise
            self.state.event('cpu_events', {'at': timestamp(), 'active_tasks': count,
                'per_job_percent': share, 'total_budget_percent': self.budget})

    def for_task(self, task_id):
        return TaskControl(self, task_id)


class TaskControl:
    def __init__(self, controls, task_id):
        self.controls, self.task_id = controls, task_id

    def cancelled(self):
        return self.controls.cancelled()

    def register(self, job, stage, result):
        with self.controls.lock:
            share = self.controls.share()
            job.set_cpu_percent(share)
            self.controls.jobs[job] = self.task_id
            result['initial_cpu_percent'] = share
            self.controls.state.task(self.task_id, stage=stage, stage_started_at=timestamp(),
                                     cpu_percent=share)

    def unregister(self, job):
        with self.controls.lock:
            self.controls.jobs.pop(job, None)


def parse_manifest(args, root, batch_out, limits):
    path = runner.local_path(args.parallel_batch, root)
    if not path.is_file() or path.stat().st_size > MIB:
        raise ValueError('parallel manifest missing or exceeds 1 MiB')
    raw = path.read_bytes()
    document = json.loads(raw.decode('utf-8-sig'))
    if runner.sha(path) != __import__('hashlib').sha256(raw).hexdigest().upper():
        raise ValueError('parallel manifest changed during reading')
    tasks = document.get('tasks') if isinstance(document, dict) else None
    if not isinstance(tasks, list) or not 1 <= len(tasks) <= 20:
        raise ValueError('parallel manifest must contain 1 to 20 tasks')
    selected, outputs, seen = [], [], set()
    for entry in tasks:
        if not isinstance(entry, dict):
            raise ValueError('invalid parallel task')
        sid, drawing, output = entry.get('id'), entry.get('drawing'), entry.get('output')
        if (not isinstance(sid, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,63}', sid)
                or re.fullmatch(r'CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9]', sid, re.I)
                or sid.casefold() in seen):
            raise ValueError('invalid or repeated task id')
        seen.add(sid.casefold())
        if not isinstance(drawing, str) or not drawing.strip():
            raise ValueError('drawing path must be a nonempty workspace path')
        if not isinstance(output, str) or not output.strip():
            raise ValueError('task output path must be a nonempty workspace path')
        source = runner.local_path(drawing, root)
        target = runner.local_path(output, root)
        output_root = (root / 'outputs').resolve()
        if target == output_root or not target.is_relative_to(output_root) or target.exists():
            raise ValueError('each task output must be a new directory below outputs')
        if target == batch_out or batch_out.is_relative_to(target) or target.is_relative_to(batch_out):
            raise ValueError('task output overlaps the batch output')
        if any(target == other or target.is_relative_to(other) or other.is_relative_to(target) for other in outputs):
            raise ValueError('task output directories overlap')
        outputs.append(target)
        selected.append({'id': sid, 'dwg': str(source), 'dwg_sha256': '0' * 64})
    admission = runner.inspect_selection({'selected': selected}, limits, root, verify_hashes=False)
    if admission['status'] != 'admitted':
        raise ValueError('parallel selection rejected: ' + json.dumps(admission['reasons'], ensure_ascii=False))
    for item in selected:
        item['dwg_sha256'] = runner.sha(item['dwg'])
    admission = runner.inspect_selection({'selected': selected}, limits, root)
    if admission['status'] != 'admitted':
        raise ValueError('parallel selection rejected: ' + json.dumps(admission['reasons'], ensure_ascii=False))
    normalized = []
    for item, output in zip(selected, outputs):
        normalized.append({'id': item['id'], 'drawing': item['dwg'], 'output': str(output),
            'sha256': item['dwg_sha256'], 'bytes': Path(item['dwg']).stat().st_size,
            'status': 'queued', 'blocked_reason': None, 'attempts': []})
    return normalized, admission, runner.sha(path)


def is_memory_failure(recognition, review_status):
    for name in (recognition / 'samples' / 'D001' / 'run.json', review_status):
        try:
            value = json.loads(name.read_text(encoding='utf-8'))
        except (OSError, ValueError):
            continue
        stages = list(value.values()) + list(value.get('stages', {}).values())
        if any(isinstance(s, dict) and s.get('memory_limit_exceeded') is True for s in stages):
            return True
    # Some managed processes throw before a Job memory-limit notification; their
    # explicit out-of-memory stderr is still evidence, not a generic exit code.
    for folder in (recognition / 'samples' / 'D001', review_status.parent / 'review-logs'):
        if not folder.exists():
            continue
        for path in folder.glob('*.stderr.log'):
            try:
                with path.open('rb') as stream:
                    chunk = stream.read(131072).lower()
                if b'out of memory' in chunk or b'outofmemoryexception' in chunk:
                    return True
            except OSError:
                continue
    return False


def run_task(task, output, limit, node, root, template, manifest, template_sha,
             harness_sha, python_sha, node_sha, settings, controls, admission):
    started = timestamp()
    out = Path(output)
    control = controls.for_task(task['id'])
    try:
        if control.cancelled():
            return {'status': 'cancelled', 'reason': 'cancelled', 'output': str(out), 'started_at': started, 'finished_at': timestamp()}
        out.mkdir(parents=True, exist_ok=False)
        recognition = out / 'recognition'
        recognition.mkdir()
        selection = {'selected': [{'id': 'D001', 'dwg': task['drawing'], 'dwg_sha256': task['sha256']}]}
        runner.save(recognition / 'selection.json', selection)
        selection_sha = runner.sha(recognition / 'selection.json')
        shutil.copytree(template, recognition / 'frozen-source')
        frozen = recognition / 'frozen-source'
        if runner.sha(frozen / 'manifest.json') != template_sha:
            raise ValueError('copied frozen manifest differs from batch template')
        runner.verify_frozen(frozen, manifest)
        local_admission = runner.inspect_selection(selection, limit, root)
        local_admission.update(concurrency=admission['active_tasks_after'], parallel=True,
                               reserved_memory_mib=limit.memory_mib, admission=admission)
        runner.save(recognition / 'admission.json', local_admission)
        if local_admission['status'] != 'admitted':
            raise ValueError('task input failed admission')
        protocol = {'concurrency': admission['active_tasks_after'], 'limits': asdict(limit),
            'selection_sha256': selection_sha, 'reference_sha256': None,
            'native_dll_sha256': runner.sha(frozen / 'native/CADtest.dll'),
            'code_sha256': {v['source']: v['sha256'] for v in manifest.values()},
            'frozen_manifest_sha256': template_sha,
            'extractor_projection_contract_sha256': runner.EXTRACTOR_CONTRACT_SHA256,
            'harness_sha256': harness_sha, 'python': sys.executable, 'python_sha256': python_sha,
            'node': str(node), 'node_sha256': node_sha, 'recognition_mode': True,
            'memory_monitor': 'Windows Job Object process + total job commit caps, KILL_ON_JOB_CLOSE; working-set polling',
            'poll_interval_seconds': runner.POLL_SECONDS,
            'stage_gate': 'stdin GO after job assignment and memory/CPU limit verification',
            'comparison_reads_excel_only_after_predictions': True,
            'reference_policy': 'No reference workbook passed to prediction',
            'scope': 'Recognition only; no quotation workbook export or automatic comparison',
            'parallel': {'task_id': task['id'], 'started_at': started, 'admission': admission,
                'settings': settings, 'batch_status': str(controls.state.path)}}
        runner.save(recognition / 'protocol.json', protocol)
        runs = runner.run_samples(selection['selected'], recognition, frozen, manifest, limit,
                                  node, True, root, control=control)
        passed = len(runs) == 1 and runs[0]['status'] == 'passed'
        runner.save(recognition / 'batch-status.json', {'status': 'passed' if passed else 'failed',
            'selected_count': 1, 'passed_count': int(passed), 'failed_or_blocked_count': int(not passed)})
        if (runner.sha(runner.__file__) != harness_sha or runner.sha(sys.executable) != python_sha
                or runner.sha(node) != node_sha or runner.sha(frozen / 'manifest.json') != template_sha):
            raise ValueError('runtime_or_manifest_changed_during_recognition')
        runner.verify_frozen(frozen, manifest)
        review_completed = False
        if passed and not control.cancelled():
            review_completed = runner.prepare_review(recognition, frozen, manifest, limit, root, control=control)
        if passed and review_completed:
            status, reason = 'passed', None
        elif control.cancelled():
            status, reason = 'cancelled', 'cancelled'
        else:
            status = 'failed'
            reason = (runs[0].get('blocked_reason') if runs and runs[0]['status'] != 'passed'
                      else 'review preparation failed')
        memory = status == 'failed' and is_memory_failure(recognition, out / 'review-status.json')
        return {'status': status, 'reason': reason, 'memory_failure': memory,
                'output': str(out), 'started_at': started, 'finished_at': timestamp()}
    except Exception as exc:
        return {'status': 'cancelled' if control.cancelled() else 'failed',
                'reason': 'cancelled' if control.cancelled() else str(exc),
                'memory_failure': False, 'output': str(out), 'started_at': started, 'finished_at': timestamp()}


def main(args, root):
    root = Path(root).resolve()
    try:
        if not 0 <= args.max_parallel <= MAX_TASKS:
            raise ValueError('--max-parallel must be 0 through 6')
        out = runner.prepare_output(args.output, root, require_new=True)
    except (OSError, ValueError) as exc:
        print('Parallel batch rejected: ' + str(exc), flush=True)
        return 2
    state = BatchState(out, [])
    controls = None
    try:
        marker = runner.local_path(args.cancel_file or out / 'cancel.request', root)
        if marker.parent != out or marker.name != 'cancel.request':
            raise ValueError('cancel marker must be batch output/cancel.request')
        limits = runner.Limits(args.max_files, args.max_file_mib, args.max_batch_mib,
                               args.memory_mib, args.timeout_seconds, args.cpu_percent)
        limits.validate()
        with runner.workspace_lock(root):
            tasks, admission, manifest_sha = parse_manifest(args, root, out, limits)
            node = runner.node_executable(args.node, root)
            cores = os.cpu_count() or 1
            capacity, budget = safe_parallel_capacity(cores, args.cpu_percent, args.max_parallel)
            settings = {'max_parallel': args.max_parallel, 'capacity': capacity, 'logical_cores': cores,
                'cpu_budget_percent': budget, 'system_memory_reserve_mib': RESERVE_MIB,
                'per_task_memory_mib': limits.memory_mib, 'disk_multiplier': 50,
                'minimum_disk_reservation_mib': 1024, 'manifest_sha256': manifest_sha}
            state.update(status='preparing', tasks=tasks, settings=settings)
            admission.update(concurrency=capacity, parallel=True, settings=settings)
            runner.save(out / 'admission.json', admission)
            template, frozen_manifest = runner.freeze_sources(out, root)
            template_sha = runner.sha(template / 'manifest.json')
            harness_sha, python_sha, node_sha = runner.sha(runner.__file__), runner.sha(sys.executable), runner.sha(node)
            controls = Controls(state, marker, cores, budget)
            previous_sigint = signal.getsignal(signal.SIGINT)
            signal.signal(signal.SIGINT, lambda *_: controls.stop.set())
            pending = sorted(tasks, key=lambda t: (-t['bytes'], t['id']))
            active, retries = {}, []
            state.update(status='running')
            idle_since = None
            with ThreadPoolExecutor(max_workers=capacity, thread_name_prefix='cad-drawing') as pool:
                while pending or active:
                    if controls.cancelled():
                        for task in pending:
                            state.task(task['id'], status='cancelled', blocked_reason='cancelled', finished_at=timestamp())
                        pending.clear()
                    launched = False
                    while pending and not controls.cancelled() and len(active) < capacity:
                        task = pending[0]
                        snapshot = memory_snapshot()
                        disk_free = shutil.disk_usage(out).free
                        memory_held = sum(v['reserved_memory_mib'] for v in active.values())
                        disk_held = sum(v['reserved_disk_bytes'] for v in active.values())
                        need_disk = disk_reservation(Path(task['drawing']))
                        memory_ok = (snapshot['available_physical_mib'] - RESERVE_MIB - memory_held >= limits.memory_mib
                                     and snapshot['available_commit_mib'] - memory_held >= limits.memory_mib)
                        disk_ok = disk_free - disk_held >= need_disk
                        decision = {'at': timestamp(), 'id': task['id'], **snapshot,
                            'active_tasks_before': len(active), 'reserved_by_active_mib': memory_held,
                            'reserved_memory_mib': limits.memory_mib, 'disk_free_bytes': disk_free,
                            'disk_reserved_by_active_bytes': disk_held, 'reserved_disk_bytes': need_disk,
                            'memory_ok': memory_ok, 'disk_ok': disk_ok}
                        if not memory_ok or not disk_ok:
                            decision['decision'] = 'wait'
                            state.event('admissions', decision)
                            break
                        pending.pop(0)
                        decision.update(decision='start', active_tasks_after=len(active) + 1,
                                        cpu_percent=math.floor(budget / (len(active) + 1) * 100) / 100)
                        state.event('admissions', decision)
                        controls.set_active(len(active) + 1)
                        state.task(task['id'], status='running', started_at=timestamp(),
                                   reserved_memory_mib=limits.memory_mib, reserved_disk_bytes=need_disk)
                        future = pool.submit(run_task, task, task['output'], limits, node, root, template,
                            frozen_manifest, template_sha, harness_sha, python_sha, node_sha,
                            settings, controls, decision)
                        active[future] = {'task': task, 'reserved_memory_mib': limits.memory_mib,
                                          'reserved_disk_bytes': need_disk}
                        launched, idle_since = True, None
                    if active:
                        done, _ = wait(active, timeout=POLL_SECONDS, return_when=FIRST_COMPLETED)
                        for future in done:
                            meta = active.pop(future)
                            result = future.result()
                            task = meta['task']
                            task['attempts'].append(result)
                            retry_pending = result.get('memory_failure') and not controls.cancelled()
                            state.task(task['id'], status='queued' if retry_pending else result['status'], output=result['output'],
                                blocked_reason=result['reason'], finished_at=result['finished_at'], attempts=task['attempts'])
                            if retry_pending:
                                retries.append(task)
                            controls.set_active(len(active))
                    elif pending and not launched:
                        if idle_since is None:
                            idle_since = time.monotonic()
                        if time.monotonic() - idle_since >= IDLE_WAIT_SECONDS:
                            for task in pending:
                                reason = 'insufficient_available_memory_or_disk_after_wait'
                                state.task(task['id'], status='failed', blocked_reason=reason, finished_at=timestamp())
                            pending.clear()
                        else:
                            time.sleep(POLL_SECONDS)
                # A failed task gets one isolated larger-memory attempt only after
                # all first attempts have ended; the original evidence is retained.
                for task in retries:
                    if controls.cancelled():
                        break
                    snapshot = memory_snapshot()
                    retry_mem = min(8192, math.floor(snapshot['available_physical_mib'] * .8),
                                    math.floor(snapshot['available_commit_mib'] * .8),
                                    math.floor(snapshot['available_physical_mib'] - RESERVE_MIB))
                    retry_out = Path(task['output']).with_name(Path(task['output']).name + '-memory-retry-01')
                    need_disk = disk_reservation(Path(task['drawing']))
                    if retry_mem <= limits.memory_mib or retry_out.exists() or shutil.disk_usage(out).free < need_disk:
                        state.task(task['id'], status='failed', retry_blocked_reason='insufficient_memory_disk_or_retry_output_exists')
                        continue
                    retry_limits = runner.Limits(limits.max_files, limits.max_file_mib, limits.max_batch_mib,
                                                 retry_mem, limits.timeout_seconds, limits.cpu_percent)
                    decision = {'at': timestamp(), 'id': task['id'], **snapshot, 'decision': 'retry_start',
                        'active_tasks_before': 0, 'active_tasks_after': 1, 'reserved_memory_mib': retry_mem,
                        'reserved_disk_bytes': need_disk, 'cpu_percent': budget}
                    state.event('admissions', decision)
                    controls.set_active(1)
                    state.task(task['id'], status='running', blocked_reason=None, stage='memory_retry',
                               reserved_memory_mib=retry_mem, reserved_disk_bytes=need_disk)
                    result = pool.submit(run_task, task, str(retry_out), retry_limits, node, root, template,
                        frozen_manifest, template_sha, harness_sha, python_sha, node_sha,
                        settings, controls, decision).result()
                    controls.set_active(0)
                    task['attempts'].append(result)
                    state.task(task['id'], status=result['status'], output=result['output'],
                        blocked_reason=result['reason'], finished_at=result['finished_at'], attempts=task['attempts'])
            if controls.cancelled():
                for task in retries:
                    if task['status'] == 'queued':
                        state.task(task['id'], status='cancelled', blocked_reason='cancelled', finished_at=timestamp())
            signal.signal(signal.SIGINT, previous_sigint)
            if controls.error:
                final = 'failed'
            elif controls.cancelled():
                final = 'cancelled'
            else:
                final = 'passed' if all(t['status'] == 'passed' for t in tasks) else 'failed'
            state.update(status=final, finished_at=timestamp(), error=controls.error)
            print('Parallel batch ' + final + ': ' + str(state.path), flush=True)
            return 0 if final == 'passed' else (130 if final == 'cancelled' else 1)
    except (Exception, KeyboardInterrupt) as exc:
        if controls:
            controls.stop.set()
        reason = 'interrupted' if isinstance(exc, KeyboardInterrupt) else str(exc)
        state.update(status='blocked', blocked_reason=reason, finished_at=timestamp())
        runner.save(out / 'admission.json', {'status': 'blocked', 'concurrency': 0,
                                            'reasons': [{'code': 'batch_error', 'detail': reason}]})
        print('Parallel batch blocked: ' + reason, flush=True)
        return 130 if isinstance(exc, KeyboardInterrupt) else 2
