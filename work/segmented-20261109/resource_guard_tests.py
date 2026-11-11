"""Serial, synthetic Windows resource-guard tests for this runner.

Run only when the owner chooses a suitable time:
    python -B D:\\CADtest\\work\\segmented-20261109\\resource_guard_tests.py

No DWG/XLSX is opened. All fixtures, isolated lock files and evidence are
created in a fresh outputs/segmented-20261109/resource-tests-* directory.
The real shared lock is deliberately never opened. A lock-contention test
uses the actual old and new lock implementations against an isolated root.

Some checks deliberately inject a bad readback or guard failure; their names
and evidence distinguish these from tests of actual Windows enforcement.
The CPU load check is a short, coarse observation, not a throughput benchmark.
"""

import ctypes
from ctypes import wintypes
from dataclasses import replace
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import unittest
from unittest.mock import patch
import uuid

sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['DOTNET_DbgEnableMiniDump'] = '0'
os.environ['COMPlus_DbgEnableMiniDump'] = '0'
os.environ['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import runner  # noqa: E402 -- bytecode has already been disabled

LEGACY_PATH = runner.ROOT / 'work/batch-fix-20261106/resource_runner.py'
OUTPUT_PARENT = runner.ROOT / 'outputs/segmented-20261109'
SELF = Path(__file__).resolve()
LIMITS = runner.Limits(memory_mib=128, timeout_seconds=8, cpu_percent=25)
EVIDENCE = None


def legacy_module():
    name = 'cadtest_resource_guard_legacy'
    if name not in sys.modules:
        spec = importlib.util.spec_from_file_location(name, LEGACY_PATH)
        module = importlib.util.module_from_spec(spec)
        sys.modules[name] = module  # dataclasses needs the module registered
        spec.loader.exec_module(module)
    return sys.modules[name]


def save_json(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


def wait_until(predicate, seconds):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(.025)
    return bool(predicate())


def child_probe(argv):
    """Internal helpers, invoked with an isolated test root only."""
    if argv[0] == '--lock-probe':
        which, root = argv[1], Path(argv[2])
        require_isolated_lock_root(root)
        module = runner if which == 'current' else legacy_module()
        try:
            with module.workspace_lock(root):
                print('acquired')
                return 0
        except RuntimeError as exc:
            if 'workspace_busy' not in str(exc):
                raise
            print('workspace_busy')
            return 23
    if argv[0] == '--main-probe':
        root = Path(argv[1])
        require_isolated_lock_root(root)
        # This input does not exist. Lock acquisition must fail before it is read.
        return runner.main(['--output', str(root / 'outputs/busy'),
                            '--drawing', str(root / 'never-created.dwg')], root=root)
    raise ValueError('unknown internal probe')


def require_isolated_lock_root(root):
    # Refuse to let even an accidentally invoked helper target the real lock.
    relative = Path(root).resolve().relative_to(OUTPUT_PARENT.resolve())
    if (len(relative.parts) != 3 or not relative.parts[0].startswith('resource-tests-')
            or relative.parts[1] != 'test_isolated_shared_lock_contention_and_busy_main'
            or relative.parts[2] != 'isolated-workspace'):
        raise ValueError('lock probe requires its fresh isolated test workspace')


GATED_WORKER = r'''
import sys
import time
from pathlib import Path
ready, worked = map(Path, sys.argv[1:3])
ready.write_text('ready', encoding='utf-8')
if sys.stdin.readline() != 'GO\n':
    raise SystemExit(31)
worked.write_text('worked after GO', encoding='utf-8')
if len(sys.argv) > 3:
    time.sleep(float(sys.argv[3]))
'''

MEMORY_WORKER = r'''
import ctypes
import json
from pathlib import Path
import sys
import time
if sys.stdin.readline() != 'GO\n':
    raise SystemExit(31)
kernel = ctypes.WinDLL('kernel32', use_last_error=True)
kernel.VirtualAlloc.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_ulong, ctypes.c_ulong]
kernel.VirtualAlloc.restype = ctypes.c_void_p
kernel.VirtualFree.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_ulong]
kernel.VirtualFree.restype = ctypes.c_int
# At most 96 MiB of additional commit, with no page touching and no drawing.
address = kernel.VirtualAlloc(None, 96 * 1024 ** 2, 0x1000 | 0x2000, 0x04)
error = ctypes.get_last_error()
freed = bool(kernel.VirtualFree(address, 0, 0x8000)) if address else None
Path(sys.argv[1]).write_text(json.dumps({'denied': not bool(address),
                                        'winerror': error, 'freed': freed}), encoding='utf-8')
if sys.argv[2] == 'hold':
    time.sleep(20)  # The supervisor must stop this process on the real Job event.
elif not address or not freed:
    raise SystemExit(32)
'''

GROUP_MEMORY_CHILD = r'''
import ctypes
from ctypes import wintypes
import json
from pathlib import Path
import sys
import time
class Counters(ctypes.Structure):
    _fields_ = [('cb', wintypes.DWORD), ('PageFaultCount', wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in ('PeakWorkingSetSize', 'WorkingSetSize',
        'QuotaPeakPagedPoolUsage', 'QuotaPagedPoolUsage', 'QuotaPeakNonPagedPoolUsage',
        'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage')]
kernel = ctypes.WinDLL('kernel32', use_last_error=True)
psapi = ctypes.WinDLL('psapi', use_last_error=True)
kernel.GetCurrentProcess.restype = wintypes.HANDLE
psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
counters = Counters()
counters.cb = ctypes.sizeof(counters)
if not psapi.GetProcessMemoryInfo(kernel.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
    raise ctypes.WinError(ctypes.get_last_error())
kernel.VirtualAlloc.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_ulong, ctypes.c_ulong]
kernel.VirtualAlloc.restype = ctypes.c_void_p
address = kernel.VirtualAlloc(None, 64 * 1024 ** 2, 0x1000 | 0x2000, 0x04)
error = ctypes.get_last_error()
Path(sys.argv[1]).write_text(json.dumps({'denied': not bool(address), 'winerror': error,
                                        'before_bytes': counters.PagefileUsage}), encoding='utf-8')
time.sleep(15)
'''

GROUP_MEMORY_WORKER = r'''
import json
from pathlib import Path
import subprocess
import sys
import time
if sys.stdin.readline() != 'GO\n':
    raise SystemExit(31)
first, second, combined = map(Path, sys.argv[1:4])
code = sys.argv[4]
def spawn(marker):
    return subprocess.Popen([sys.executable, '-B', '-c', code, str(marker)],
                            creationflags=subprocess.CREATE_NO_WINDOW)
def wait_file(marker):
    deadline = time.monotonic() + 6
    while not marker.exists() and time.monotonic() < deadline:
        time.sleep(.025)
    if not marker.exists():
        raise SystemExit(34)
one = spawn(first)
wait_file(first)
two = spawn(second)
wait_file(second)
combined.write_text(json.dumps({'first': json.loads(first.read_text()),
                                'second': json.loads(second.read_text())}), encoding='utf-8')
time.sleep(20)
'''

CPU_WORKER = r'''
import json
from pathlib import Path
import sys
import time
if sys.stdin.readline() != 'GO\n':
    raise SystemExit(31)
w0, c0 = time.perf_counter(), time.process_time()
while time.perf_counter() - w0 < 1.6:
    pass
Path(sys.argv[1]).write_text(json.dumps({'wall': time.perf_counter() - w0,
                                        'cpu': time.process_time() - c0}), encoding='utf-8')
'''

DESCENDANT_WORKER = r'''
from pathlib import Path
import subprocess
import sys
import time
if sys.stdin.readline() != 'GO\n':
    raise SystemExit(31)
pid_file, ready, release = map(Path, sys.argv[1:4])
code = 'from pathlib import Path; import sys,time; Path(sys.argv[1]).write_text("ready"); time.sleep(12)'
child = subprocess.Popen([sys.executable, '-B', '-c', code, str(ready)],
                         creationflags=subprocess.CREATE_NO_WINDOW)
pid_file.write_text(str(child.pid), encoding='ascii')
deadline = time.monotonic() + 6
while not release.exists() and time.monotonic() < deadline:
    time.sleep(.025)
if not release.exists():
    raise SystemExit(33)
# Exit while the descendant is still alive; closing the Job must reclaim it.
'''


class ResourceGuardTests(unittest.TestCase):
    def setUp(self):
        self.folder = EVIDENCE / self._testMethodName
        self.folder.mkdir()

    def run_worker(self, code, name, *args, limits=LIMITS):
        result = runner.command([sys.executable, '-B', '-c', code, *map(str, args)],
                                self.folder, name, limits, wait_for_go=True, cwd=self.folder)
        save_json(self.folder / (name + '.result.json'), result)
        return result

    def assert_passed(self, result):
        self.assertEqual(result['status'], 'passed', result)
        self.assertEqual(result['returncode'], 0, result)
        self.assertTrue(result['guards_installed'], result)
        self.assertTrue(result['input_gate_released'], result)
        self.assertFalse(result['timeout'], result)
        self.assertFalse(result['memory_limit_exceeded'], result)

    def test_limit_validation_and_stage_gate_wiring(self):
        for field, valid in [('memory_mib', 32), ('timeout_seconds', .05), ('cpu_percent', 1)]:
            replace(LIMITS, **{field: valid}).validate()
        for field, invalid in [('memory_mib', 31), ('timeout_seconds', .04),
                               ('cpu_percent', 0), ('cpu_percent', 101),
                               ('cpu_percent', float('nan'))]:
            with self.subTest(field=field, invalid=invalid):
                with self.assertRaises(ValueError):
                    replace(LIMITS, **{field: invalid}).validate()
        stages = runner.stage_commands(self.folder / 'synthetic-frozen',
                                       {'dwg': str(self.folder / 'never-created.dwg')},
                                       self.folder, runner.NODE)
        self.assertEqual([stage[0] for stage in stages],
                         ['native', 'profile', 'projection', 'projection_profile', 'projection_audit', 'fields', 'prediction'])
        for stage in stages[:5]:
            self.assertIn('--wait-for-go', stage[1], stage[0])
        for stage in stages[5:]:
            self.assertEqual(stage[1][1], self.folder / 'synthetic-frozen' / runner.NODE_GATE)

    def test_every_sample_stage_is_executed_behind_the_go_gate(self):
        seed = self.folder / 'synthetic-input.bin'
        seed.write_bytes(b'synthetic input; not a drawing')
        out = self.folder / 'synthetic-run'
        out.mkdir()
        frozen = self.folder / 'synthetic-frozen'
        frozen.mkdir()
        item = {'id': 'fixture', 'dwg': str(seed), 'dwg_sha256': runner.sha(seed)}
        code = ("import sys\nfrom pathlib import Path\n"
                "if sys.stdin.readline() != 'GO\\n': raise SystemExit(31)\n"
                "Path(sys.argv[2]).write_bytes(Path(sys.argv[1]).read_bytes() + b'!')\n")

        def synthetic_stages(frozen_arg, item_arg, folder, node, recognition_mode):
            previous = seed
            stages = []
            for name in ('native', 'profile', 'projection', 'projection_profile', 'projection_audit', 'fields', 'prediction'):
                output = folder / (name + '.bin')
                stages.append((name, [sys.executable, '-B', '-c', code, str(previous), str(output)],
                               [previous], [output]))
                previous = output
            return stages

        # Replace only the expensive stage commands. The real serial loop,
        # Job/GO wrapper, hashes and result persistence are exercised.
        with patch.object(runner, 'stage_commands', synthetic_stages):
            runs = runner.run_samples([item], out, frozen, {}, LIMITS, root=self.folder)
        self.assertEqual(len(runs), 1)
        self.assertEqual(runs[0]['status'], 'passed', runs[0])
        for name in ('native', 'profile', 'projection', 'projection_profile', 'projection_audit', 'fields', 'prediction'):
            with self.subTest(stage=name):
                self.assert_passed(runs[0][name])
        self.assertEqual((out / 'samples/fixture/prediction.bin').read_bytes(), seed.read_bytes() + b'!!!!!!!')

    def test_real_job_readback_assignment_and_go_order(self):
        ready, worked = self.folder / 'ready', self.folder / 'worked'
        observed = {}
        original_memory = runner.WindowsJob.memory

        def observe_before_go(job, process):
            sample = original_memory(job, process)
            if not observed:
                limits = job.limits()
                cpu = runner.CpuRateLimits()
                runner.check(job.kernel.QueryInformationJobObject(job.handle, 15, ctypes.byref(cpu),
                                                                  ctypes.sizeof(cpu), None))
                in_job = wintypes.BOOL()
                fn = job.kernel.IsProcessInJob
                fn.argtypes = [wintypes.HANDLE, wintypes.HANDLE, ctypes.POINTER(wintypes.BOOL)]
                fn.restype = wintypes.BOOL
                runner.check(fn(wintypes.HANDLE(int(process._handle)), job.handle, ctypes.byref(in_job)))
                observed.update(process_memory=limits.ProcessMemoryLimit, job_memory=limits.JobMemoryLimit,
                                flags=limits.BasicLimitInformation.LimitFlags,
                                cpu_flags=cpu.ControlFlags, cpu_rate=cpu.CpuRate,
                                assigned=bool(in_job.value), ready=wait_until(ready.exists, 5))
                time.sleep(.2)
                observed['worked_before_go'] = worked.exists()
                observed['process'] = process
            return sample

        with patch.object(runner.WindowsJob, 'memory', observe_before_go):
            result = self.run_worker(GATED_WORKER, 'normal', ready, worked, .2)
        process = observed.pop('process', None)
        save_json(self.folder / 'observed-job.json', observed)
        self.assert_passed(result)
        self.assertEqual(observed['process_memory'], 128 * runner.MIB)
        self.assertEqual(observed['job_memory'], 128 * runner.MIB)
        self.assertEqual(observed['flags'] & 0x2300, 0x2300)
        self.assertEqual((observed['cpu_flags'], observed['cpu_rate']), (5, 2500))
        self.assertTrue(observed['assigned'])
        self.assertTrue(observed['ready'], 'worker never reached its pre-GO wait')
        self.assertFalse(observed['worked_before_go'])
        self.assertTrue(worked.is_file())
        self.assertIsNotNone(process)
        self.assertIsNotNone(process.poll(), 'the direct worker was not reaped')
        self.assertGreater(result['peak_rss_mib'], 0)

    def test_assignment_failure_does_not_release_waiting_worker(self):
        ready, worked, captured = self.folder / 'ready', self.folder / 'worked', []

        def fail_assignment(job, process):
            captured.append(process)
            if not wait_until(ready.exists, 5):
                raise RuntimeError('worker never reached gate')
            time.sleep(.15)
            raise OSError('synthetic assignment failure')

        with patch.object(runner.WindowsJob, 'assign', fail_assignment):
            result = self.run_worker(GATED_WORKER, 'assign-failure', ready, worked)
        self.assertEqual(result['status'], 'blocked', result)
        self.assertEqual(result['blocked_reason'], 'guard_or_launch_error', result)
        self.assertIn('synthetic assignment failure', result.get('error', ''))
        self.assertFalse(result['guards_installed'])
        self.assertFalse(result['input_gate_released'])
        self.assertTrue(ready.is_file())
        self.assertFalse(worked.exists())
        self.assertEqual(len(captured), 1)
        self.assertIsNotNone(captured[0].poll())

    def test_memory_query_failure_does_not_release_assigned_worker(self):
        ready, worked, captured = self.folder / 'ready', self.folder / 'worked', []
        original_memory = runner.WindowsJob.memory

        def fail_memory(job, process):
            original_memory(job, process)
            captured.append(process)
            if not wait_until(ready.exists, 5):
                raise RuntimeError('worker never reached gate')
            time.sleep(.15)
            raise OSError('synthetic initial memory query failure')

        with patch.object(runner.WindowsJob, 'memory', fail_memory):
            result = self.run_worker(GATED_WORKER, 'memory-query-failure', ready, worked)
        self.assertEqual(result['status'], 'blocked', result)
        self.assertIn('synthetic initial memory query failure', result.get('error', ''))
        self.assertFalse(result['guards_installed'])
        self.assertFalse(result['input_gate_released'])
        self.assertTrue(ready.is_file())
        self.assertFalse(worked.exists())
        self.assertEqual(len(captured), 1)
        self.assertIsNotNone(captured[0].poll())

    def test_pre_go_high_memory_sample_must_not_release_gate(self):
        ready, worked, observed = self.folder / 'ready', self.folder / 'worked', []
        original_memory = runner.WindowsJob.memory

        def already_over_limit(job, process):
            sample = original_memory(job, process)
            if not observed:
                observed.append(wait_until(ready.exists, 5))
                # Inject a pre-GO measurement, not an actual large allocation.
                sample.PeakWorkingSetSize = 129 * runner.MIB
            return sample

        with patch.object(runner.WindowsJob, 'memory', already_over_limit):
            result = self.run_worker(GATED_WORKER, 'pre-go-high-rss', ready, worked, 20)
        self.assertEqual(observed, [True], 'worker never reached its pre-GO wait')
        self.assertTrue(result['memory_limit_exceeded'], result)
        self.assertNotEqual(result['returncode'], 0)
        self.assertFalse(result['input_gate_released'], 'GO sent despite an already excessive sample')
        self.assertFalse(worked.exists())

    def test_pre_go_pending_memory_event_must_not_release_gate(self):
        ready, worked, observed = self.folder / 'ready', self.folder / 'worked', []
        original_memory = runner.WindowsJob.memory

        def wait_at_gate(job, process):
            sample = original_memory(job, process)
            if not observed:
                observed.append(wait_until(ready.exists, 5))
            return sample

        # Simulate a limit event that was already queued before the GO decision.
        with patch.object(runner.WindowsJob, 'memory', wait_at_gate), \
             patch.object(runner.WindowsJob, 'memory_limit_hit', return_value=True):
            result = self.run_worker(GATED_WORKER, 'pre-go-memory-event', ready, worked, 20)
        self.assertEqual(observed, [True], 'worker never reached its pre-GO wait')
        self.assertTrue(result['memory_limit_exceeded'], result)
        self.assertNotEqual(result['returncode'], 0)
        self.assertFalse(result['input_gate_released'], 'GO sent with a pending Job limit event')
        self.assertFalse(worked.exists())

    def test_expired_deadline_before_go_must_not_release_gate(self):
        ready, worked, observed = self.folder / 'ready', self.folder / 'worked', []
        original_memory = runner.WindowsJob.memory

        def delay_before_release(job, process):
            sample = original_memory(job, process)
            if not observed:
                observed.append(wait_until(ready.exists, 5))
                time.sleep(1)  # The 0.5 second wall-clock limit is already past.
            return sample

        with patch.object(runner.WindowsJob, 'memory', delay_before_release):
            result = self.run_worker(GATED_WORKER, 'pre-go-expired', ready, worked, 20,
                                     limits=replace(LIMITS, timeout_seconds=.5))
        self.assertEqual(observed, [True], 'worker never reached its pre-GO wait')
        self.assertTrue(result['timeout'], result)
        self.assertNotEqual(result['returncode'], 0)
        self.assertFalse(result['input_gate_released'], 'GO sent after the deadline had already expired')
        self.assertFalse(worked.exists())

    def test_memory_readback_mismatch_fails_before_launch(self):
        original = runner.WindowsJob.limits

        def corrupt_readback(job):
            value = original(job)  # Actual OS readback, then inject a mismatch.
            value.ProcessMemoryLimit += 4096
            return value

        with patch.object(runner.WindowsJob, 'limits', corrupt_readback):
            result = self.run_worker(GATED_WORKER, 'bad-memory-readback',
                                     self.folder / 'ready', self.folder / 'worked')
        self.assertEqual(result['status'], 'blocked', result)
        self.assertEqual(result['blocked_reason'], 'guard_or_launch_error', result)
        self.assertIn('Job Object limit verification failed', result.get('error', ''))
        self.assertNotIn('child_pid', result)
        self.assertFalse(result['input_gate_released'])
        self.assertFalse((self.folder / 'ready').exists())

    def test_cpu_readback_mismatch_fails_before_launch(self):
        real_windll = ctypes.WinDLL

        class QueryWithMismatch:
            def __init__(self, function):
                self.function = function

            @property
            def argtypes(self):
                return self.function.argtypes

            @argtypes.setter
            def argtypes(self, value):
                self.function.argtypes = value

            @property
            def restype(self):
                return self.function.restype

            @restype.setter
            def restype(self, value):
                self.function.restype = value

            def __call__(self, handle, info_class, buffer, size, returned):
                ok = self.function(handle, info_class, buffer, size, returned)
                if ok and info_class == 15:
                    info = ctypes.cast(buffer, ctypes.POINTER(runner.CpuRateLimits)).contents
                    info.CpuRate += 1  # Inject only after the actual OS query succeeded.
                return ok

        class KernelProxy:
            def __init__(self, library):
                self.library = library
                self.QueryInformationJobObject = QueryWithMismatch(library.QueryInformationJobObject)

            def __getattr__(self, name):
                return getattr(self.library, name)

        def intercepted_windll(name, *args, **kwargs):
            library = real_windll(name, *args, **kwargs)
            return KernelProxy(library) if name == 'kernel32' else library

        with patch.object(runner.ctypes, 'WinDLL', side_effect=intercepted_windll):
            result = self.run_worker(GATED_WORKER, 'bad-cpu-readback',
                                     self.folder / 'ready', self.folder / 'worked')
        self.assertEqual(result['status'], 'blocked', result)
        self.assertIn('Job Object CPU limit verification failed', result.get('error', ''))
        self.assertNotIn('child_pid', result)
        self.assertFalse(result['input_gate_released'])
        self.assertFalse((self.folder / 'ready').exists())

    def test_timeout_terminates_and_reaps_the_worker(self):
        ready, worked, captured = self.folder / 'ready', self.folder / 'worked', []
        original_assign = runner.WindowsJob.assign

        def capture(job, process):
            captured.append(process)
            return original_assign(job, process)

        with patch.object(runner.WindowsJob, 'assign', capture):
            result = self.run_worker(GATED_WORKER, 'timeout', ready, worked, 20,
                                     limits=replace(LIMITS, timeout_seconds=2))
        self.assertEqual(result['status'], 'failed', result)
        self.assertEqual(result['blocked_reason'], 'timeout', result)
        self.assertTrue(result['timeout'])
        self.assertFalse(result['memory_limit_exceeded'])
        self.assertTrue(result['guards_installed'])
        self.assertTrue(result['input_gate_released'])
        self.assertNotEqual(result['returncode'], 0)
        self.assertTrue(worked.exists(), 'worker did not actually reach the sleeping workload')
        self.assertLess(result['elapsed_seconds'], 6)
        self.assertEqual(len(captured), 1)
        self.assertIsNotNone(captured[0].poll())

    def test_real_memory_cap_denies_commit_and_is_reported(self):
        # Control: the identical 96 MiB request succeeds under a 256 MiB Job.
        control_marker = self.folder / 'control.json'
        control = self.run_worker(MEMORY_WORKER, 'memory-control', control_marker, 'exit',
                                  limits=replace(LIMITS, memory_mib=256))
        self.assert_passed(control)
        control_data = json.loads(control_marker.read_text(encoding='utf-8'))
        self.assertFalse(control_data['denied'], control_data)
        self.assertTrue(control_data['freed'], control_data)

        marker, captured = self.folder / 'limited.json', []
        original_hit = runner.WindowsJob.memory_limit_hit
        original_assign = runner.WindowsJob.assign
        first_poll = [True]

        def capture(job, process):
            captured.append(process)
            return original_assign(job, process)

        def let_worker_record_os_result(job):
            # Delay only observation, not the real Windows limit; this avoids
            # killing the child between the denied allocation and its evidence.
            if first_poll[0]:
                first_poll[0] = False
                return original_hit(job)  # Pre-GO guard must never wait for work.
            if not marker.exists():
                wait_until(marker.exists, 5)
            return original_hit(job)

        with patch.object(runner.WindowsJob, 'assign', capture), \
             patch.object(runner.WindowsJob, 'memory_limit_hit', let_worker_record_os_result):
            result = self.run_worker(MEMORY_WORKER, 'memory-limited', marker, 'hold',
                                     limits=replace(LIMITS, memory_mib=64, timeout_seconds=8))
        self.assertTrue(marker.is_file(), result)
        data = json.loads(marker.read_text(encoding='utf-8'))
        self.assertTrue(data['denied'], data)
        self.assertEqual(result['status'], 'failed', result)
        self.assertEqual(result['blocked_reason'], 'memory_limit_exceeded', result)
        self.assertTrue(result['memory_limit_exceeded'], result)
        self.assertFalse(result['timeout'], result)
        self.assertNotEqual(result['returncode'], 0)
        self.assertLessEqual(result['peak_commit_mib'], 65)
        self.assertEqual(len(captured), 1)
        self.assertIsNotNone(captured[0].poll())

    def test_fractional_memory_limit_either_verifies_or_fails_closed(self):
        # A byte above a page-aligned cap exercises the OS readback/rounding
        # boundary. Rejection is acceptable if no child or GO is released.
        requested = 64 * runner.MIB + 1
        ready, worked = self.folder / 'ready', self.folder / 'worked'
        observed = {}
        original_assign = runner.WindowsJob.assign

        def capture(job, process):
            original_assign(job, process)
            actual = job.limits()
            observed.update(process_memory=actual.ProcessMemoryLimit,
                            job_memory=actual.JobMemoryLimit)

        with patch.object(runner.WindowsJob, 'assign', capture):
            result = self.run_worker(GATED_WORKER, 'fractional-limit', ready, worked,
                                     limits=replace(LIMITS, memory_mib=requested / runner.MIB))
        save_json(self.folder / 'fractional-observation.json', observed)
        if result['status'] == 'passed':
            self.assert_passed(result)
            self.assertTrue(worked.exists())
            self.assertGreater(observed['process_memory'], 0)
            self.assertLessEqual(observed['process_memory'], requested)
            self.assertLessEqual(observed['job_memory'], requested)
        else:
            self.assertEqual(result['status'], 'blocked', result)
            self.assertEqual(result['blocked_reason'], 'guard_or_launch_error', result)
            self.assertIn('Job Object limit verification failed', result.get('error', ''))
            self.assertNotIn('child_pid', result)
            self.assertFalse(result['input_gate_released'])
            self.assertFalse(ready.exists())
            self.assertFalse(worked.exists())

    def test_job_total_memory_cap_covers_multiple_processes(self):
        first, second, combined = (self.folder / name for name in ('first.json', 'second.json', 'combined.json'))
        original_hit = runner.WindowsJob.memory_limit_hit
        first_poll = [True]

        def wait_for_evidence(job):
            if first_poll[0]:
                first_poll[0] = False
                return original_hit(job)  # Pre-GO guard must never wait for work.
            if not combined.exists():
                wait_until(combined.exists, 8)
            return original_hit(job)

        # Each child requests 64 MiB, below its 160 MiB process cap. The first
        # keeps its allocation while the second attempts to exceed the Job cap.
        with patch.object(runner.WindowsJob, 'memory_limit_hit', wait_for_evidence):
            result = self.run_worker(GROUP_MEMORY_WORKER, 'group-memory', first, second, combined,
                                     GROUP_MEMORY_CHILD,
                                     limits=replace(LIMITS, memory_mib=160, timeout_seconds=12))
        self.assertTrue(combined.is_file(), result)
        data = json.loads(combined.read_text(encoding='utf-8'))
        self.assertFalse(data['first']['denied'], data)
        self.assertLess(data['second']['before_bytes'] + 64 * runner.MIB, 160 * runner.MIB, data)
        self.assertTrue(data['second']['denied'], data)
        self.assertEqual(result['status'], 'failed', result)
        self.assertEqual(result['blocked_reason'], 'memory_limit_exceeded', result)
        self.assertTrue(result['memory_limit_exceeded'], result)
        self.assertFalse(result['timeout'], result)
        self.assertNotEqual(result['returncode'], 0)
        # On this Windows host PeakJobMemoryUsed recorded 165.4 MiB at a
        # configured 160 MiB cap even though the second allocation was denied.
        # Report that observed peak; do not equate the counter with a byte-exact
        # ceiling. The denial, event, and termination assertions above are primary.
        self.assertLess(result['peak_commit_mib'], 192)

    def test_cpu_hard_cap_has_observable_effect_when_machine_allows(self):
        logical_cpus = os.cpu_count() or 1
        # A single busy thread cannot distinguish 1% of a very large machine
        # from no cap; do not turn that inconclusive observation into a pass.
        upper = logical_cpus * .01 + .15
        if upper >= .8:
            self.skipTest('single-thread CPU observation is inconclusive on this CPU count')
        samples = {}
        for name, rate in [('baseline', 100), ('capped', 1)]:
            marker = self.folder / (name + '.json')
            result = self.run_worker(CPU_WORKER, name, marker,
                                     limits=replace(LIMITS, cpu_percent=rate, timeout_seconds=10))
            self.assert_passed(result)
            data = json.loads(marker.read_text(encoding='utf-8'))
            self.assertGreater(data['wall'], 0)
            samples[name] = {**data, 'ratio': data['cpu'] / data['wall']}
        samples.update(logical_cpus=logical_cpus, upper_ratio=upper)
        save_json(self.folder / 'cpu-observation.json', samples)
        if samples['baseline']['ratio'] <= max(.55, upper + .1):
            self.skipTest('baseline was too busy/throttled to distinguish the CPU cap')
        self.assertLess(samples['capped']['ratio'], upper, samples)

    def test_job_close_reclaims_a_live_descendant(self):
        pid_file, ready, release = (self.folder / name for name in ('pid', 'ready', 'release'))
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.WaitForSingleObject.restype = wintypes.DWORD
        kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
        kernel.TerminateProcess.restype = wintypes.BOOL
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle.restype = wintypes.BOOL
        state = {'handle': None, 'alive_before_parent_exit': False}
        original_hit = runner.WindowsJob.memory_limit_hit

        def observe_descendant(job):
            if state['handle'] is None and pid_file.exists() and ready.exists():
                pid = int(pid_file.read_text(encoding='ascii'))
                handle = kernel.OpenProcess(0x00100000 | 0x0001, False, pid)
                if handle:
                    state['handle'] = handle
                    state['alive_before_parent_exit'] = kernel.WaitForSingleObject(handle, 0) == 258
                    release.write_text('observed', encoding='ascii')
            return original_hit(job)

        try:
            with patch.object(runner.WindowsJob, 'memory_limit_hit', observe_descendant):
                result = self.run_worker(DESCENDANT_WORKER, 'descendant', pid_file, ready, release,
                                         limits=replace(LIMITS, timeout_seconds=9))
            self.assert_passed(result)
            self.assertTrue(state['alive_before_parent_exit'], state)
            self.assertIsNotNone(state['handle'])
            waited = kernel.WaitForSingleObject(state['handle'], 2000)
            save_json(self.folder / 'descendant-observation.json',
                      {'alive_before_parent_exit': state['alive_before_parent_exit'],
                       'wait_after_job_close': waited, 'pid': int(pid_file.read_text(encoding='ascii'))})
            self.assertEqual(waited, 0, 'descendant survived Job close')
        finally:
            if state['handle']:
                # Only this captured synthetic descendant can be terminated here.
                if kernel.WaitForSingleObject(state['handle'], 0) == 258:
                    kernel.TerminateProcess(state['handle'], 99)
                    kernel.WaitForSingleObject(state['handle'], 2000)
                kernel.CloseHandle(state['handle'])

    def test_isolated_shared_lock_contention_and_busy_main(self):
        legacy = legacy_module()
        self.assertEqual(runner.LOCK, legacy.LOCK)
        self.assertEqual(runner.LOCK, Path('work/batch-validation-20261106/runner.lock'))
        root = self.folder / 'isolated-workspace'
        require_isolated_lock_root(root)
        (root / runner.LOCK).parent.mkdir(parents=True)
        (root / 'outputs').mkdir()

        def probe(label, *arguments):
            proc = subprocess.run([sys.executable, '-B', str(SELF), *map(str, arguments)],
                                  cwd=self.folder, capture_output=True, timeout=10,
                                  creationflags=subprocess.CREATE_NO_WINDOW)
            (self.folder / (label + '.stdout.log')).write_bytes(proc.stdout)
            (self.folder / (label + '.stderr.log')).write_bytes(proc.stderr)
            return proc.returncode

        with legacy.workspace_lock(root):
            self.assertEqual(probe('legacy-holds-current-blocked', '--lock-probe', 'current', root), 23)
            self.assertEqual(probe('busy-main', '--main-probe', root), 2)
        busy = root / 'outputs/busy'
        admission = json.loads((busy / 'admission.json').read_text(encoding='utf-8'))
        status = json.loads((busy / 'batch-status.json').read_text(encoding='utf-8'))
        self.assertEqual(admission['status'], 'blocked')
        self.assertIn('workspace_busy', admission['reasons'][0]['detail'])
        self.assertEqual(status['status'], 'blocked')
        self.assertIn('workspace_busy', status['blocked_reason'])
        self.assertFalse((busy / 'selection.json').exists())
        self.assertFalse((root / 'never-created.dwg').exists())
        self.assertEqual(probe('current-after-release', '--lock-probe', 'current', root), 0)
        with runner.workspace_lock(root):
            self.assertEqual(probe('current-holds-legacy-blocked', '--lock-probe', 'legacy', root), 23)
        self.assertEqual(probe('legacy-after-release', '--lock-probe', 'legacy', root), 0)

    def test_python_and_node_gate_accept_go_and_reject_bad_input(self):
        py_target = self.folder / 'payload.py'
        js_target = self.folder / 'payload.mjs'
        py_target.write_text('import sys\nfrom pathlib import Path\nPath(sys.argv[1]).write_text("worked")\n',
                             encoding='utf-8')
        js_target.write_text("import {writeFileSync} from 'node:fs';\nwriteFileSync(process.argv[2], 'worked');\n",
                             encoding='utf-8')
        self.assertTrue(runner.NODE.is_file(), 'configured Node runtime is missing')
        variants = [('go', b'GO\n', True), ('wrong', b'NO\n', False),
                    ('extra-space', b'GO \n', False), ('eof', b'', False)]
        for kind in ('python', 'node'):
            for label, signal, should_work in variants:
                with self.subTest(gate=kind, input=label):
                    marker = self.folder / (kind + '-' + label + '.worked')
                    if kind == 'python':
                        args = [sys.executable, '-B', str(HERE / 'python-gate.py'), str(py_target), str(marker)]
                    else:
                        args = [str(runner.NODE), str(HERE / 'node-gate.mjs'), str(js_target), str(marker)]
                    rc, early_work, early_exit = self.run_manual_gate(kind + '-' + label, args, signal, marker)
                    self.assertFalse(early_work, 'payload ran before input was sent')
                    self.assertFalse(early_exit, 'gate process exited before input was sent')
                    self.assertEqual(marker.exists(), should_work)
                    if should_work:
                        self.assertEqual(rc, 0)
                    else:
                        self.assertNotEqual(rc, 0)
                        stderr = (self.folder / (kind + '-' + label + '.stderr.log')).read_text(
                            encoding='utf-8', errors='replace')
                        self.assertIn('invalid_gate', stderr)

    def run_manual_gate(self, name, args, signal, marker):
        # This directly exercises rejection by each real gate, under a real Job.
        # runner.command itself only sends GO, so it cannot send invalid input.
        job, process = None, None
        stdout_path = self.folder / (name + '.stdout.log')
        stderr_path = self.folder / (name + '.stderr.log')
        try:
            job = runner.WindowsJob(256 * runner.MIB, 25)
            with stdout_path.open('wb') as stdout, stderr_path.open('wb') as stderr:
                process = subprocess.Popen(args, cwd=self.folder, env=os.environ.copy(), stdin=subprocess.PIPE,
                                           stdout=stdout, stderr=stderr, creationflags=subprocess.CREATE_NO_WINDOW)
                job.assign(process)
                job.memory(process)
                time.sleep(.25)
                early_work, early_exit = marker.exists(), process.poll() is not None
                if signal:
                    process.stdin.write(signal)
                    process.stdin.flush()
                process.stdin.close()
                process.wait(timeout=8)
                rc = process.returncode
            save_json(self.folder / (name + '.result.json'),
                      {'returncode': rc, 'payload_before_signal': early_work,
                       'process_exited_before_signal': early_exit})
            return rc, early_work, early_exit
        finally:
            if job:
                job.close()
            if process:
                if process.stdin and not process.stdin.closed:
                    process.stdin.close()
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=5)


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    global EVIDENCE
    if sys.argv[1:2] in (['--lock-probe'], ['--main-probe']):
        return child_probe(sys.argv[1:])
    if len(sys.argv) != 1:
        raise SystemExit('Run without arguments; all tests are serial and synthetic.')
    if os.name != 'nt':
        raise SystemExit('Windows is required for these real Job and byte-range lock tests.')
    if not OUTPUT_PARENT.is_dir():
        raise SystemExit('Expected existing output parent is missing: ' + str(OUTPUT_PARENT))
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    EVIDENCE = OUTPUT_PARENT / ('resource-tests-' + stamp + '-' + uuid.uuid4().hex[:8])
    EVIDENCE.mkdir(exist_ok=False)
    sources = [SELF, HERE / 'runner.py', LEGACY_PATH, HERE / 'python-gate.py', HERE / 'node-gate.mjs']
    before = {str(p): digest(p) for p in sources}
    save_json(EVIDENCE / 'protocol.json', {
        'scope': 'synthetic tests only; no DWG or XLSX',
        'real_shared_lock_opened': False,
        'isolated_lock_root': 'each lock test folder/isolated-workspace',
        'max_extra_virtual_commit_request_mib': 96,
        'group_test_max_extra_commit_if_cap_failed_mib': 128,
        'cpu_observation': 'one short single-thread baseline and 1% Job; may be skipped if inconclusive',
        'sources_sha256_before': before,
        'python': sys.executable,
    })
    print('Evidence:', EVIDENCE, flush=True)
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(ResourceGuardTests)
    with (EVIDENCE / 'test-results.txt').open('w', encoding='utf-8') as stream:
        result = unittest.TextTestRunner(stream=stream, verbosity=2).run(suite)
    after = {str(p): digest(p) for p in sources}
    unchanged = before == after
    successful = result.wasSuccessful() and unchanged
    status = 'passed_with_skips' if successful and result.skipped else 'passed' if successful else 'failed'
    summary = {'tests_run': result.testsRun, 'failures': len(result.failures),
               'errors': len(result.errors), 'skipped': len(result.skipped),
               'skip_reasons': [reason for _, reason in result.skipped],
               'sources_unchanged_during_run': unchanged,
               'sources_sha256_after': after,
               'status': status}
    save_json(EVIDENCE / 'summary.json', summary)
    print(json.dumps(summary, ensure_ascii=False, indent=2), flush=True)
    print('Details:', EVIDENCE / 'test-results.txt', flush=True)
    return 0 if successful else 1


if __name__ == '__main__':
    raise SystemExit(main())
