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
await call('Page.enable');await call('Runtime.enable');
await fetch('http://127.0.0.1:18081/fixture/history-many');
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09:00:00Z'});
await until("document.readyState==='complete'&&!!document.getElementById('timeline-stage')");
await until("document.getElementById('timeline-history-state').textContent.includes('64/64 issues loaded')");
assert.match(await evaluate("document.getElementById('timeline-history-page').textContent"),/1\/2.*1–64 of 73/);
await evaluate("document.getElementById('timeline-history-next').click()");
await until("document.getElementById('timeline-history-state').textContent.includes('9/9 issues loaded')");
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.recordedStarts"),'9');
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.closedEpisodes"),'9');
assert.ok(await evaluate("[...document.querySelectorAll('.lane-card')].some(n=>n.dataset.issueId==='work-069')"),'Last issue beyond first 64 is reachable');
assert.equal(await evaluate("document.getElementById('timeline-history-next').disabled"),true);
await evaluate("document.getElementById('timeline-history-prev').click()");
await until("document.getElementById('timeline-history-state').textContent.includes('64/64 issues loaded')");
assert.match(await evaluate("document.getElementById('timeline-history-page').textContent"),/1\/2/);
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('PASS: bounded history batches reach all 73 issues, show closed episodes beyond 64, and return to the first batch');
await call('Browser.close');ws.close();
