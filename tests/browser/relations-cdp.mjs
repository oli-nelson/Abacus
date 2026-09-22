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

await fetch('http://127.0.0.1:18081/fixture/relations');
await until("document.querySelectorAll('#relations li').length===2");
assert.match(await evaluate("document.getElementById('relations').textContent"),/missing-child · not present/);
assert.match(await evaluate("document.getElementById('relations-state').textContent"),/Incoming coverage incomplete/);
await evaluate("document.querySelector('#relations button').click()");
await until("document.getElementById('selected-id').textContent==='bd-b7c'");
assert.match(await evaluate("document.getElementById('relations').textContent"),/incoming · blocks · bd-a1f/);
await fetch('http://127.0.0.1:18081/fixture/relations?clear');
await until("document.getElementById('relations').textContent.includes('No recorded links')");
await evaluate("document.getElementById('timeline-view').click();document.getElementById('timeline-scrub').value=0;document.getElementById('timeline-scrub').dispatchEvent(new Event('input'))");
await until("document.getElementById('relations-state').textContent.includes('at this playhead are unknown')");
assert.equal(await evaluate("document.querySelectorAll('#relations li').length"),0);
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('Relationships browser: exported links, missing targets, navigation, incoming-only live changes and historical unknown coverage passed');
await call('Browser.close');ws.close();
