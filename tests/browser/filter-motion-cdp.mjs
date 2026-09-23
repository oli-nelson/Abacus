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
await call('Page.navigate',{url:'http://127.0.0.1:18081/?issue=bd-a1f'});
await until("!document.getElementById('issue-form').hidden");
const active="['timeline-canvas','timeline-fallback','timeline-markers','timeline-labels'].reduce((n,id)=>n+document.getElementById(id).getAnimations().length,0)";
const requests=apiCalls;
const change=kind=>evaluate(`document.getElementById('timeline-kind').value='${kind}';document.getElementById('timeline-kind').dispatchEvent(new Event('change'));${active}`);
await evaluate("document.getElementById('timeline-motion').value='full';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
assert.equal(await change('comment'),4);

assert.equal(await change('closure'),4,'Rapid change replaces rather than stacks effects');
await until(`(${active})===0`);
const settledUploads=await evaluate("document.getElementById('timeline-stage').dataset.uploads");
assert.equal(await change('closure'),0,'Identical selection does not replay');
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.uploads"),settledUploads);
assert.equal(await change('all'),4);
await evaluate("document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
assert.equal(await evaluate(active),0,'Reduced motion cancels current fade');
assert.equal(await change('comment'),0);
await evaluate("document.getElementById('timeline-motion').value='full';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));document.getElementById('search').value='renderer';document.getElementById('search').dispatchEvent(new Event('input'))");
assert.equal(await evaluate(active),4,'Timeline search changes also animate');
await until(`(${active})===0`);
const finalUploads=await evaluate("document.getElementById('timeline-stage').dataset.uploads");
await delay(250);
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.uploads"),finalUploads,'Settled animation does not upload geometry');
await evaluate("document.getElementById('timeline-motion').value='system';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
assert.equal(await change('all'),4);
await call('Emulation.setEmulatedMedia',{features:[{name:'prefers-reduced-motion',value:'reduce'}]});
await until(`(${active})===0`);
assert.equal(await change('closure'),0,'System reduced motion suppresses new fades');
await evaluate("document.getElementById('timeline-motion').value='full';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
assert.equal(await change('all'),4,'Explicit full motion overrides system preference');
await evaluate("document.getElementById('issues-view').click()");
assert.equal(await evaluate(active),0,'Leaving timeline cancels filter effects');
await evaluate("document.getElementById('timeline-view').click()");
assert.equal(await evaluate(active),0,'Returning does not replay filter effects');
assert.equal(apiCalls,requests,'Filter transitions stay local');
assert.deepEqual(errors,[]);
console.log('PASS: finite filter fades, rapid replacement, identical no-op, reduced-motion cancellation and no source requests');
await call('Browser.close');ws.close();
