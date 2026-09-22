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
await call('Page.navigate',{url:base+'/?from=2026-09-21T09%3A00%3A00Z&issue=bd-a1f'});
await until("document.querySelectorAll('.lane-card').length===3&&document.getElementById('connection').textContent.includes('Live')");
await evaluate("document.getElementById('timeline-motion').value='full';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
await until("Number(document.getElementById('timeline-stage').dataset.frames)>0");await delay(300);
await fetch(base+'/fixture/comment-arrival?id=cluster-new&time=11:00');
await until("Number(document.getElementById('timeline-stage').dataset.clusterArrivals)>0");
const uploads=await evaluate("document.getElementById('timeline-stage').dataset.uploads"),requests=apiCalls;
assert.match(await evaluate("document.getElementById('timeline-event-list').textContent"),/Confirmed: drag/);
assert.match(await evaluate("document.getElementById('timeline-event-list').textContent"),/New live recorded event cluster-new/);
await until("document.getElementById('timeline-stage').dataset.clusterArrivals==='0'");
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.uploads"),uploads);
assert.equal(apiCalls,requests);
await fetch(base+'/fixture/comment-arrival?id=cluster-new&time=11:00');await delay(150);
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.clusterArrivals"),'0');
await evaluate("document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
await fetch(base+'/fixture/comment-arrival?id=cluster-reduced&time=11:00');
await until("document.getElementById('timeline-event-list').textContent.includes('cluster-reduced')");
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.clusterArrivals"),'0');
const point=await evaluate("(()=>{const r=document.getElementById('timeline-minimap').getBoundingClientRect();return {x:r.left+r.width*2/3,y:r.top+r.height*(8+.5*38/3)/54}})()");
await call('Input.dispatchMouseEvent',{type:'mousePressed',...point,button:'left',clickCount:1});
await call('Input.dispatchMouseEvent',{type:'mouseReleased',...point,button:'left',clickCount:1});
await until("document.querySelectorAll('#selected-event article').length===3");
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/Oliver/);
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/Fixture author/);
assert.ok(await evaluate("[...document.querySelectorAll('#selected-event article time')].every(n=>n.dateTime==='2026-09-21T11:00:00.000Z')"));
assert.ok(await evaluate("[...document.querySelectorAll('#selected-event article')].every(n=>n.textContent.includes('Source:'))"));
await fetch(base+'/fixture/comment-arrival?id=cluster-fourth&time=11:00');
await until("document.querySelectorAll('#selected-event article').length===4");
assert.match(await evaluate("document.getElementById('timeline-callout').textContent"),/4 recorded events/);
assert.equal(await evaluate("document.getElementById('timeline-callout').getAnimations().length"),0,'Membership refresh does not replay entrance');
assert.deepEqual(errors,[]);
console.log('PASS: clustered live arrival fades once, retains individual events, reuses geometry and respects reduced motion');
await call('Browser.close');ws.close();
