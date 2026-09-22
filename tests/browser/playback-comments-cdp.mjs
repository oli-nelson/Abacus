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
await call('Page.navigate',{url:'http://127.0.0.1:18081/?issue=bd-a1f&from=2026-09-21T09:00:00Z'});
await until("!document.getElementById('issue-form').hidden");
await evaluate("document.getElementById('write-text').value='My unsent comment';document.getElementById('write-text').dispatchEvent(new Event('input',{bubbles:true}))");
await evaluate("document.getElementById('timeline-maximize').click();document.getElementById('timeline-scrub').value=900;document.getElementById('timeline-scrub').dispatchEvent(new Event('input',{bubbles:true}))");
assert.ok(await evaluate("document.getElementById('issue-form').hidden&&!document.getElementById('inspector-mode').hidden"));
await evaluate("document.getElementById('inspector-comments').click()");
assert.ok(await evaluate("!document.getElementById('inspector-panel-activity').hidden&&document.getElementById('comments').textContent.includes('Oliver')&&document.getElementById('comments').textContent.includes('Confirmed:')"));
await evaluate("document.getElementById('timeline-scrub').value=100;document.getElementById('timeline-scrub').dispatchEvent(new Event('input',{bubbles:true}))");
assert.equal(await evaluate("document.querySelectorAll('#comments>article').length"),0);
assert.ok(await evaluate("document.getElementById('comments-coverage').textContent.includes('later or undated')"));
// A failed current-issue reread must leave a visible retry, not a hidden error.
await evaluate("window.originalFetch=window.fetch;window.fetch=(url,...args)=>String(url)==='/api/v1/issues/bd-a1f'?Promise.reject(new Error('fixture read failure')):originalFetch(url,...args);document.getElementById('inspector-edit-live').click()");
await until("document.getElementById('inspector-edit-live').textContent==='Retry current issue'");
assert.ok(await evaluate("document.getElementById('issue-form').hidden&&!document.getElementById('inspector-edit-live').disabled"));
await evaluate("window.fetch=window.originalFetch;document.getElementById('inspector-edit-live').click()");
await until("!document.getElementById('issue-form').hidden");
assert.equal(await evaluate("document.getElementById('write-text').value"),'My unsent comment');
assert.ok(await evaluate("document.getElementById('comments').textContent.includes('Oliver')&&document.getElementById('inspector-mode').hidden"));
await evaluate("document.getElementById('write-action').value='edit';document.getElementById('write-action').dispatchEvent(new Event('input',{bubbles:true}))");
assert.ok(await evaluate("!document.getElementById('edit-fields').hidden&&!document.getElementById('write-add-labels').disabled"));
assert.deepEqual(errors,[]);
console.log('PASS: playback comments, future exclusion, maximized return-to-live, visible refresh retry, draft retention and label editor');
await call('Browser.close');ws.close();
