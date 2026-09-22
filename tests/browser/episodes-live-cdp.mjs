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
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09:00:00Z'});
await until("document.readyState==='complete'&&!!document.getElementById('timeline-stage')");
await until("document.getElementById('timeline-history-state').textContent.includes('3/3 issues loaded')");
await fetch('http://127.0.0.1:18081/fixture/live-close');
await until("document.getElementById('timeline-stage').dataset.closedEpisodes==='2'");
assert.equal(await evaluate("[...document.querySelectorAll('.observation-list-item')].some(n=>n.dataset.timelineFocus.includes('bd-a1f'))"),false,'Live closure removes current presence');
await fetch('http://127.0.0.1:18081/fixture/live-reopen');
await until("[...document.querySelectorAll('.observation-list-item')].some(n=>n.textContent.includes('start time not recorded'))");
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.closedEpisodes"),'2','Unknown restart must not erase the old closure');
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.recordedStarts"),'3','Unknown restart must not invent a dated start');
await fetch('http://127.0.0.1:18081/fixture/live-reopen?recorded');
await until("document.getElementById('timeline-stage').dataset.recordedStarts==='4'");
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.closedEpisodes"),'2');
assert.equal(await evaluate("[...document.querySelectorAll('.observation-list-item')].some(n=>n.textContent.includes('start time not recorded'))"),false);
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('PASS: live closure removes current presence, unknown restart stays explicit, recorded restart adds an independent episode');
await call('Browser.close');ws.close();
