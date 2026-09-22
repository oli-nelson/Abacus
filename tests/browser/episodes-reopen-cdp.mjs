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
await call('Emulation.setDeviceMetricsOverride',{width:1671,height:1000,deviceScaleFactor:1,mobile:false});
await fetch('http://127.0.0.1:18081/fixture/reopened');
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09:00:00Z'});
await until("document.readyState==='complete'&&!!document.getElementById('timeline-stage')");
await until("document.getElementById('timeline-history-state').textContent.includes('3/3 issues loaded')");
const counts=()=>evaluate("[Number(document.getElementById('timeline-stage').dataset.recordedStarts),Number(document.getElementById('timeline-stage').dataset.closedEpisodes)]");
assert.deepEqual(await counts(),[4,2]);
assert.equal(await evaluate("[...document.querySelectorAll('.event-list-item')].some(n=>n.textContent.includes('Hidden inactive gap comment'))"),false);
async function seek(minutes){await evaluate(`document.getElementById('timeline-scrub').value=${minutes/180*1000};document.getElementById('timeline-scrub').dispatchEvent(new Event('input'))`);}
await seek(154);assert.deepEqual(await counts(),[3,0],'Future closure is not rendered early');
await seek(160);assert.deepEqual(await counts(),[3,1],'Closed episode remains ended throughout gap');
await seek(170);assert.deepEqual(await counts(),[4,1],'Restart adds a separate episode');
await seek(178);assert.deepEqual(await counts(),[4,2],'Second closure terminates reopened episode');
await seek(154);
await evaluate("document.getElementById('timeline-speed').value='600';document.getElementById('timeline-play').click()");
await until("document.getElementById('timeline-stage').dataset.closedEpisodes==='2'");
await evaluate("document.getElementById('timeline-play').click()");
assert.deepEqual(await counts(),[4,2],'Playing across boundaries rebuilds episode topology');
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('PASS: two closed work episodes, empty inactive gap, no future closure, seek and continuous playback boundaries');
await call('Browser.close');ws.close();
