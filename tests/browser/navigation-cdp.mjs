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
await call('Emulation.setDeviceMetricsOverride',{width:1280,height:550,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:'http://127.0.0.1:18081/?view=workers'});
await until("document.readyState==='complete'&&!!document.getElementById('timeline-stage')");
await until("document.getElementById('connection').textContent==='● Live'");
assert.equal(await evaluate("document.getElementById('workers-pane').hidden"),false);
assert.equal(await evaluate("document.getElementById('workers-empty').hidden"),false);
await evaluate("document.getElementById('worktrees-view').click()");
assert.match(await evaluate("document.getElementById('worktree-health').textContent"),/Git facts not available/);
await evaluate("document.getElementById('workers-view').click()");
await fetch('http://127.0.0.1:18081/fixture/runtime');
await until("document.querySelectorAll('#runtime-workers button').length===3");
assert.equal(await evaluate("document.getElementById('workers-empty').hidden"),true);
for(const view of ['issues','branches','timeline']){
 await evaluate(`document.getElementById('${view}-view').click()`);
 assert.equal(await evaluate("document.getElementById('runtime-panel').checkVisibility()"),false,'Workers must not occupy other pages');
}
await evaluate("document.getElementById('timeline-hours').value='2.5';document.getElementById('timeline-live-range').requestSubmit()");
await until("new URL(location.href).searchParams.get('hours')==='2.5'");
assert.match(await evaluate("document.getElementById('timeline-range-mode').textContent"),/Rolling window/);
assert.ok(Math.abs(await evaluate("Date.parse(new URL(location.href).searchParams.get('from'))")-Date.parse('2026-09-21T09:30:00Z'))<10000,'Live window begins 2.5 hours before server now');
assert.equal(await evaluate("document.getElementById('timeline-expand').textContent"),'First event → now');
await evaluate("document.getElementById('timeline-scrub').value=10;document.getElementById('timeline-scrub').dispatchEvent(new Event('input'));document.getElementById('search').value='bd-b7c';document.getElementById('search').dispatchEvent(new Event('input'));document.getElementById('timeline-expand').click()");
await until("!new URL(location.href).searchParams.has('at')");
assert.equal(await evaluate("new URL(location.href).searchParams.has('hours')"),false,'First event clears rolling offset');
assert.equal(await evaluate("new URL(location.href).searchParams.get('from')"),'2026-09-21T09:00:00.000Z','All entries restores earliest entry even from playback and a filtered viewport');
assert.equal(await evaluate("document.getElementById('timeline-event-span').textContent"),'First event → last event');
await evaluate("document.getElementById('timeline-event-span').click()");
await until("new URL(location.href).searchParams.get('to')==='2026-09-21T11:35:00.000Z'");
assert.equal(await evaluate("new URL(location.href).searchParams.get('from')"),'2026-09-21T09:00:00.000Z','Event span uses all issues despite the search filter');
assert.equal(await evaluate("new URL(location.href).searchParams.get('at')"),'2026-09-21T11:35:00.000Z','Event span pauses at the latest recorded event');
assert.match(await evaluate("document.getElementById('timeline-range-mode').textContent"),/Historical range/);
await evaluate("document.getElementById('search').value='';document.getElementById('search').dispatchEvent(new Event('input'))");
await evaluate("document.querySelector('.timeline-options').open=true;document.getElementById('workspace-tools').open=true");
await delay(600);
const before=await evaluate("({scroll:document.querySelector('.workspace').scrollTop,camera:document.getElementById('timeline-stage').dataset.camera})");
const point=await evaluate("(()=>{const r=document.getElementById('timeline-stage').getBoundingClientRect();return {x:r.x+r.width/2,y:Math.min(r.y+80,450)}})()");
await call('Input.dispatchMouseEvent',{type:'mouseWheel',...point,deltaX:0,deltaY:350});await delay(300);
assert.ok(await evaluate("document.querySelector('.workspace').scrollTop")>before.scroll,'Wheel over the scene scrolls workspace');
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.camera"),before.camera,'Ordinary wheel must not zoom');
await evaluate("document.querySelector('.workspace').scrollTop=0");await delay(100);
await call('Input.dispatchMouseEvent',{type:'mouseWheel',...point,deltaX:0,deltaY:100,modifiers:2});await delay(200);
assert.notEqual(await evaluate("document.getElementById('timeline-stage').dataset.camera"),before.camera,'Ctrl+wheel zooms');
await evaluate("document.querySelector('.workspace').scrollTop=100000");
assert.ok(await evaluate("(()=>{const w=document.querySelector('.workspace'),f=w.querySelector('footer');return f.getBoundingClientRect().bottom<=w.getBoundingClientRect().bottom+1})()"),'Footer is reachable');
await call('Emulation.setDeviceMetricsOverride',{width:390,height:700,deviceScaleFactor:1,mobile:true});await delay(200);
assert.equal(await evaluate("document.documentElement.scrollWidth<=390"),true,'Five navigation tabs fit narrow screens');
await call('Emulation.setTouchEmulationEnabled',{enabled:true,maxTouchPoints:2});
await evaluate("document.querySelector('.workspace').scrollTop=0;document.getElementById('timeline-stage').scrollIntoView({block:'center'})");await delay(200);
const touch=await evaluate("(()=>{const r=document.getElementById('timeline-stage').getBoundingClientRect();return {x:r.x+r.width/2,y:Math.min(r.bottom-20,600),scroll:scrollY+document.querySelector('.workspace').scrollTop,camera:document.getElementById('timeline-stage').dataset.camera}})()");
await call('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{x:touch.x,y:touch.y}]});
for(let i=1;i<=8;i++){await call('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{x:touch.x,y:touch.y-i*25}]});await delay(30);}
await call('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});await delay(300);
assert.ok(await evaluate("scrollY+document.querySelector('.workspace').scrollTop")>touch.scroll,'Vertical swipe over scene scrolls the page');
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.camera"),touch.camera,'Vertical touch scroll does not move camera');
await evaluate("document.getElementById('workers-view').click()");
assert.equal(await evaluate("document.getElementById('runtime-panel').checkVisibility()"),true);
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('PASS: dedicated Workers visibility, live updates, short-window wheel scrolling, Ctrl zoom, reachable footer and narrow navigation');
await call('Browser.close');ws.close();
