// Requires fixture-server.mjs and a local Chrome DevTools endpoint (default 19222).
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
await call('Page.enable');await call('Runtime.enable');await call('Network.enable');
await call('Emulation.setDeviceMetricsOverride',{width:1280,height:941,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:'http://127.0.0.1:18081/?view=issues&issue=bd-a1f&from=2026-09-21T09%3A00%3A00Z'});
await until("document.querySelectorAll('#issues tr').length===3 && !document.getElementById('activity-section').hidden");


assert.equal(await evaluate("document.querySelectorAll('#details .label-chip').length"),2);
assert.match(await evaluate("document.querySelector('#details dd[data-priority]').textContent"),/P1 · High/);
async function press(key){await call('Input.dispatchKeyEvent',{type:'keyDown',key});await call('Input.dispatchKeyEvent',{type:'keyUp',key});}
assert.equal(await evaluate("document.querySelectorAll('[role=tab][aria-selected=true]').length"),1);
await evaluate("document.getElementById('write-title').value='Keep this draft';document.getElementById('write-title').dispatchEvent(new Event('input',{bubbles:true}));document.getElementById('inspector-tab-overview').focus()");
const calls=apiCalls;
await press('ArrowRight');
assert.equal(await evaluate("document.activeElement.id"),'inspector-tab-activity');
assert.equal(await evaluate("document.getElementById('inspector-panel-activity').hidden"),false);
assert.equal(await evaluate("document.getElementById('inspector-panel-overview').hidden"),true);
assert.match(await evaluate("document.getElementById('comments').textContent"),/Confirmed: drag/);
await press('End');
assert.equal(await evaluate("document.activeElement.id"),'inspector-tab-git');
assert.match(await evaluate("document.getElementById('issue-git-state').textContent"),/Load recorded binding evidence/);
assert.equal(apiCalls,calls,'Tab navigation must not trigger source reads');
assert.equal(await evaluate("new URL(location.href).searchParams.get('inspector')"),'git');
await evaluate("document.getElementById('load-issue-git').click()");
await until("document.getElementById('issue-git-state').textContent.startsWith('unbound')");
assert.match(await evaluate("document.getElementById('issue-git-facts').textContent"),/Effective targetmain/);
await press('Home');
assert.equal(await evaluate("document.activeElement.id"),'inspector-tab-overview');
assert.equal(await evaluate("document.getElementById('write-title').value"),'Keep this draft');
await fetch('http://127.0.0.1:18081/fixture/relations');
await until("!document.getElementById('ongoing-section').hidden");
assert.equal(await evaluate("document.querySelectorAll('#ongoing-issues button').length"),1);
assert.match(await evaluate("document.getElementById('ongoing-issues').textContent"),/bd-b7c.*blocked/);
await evaluate("document.querySelector('#ongoing-issues button').click()");
await until("document.getElementById('selected-id').textContent==='bd-b7c'");
assert.equal(await evaluate("document.getElementById('selected-current-status').textContent"),'Current: blocked');
await evaluate("history.pushState(null,'','?view=issues&issue=bd-a1f&inspector=activity');dispatchEvent(new PopStateEvent('popstate'))");
assert.equal(await evaluate("document.getElementById('inspector-panel-activity').hidden"),false);
await evaluate("document.getElementById('load-activity').click()");
await until("document.querySelectorAll('#activity article').length===4");
await call('Page.reload');
await until("document.getElementById('selected-id').textContent==='bd-a1f' && !document.getElementById('inspector-panel-activity').hidden");
await call('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
assert.equal(await evaluate("document.documentElement.scrollWidth<=390"),true);
assert.equal(errors.length,0,JSON.stringify(errors));
await evaluate("document.getElementById('inspector').scrollIntoView({block:'start'})");
const shot=await call('Page.captureScreenshot',{format:'png'});await writeFile('/tmp/abacus-inspector-tabs.png',Buffer.from(shot.data,'base64'));
await evaluate("document.getElementById('inspector-tab-overview').click();document.getElementById('inspector').scrollIntoView({block:'start'})");
assert.equal(await evaluate("document.documentElement.scrollWidth<=390"),true);
await writeFile('/tmp/abacus-inspector-overview.png',Buffer.from((await call('Page.captureScreenshot',{format:'png'})).data,'base64'));
console.log('Inspector browser: keyboard/ARIA tabs, draft retention, explicit history, URL restore, related ongoing navigation and narrow layout passed');
await call('Browser.close');ws.close();
