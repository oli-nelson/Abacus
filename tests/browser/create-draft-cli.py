"""Real Beads draft/publish contract; never operates on the source repository.

Leaves a recovery journal before each command. Only the newly created fixture
issues are closed at exit; unknown create outcomes remain safely deferred for
manual inspection. This verifies CLI semantics, not the dashboard HTTP action.
"""
import datetime
import json
import os
from pathlib import Path
import subprocess
import uuid


REPO = Path('/tmp/abacus-web-contract')
assert REPO.resolve() == Path('/private/tmp/abacus-web-contract').resolve()
assert (REPO / '.beads').is_dir(), 'Prepare the disposable contract fixture first'
RUN = uuid.uuid4().hex
JOURNAL = Path('/tmp') / f'abacus-create-draft-{RUN}.json'
DEFER = '9999-12-31T00:00:00Z'
report = {'run': RUN, 'repository': str(REPO.resolve()), 'issues': [], 'checks': [],
          'scope': 'Real CLI draft staging and dependency-aware publication; no HTTP/runtime acceptance.'}
created = []


def save():
    JOURNAL.write_text(json.dumps(report, indent=2) + '\n')


def bd(*args):
    report['lastCommand'] = list(args)
    save()
    result = subprocess.run(['bd', *args], cwd=REPO,
                            env={**os.environ, 'BEADS_ACTOR': 'draft-contract-smoke'},
                            text=True, capture_output=True, timeout=30)
    if result.returncode:
        report.setdefault('commandFailures', []).append(
            {'args': list(args), 'exitCode': result.returncode,
             'stdout': result.stdout, 'stderr': result.stderr})
        save()
        result.check_returncode()
    return json.loads(result.stdout)


def show(issue):
    result = bd('--readonly', 'show', issue, '--json')
    assert isinstance(result, list) and len(result) == 1, result
    assert result[0]['id'] == issue, result
    return result[0]


def ready(issue):
    result = bd('--readonly', 'ready', '--unassigned', '--limit', '0', '--json')
    assert isinstance(result, list), result
    return any(row['id'] == issue for row in result)


def check(name):
    report['checks'].append(name)
    save()


def stage(kind):
    title = f'--literal draft {kind} {RUN}'
    description = '--literal description\n<script>not executable</script>'
    result = bd('create', '--title=' + title, '--description=' + description,
                '--type=' + kind, '--priority=2', '--labels=draft-contract,abacus:low_reasoning',
                '--metadata={"abacus_target":"main"}', '--defer=' + DEFER, '--json')
    issue = result['id']
    assert isinstance(issue, str) and issue, result
    created.append(issue)
    report['issues'].append({'id': issue, 'type': kind})
    save()
    row = show(issue)
    assert row['title'] == title and row['description'] == description, row
    assert row['issue_type'] == kind and row['priority'] == 2, row
    assert set(row['labels']) == {'draft-contract', 'abacus:low_reasoning'}, row
    assert row['metadata']['abacus_target'] == 'main', row
    assert not row.get('assignee'), row
    assert datetime.datetime.fromisoformat(row['defer_until'].replace('Z', '+00:00')).year == 9999, row
    assert not ready(issue), row
    check(kind + ': persisted deferred creation is non-ready with exact content and target')

    # Separate process/read boundaries model interruption after each committed
    # stage. Never clear deferral based solely on an update command's exit code.
    bd('update', issue, '--status=blocked', '--json')
    row = show(issue)
    assert row['status'] == 'blocked' and row.get('defer_until') and not ready(issue), row
    check(kind + ': blocked plus deferred survives a fresh read')
    bd('update', issue, '--defer=', '--json')
    row = show(issue)
    assert row['status'] == 'blocked' and not row.get('defer_until') and not ready(issue), row
    check(kind + ': clearing only the staging deferral leaves a non-ready draft')
    return issue


save()
try:
    report['beadsVersion'] = subprocess.check_output(['bd', '--version'], text=True).strip()
    prerequisite = stage('task')
    epic = stage('epic')
    # Beads 1.2.2 rejects this mixed-type edge. Preserve the exact observed
    # restriction rather than assuming every syntactically valid edge is valid.
    try:
        bd('dep', 'add', epic, prerequisite, '--json')
    except subprocess.CalledProcessError as error:
        assert 'epics can only block other epics' in json.loads(error.stdout)['error']
        assert bd('--readonly', 'dep', 'list', epic, '--json') == []
        check('unsupported epic/task dependency is rejected without adding an edge')
    else:
        raise AssertionError('Beads mixed-type edge contract changed; review before updating expectations')
    dependent = stage('task')
    bd('dep', 'add', dependent, prerequisite, '--json')
    bd('update', dependent, '--status=open', '--json')
    assert show(dependent)['status'] == 'open' and not ready(dependent)
    edges = bd('--readonly', 'dep', 'list', dependent, '--json')
    assert any(edge.get('id') == prerequisite or edge.get('depends_on_id') == prerequisite for edge in edges), edges
    check('explicit publication preserves the real dependency and does not imply readiness')
    bd('update', prerequisite, '--status=open', '--json')
    assert show(prerequisite)['status'] == 'open' and ready(prerequisite)
    assert not ready(dependent)
    check('explicit publication exposes only the dependency-free first wave')
    report['contractPassed'] = True
finally:
    # Do not clear unknown staging state or delete evidence. The disposable
    # fixture has no dispatchers. Close only IDs returned by this invocation.
    cleanup_errors = []
    # Prerequisites were created first: close them before their dependents.
    for issue in created:
        try:
            bd('close', issue, '--reason=Disposable draft contract fixture cleanup', '--json')
            assert show(issue)['status'] == 'closed'
        except Exception as error:
            cleanup_errors.append({'id': issue, 'error': str(error)})
    report['cleanupErrors'] = cleanup_errors
    report['passed'] = report.get('contractPassed', False) and not cleanup_errors
    save()
    print(json.dumps({'journal': str(JOURNAL), **report}, indent=2))
    if cleanup_errors:
        raise RuntimeError('Fixture cleanup incomplete; inspect the recorded IDs before rerunning')
