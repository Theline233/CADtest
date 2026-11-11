"""Run a local Python entrypoint only after Job Object GO handshake."""
import runpy
import sys
if sys.stdin.readline() != 'GO\n': raise RuntimeError('invalid_gate')
target = sys.argv[1]
sys.argv = [target, *sys.argv[2:]]
runpy.run_path(target, run_name='__main__')
