// Issue composer checks; requires fixture-server.mjs and a local Chrome DevTools endpoint (default 19222).
import assert from 'node:assert/strict';
import {writeFile} from 'node:fs/promises';
const pages=await (await fetch(`http://127.0.0.1:${process.env.CDP_PORT||19222}/json/list`)).json();
const ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
await new Promise(r=>ws.addEventListener('open',r,{once:true}));
let id=0;const pending=new Map(),errors=[];let apiCalls=0;
ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.method==='Network.requestWillBeSent'&&m.params.request.url.includes('/api/v1/'))apiCalls++;if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);}});
function call(method,params={}){return new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}));});}
async function evaluate(expression){const r=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function until(expression){for(let i=0;i<100;i++){if(await evaluate(expression))return;await delay(100);}throw new Error('Timed out: '+expression+' errors: '+JSON.stringify(errors));}
await call('Page.enable');await call('Runtime.enable');
await call('Page.addScriptToEvaluateOnNewDocument',{source:"window.confirmations=[];window.confirm=s=>{confirmations.push(s);return true};"});
await call('Emulation.setDeviceMetricsOverride',{width:1671,height:941,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:'http://127.0.0.1:18081/?issue=bd-a1f&from=2026-09-21T09%3A00%3A00Z'});
await until("!document.getElementById('issue-form').hidden && document.getElementById('write-actor').textContent.includes('fixture operator')");
async function set(id,value){await evaluate(`document.getElementById(${JSON.stringify(id)}).value=${JSON.stringify(value)};document.getElementById(${JSON.stringify(id)}).dispatchEvent(new Event('input',{bubbles:true}));`);}
await set('write-action','edit');
await evaluate("document.getElementById('write-submit').click()");
await until("document.getElementById('write-result').textContent.includes('No content')");
assert.equal((await (await fetch('http://127.0.0.1:18081/fixture/requests')).json()).length,0);
await set('write-add-labels','team:ui\n--literal');await set('write-remove-labels','frontend');
for(const id of ['bd-b7c','bd-a1f'])await evaluate(`[...document.querySelectorAll('.lane-card')].find(n=>n.textContent.includes('${id}')).click()`);
assert.equal(await evaluate("document.getElementById('write-add-labels').value"),'team:ui\n--literal');
await evaluate("document.getElementById('timeline-scrub').value=100;document.getElementById('timeline-scrub').dispatchEvent(new Event('input'))");
assert.equal(await evaluate("document.getElementById('issue-form').hidden"),true);
await evaluate("document.getElementById('timeline-live').click()");await until("!document.getElementById('issue-form').hidden");
assert.equal(await evaluate("document.getElementById('write-remove-labels').value"),'frontend');
await fetch('http://127.0.0.1:18081/fixture/race');
await until("document.getElementById('selected-title').textContent==='Externally updated title'");
assert.equal(await evaluate("document.getElementById('write-add-labels').value"),'team:ui\n--literal');
await evaluate("document.getElementById('write-submit').click()");
await until("document.getElementById('write-result').textContent.includes('rejected')");
await evaluate("document.getElementById('write-review').click();document.getElementById('write-submit').click()");
await until("document.getElementById('write-result').textContent.includes('Result unavailable')");
assert.equal(await evaluate("document.getElementById('write-add-labels').disabled"),true);
assert.equal(await evaluate("document.getElementById('write-add-labels').value"),'team:ui\n--literal');
await evaluate("document.getElementById('write-submit').click()");
await until("document.getElementById('write-result').textContent.includes('completed') && !document.getElementById('write-add-labels').disabled");
const requests=await (await fetch('http://127.0.0.1:18081/fixture/requests')).json();
assert.equal(requests.length,3);assert.deepEqual(requests[1],requests[2]);assert.notEqual(requests[0].requestId,requests[1].requestId);
for(const request of requests){assert.deepEqual(request.addLabels,['team:ui','--literal']);assert.deepEqual(request.removeLabels,['frontend']);for(const key of ['title','description','priority'])assert.ok(!(key in request),'Unchanged fields must not overwrite external edits');}
assert.equal(await evaluate("document.getElementById('write-add-labels').value"),'');
assert.equal(await evaluate("document.getElementById('write-title').value"),'Externally updated title');
assert.ok(await evaluate("confirmations.every(s=>s.includes('fixture operator'))"));
await set('write-title','Unsent title draft');await set('write-add-labels','unsent-label');await set('write-notes','unsent notes');
await set('write-action','comment');await set('write-text','Independent comment');
await evaluate("document.getElementById('write-submit').click()");
await until("document.getElementById('write-result').textContent.includes('completed') && !document.getElementById('write-submit').disabled");
await set('write-action','edit');
assert.equal(await evaluate("document.getElementById('write-title').value"),'Unsent title draft');
assert.equal(await evaluate("document.getElementById('write-add-labels').value"),'unsent-label');
assert.equal(await evaluate("document.getElementById('write-notes').value"),'unsent notes');
await evaluate("document.getElementById('issue-form').scrollIntoView({block:'end'})");
const shot=await call('Page.captureScreenshot',{format:'png'});await writeFile('/tmp/abacus-label-composer.png',Buffer.from(shot.data,'base64'));
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('Issue composer: label draft navigation/playback/live-update retention, revision conflict, same-ID unknown retry, delta-only payload and authoritative refresh passed');
await call('Browser.close');ws.close();
