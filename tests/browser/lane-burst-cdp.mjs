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

await call('Page.navigate',{url:base+'/?from=2026-09-21T09%3A00%3A00Z'});
await until("document.querySelectorAll('.lane-card').length===3&&document.getElementById('connection').textContent.includes('Live')");
await evaluate("document.getElementById('timeline-motion').value='full';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));window.maxBurstEffects=0;new MutationObserver(()=>{window.maxBurstEffects=Math.max(window.maxBurstEffects,Number(document.getElementById('timeline-stage').dataset.laneArrivals)||0)}).observe(document.getElementById('timeline-stage'),{attributes:true,attributeFilter:['data-lane-arrivals']})");
await delay(600);
const beforeBurst=Number(await evaluate("document.getElementById('timeline-stage').dataset.frames"));
assert.equal((await (await fetch(base+'/fixture/lane-burst')).json()).count,1000);
await until("document.querySelectorAll('.lane-card').length===24");
await until("Number(document.getElementById('timeline-stage').dataset.frames)>"+beforeBurst);
await until("document.getElementById('timeline-stage').dataset.laneArrivals==='0'");
assert.ok(await evaluate('window.maxBurstEffects<=24'),'Only visible lanes get entrance effects');
await delay(150);const frames=await evaluate("document.getElementById('timeline-stage').dataset.frames");await delay(600);
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.frames"),frames,'Burst does not create an animation backlog');
await evaluate("document.getElementById('timeline-next').click()");
await until("document.getElementById('timeline-page').textContent.startsWith('25')");
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.laneArrivals"),'0','Previously offscreen events do not replay');
assert.deepEqual(errors,[]);
console.log('PASS: 1,000-lane burst retains visible lanes, bounds effects, settles idle and does not replay offscreen arrivals');
await call('Browser.close');ws.close();
