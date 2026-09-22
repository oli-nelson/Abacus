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
await call('Emulation.setTimezoneOverride',{timezoneId:'UTC'});
await call('Emulation.setDeviceMetricsOverride',{width:1671,height:1000,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09:00:00Z'});
await until("document.readyState==='complete'&&document.getElementById('timeline-history-state')?.textContent.includes('3/3 issues loaded')");
async function reset(){await evaluate("document.getElementById('timeline-from').value='2026-09-21T09:00';document.getElementById('timeline-to').value='2026-09-21T12:00';document.getElementById('timeline-range').requestSubmit();document.getElementById('timeline-minimap').scrollIntoView({block:'center'})");await delay(100);}
async function point(f){return evaluate(`(()=>{const r=document.getElementById('timeline-minimap').getBoundingClientRect();return {x:r.x+r.width*${f},y:r.y+r.height/2}})()`);}
async function mouse(type,p){await call('Input.dispatchMouseEvent',{type,...p,button:'left',buttons:type==='mouseReleased'?0:1,clickCount:1});}
const range=()=>evaluate("(()=>{const u=new URL(location.href);return [Date.parse(u.searchParams.get('from')),Date.parse(u.searchParams.get('to'))]})()");
await reset();const requests=apiCalls;
for(const [a,b] of [[.25,.75],[.75,.25]]){
 await reset();await mouse('mousePressed',await point(a));await mouse('mouseMoved',await point(b));
 assert.equal(await evaluate("document.getElementById('timeline-range-brush').hidden"),false);
 await mouse('mouseReleased',await point(b));
 const selected=await range();
 assert.ok(Math.abs(selected[0]-Date.parse('2026-09-21T09:45:00Z'))<2000);
 assert.ok(Math.abs(selected[1]-Date.parse('2026-09-21T11:15:00Z'))<2000);
 assert.equal(await evaluate("document.getElementById('timeline-range-brush').hidden"),true);
 assert.equal(await evaluate("new URL(location.href).searchParams.has('hours')"),false);
}
await reset();const before=await range();
await mouse('mousePressed',await point(.2));await mouse('mouseMoved',await point(.8));
await call('Input.dispatchKeyEvent',{type:'keyDown',key:'Escape'});await call('Input.dispatchKeyEvent',{type:'keyUp',key:'Escape'});
await mouse('mouseReleased',await point(.8));assert.deepEqual(await range(),before);
await mouse('mousePressed',await point(.5));await mouse('mouseReleased',await point(.5));
assert.deepEqual(await range(),before,'Click seeks without replacing range');
await call('Emulation.setTouchEmulationEnabled',{enabled:true,maxTouchPoints:1});
await call('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[await point(.2)]});
await call('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[await point(.8)]});
await call('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});
const touch=await range();assert.ok(Math.abs(touch[0]-Date.parse('2026-09-21T09:36:00Z'))<2000);assert.ok(Math.abs(touch[1]-Date.parse('2026-09-21T11:24:00Z'))<2000);
assert.equal(apiCalls,requests,'Brushing must use loaded data');assert.equal(errors.length,0,JSON.stringify(errors));
console.log('PASS: map range brush in both directions, preview, Escape cancellation, retained click seeking, touch and no source requests');
await call('Browser.close');ws.close();
