#!/usr/bin/env python3
"""Opt-in real-CLI idle test. Requires an existing disposable abacus-web-contract repo.
No API writes are submitted. A uniquely named untracked fixture file is created
and removed after shutdown. Ten clients each open main and worktree SSE streams. Run: python3 tests/browser/noop-http.py /tmp/abacus-web-contract
"""
import concurrent.futures, hashlib, json, os, platform, pathlib, shutil, signal, socket, subprocess, sys, tempfile, threading, time, urllib.request, urllib.parse

repo = pathlib.Path(sys.argv[1]).resolve()
assert repo.name == 'abacus-web-contract', 'Only the named disposable fixture is allowed'
dll = pathlib.Path(__file__).resolve().parents[2] / 'src/Abacus/bin/Debug/net10.0/abacus.dll'
assert dll.exists(), 'Build the application first'
source_root = pathlib.Path(__file__).resolve().parents[2] / 'src/Abacus'
source_files = sorted(p for p in source_root.rglob('*') if p.is_file() and p.suffix in {'.cs','.csproj','.js','.css','.html'} and not {'bin','obj'}.intersection(p.relative_to(source_root).parts))
assert dll.stat().st_mtime >= max(p.stat().st_mtime for p in source_files), 'Rebuild after source changes before measuring'
source_hash = hashlib.sha256()
for path in source_files:
    source_hash.update(str(path.relative_to(source_root)).encode()+b'\0'+path.read_bytes()+b'\0')

