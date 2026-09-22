// Requires fixture-server.mjs and a local Chrome DevTools endpoint (default 19222).
// A source that advertises whole-project history and then cannot answer must fall
// back to the per-issue reader, not leave the timeline with no recorded history.
import assert from 'node:assert/strict';
const pages=await (await fetch(`http://127.0.0.1:${process.env.CDP_PORT||19222}/json/list`)).json();
const ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
await new Promise(r=>ws.addEventListener('open',r,{once:true}));
let id=0;const pending=new Map(),errors=[];const activityCalls=[];
ws.addEventListener('message',e=>{const m=JSON.parse(e.data);
 if(m.method==='Network.requestWillBeSent'&&m.params.request.url.includes('/activity'))activityCalls.push(new URL(m.params.request.url).pathname);
 if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);
 if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);}});
function call(method,params={}){return new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}));});}
async function evaluate(expression){const r=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function until(expression){for(let i=0;i<100;i++){if(await evaluate(expression))return;await delay(100);}throw new Error('Timed out: '+expression+' errors: '+JSON.stringify(errors));}
await call('Page.enable');await call('Runtime.enable');await call('Network.enable');
await call('Emulation.setDeviceMetricsOverride',{width:1671,height:941,deviceScaleFactor:1,mobile:false});
await fetch('http://127.0.0.1:18081/fixture/project-history-unsupported');
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09%3A00%3A00Z'});
await until("document.querySelectorAll('.lane-card').length===3");
await until("document.getElementById('timeline-history-state').textContent.includes('3/3 issues loaded')");
// The refused whole-project read is tried once and then abandoned for per-issue reads.
assert.equal(activityCalls.filter(p=>p==='/api/v1/issues/activity').length,1,'Refused whole-project history is not retried in a loop');
assert.deepEqual([...new Set(activityCalls.filter(p=>p!=='/api/v1/issues/activity'))].sort(),
 ['/api/v1/issues/bd-a1f/activity','/api/v1/issues/bd-b7c/activity','/api/v1/issues/bd-c3e/activity'],
 'Every issue is read individually after the fallback');
assert.match(await evaluate("document.getElementById('timeline-history-state').textContent"),/Range-relevant issues load first/);
assert.equal(await evaluate("document.getElementById('timeline-history-retry').hidden"),true,'A successful fallback offers no retry');
// Recorded history really arrived: episodes, not empty lanes.
assert.ok(await evaluate("document.querySelectorAll('.event-list-item').length>0"),'Fallback history produces recorded events');
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.recordedStarts"),'3');
await delay(500);
assert.equal(activityCalls.filter(p=>p==='/api/v1/issues/activity').length,1,'Still no whole-project retry after settling');
assert.deepEqual(errors,[]);
console.log('PASS: refused whole-project history falls back to per-issue reads without retry loops or lost episodes');
await call('Browser.close');ws.close();
