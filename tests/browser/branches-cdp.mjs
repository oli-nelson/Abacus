// Requires fixture-server.mjs and a disposable local Chrome DevTools endpoint.
import assert from 'node:assert/strict';
const pages=await(await fetch(`http://127.0.0.1:${process.env.CDP_PORT||19222}/json/list`)).json();
const ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
await new Promise(r=>ws.addEventListener('open',r,{once:true}));
let id=0;const pending=new Map(),errors=[];
ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);}});
const call=(method,params={})=>new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}));});
async function evaluate(expression){const r=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
async function until(expression){for(let i=0;i<80;i++){if(await evaluate(expression))return;await new Promise(r=>setTimeout(r,100));}throw new Error('Timed out: '+expression);}
await call('Page.enable');await call('Runtime.enable');
await call('Page.navigate',{url:'http://127.0.0.1:18081/?view=branches'});
await until("document.getElementById('connection').textContent==='● Live'");
await fetch('http://127.0.0.1:18081/fixture/branches');
await until("document.querySelectorAll('#branches tr').length===3");
await evaluate("document.querySelectorAll('#branches tr')[1].querySelector('td button').click()");
await until("document.getElementById('comparison').textContent.includes('2 / 0')");
assert.equal(await evaluate("document.getElementById('branch-title').textContent"),'abacus/bd-a1f');
assert.equal(await evaluate("document.getElementById('comparison-evidence').open"),false);
assert.equal(await evaluate("document.getElementById('comparison-provenance').checkVisibility()"),false);
assert.equal(await evaluate("document.querySelectorAll('#branches .branch-issue-link').length"),1);
await evaluate("document.getElementById('comparison-evidence').open=true");
assert.match(await evaluate("document.getElementById('comparison-provenance').textContent"),/Comparison basis.*target\.\.\.issue/);
await evaluate("document.getElementById('load-patch').click()");
await until("document.getElementById('patch').textContent.includes('const scene')");
await call('Emulation.setDeviceMetricsOverride',{width:390,height:780,deviceScaleFactor:1,mobile:true});
await new Promise(r=>setTimeout(r,150));
assert.equal(await evaluate('document.documentElement.scrollWidth<=innerWidth'),true);
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('PASS: branch comparison prioritizes results, optional evidence and recorded commits remain accessible, patch loads, mobile has no page overflow');
await call('Browser.close');ws.close();
