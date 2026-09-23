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
await call('Page.navigate',{url:'http://127.0.0.1:18081/?view=timeline&issue=bd-a1f&from=2026-09-21T09%3A00%3A00Z'});
await until("document.querySelectorAll('.lane-card').length===3 && !document.getElementById('activity-section').hidden");


await fetch('http://127.0.0.1:18081/fixture/git');
await evaluate("document.getElementById('inspector-tab-git').click();document.getElementById('load-issue-git').click()");
await until("!document.getElementById('load-issue-patch').hidden");
assert.equal(await evaluate("document.getElementById('issue-git-patch').hidden"),true);
assert.match(await evaluate("document.querySelector('[data-git-evidence=validated]').textContent"),/1 files.*Not proven integrated/s);
assert.match(await evaluate("document.getElementById('issue-git-facts').textContent"),/attached-checkout · dirty.*unavailable-checkout · dirty state unknown/s);
await evaluate("document.getElementById('load-issue-patch').click()");
await until("!document.getElementById('issue-git-patch').hidden");
assert.match(await evaluate("document.getElementById('issue-git-patch').textContent"),/<img src=x/);
assert.equal(await evaluate("document.querySelectorAll('#issue-git-patch img').length"),0);
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.currentConnections"),'0');
await fetch('http://127.0.0.1:18081/fixture/integrated');
await evaluate("document.getElementById('load-issue-history').click()");
await until("document.querySelectorAll('#issue-git-history article').length===2");
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.currentConnections"),'1');
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.recordedStarts"),'1');
await evaluate("document.getElementById('timeline-fit').click()");
await delay(650);
await writeFile('/tmp/abacus-current-topology.png',Buffer.from((await call('Page.captureScreenshot',{format:'png'})).data,'base64'));
assert.equal(await evaluate("document.getElementById('issue-git-patch').hidden"),true);
assert.match(await evaluate("document.getElementById('issue-git-detail-state').textContent"),/Latest 100.*topology is authoritative.*shared ancestors/);
assert.equal(await evaluate("document.querySelectorAll('#issue-git-history script').length"),0);
const beforeViewSwitch=apiCalls;
await evaluate("document.getElementById('issues-view').click();document.getElementById('timeline-view').click()");
assert.equal(await evaluate("document.querySelectorAll('#issue-git-history article').length"),2,'Changing view must preserve loaded inspector evidence');
assert.match(await evaluate("document.getElementById('issue-git-detail-state').textContent"),/shared ancestors/);
assert.equal(apiCalls,beforeViewSwitch,'View navigation must not reload Git evidence');

assert.equal(await evaluate("[...document.querySelectorAll('.event-list-item')].filter(n=>n.textContent.includes(' · git · ')).length"),2);
await evaluate("document.querySelectorAll('#issue-git-history .show-timeline-event')[1].click()");
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/shared ancestor/);
assert.equal(await evaluate("document.getElementById('issue-form').hidden"),true);
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.currentConnections"),'0');
assert.equal(await evaluate("document.querySelectorAll('#selected-event script').length"),0);
await evaluate("document.getElementById('timeline-live').click()");
await until("!document.getElementById('load-issue-git').disabled");
await evaluate("document.getElementById('timeline-view').click();document.getElementById('timeline-scrub').value=0;document.getElementById('timeline-scrub').dispatchEvent(new Event('input'))");
assert.equal(await evaluate("document.querySelectorAll('[data-git-evidence=validated]').length"),0,'Current Git evidence cannot backfill playback');
await evaluate("document.getElementById('timeline-live').click()");
await until("!document.getElementById('load-issue-git').disabled");
await evaluate("document.getElementById('load-issue-git').click()");
await until("document.querySelectorAll('[data-git-evidence=validated]').length===1");
await fetch('http://127.0.0.1:18081/fixture/race');
await until("document.getElementById('selected-title').textContent==='Externally updated title'");
assert.equal(await evaluate("document.querySelectorAll('#issue-git-history article').length"),0);
assert.equal(await evaluate("[...document.querySelectorAll('.event-list-item')].filter(n=>n.textContent.includes(' · git · ')).length"),0);
assert.equal(await evaluate("document.querySelectorAll('[data-git-evidence=validated]').length"),0);
assert.equal(await evaluate("document.getElementById('load-issue-history').hidden"),true);
await evaluate("document.getElementById('timeline-view').click();document.getElementById('timeline-scrub').value=0;document.getElementById('timeline-scrub').dispatchEvent(new Event('input'))");
assert.equal(await evaluate("document.getElementById('load-issue-git').disabled"),true);
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('Issue Git browser: explicit bounded patch/history, literal content, coverage warnings, source invalidation and historical disable passed');
await call('Browser.close');ws.close();
