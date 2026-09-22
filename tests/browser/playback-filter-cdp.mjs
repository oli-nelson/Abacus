// Requires fixture-server.mjs and a local Chrome DevTools endpoint (default 19222).
import assert from 'node:assert/strict';
const pages=await (await fetch(`http://127.0.0.1:${process.env.CDP_PORT||19222}/json/list`)).json();
const ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
await new Promise(r=>ws.addEventListener('open',r,{once:true}));
let id=0;const pending=new Map(),errors=[];let apiCalls=0;
ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.method==='Network.requestWillBeSent'&&m.params.request.url.includes('/api/v1/'))apiCalls++;if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);}});
function call(method,params={}){return new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}));});}
async function evaluate(expression){const r=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function until(expression){for(let i=0;i<100;i++){if(await evaluate(expression))return;await delay(100);}throw new Error('Timed out: '+expression+' errors: '+JSON.stringify(errors));}
await call('Page.enable');
await call('Page.addScriptToEvaluateOnNewDocument',{source:"localStorage.removeItem('abacus.timeline.mode');localStorage.removeItem('abacus.motion');"});
await call('Network.enable');await call('Runtime.enable');await call('Emulation.setDeviceMetricsOverride',{width:1671,height:941,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T10%3A19%3A00Z&to=2026-09-21T10%3A21%3A00Z&at=2026-09-21T10%3A19%3A00Z&issue=bd-a1f'});
await until("!document.getElementById('activity-section').hidden");
await evaluate("document.getElementById('load-activity').click()");
await until("document.querySelectorAll('#activity article').length===3");
await evaluate("document.getElementById('status').value='blocked';document.getElementById('status').dispatchEvent(new Event('change'));document.getElementById('timeline-speed').value='60'");
assert.equal(await evaluate("document.querySelectorAll('.lane-card').length"),0);
const requests=apiCalls;
await evaluate("document.getElementById('timeline-play').click()");
await until("document.querySelectorAll('.lane-card').length===1");
assert.match(await evaluate("document.getElementById('timeline-counts').textContent"),/1 blocked/);
await until("document.getElementById('timeline-play').textContent==='Play'");
assert.equal(await evaluate("document.getElementById('timeline-scrub').value"),'1000');
assert.equal(await evaluate("new URL(location.href).searchParams.get('at')"),'2026-09-21T10:21:00.000Z');
assert.match(await evaluate("document.getElementById('details').textContent"),/blocked/);
assert.equal(apiCalls,requests,'Playback filtering must not request source data');
assert.deepEqual(errors,[]);
console.log('PASS: playback updates filtered lane membership/counts and settles transport, inspector and URL at the exact endpoint');
await call('Browser.close');ws.close();
