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
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09%3A00%3A00Z&issue=bd-a1f'});
await until("document.querySelectorAll('.lane-card').length===3");
const width=()=>evaluate("document.getElementById('inspector').getBoundingClientRect().width");
const original=await width();
const requests=apiCalls;
await evaluate("document.getElementById('inspector-resizer').focus()");
await call('Input.dispatchKeyEvent',{type:'keyDown',key:'ArrowLeft',code:'ArrowLeft'});
await call('Input.dispatchKeyEvent',{type:'keyUp',key:'ArrowLeft',code:'ArrowLeft'});
await until(`document.getElementById('inspector').getBoundingClientRect().width>${original+10}`);
const saved=await width();
const box=await evaluate("(()=>{const r=document.getElementById('inspector-resizer').getBoundingClientRect();return {x:r.x+3,y:r.y+100}})()");
await call('Input.dispatchMouseEvent',{type:'mousePressed',x:box.x,y:box.y,button:'left',clickCount:1});
await call('Input.dispatchMouseEvent',{type:'mouseMoved',x:box.x-80,y:box.y,button:'left',buttons:1});
await call('Input.dispatchMouseEvent',{type:'mouseReleased',x:box.x-80,y:box.y,button:'left',clickCount:1});
await until(`document.getElementById('inspector').getBoundingClientRect().width>${saved+70}`);
assert.equal(apiCalls,requests,'Resizing does not query sources');
const dragged=await width();
await call('Page.reload');await until("document.querySelectorAll('.lane-card').length===3");
assert.equal(await width(),dragged);
await evaluate("document.getElementById('inspector-resizer').dispatchEvent(new KeyboardEvent('keydown',{key:'Home',bubbles:true}))");
assert.equal(await width(),280);
await call('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
await until("getComputedStyle(document.getElementById('inspector-resizer')).display==='none'");
assert.ok(await evaluate('document.documentElement.scrollWidth<=innerWidth'),'No mobile horizontal overflow');
assert.deepEqual(errors,[]);
console.log('PASS: inspector keyboard/pointer resizing, saved width, bounds, no source requests and mobile layout');
await call('Browser.close');ws.close();
