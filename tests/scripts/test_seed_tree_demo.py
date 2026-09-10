"""Run with: python3 -m unittest discover -s tests/scripts -v"""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / 'scripts' / 'seed-tree-demo.sh'


class SeedTreeDemoTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.repo = Path(self.tmp.name) / 'existing project'
        self.repo.mkdir()
        self.log = Path(self.tmp.name) / 'calls.jsonl'
        binary = Path(self.tmp.name) / 'bd'
        binary.write_text('''#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
log = Path(os.environ['FAKE_BD_LOG'])
args = sys.argv[1:]
previous = log.read_text().splitlines() if log.exists() else []
with log.open('a') as f:
    f.write(json.dumps({'args': args, 'cwd': os.getcwd()}) + '\\n')
if args[0] == 'where':
    sys.exit(1 if os.environ.get('FAIL_WHERE') else 0)
if args[0] == 'status':
    sys.exit(0)
assert args[0] == 'create', args
number = sum(json.loads(line)['args'][0] == 'create' for line in previous) + 1
if str(number) == os.environ.get('FAIL_CREATE'):
    sys.exit(1)
print('demo-' + str(number))
''')
        binary.chmod(0o755)
        self.env = dict(os.environ, PATH=f'{self.tmp.name}:{os.environ["PATH"]}',
                        FAKE_BD_LOG=str(self.log))

    def run_seed(self, *args, **env):
        return subprocess.run(['bash', str(SCRIPT), *map(str, args)],
                              cwd=self.repo, env=dict(self.env, **env),
                              text=True, capture_output=True)

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()]

    def test_graph_and_template(self):
        result = self.run_seed(self.repo)
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls()
        self.assertEqual([c['args'][0] for c in calls[:2]], ['where', 'status'])
        self.assertTrue(all(Path(c['cwd']).resolve() == self.repo.resolve() for c in calls))
        issues = [c['args'] for c in calls[2:]]
        self.assertEqual(len(issues), 22)
        get = lambda args, flag: args[args.index(flag) + 1]
        self.assertEqual(get(issues[0], '--type'), 'epic')
        paths = {'root': 'demo-2'}
        for number, issue in enumerate(issues[1:], 2):
            self.assertEqual(get(issue, '--type'), 'task')
            self.assertEqual(get(issue, '--parent'), 'demo-1')
            self.assertIn('abacus:low_reasoning', get(issue, '--labels'))
            body = get(issue, '--description')
            self.assertIn('BEADS_ACTOR', body)
            self.assertIn('Close only after', body)
            if number == 2:
                self.assertNotIn('--deps', issue)
            else:
                path, parent = re.search(r"\{ path: '([\d.]+)', parent: '([^']+)'", body).groups()
                self.assertNotIn(path, paths)
                self.assertEqual(get(issue, '--deps'), paths[parent])
                paths[path] = 'demo-' + str(number)
        expected = {'root'} | {str(i) for i in range(1, 5)} | {
            f'{i}.{j}' for i in range(1, 5) for j in range(1, 5)}
        self.assertEqual(set(paths), expected)
        body = get(issues[1], '--description')
        self.assertIn('The agent MUST NOT choose, invent, supply', body)
        self.assertIn('comment or appended note on THIS ticket', body)
        self.assertIn('mark this ticket blocked', body)
        html = body.split('~~~html\n', 1)[1].split('\n~~~', 1)[0]
        records = html.split('const nodes = [', 1)[1].split('];', 1)[0]
        self.assertEqual(records.count('{ path:'), 1)
        self.assertIn("path: 'root', parent: null", records)
        self.assertNotIn('src=', html)
        self.assertEqual(list(self.repo.iterdir()), [])  # Seed issues, never HTML.
        self.assertIn('Created epic demo-1 and 21 node tasks', result.stdout)
        if shutil.which('node'):
            self.check_renderer(html, paths)

    def check_renderer(self, html, paths):
        js = html.split('<script>\n', 1)[1].split('</script>', 1)[0]
        stub = '''
const assert = require('node:assert/strict');
let arcs = 0, edges = 0;
const labels = [];
const fakeContext = new Proxy({}, { get: (_, key) => key === 'createRadialGradient' ?
  () => ({ addColorStop() {} }) : key === 'arc' ? () => arcs++ :
  key === 'lineTo' ? () => edges++ : () => {} });
const elements = new Map();
global.document = {
  getElementById(id) {
    if (!elements.has(id)) elements.set(id, { value: '20', clientWidth: 1200,
      clientHeight: 680, getContext: () => fakeContext, addEventListener() {},
      append(li) { labels.push(li.textContent); } });
    return elements.get(id);
  }, createElement: () => ({})
};
global.window = { devicePixelRatio: 2, addEventListener() {} };
'''
        for full in (False, True):
            source = js
            if full:
                extra = []
                for path, task in paths.items():
                    if path == 'root':
                        continue
                    parent = path.split('.')[0] if '.' in path else 'root'
                    extra.append(json.dumps(dict(path=path, parent=parent, agent='agent-' + path, task=task)))
                source = source.replace('\n];', '\n' + ',\n'.join(extra) + ',\n];', 1)
            count = 21 if full else 1
            assertions = f'''
assert.equal(arcs, {count}); assert.equal(edges, {count - 1});
assert.equal(labels.length, {count});
for (const p of projectedForTest()) assert.ok(p.every(Number.isFinite));
function projectedForTest() {{ return nodes.map(n => position(n.path)); }}
assert.ok(labels.every(label => label.includes(' · ')));
'''
            result = subprocess.run(['node', '-e', stub + source + assertions], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_help_and_bad_arguments_do_not_call_beads(self):
        self.assertEqual(self.run_seed('--help').returncode, 0)
        self.assertNotEqual(self.run_seed('--unknown').returncode, 0)
        self.assertNotEqual(self.run_seed('one', 'two').returncode, 0)
        self.assertFalse(self.log.exists())

    def test_missing_project_does_not_create(self):
        self.assertNotEqual(self.run_seed(FAIL_WHERE='1').returncode, 0)
        self.assertEqual(len(self.calls()), 1)

    def test_existing_html_is_preserved(self):
        file = self.repo / 'agent-tree.html'
        file.write_text('preserve me')
        self.assertNotEqual(self.run_seed().returncode, 0)
        self.assertEqual(file.read_text(), 'preserve me')
        self.assertEqual(len(self.calls()), 2)

    def test_creation_failure_stops_with_partial_ids(self):
        result = self.run_seed(FAIL_CREATE='4')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Created demo-3', result.stderr)
        self.assertIn('inspect the issue IDs', result.stderr)
        self.assertEqual(len(self.calls()), 6)
