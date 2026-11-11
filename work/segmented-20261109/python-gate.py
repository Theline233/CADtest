"""Run a local Python entrypoint only after Job Object GO handshake."""
import runpy
import sys
from pathlib import Path
if sys.stdin.readline() != 'GO\n': raise RuntimeError('invalid_gate')
target = str(Path(sys.argv[1]).resolve())
# Embeddable Python uses an explicit _pth file rather than the script directory.
# A frozen entrypoint must import its own frozen helpers before live package code.
sys.path.insert(0, str(Path(target).parent))
sys.argv = [target, *sys.argv[2:]]
runpy.run_path(target, run_name='__main__')
