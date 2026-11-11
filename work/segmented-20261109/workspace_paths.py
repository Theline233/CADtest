"""Workspace and executable discovery shared by live and frozen Python stages."""
import os
from pathlib import Path
import shutil


def workspace_root():
    configured = os.environ.get('CADTEST_ROOT')
    if configured:
        root = Path(configured)
        if not root.is_absolute():
            raise ValueError('CADTEST_ROOT must be an absolute workspace directory')
    else:
        root = Path(__file__).resolve().parents[2]
    root = root.resolve(strict=True)
    if not root.is_dir():
        raise ValueError('Workspace root must be a directory')
    return root


def node_executable(value=None, root=None):
    root = root or workspace_root()
    requested = value or os.environ.get('CADTEST_NODE')
    if requested:
        found = shutil.which(str(requested))
        candidate = Path(found or requested).expanduser().resolve()
    else:
        bundled = root / 'runtime/node/node.exe'
        found = str(bundled) if bundled.is_file() else shutil.which('node')
        if not found:
            raise ValueError('Node.js not found. Install Node.js or provide --node / CADTEST_NODE.')
        candidate = Path(found).resolve()
    if not candidate.is_file():
        raise ValueError(f'Node.js executable not found: {candidate}')
    return candidate


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument('--task-output', required=True)
    value = parser.parse_args().task_output
    root = workspace_root()
    target = (root / value).resolve()
    outputs = (root / 'outputs').resolve()
    if not outputs.is_relative_to(root) or target == outputs or not target.is_relative_to(outputs) or target.exists():
        parser.error('Use a new task directory strictly below this workspace outputs directory')
    print(target)
