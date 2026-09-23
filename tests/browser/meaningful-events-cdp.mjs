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
await fetch('http://127.0.0.1:18081/fixture/semantic-events');
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09:00:00Z&to=2026-09-21T12:00:00Z'});
await until("document.readyState==='complete'&&document.getElementById('timeline-history-state')?.textContent.includes('1/1 issues loaded')");
const entries=()=>evaluate("[...document.querySelectorAll('.event-list-item')].map(e=>e.textContent)");
assert.equal((await entries()).length,6);
assert.ok((await entries()).every(t=>!t.includes('snapshot')&&!t.includes('Repeated issue title')));
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.closedEpisodes"),'1');
for(const [kind,count] of [['status',2],['labels',1],['notes',2],['comment',1]]){
 await evaluate(`document.getElementById('timeline-kind').value='${kind}';document.getElementById('timeline-kind').dispatchEvent(new Event('change'))`);
 assert.equal((await entries()).length,count,kind);
}
await evaluate("document.getElementById('timeline-kind').value='notes';document.getElementById('timeline-kind').dispatchEvent(new Event('change'));document.querySelector('.event-list-item').click()");
await until("document.getElementById('selected-event').textContent.includes('Notes updated')");
assert.ok(await evaluate("document.getElementById('selected-event').textContent.includes('Original note')&&document.getElementById('selected-event').textContent.includes('Updated note')"));
assert.equal(await evaluate("document.querySelector('.event-evidence').open"),false);
await call('Page.reload');
await until("document.readyState==='complete'&&document.getElementById('selected-event')?.textContent.includes('Notes updated')");
// Restore the whole range after selecting the first note seeks the playhead.
await evaluate("document.getElementById('timeline-kind').value='all';document.getElementById('timeline-kind').dispatchEvent(new Event('change'));document.getElementById('timeline-from').value='2026-09-21T09:00';document.getElementById('timeline-to').value='2026-09-21T12:00';document.getElementById('timeline-range').requestSubmit();document.getElementById('timeline-minimap').scrollIntoView({block:'center'})");
await delay(200);
const point=await evaluate("(()=>{const r=document.getElementById('timeline-minimap').getBoundingClientRect();return {x:r.x+r.width*50/180,y:r.y+27}})()");
await call('Input.dispatchMouseEvent',{type:'mousePressed',...point,button:'left',clickCount:1});
await call('Input.dispatchMouseEvent',{type:'mouseReleased',...point,button:'left',clickCount:1});
await until("document.getElementById('selected-event').textContent.includes('4 changes')");
assert.equal(await evaluate("document.getElementById('historical-coverage').hidden"),false);
assert.equal(await evaluate("document.getElementById('historical-coverage').open"),false);
assert.match(await evaluate("document.getElementById('historical-facts').textContent"),/Fields recorded at/);
assert.match(await evaluate("document.querySelector('#timeline-callout .callout-status-changes').textContent"),/in_progress → blocked/);
assert.equal(await evaluate("getComputedStyle(document.getElementById('timeline-callout')).borderColor"),'rgb(255, 100, 108)');
assert.equal(await evaluate("getComputedStyle(document.querySelector('#timeline-callout .callout-heading strong')).color"),'rgb(255, 100, 108)');
assert.equal(await evaluate("document.querySelectorAll('#selected-event article').length"),4);
assert.equal(await evaluate("document.querySelector('.callout-comment strong').textContent"),'Ollie');
assert.equal(await evaluate("document.querySelector('.callout-comment p').textContent"),'Ready for review');
assert.equal(await evaluate("[...document.querySelectorAll('#selected-event .event-evidence')].every(e=>!e.open)"),true);
assert.deepEqual(errors,[]);
await writeFile('/tmp/abacus-meaningful-events.png',Buffer.from((await call('Page.captureScreenshot',{format:'png'})).data,'base64'));
console.log('PASS: 12 unchanged snapshots hidden; six meaningful events; four-event cluster; filters, note comparison, hidden evidence and status geometry');
await call('Browser.close');ws.close();
