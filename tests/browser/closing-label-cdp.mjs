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


await call('Page.enable');await call('Runtime.enable');
await call('Emulation.setTimezoneOverride',{timezoneId:'UTC'});
await call('Emulation.setDeviceMetricsOverride',{width:1671,height:1000,deviceScaleFactor:1,mobile:false});
await fetch('http://127.0.0.1:18081/fixture/semantic-events');await fetch('http://127.0.0.1:18081/fixture/closing-cluster');
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09:00:00Z&to=2026-09-21T12:00:00Z'});
await until("document.readyState==='complete'&&document.getElementById('timeline-history-state')?.textContent.includes('1/1 issues loaded')");
await evaluate("document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));document.querySelector('.lane-card').click();document.getElementById('timeline-fit').click()");
const closed="[...document.querySelectorAll('.event-annotation')].find(n=>n.getAttribute('aria-label').includes('→ closed'))";
await until(`!!(${closed})&&getComputedStyle(${closed}).visibility==='visible'`);
for(const mode of ['2d','3d']){
 await evaluate(`document.getElementById('timeline-${mode}').click()`);await delay(100);
 assert.equal(await evaluate(`getComputedStyle(${closed}).visibility`),'visible');
}
await evaluate(`(${closed}).click()`);
assert.ok(await evaluate("document.getElementById('selected-event').textContent.includes('Status: blocked → closed')"));
assert.equal(await evaluate("getComputedStyle(document.getElementById('timeline-callout')).borderColor"),'rgb(50, 230, 173)');
await evaluate("document.getElementById('timeline-kind').value='comment';document.getElementById('timeline-kind').dispatchEvent(new Event('change'))");
assert.equal(await evaluate(`!!(${closed})`),false,'Respect event-kind filtering');
await evaluate("document.getElementById('timeline-kind').value='all';document.getElementById('timeline-kind').dispatchEvent(new Event('change'))");
await call('Emulation.setDeviceMetricsOverride',{width:700,height:900,deviceScaleFactor:1,mobile:false});
await evaluate("document.getElementById('timeline-fit').click()");
await until(`!!(${closed})&&getComputedStyle(${closed}).visibility==='visible'`);
assert.deepEqual(errors,[]);
console.log('PASS: clustered closure has a visible clickable label in 2D/3D and narrow layout; filtering remains respected');
await call('Browser.close');ws.close();
