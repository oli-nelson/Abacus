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

const requested=[];
ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.method==='Network.requestWillBeSent'){const hit=m.params.request.url.match(/issues\/([^/]+)\/activity/);if(hit)requested.push(hit[1]);}});
await call('Page.enable');await call('Runtime.enable');await call('Network.enable');
await fetch('http://127.0.0.1:18081/fixture/history-many');
await fetch('http://127.0.0.1:18081/fixture/history-priority');
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09:00:00Z&to=2026-09-21T12:00:00Z'});
await until("document.readyState==='complete'&&document.getElementById('timeline-history-state')?.textContent.includes('64/64 issues loaded')");
assert.equal(requested[0],'work-069','Relevant issue beyond the old first batch must load first');
await evaluate("document.getElementById('timeline-history-next').click()");
await until("document.getElementById('timeline-history-state').textContent.includes('9/9 issues loaded')");
await evaluate("document.getElementById('timeline-from').value='2026-09-21T02:00';document.getElementById('timeline-to').value='2026-09-21T04:00';document.getElementById('timeline-range').requestSubmit()");
await until("document.getElementById('timeline-history-page').textContent.includes('1/2')&&document.getElementById('timeline-history-state').textContent.includes('64/64 issues loaded')");
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('PASS: range-relevant high-ID issue loads first across 73 issues; range change resets prioritized batch; all histories remain reachable');
await call('Browser.close');ws.close();
