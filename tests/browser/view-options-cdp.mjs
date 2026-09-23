// Requires fixture-server.mjs and a disposable local Chrome DevTools endpoint.
import assert from 'node:assert/strict';
const pages=await(await fetch(`http://127.0.0.1:${process.env.CDP_PORT||19222}/json/list`)).json();
const ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
await new Promise(r=>ws.addEventListener('open',r,{once:true}));
let id=0;const pending=new Map(),errors=[];
ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);}});
const call=(method,params={})=>new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}));});
async function evaluate(expression){const r=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function menuBounds(){return evaluate("(()=>{const a=document.querySelector('.view-options').getBoundingClientRect(),m=document.querySelector('.view-options-content').getBoundingClientRect(),w=document.querySelector('.workspace').getBoundingClientRect();return {anchor:a.x,left:m.left,right:m.right,width:m.width,workspaceLeft:w.left,workspaceRight:w.right,viewport:innerWidth,overflow:document.documentElement.scrollWidth>innerWidth}})()");}
await call('Page.enable');await call('Runtime.enable');
await call('Emulation.setDeviceMetricsOverride',{width:1671,height:941,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:'http://127.0.0.1:18081/?view=timeline&from=2026-09-21T09%3A00%3A00Z'});
for(let i=0;i<80&&!await evaluate("document.querySelector('.view-options')&&document.getElementById('connection').textContent==='● Live'");i++)await delay(100);
await evaluate("document.querySelector('.view-options').open=true");await delay(100);
let box=await menuBounds();
assert.ok(box.left>=box.workspaceLeft-1&&box.right<=box.workspaceRight+1,`Right-anchored menu must fit workspace: ${JSON.stringify(box)}`);
await evaluate("document.querySelector('.view-options').open=false;document.querySelector('.view-options').style.order='-10';document.querySelector('.view-options').open=true");await delay(100);
box=await menuBounds();
assert.ok(box.anchor<100&&box.left>=box.workspaceLeft-1&&box.right<=box.workspaceRight+1,`Left-anchored menu must fit workspace: ${JSON.stringify(box)}`);
await call('Emulation.setDeviceMetricsOverride',{width:390,height:780,deviceScaleFactor:1,mobile:true});await delay(150);
box=await menuBounds();
assert.ok(box.left>=15&&box.right<=box.viewport-15&&!box.overflow,`Mobile menu must fit viewport: ${JSON.stringify(box)}`);
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('PASS: View options fits right, left, and mobile anchors without horizontal overflow');
await call('Browser.close');ws.close();
