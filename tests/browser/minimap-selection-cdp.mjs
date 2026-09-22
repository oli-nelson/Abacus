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
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09%3A00%3A00Z'});
await until("document.querySelectorAll('.lane-card').length===3&&document.getElementById('timeline-stage').dataset.frames");
const requests=apiCalls;
const point=await evaluate("(()=>{const r=document.getElementById('timeline-minimap').getBoundingClientRect();return {x:r.left+r.width*2/3,y:r.top+r.height*(8+.5*38/3)/54}})()");
await call('Input.dispatchMouseEvent',{type:'mousePressed',...point,button:'left',clickCount:1});
await call('Input.dispatchMouseEvent',{type:'mouseReleased',...point,button:'left',clickCount:1});
await until("document.getElementById('selected-event').textContent.includes('Confirmed: drag')");
assert.equal(await evaluate("document.getElementById('selected-id').textContent"),'bd-a1f');
assert.equal(await evaluate("new URL(location.href).searchParams.get('event')"),'comment:bd-a1f:c0');
assert.equal(await evaluate("new URL(location.href).searchParams.get('at')"),'2026-09-21T11:00:00.000Z');
assert.equal(await evaluate("document.getElementById('issue-form').hidden"),true);
assert.equal(apiCalls,requests,'Minimap navigation must not query sources');
// Hidden event kinds must not remain pickable in the overview.
await evaluate("document.getElementById('timeline-kind').value='closure';document.getElementById('timeline-kind').dispatchEvent(new Event('change'))");
await delay(150);
const blankPoint=await evaluate("(()=>{const r=document.getElementById('timeline-minimap').getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+2}})()");
await call('Input.dispatchMouseEvent',{type:'mousePressed',...blankPoint,button:'left',clickCount:1});
await call('Input.dispatchMouseEvent',{type:'mouseReleased',...blankPoint,button:'left',clickCount:1});
assert.equal(await evaluate("new URL(location.href).searchParams.get('event')"),null,'Filtered-out comments must not be selected');
assert.ok(Math.abs(Date.parse(await evaluate("new URL(location.href).searchParams.get('at')"))-Date.parse('2026-09-21T10:30:00Z'))<30000,'Empty minimap positions scrub within pointer pixel precision');
assert.equal(apiCalls,requests,'Filtering and empty-space scrubbing stay local');
assert.deepEqual(errors,[]);
console.log('PASS: minimap event click selects its issue/event and exact recorded playhead without source requests');
await call('Browser.close');ws.close();
