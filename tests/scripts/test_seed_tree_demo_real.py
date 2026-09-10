"""Opt-in real Beads regression; creates only a disposable embedded database.

ABACUS_TEST_REAL_BEADS=1 python3 -m unittest discover -s tests/scripts -v
"""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / 'scripts' / 'seed-tree-demo.sh'


@unittest.skipUnless(os.environ.get('ABACUS_TEST_REAL_BEADS') == '1',
                     'set ABACUS_TEST_REAL_BEADS=1 to exercise real bd')
class RealBeadsTreeTests(unittest.TestCase):
    def test_parent_first_ready_queue(self):
        with tempfile.TemporaryDirectory(prefix='abacus-tree-real-') as tmp:
            # Never inherit a caller's database, shared-server, or Git routing.
            env = {k: v for k, v in os.environ.items()
                   if not k.startswith(('BEADS_', 'BD_', 'DOLT_', 'GIT_'))}
            home = Path(tmp) / 'home'
            home.mkdir()
            env.update(HOME=str(home), XDG_CONFIG_HOME=str(home / '.config'),
                       GIT_CONFIG_NOSYSTEM='1')
            repo = Path(tmp) / 'repo'
            repo.mkdir()

            def run(*args):
                result = subprocess.run(args, cwd=repo, env=env, text=True,
                                        capture_output=True, timeout=180)
                self.assertEqual(result.returncode, 0, result.stderr)
                return result.stdout

            def bd(*args):
                return json.loads(run('bd', *args, '--json'))

            run('git', 'init', '-q')
            run('git', 'config', 'user.name', 'Tree Regression')
            run('git', 'config', 'user.email', 'tree@example.invalid')
            run('bd', 'init', '--prefix', 'treecheck', '--skip-agents',
                '--skip-hooks', '--role', 'maintainer', '--non-interactive')
            run('bash', str(SCRIPT), str(repo))
            issues = bd('list', '--label', 'demo:agent-tree', '--limit', '0')
            self.assertEqual(len(issues), 22)
            epic = next(i['id'] for i in issues if i['issue_type'] == 'epic')
            nodes = {}
            for issue in issues:
                if issue['issue_type'] != 'task':
                    continue
                path = ('root' if issue['title'].startswith('Tree root:') else
                        re.match(r'Tree node ([\d.]+):', issue['title'])[1])
                nodes[path] = issue['id']

            for path, issue_id in nodes.items():
                deps = bd('dep', 'list', issue_id)
                self.assertEqual({d['id'] for d in deps if d['dependency_type'] == 'parent-child'}, {epic})
                parent = path.split('.')[0] if '.' in path else 'root'
                expected = set() if path == 'root' else {nodes[parent]}
                self.assertEqual({d['id'] for d in deps if d['dependency_type'] == 'blocks'}, expected)

            def ready():
                return {i['id'] for i in bd('ready', '--type', 'task', '--label',
                                           'demo:agent-tree', '--limit', '0')}

            root = nodes['root']
            self.assertEqual(ready(), {root})
            run('bd', 'update', root, '--status', 'in_progress')
            self.assertEqual(ready(), set())
            run('bd', 'update', root, '--status', 'blocked')
            self.assertEqual(ready(), set())  # Human naming gate blocks the tree.
            # Simulated completion is confined to this disposable test database.
            run('bd', 'close', root, '--reason', 'Regression: simulate root completion')
            branches = {nodes[str(i)] for i in range(1, 5)}
            self.assertEqual(ready(), branches)
            for i in range(1, 5):
                run('bd', 'close', nodes[str(i)], '--reason', 'Regression: simulate branch completion')
                expected = {nodes[str(j)] for j in range(i + 1, 5)} | {
                    nodes[f'{j}.{k}'] for j in range(1, i + 1) for k in range(1, 5)}
                self.assertEqual(ready(), expected)
