"""Portable fake-HTTP/real-filesystem regression runner. No Wine or GUI."""
import argparse
import json
import os
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--baseline', action='store_true')
parser.add_argument('--dll', type=Path)
parser.add_argument('--artifact', action='append', type=Path, default=[])
args = parser.parse_args()
root = Path(__file__).resolve().parent
if not args.dll:
    subprocess.run(['dotnet', 'build', '-c', 'Release'], cwd=root, check=True)
dll = args.dll.resolve() if args.dll else root / 'bin/Release/net10.0/ForkContentCache.dll'
cases = [['baseline'], ['baseline-scripts']] if args.baseline else json.loads((root / 'cases.json').read_text())
if not args.baseline:
    cases += [['artifact', str(p.resolve())] for p in args.artifact]
for case in cases:
    result = subprocess.run(['dotnet', str(dll), *case], cwd=root, capture_output=True, text=True)
    output = result.stdout + result.stderr
    print('CASE', json.dumps(case), '\n' + output, 'EXIT=' + str(result.returncode), flush=True)
    if args.baseline:
        expected = 'ASSERT ' + case[0] + ': previous content lost; actual='
        if result.returncode == 0 or expected not in output:
            raise SystemExit('Baseline did not reproduce expected content-loss assertion')
    elif result.returncode != 0:
        raise SystemExit('Failed: ' + repr(case))
print('PASS', len(cases), 'expected baseline assertion failures' if args.baseline else 'real-IO scenarios')
