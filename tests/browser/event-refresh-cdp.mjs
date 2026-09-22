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
const base='http://127.0.0.1:18081';
await call('Page.navigate',{url:base+'/?from=2026-09-21T09%3A00%3A00Z&issue=bd-a1f'});
await until("document.querySelectorAll('.lane-card').length===3");
await evaluate("document.querySelector('.timeline-options').open=true;[...document.querySelectorAll('.event-list-item')].find(n=>n.textContent.includes('Confirmed: drag')).click()");
await until("document.getElementById('timeline-callout').getAnimations().length===0");
await evaluate("document.querySelector('#selected-event summary').click();document.querySelector('#selected-event summary').focus()");
await fetch(base+'/fixture/comment-refresh');
await until("document.getElementById('timeline-callout').textContent.includes('Updated recorded comment text')");
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/Updated recorded comment text/);
assert.equal(await evaluate("document.querySelector('#selected-event details').open"),true);
assert.equal(await evaluate("document.activeElement===document.querySelector('#selected-event summary')"),true);
assert.equal(await evaluate("document.getElementById('timeline-callout').getAnimations().length"),0,'Source updates do not replay entrance effects');
await evaluate("[...document.querySelectorAll('.lane-card')].find(n=>n.dataset.issueId==='bd-a1f').click()");
assert.equal(await evaluate("document.getElementById('timeline-callout').hidden"),true,'Selecting the issue clears the previous event');
await evaluate("[...document.querySelectorAll('.event-list-item')].find(n=>n.textContent.includes('Updated recorded comment text')).click()");
await fetch(base+'/fixture/comment-refresh?remove');
await until("document.getElementById('timeline-callout').hidden");
assert.equal(await evaluate("document.getElementById('selected-event').hidden"),true);
assert.equal(await evaluate("new URL(location.href).searchParams.has('event')"),false);
assert.deepEqual(errors,[]);
console.log('PASS: pinned source updates refresh both views without motion replay, preserve disclosure focus, and remove invalidated selections');
await call('Browser.close');ws.close();
