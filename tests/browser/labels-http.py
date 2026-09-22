import json,subprocess,socket,time,urllib.request,urllib.error,uuid,signal
from pathlib import Path
repo=Path('/tmp/abacus-web-contract')
assert repo.name=='abacus-web-contract' and (repo/'.beads').is_dir()
sock=socket.socket();sock.bind(('127.0.0.1',0));port=sock.getsockname()[1];sock.close()
process=subprocess.Popen(['dotnet','src/Abacus/bin/Debug/net10.0/abacus.dll','dashboard','--repo',str(repo),'--port',str(port),'--actor','label-contract-smoke'],stdout=subprocess.PIPE,stderr=subprocess.PIPE)
base=f'http://127.0.0.1:{port}'
def get(path):
 with urllib.request.urlopen(base+path,timeout=20) as r:return json.load(r)
def action(issue,revision,**fields):
 context=get('/api/v1/mutations/context')
 body=dict(requestId=f"{context['session']}:{context['serverUnixMilliseconds']}:{uuid.uuid4().hex}",expectedRevision=revision,action='edit',**fields)
 req=urllib.request.Request(base+'/api/v1/issues/'+issue+'/actions',data=json.dumps(body).encode(),headers={'Content-Type':'application/json','X-Abacus-Request':'1'})
 try:
  with urllib.request.urlopen(req,timeout=20) as r:return r.status,json.load(r)
 except urllib.error.HTTPError as e:return e.code,json.load(e)
label='--web-label-smoke-'+uuid.uuid4().hex[:8]
try:
 for i in range(150):
  if process.poll() is not None:raise RuntimeError(process.stderr.read().decode())
  try:snapshot=get('/api/v1/snapshot');break
  except (urllib.error.URLError,ConnectionError):time.sleep(.2)
 else:raise RuntimeError('startup timed out; no restart attempted')
 issue=snapshot['issues'][0];old=set(issue['labels'])
 code,rejected=action(issue['id'],issue['revision'],addLabels=['abacus:needs-user-attention'])
 assert code==400,(code,rejected)
 code,added=action(issue['id'],issue['revision'],addLabels=[label])
 assert code==200 and added['outcome']=='completed',(code,added)
 assert set(added['issue']['labels'])==old|{label},added
 code,removed=action(issue['id'],added['revision'],removeLabels=[label])
 assert code==200 and removed['outcome']=='completed',(code,removed)
 assert set(removed['issue']['labels'])==old,removed
 print('Real bd/HTTP label delta: reserved rejection, literal leading-option add, verified removal and original-label preservation passed')
finally:
 if process.poll() is None:process.send_signal(signal.SIGTERM)
 try:stdout,stderr=process.communicate(timeout=25)
 except subprocess.TimeoutExpired:
  process.kill();stdout,stderr=process.communicate();raise
 assert not stdout,stdout
