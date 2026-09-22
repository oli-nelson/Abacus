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
await call('Network.enable');await call('Runtime.enable');await call('Emulation.setDeviceMetricsOverride',{width:1671,height:941,deviceScaleFactor:1,mobile:false});
const base='http://127.0.0.1:18081/?from=2026-09-21T09%3A00%3A00Z&issue=bd-a1f';
await call('Page.navigate',{url:base});
await until("document.getElementById('timeline-stage').dataset.camera&&document.querySelectorAll('.lane-card').length===3");
await evaluate("document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));document.getElementById('timeline-stage').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}));document.getElementById('timeline-camera').value='pan';document.getElementById('timeline-camera').dispatchEvent(new Event('change'))");
await until("Math.abs(JSON.parse(document.getElementById('timeline-stage').dataset.camera).yaw-.21)<.0001");
await until("localStorage.getItem('abacus.timeline.camera')===document.getElementById('timeline-stage').dataset.camera");
const pose=await evaluate("document.getElementById('timeline-stage').dataset.camera");
await call('Page.navigate',{url:base});
await until("document.querySelectorAll('.lane-card').length===3&&document.getElementById('timeline-stage').dataset.camera");
assert.deepEqual(JSON.parse(await evaluate("document.getElementById('timeline-stage').dataset.camera")),JSON.parse(pose));
assert.equal(await evaluate("document.getElementById('timeline-camera').value"),'pan');
const before=apiCalls;
await evaluate("document.getElementById('timeline-2d').click()");
await until("JSON.parse(document.getElementById('timeline-stage').dataset.camera).perspective===0");
assert.equal(await evaluate("new URL(location.href).searchParams.get('camera')"),'2d');
await until("JSON.parse(localStorage.getItem('abacus.timeline.camera')).perspective===0");
assert.equal(apiCalls,before,'Camera preferences must not query sources');
const link=await evaluate('location.href');
await call('Page.navigate',{url:link.replace('camera=2d','camera=3d')});
await until("document.getElementById('timeline-stage').dataset.camera&&document.querySelectorAll('.lane-card').length===3");
assert.equal(await evaluate("JSON.parse(document.getElementById('timeline-stage').dataset.camera).perspective"),1,'URL mode overrides local preference');
await evaluate("localStorage.setItem('abacus.timeline.camera','{broken');localStorage.setItem('abacus.timeline.mode','invalid')");
await call('Page.navigate',{url:base});
await until("document.getElementById('timeline-stage').dataset.camera&&document.querySelectorAll('.lane-card').length===3");
assert.equal(await evaluate("JSON.parse(document.getElementById('timeline-stage').dataset.camera).perspective"),1);
assert.deepEqual(errors,[]);
console.log('PASS: camera pose/control survive reload, URL mode overrides preferences, malformed preferences fall back safely, mode changes make no requests');
await call('Browser.close');ws.close();
