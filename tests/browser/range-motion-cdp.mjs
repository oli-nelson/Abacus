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
await until("document.querySelectorAll('.lane-card').length===3&&document.getElementById('connection').textContent.includes('Live')");
await evaluate("document.getElementById('timeline-motion').value='full';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
await until("Number(document.getElementById('timeline-stage').dataset.frames)>0");await delay(300);
await call('Emulation.setTimezoneOverride',{timezoneId:'UTC'});
await fetch(base+'/fixture/comment-arrival?id=range-neighbour&time=10:59');
await until("document.getElementById('timeline-event-list').textContent.includes('range-neighbour')");
await evaluate("[...document.querySelectorAll('.event-list-item')].find(n=>n.textContent.includes('Confirmed: drag')).click()");
await until("document.getElementById('timeline-history-state').textContent.includes('3/3 issues loaded')");
await evaluate("document.getElementById('timeline-kind').value='comment';document.getElementById('timeline-kind').dispatchEvent(new Event('change'))");
await delay(250);
const requests=apiCalls;
const active="document.getElementById('timeline-canvas').getAnimations().length";
async function range(from,to){return evaluate(`document.getElementById('timeline-from').value='2026-09-21T${from}';document.getElementById('timeline-to').value='2026-09-21T${to}';document.getElementById('timeline-range').dispatchEvent(new Event('submit',{cancelable:true}));${active}`);}
assert.equal(await range('09:00','12:00'),1);
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.clusters"),'1','Wide range groups neighbouring comments');
assert.equal(await range('10:58','11:02'),1);
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.clusters"),'0','Narrow range splits the cluster');
assert.equal(await evaluate("new URL(location.href).searchParams.get('event')"),'comment:bd-a1f:c0');
assert.equal(await range('09:00','12:00'),1);
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.clusters"),'1','Widening merges the same comments');
assert.equal(await evaluate("new URL(location.href).searchParams.get('event')"),'comment:bd-a1f:c0');
assert.equal(await evaluate("[...document.querySelectorAll('.event-list-item')].filter(n=>/Confirmed: drag|range-neighbour/.test(n.textContent)).length"),2,'Both underlying events remain accessible');
assert.equal(await range('10:00','12:00'),1);
assert.equal(await evaluate("new URL(location.href).searchParams.get('event')"),'comment:bd-a1f:c0');
assert.match(await evaluate("document.getElementById('timeline-callout').textContent"),/Confirmed: drag/);
assert.equal(await evaluate("document.getElementById('timeline-callout').getAnimations().length"),0,'Retained pin does not replay entrance');
await until(`(${active})===0`);
assert.equal(await range('10:00','12:00'),0,'Unchanged range does not fade again');
await evaluate("document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
assert.equal(await range('11:30','12:00'),0);
assert.equal(await evaluate("document.getElementById('timeline-callout').hidden"),true,'Out-of-range pin clears');
assert.equal(apiCalls,requests,'Range regrouping stays local');
assert.deepEqual(errors,[]);
console.log('PASS: range regrouping fade, retained in-range pin, no repeated entrance, reduced motion and local-only changes');
await call('Browser.close');ws.close();
