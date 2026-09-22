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
await fetch('http://127.0.0.1:18081/fixture/reference');
await call('Page.navigate',{url:'http://127.0.0.1:18081/?issue=bd-c3e&from=2026-09-21T09:00:00Z'});
await until("document.readyState==='complete'&&!!document.getElementById('timeline-stage')");
await until("document.getElementById('timeline-history-state').textContent.includes('3/3 issues loaded')&&document.getElementById('integration-confirmation-state').textContent.includes('Git confirms integration')");
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.closedEpisodes"),'1');
assert.match(await evaluate("document.getElementById('integration-confirmation-state').textContent"),/Exact merge time is unknown/);
await fetch('http://127.0.0.1:18081/fixture/git-refresh?contained=false');
await until("document.getElementById('integration-confirmation-state').textContent.includes('not currently confirmed')");
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.closedEpisodes"),'1','Revoking Git evidence cannot remove status return');
assert.equal(await evaluate("document.querySelectorAll('.lane-card[data-git-evidence]').length"),0);
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('PASS: selected closed issue gets optional Git confirmation; invalidation removes confirmation without changing status curves');
await call('Browser.close');ws.close();
