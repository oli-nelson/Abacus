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
await evaluate("document.getElementById('timeline-motion').value='full';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));document.getElementById('write-text').value='Unsent retained draft';document.getElementById('write-text').dispatchEvent(new Event('input'));document.getElementById('inspector-tab-activity').click()");
assert.equal(await evaluate("document.getElementById('inspector-panel-activity').getAnimations().length"),1);
const requests=apiCalls;
await evaluate("document.getElementById('inspector-tab-git').click()");
assert.equal(await evaluate("document.getElementById('inspector-panel-activity').getAnimations().length"),0);
assert.equal(await evaluate("document.getElementById('inspector-panel-activity').hidden"),true);
assert.equal(await evaluate("document.getElementById('inspector-panel-git').getAnimations().length"),1);
await until("document.getElementById('inspector-panel-git').getAnimations().length===0");
await evaluate("document.getElementById('inspector-tab-git').click()");
assert.equal(await evaluate("document.getElementById('inspector-panel-git').getAnimations().length"),0,'Same tab does not replay');
await evaluate("document.getElementById('inspector-tab-overview').click();document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
await until("document.getElementById('inspector-panel-overview').getAnimations().length===0");
assert.equal(await evaluate("getComputedStyle(document.querySelector('.inspector-tab-indicator')).transitionDuration"),'0s');
assert.equal(await evaluate("document.getElementById('write-text').value"),'Unsent retained draft');
assert.equal(apiCalls,requests,'Tab motion does not load source details');
await evaluate("document.getElementById('timeline-motion').value='system';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));document.getElementById('inspector-tab-activity').click()");
assert.equal(await evaluate("document.getElementById('inspector-panel-activity').getAnimations().length"),1);
await call('Emulation.setEmulatedMedia',{features:[{name:'prefers-reduced-motion',value:'reduce'}]});
await until("document.getElementById('inspector-panel-activity').getAnimations().length===0");
assert.equal(await evaluate("getComputedStyle(document.querySelector('.inspector-tab-indicator')).transitionDuration"),'0s');
await evaluate("document.getElementById('inspector-tab-git').click()");
assert.equal(await evaluate("document.getElementById('inspector-panel-git').getAnimations().length"),0);
await evaluate("document.getElementById('timeline-motion').value='full';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));document.getElementById('inspector-tab-overview').click()");
assert.equal(await evaluate("document.getElementById('inspector-panel-overview').getAnimations().length"),1,'Explicit full motion overrides system preference');
assert.equal(apiCalls,requests,'Preference changes and tab effects do not fetch sources');
await evaluate("document.getElementById('branches-view').click()");
assert.equal(await evaluate("document.getElementById('inspector-panel-overview').getAnimations().length"),0);
await evaluate("document.getElementById('timeline-view').click()");
assert.equal(await evaluate("document.getElementById('inspector-panel-overview').getAnimations().length"),0,'Returning does not replay hidden effects');
assert.equal(await evaluate("document.getElementById('write-text').value"),'Unsent retained draft');
assert.deepEqual(errors,[]);
console.log('PASS: finite inspector transitions cancel on rapid selection/reduced motion, preserve drafts, and make no source requests');
await call('Browser.close');ws.close();
