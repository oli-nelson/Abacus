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
await until("document.getElementById('connection').textContent.includes('Live')&&document.querySelectorAll('.lane-card').length===3");
await evaluate("document.getElementById('timeline-motion').value='full';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));window.maxReconnectArrivals=0;window.arrivalObserver=new MutationObserver(()=>{window.maxReconnectArrivals=Math.max(window.maxReconnectArrivals,Number(document.getElementById('timeline-stage').dataset.arrivals)||0,Number(document.getElementById('timeline-stage').dataset.laneArrivals)||0)});window.arrivalObserver.observe(document.getElementById('timeline-stage'),{attributes:true,attributeFilter:['data-arrivals','data-lane-arrivals']})");
await fetch(base+'/fixture/reconnect-arrival?lane');
await until("document.getElementById('connection').textContent==='Disconnected'");
await until("document.getElementById('connection').textContent.includes('Live')&&document.getElementById('timeline-event-list').textContent.includes('Recorded while disconnected')");
assert.equal(await evaluate('window.maxReconnectArrivals'),0,'Missed events must establish a snapshot baseline, not replay arrivals');
assert.equal(await evaluate("document.querySelectorAll('.lane-card').length"),4,'Missed lane is present in the new baseline');
await evaluate('window.arrivalObserver.disconnect()');
await fetch(base+'/fixture/comment-arrival?id=after-reconnect&time=11:40');
await until("Number(document.getElementById('timeline-stage').dataset.arrivals)>0");
await until("document.getElementById('timeline-stage').dataset.arrivals==='0'");
await fetch(base+'/fixture/lane-arrival?id=post-reconnect-lane');
await until("Number(document.getElementById('timeline-stage').dataset.laneArrivals)>0");
await until("document.getElementById('timeline-stage').dataset.laneArrivals==='0'");
assert.deepEqual(errors,[]);
console.log('PASS: automatic stream reconnect refreshes baseline without replay; subsequent live arrivals still animate');
await call('Browser.close');ws.close();
