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
// A shareable recorded-event link restores only already-loaded facts.
const base='http://127.0.0.1:18081/?from=2026-09-21T09%3A00%3A00Z&issue=bd-a1f';
await call('Page.navigate',{url:base});
await until('document.querySelectorAll(".lane-card").length===3');
await evaluate("document.querySelector('.timeline-options').open=true;[...document.querySelectorAll('.event-list-item')].find(n=>n.textContent.includes('Confirmed: drag')).click()");
const linked=await evaluate('location.href');
assert.ok(new URL(linked).searchParams.get('event'));
await call('Page.navigate',{url:linked});
await until("!document.getElementById('timeline-callout').hidden");
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/Confirmed: drag/);
assert.equal(await evaluate("document.querySelector('#timeline-callout time').dateTime"),'2026-09-21T11:00:00.000Z');
assert.equal(await evaluate("document.querySelector('#selected-event time').dateTime"),'2026-09-21T11:00:00.000Z');
assert.ok(await evaluate("[...document.querySelectorAll('.event-list-item')].some(n=>n.title.includes('2026-09-21T11:00:00.000Z'))"));
await evaluate("document.querySelector('.callout-close').click()");
assert.equal(await evaluate("new URL(location.href).searchParams.has('event')"),false);
await evaluate("document.getElementById('load-activity').click()");
await until("document.querySelectorAll('#activity article').length===3");
await evaluate("document.querySelector('#activity .show-timeline-event').click()");
const historical=await evaluate('location.href');
assert.ok(new URL(historical).searchParams.get('event'));
await call('Page.navigate',{url:historical});
await until("document.getElementById('selected-event').textContent.includes('not available')");
assert.equal(await evaluate("document.getElementById('timeline-callout').hidden"),true);
assert.equal(await evaluate("document.querySelectorAll('#activity article').length"),0,'Deep links do not implicitly fetch history');
await evaluate("document.getElementById('load-activity').click()");
await until("document.getElementById('selected-event').textContent.includes('snapshot')");
assert.equal(await evaluate("document.getElementById('timeline-callout').hidden"),false);
await evaluate("document.getElementById('timeline-live').click()");
assert.equal(await evaluate("new URL(location.href).searchParams.has('event')"),false);
assert.equal(await evaluate("document.getElementById('timeline-callout').hidden"),true);
await until("!document.getElementById('issue-form').hidden");
const liveUrl=await evaluate('location.href');
assert.equal(new URL(liveUrl).searchParams.has('to'),false,'Live URLs must not reopen as fixed playback');
// Exercise real same-document Back/Forward, not a synthetic popstate event.
await evaluate(`history.pushState(null,'',${JSON.stringify(historical)});history.pushState(null,'',${JSON.stringify(liveUrl)})`);
await delay(200);
const beforeBack=apiCalls;
await evaluate('history.back()');
await until("document.getElementById('timeline-asof').textContent.startsWith('As of')");
assert.equal(await evaluate("document.getElementById('issue-form').hidden"),true);
assert.equal(await evaluate("new Date(document.getElementById('timeline-from').value).getTime()"),Date.parse(new URL(historical).searchParams.get('from')));
assert.equal(await evaluate("new Date(document.getElementById('timeline-to').value).getTime()"),Math.floor(Date.parse(new URL(historical).searchParams.get('to'))/60000)*60000);
assert.equal(apiCalls,beforeBack,'Historical navigation must not query sources');
await evaluate('history.forward()');
await until("document.getElementById('timeline-asof').textContent.startsWith('Live')&&!document.getElementById('issue-form').hidden");
assert.equal(apiCalls,beforeBack+1,'Returning live rereads the selected issue before editing');
assert.equal(await evaluate("document.getElementById('timeline-callout').hidden"),true);
await call('Page.navigate',{url:liveUrl});
await until("document.querySelectorAll('.lane-card').length===3&&document.getElementById('timeline-asof').textContent.startsWith('Live')");
assert.deepEqual(errors,[]);
console.log('PASS: recorded event links restore comments and explicitly loaded history; dismissal/playback clear selection; Back/Forward restores ranges and safely rereads live state');
await call('Browser.close');ws.close();
