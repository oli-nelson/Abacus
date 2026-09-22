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
const original=pages.find(p=>p.type==='page').id;
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09%3A00%3A00Z&issue=bd-a1f'});
await until("document.querySelectorAll('.lane-card').length===3&&!document.hidden");
const background=await call('Target.createTarget',{url:'about:blank',background:true});
await call('Target.activateTarget',{targetId:original});
await until('!document.hidden');
const base='http://127.0.0.1:18081';
await evaluate("document.getElementById('timeline-motion').value='full';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
await delay(300);
async function backgroundAndReturn(kind){
 await call('Target.activateTarget',{targetId:background.targetId});
 await until('document.hidden');
 const frames=await evaluate("document.getElementById('timeline-stage').dataset.frames");
 await fetch(base+'/fixture/lane-arrival?id=while-hidden-'+kind);
 await until("document.getElementById('timeline-event-list').textContent.includes('while-hidden-"+kind+"')");
 await delay(450);
 assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.frames"),frames,'Hidden tab must not render arrivals');
 await evaluate("window.maxResumedArrivals=0;window.resumeObserver=new MutationObserver(()=>{const d=document.getElementById('timeline-stage').dataset;window.maxResumedArrivals=Math.max(window.maxResumedArrivals,Number(d.laneArrivals)||0,Number(d.connectorArrivals)||0)});window.resumeObserver.observe(document.getElementById('timeline-stage'),{attributes:true,attributeFilter:['data-lane-arrivals','data-connector-arrivals']})");
 await call('Target.activateTarget',{targetId:original});
 await until("!document.hidden&&Number(document.getElementById('timeline-stage').dataset.frames)>"+frames);
 assert.equal(await evaluate('window.maxResumedArrivals'),0,'Neither interrupted nor hidden arrivals replay on return');
 assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.arrivalDrawRanges"),'0');
 assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.glowArrivalDrawRanges"),'0');
 assert.equal(await evaluate("[...document.querySelectorAll('.lane-card')].every(n=>n.style.opacity==='1')"),true);
 await evaluate('window.resumeObserver.disconnect()');
}
await fetch(base+'/fixture/lane-arrival?id=before-hide');
await until("Number(document.getElementById('timeline-stage').dataset.laneArrivals)>0");
await backgroundAndReturn('lane');
await fetch(base+'/fixture/git');
await evaluate("document.getElementById('inspector-tab-git').click();document.getElementById('load-issue-git').click()");
await until("!document.getElementById('load-issue-history').hidden");
await fetch(base+'/fixture/integrated');
await evaluate("document.getElementById('load-issue-git').click()");
await until("Number(document.getElementById('timeline-stage').dataset.connectorArrivals)>0");
await backgroundAndReturn('connector');
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.currentConnections"),'1','Verified containment remains after cancellation');
await call('Target.closeTarget',{targetId:background.targetId});
assert.deepEqual(errors,[]);
console.log('PASS: real hidden tab cancels lane/connector arrivals, renders no frames, retains source evidence and returns without replay');
await call('Browser.close');ws.close();