with tempfile.TemporaryDirectory(prefix='abacus-idle-') as temp:
    folder = pathlib.Path(temp)
    log = folder / 'calls.jsonl'
    for tool in ['bd', 'git']:
        real = shutil.which(tool)
        assert real, tool
        wrapper = folder / tool
        wrapper.write_text(f'''#!{sys.executable}
import json, os, subprocess, sys, time
start=time.monotonic()
p=subprocess.run([{real!r}, *sys.argv[1:]], capture_output=True)
record=json.dumps(dict(tool={tool!r},args=sys.argv[1:],seconds=time.monotonic()-start,bytes=len(p.stdout),exit=p.returncode))+'\\n'
fd=os.open({str(log)!r},os.O_WRONLY|os.O_CREAT|os.O_APPEND,0o600)
os.write(fd,record.encode());os.close(fd)
sys.stdout.buffer.write(p.stdout);sys.stderr.buffer.write(p.stderr);sys.exit(p.returncode)
''')
        wrapper.chmod(0o700)
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    env = dict(os.environ, PATH=temp + os.pathsep + os.environ['PATH'])
    fixture_handle = tempfile.NamedTemporaryFile(prefix='idle-watch-', suffix='.txt', dir=repo, delete=False)
    fixture_path = pathlib.Path(fixture_handle.name)
    fixture_handle.write(b'Idle worktree fingerprint fixture.\n'); fixture_handle.close()
    server = None
    ready = threading.Barrier(21)
    measuring = threading.Event()
    pool = concurrent.futures.ThreadPoolExecutor(max_workers=20)
    fixture_file = fixture_path
    def get(path, headers=None):
        return urllib.request.urlopen(urllib.request.Request(base+path, headers=headers or {}), timeout=80)
    try:
        server = subprocess.Popen(['dotnet', str(dll), 'dashboard', '--repo', str(repo), '--port', str(port)], env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        for _ in range(300):
            assert server.poll() is None, server.communicate()
            try:
                with get('/api/v1/project') as response: project = json.load(response)
                break
            except OSError: time.sleep(.1)
        else: raise RuntimeError('startup timeout')
        assert project['name'] == 'abacus-web-contract'
        with get('/api/v1/branches') as response: branches = json.load(response)
        worktree = next(w for w in branches['facts']['worktrees'] if pathlib.Path(w['path']).resolve() == repo)
        assert worktree['dirty'] is True, 'Fixture must exercise visible dirty content'
        with get('/api/v1/branches/history?branch=refs%2Fheads%2Fmain&limit=100') as response: json.load(response)
        with get('/api/v1/snapshot') as response: initial = json.load(response)
        if initial['issues']:
            issue_id = urllib.parse.quote(initial['issues'][0]['id'], safe='')
            with get('/api/v1/issues/'+issue_id+'/activity?limit=25') as response: json.load(response)
        with get('/api/v1/snapshot') as response: cursor = response.headers['ETag'].strip('"')
        def client(kind):
            events = []
            path = '/api/v1/events' if kind == 'main' else '/api/v1/worktrees/events?id='+worktree['id']
            with get(path, {'Last-Event-ID': cursor} if kind == 'main' else {}) as response:
                if kind == 'worktree':
                    for line in response:
                        if line.startswith(b'data: '):
                            data = json.loads(line[6:]); assert not data['stale'], data
                            assert fixture_file.name in data['diff']['untracked'], data
                            break
                    else: raise RuntimeError('Worktree stream ended before initial content')
                ready.wait(timeout=40)
                for line in response:
                    if line.startswith(b'event: disconnected'): break
                    if line.startswith(b'data: ') and measuring.is_set(): events.append(kind)
            return events
        clients = [pool.submit(client, kind) for _ in range(10) for kind in ['main', 'worktree']]
        ready.wait(timeout=40)
        time.sleep(6)  # complete warmup while both streams are actually subscribed
        with get('/api/v1/snapshot') as response:
            before = response.read(); etag = response.headers['ETag']
        with get('/api/v1/diagnostics') as response: metrics_before = json.load(response)
        initial_calls = len(log.read_text().splitlines())
        started = time.monotonic(); measuring.set()
        time.sleep(60)
        with get('/api/v1/snapshot') as response:
            assert response.headers['ETag'] == etag
            assert response.read() == before
        with get('/api/v1/diagnostics') as response: metrics_after = json.load(response)
        measured = [json.loads(line) for line in log.read_text().splitlines()[initial_calls:]]
        seconds = time.monotonic()-started; measuring.clear()
        server.send_signal(signal.SIGTERM)
        out, err = server.communicate(timeout=20)
        assert server.returncode == 0, err.decode()
        events = [future.result(timeout=10) for future in clients]
        assert not any(events), events
        detail = [c for c in measured if 'history' in c['args'] or 'log' in c['args'] or 'diff' in c['args']]
        assert not detail, detail
        assert all(c['exit'] == 0 for c in measured), measured
        def delta(group, key): return metrics_after[group][key]-metrics_before[group][key]
        assert delta('issues', 'rebuiltIssues') == 0
        assert delta('stream', 'serializedIssues') == 0
        assert delta('stream', 'snapshotBuilds') == 0
        assert delta('git', 'gitDiffCommands') == 0
        exports = [c for c in measured if c['tool'] == 'bd' and 'export' in c['args']]
        hashes = [c for c in measured if c['tool'] == 'git' and 'hash-object' in c['args']]
        record = dict(environment=dict(system=platform.system(),architecture=platform.machine(),git=subprocess.check_output(['git','--version'],text=True).strip(),beads=subprocess.check_output(['bd','version'],text=True).strip(),dotnetSdk=subprocess.check_output(['dotnet','--version'],text=True).strip()),sourceFingerprint=source_hash.hexdigest(),sourceFingerprintScope='src/Abacus .cs/.csproj/.js/.css/.html excluding bin/obj; uncommitted working tree',clients=10,sseConnections=20,visibleWorktrees=1,seconds=round(seconds,2),dataEvents=0,
            repeatHistoryDiffQueries=len(detail),snapshotBytes=len(before),snapshotUnchanged=True,
            projectionRebuilds=0,issueSerializations=0,snapshotBuilds=0,
            sourceCommands=len(measured),exportCalls=len(exports),exportBytes=sum(c['bytes'] for c in exports),
            exportCommandSeconds=round(sum(c['seconds'] for c in exports),4),
            exportHashSeconds=delta('issues','hashSeconds'),worktreeFingerprintBytes=delta('git','worktreeHashBytes'),
            worktreeFingerprintSeconds=delta('git','worktreeFingerprintSeconds'),
            hashObjectCalls=len(hashes),hashObjectCommandSeconds=round(sum(c['seconds'] for c in hashes),4),
            stdoutBytes=len(out),scope='Real standalone HTTP + Beads + Git. Small fixture; not integrated runtime or large-scene benchmark. Stage times include CLI latency, not isolated CPU.')
        pathlib.Path('/tmp/abacus-http-idle.json').write_text(json.dumps(record,indent=2)+'\n')
        print(json.dumps(record, indent=2))
    finally:
        if server is not None and server.poll() is None:
            server.send_signal(signal.SIGTERM)
            try: server.communicate(timeout=20)
            except subprocess.TimeoutExpired: server.kill(); server.communicate()
        measuring.clear()
        pool.shutdown(wait=True, cancel_futures=True)
        if fixture_file is not None: fixture_file.unlink(missing_ok=True)
