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
await call('Page.navigate',{url:'http://127.0.0.1:18081/?view=issues'});
await until("!document.getElementById('create-issue').hidden && document.getElementById('project').textContent.includes('fixture')");
async function click(id){await evaluate(`document.getElementById(${JSON.stringify(id)}).click()`);}
async function set(id,value){await evaluate(`document.getElementById(${JSON.stringify(id)}).value=${JSON.stringify(value)};document.getElementById(${JSON.stringify(id)}).dispatchEvent(new Event('input',{bubbles:true}));`);}
await click('create-issue');
await until("!document.getElementById('create-fields').disabled");
assert.ok(await evaluate("document.getElementById('create-actor').textContent.includes('fixture operator')"));
await set('create-name','--literal <script>draft</script>');await set('create-description','Keep exact text\n<script>not HTML</script>');
await set('create-target','release');await set('create-reasoning','abacus:low_reasoning');await set('create-labels','team:ui\n--literal');
await call('Input.dispatchKeyEvent',{type:'rawKeyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});await call('Input.dispatchKeyEvent',{type:'keyUp',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
await until("!document.getElementById('create-dialog').open && document.activeElement.id==='create-issue'");
await click('branches-view');await click('issues-view');await click('create-issue');
assert.equal(await evaluate("document.getElementById('create-name').value"),'--literal <script>draft</script>');
await fetch('http://127.0.0.1:18081/fixture/draft-policy');
await click('create-submit');await until("document.getElementById('create-result').textContent.includes('rejected')");
await click('create-review');await until("!document.getElementById('create-fields').disabled");
assert.equal(await evaluate("document.getElementById('create-target').value"),'release');
assert.equal(await evaluate("document.getElementById('create-name').value"),'--literal <script>draft</script>');
await click('create-submit');await until("document.getElementById('create-result').textContent.includes('Result unavailable')");
assert.equal(await evaluate("document.getElementById('create-fields').disabled"),true);
assert.equal(await evaluate("document.getElementById('create-review').disabled"),true);
await click('create-close');await click('create-issue');await click('create-submit');
await until("document.getElementById('create-result').textContent.includes('completed') && !document.getElementById('create-show').disabled");
const requests=await (await fetch('http://127.0.0.1:18081/fixture/draft-requests')).json();
assert.equal(requests.length,3);assert.deepEqual(requests[1],requests[2]);assert.notEqual(requests[0].requestId,requests[1].requestId);
assert.equal(requests[1].expectedRevision,'b'.repeat(64));assert.deepEqual(requests[1].labels,['team:ui','--literal','abacus:low_reasoning']);
assert.equal(requests[1].target,'release');assert.equal(requests[1].description,'Keep exact text\n<script>not HTML</script>');
assert.equal(await evaluate("document.getElementById('create-submit').disabled"),true);
await call('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:false});
assert.ok(await evaluate("document.getElementById('create-dialog').scrollWidth<=document.getElementById('create-dialog').clientWidth+1"));
await writeFile('/tmp/abacus-create-draft.png',Buffer.from((await call('Page.captureScreenshot',{format:'png'})).data,'base64'));
await click('create-show');
await until("document.getElementById('selected-id').textContent==='bd-created'");
assert.equal(await evaluate("document.getElementById('selected-title').textContent"),'--literal <script>draft</script>');
assert.equal(await evaluate("document.getElementById('selected-title').querySelector('script')"),null);
assert.ok(!await evaluate("location.href.includes('literal')"),'Draft input must not enter URLs');
await click('timeline-view');
await evaluate("document.getElementById('timeline-scrub').value=100;document.getElementById('timeline-scrub').dispatchEvent(new Event('input'))");
assert.equal(await evaluate("document.getElementById('create-issue').disabled"),true);
await click('timeline-live');await until("!document.getElementById('create-issue').disabled");
await click('create-issue');await evaluate("window.confirm=()=>false");await click('create-new');
assert.equal(await evaluate("document.getElementById('create-name').value"),'--literal <script>draft</script>');
await evaluate("window.confirm=()=>true");await click('create-new');await until("!document.getElementById('create-fields').disabled");
assert.equal(await evaluate("document.getElementById('create-name').value"),'');
await click('create-submit');
assert.equal((await (await fetch('http://127.0.0.1:18081/fixture/draft-requests')).json()).length,3,'Empty new draft cannot create a duplicate');
assert.deepEqual(errors,[]);
console.log('Draft composer passed: actor, preserved inputs, policy conflict review, dropped response/same request, literal text, receipt link, mobile layout.');
await call('Browser.close');ws.close();
