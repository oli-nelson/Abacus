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
await call('Emulation.setDeviceMetricsOverride',{width:1280,height:941,deviceScaleFactor:1,mobile:false});

await fetch('http://127.0.0.1:18081/fixture/worktree');
await call('Page.navigate',{url:'http://127.0.0.1:18081/?view=worktrees'});
await until("document.querySelectorAll('#worktrees button').length===1");
await evaluate("document.querySelector('#worktrees button').click()");
await until("document.getElementById('worktree-unstaged').textContent.includes('+edit 1')");
assert.match(await evaluate("document.getElementById('worktree-detail-state').textContent"),/Live: shared server reconciliation/);
assert.equal(await evaluate("document.querySelectorAll('#worktree-unstaged script').length"),0);
await fetch('http://127.0.0.1:18081/fixture/worktree-edit');
await until("document.getElementById('worktree-unstaged').textContent.includes('+edit 2')");
assert.match(await evaluate("document.getElementById('worktree-detail-state').textContent"),/content-2/);
assert.match(await evaluate("document.getElementById('worktree-untracked').textContent"),/new file 2/);
assert.equal(await evaluate("document.querySelectorAll('#worktree-untracked img').length"),0);
await fetch('http://127.0.0.1:18081/fixture/worktree-edit?stale');
await until("document.getElementById('worktree-detail-state').textContent.includes('Synthetic source unavailable')");
assert.equal(await evaluate("document.getElementById('worktree-unstaged').textContent"),'');
await fetch('http://127.0.0.1:18081/fixture/worktree-edit');
await until("document.getElementById('worktree-unstaged').textContent.includes('+edit 4')");
await evaluate("document.getElementById('issues-view').click()");
for(let i=0;i<100;i++){if((await (await fetch('http://127.0.0.1:18081/fixture/worktree-clients')).json()).count===0)break;await delay(20);}
assert.equal((await (await fetch('http://127.0.0.1:18081/fixture/worktree-clients')).json()).count,0);
await evaluate("document.getElementById('worktrees-view').click()");
await until("document.getElementById('worktree-unstaged').textContent.includes('+edit 5')");
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('Worktree browser: live repeated edits, literal content, stale clearing/recovery and view-scoped unsubscribe/reconnect passed');
await call('Browser.close');ws.close();
