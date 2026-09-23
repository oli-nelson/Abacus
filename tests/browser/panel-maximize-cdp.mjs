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
await call('Emulation.setDeviceMetricsOverride',{width:1671,height:1000,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09:00:00Z'});
await until("document.readyState==='complete'&&document.getElementById('timeline-history-state')?.textContent.includes('3/3 issues loaded')");
assert.equal(await evaluate("[...document.querySelectorAll('.lane-card strong')].some(n=>/bd-[a-z0-9]+/.test(n.textContent))"),false);
assert.equal(await evaluate("document.querySelector('.lane-card[data-issue-id=bd-a1f] strong').textContent"),'Timeline renderer');
const before=await evaluate("document.getElementById('timeline-stage').getBoundingClientRect().height");
const url=await evaluate('location.href'),requests=apiCalls;
await evaluate("document.querySelector('.view-options').open=true;document.getElementById('timeline-maximize').click()");await delay(200);
const visible=id=>evaluate(`document.getElementById('${id}').getClientRects().length>0&&getComputedStyle(document.getElementById('${id}')).display!=='none'`);
assert.equal(await visible('timeline-live-range'),false);assert.equal(await visible('timeline-expand'),false);assert.equal(await visible('timeline-event-span'),false);assert.equal(await visible('workspace-tools'),false);
assert.equal(await visible('timeline-2d'),true);assert.equal(await visible('timeline-scale-time'),true);assert.equal(await visible('inspector-tab-overview'),true);
assert.ok(await evaluate("document.getElementById('timeline-stage').getBoundingClientRect().height")>before);
assert.equal(await evaluate('location.href'),url);assert.equal(apiCalls,requests);
assert.equal(await evaluate('document.fullscreenElement===null'),true);
await evaluate("document.getElementById('issues-view').click()");assert.equal(await visible('workspace-tools'),true);
await evaluate("document.getElementById('timeline-view').click()");assert.equal(await visible('timeline-live-range'),false);
await evaluate("document.getElementById('timeline-stage').focus()");await call('Input.dispatchKeyEvent',{type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
assert.equal(await visible('timeline-live-range'),true);assert.equal(await visible('timeline-expand'),true);assert.equal(await visible('timeline-event-span'),true);
await call('Emulation.setDeviceMetricsOverride',{width:700,height:900,deviceScaleFactor:1,mobile:false});await delay(100);
await evaluate("document.querySelector('.view-options').open=true;document.getElementById('timeline-maximize').click()");await delay(200);
assert.equal(await visible('timeline-maximize'),true);
const bounds=await evaluate("(()=>{const r=document.getElementById('timeline-stage').getBoundingClientRect();return {height:r.height,bottom:r.bottom}})()");
assert.ok(bounds.height>=160);assert.ok(bounds.bottom<=920,JSON.stringify(bounds));
await evaluate("document.getElementById('timeline-maximize').click()");
assert.equal(await visible('timeline-live-range'),true);assert.deepEqual(errors,[]);
console.log('PASS: title-only labels; larger panel scene; view-only controls; preserved range/inspector; Escape, tab switching, narrow restore; no extra reads');
await call('Browser.close');ws.close();
