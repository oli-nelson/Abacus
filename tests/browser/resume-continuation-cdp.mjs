// Requires fixture-server.mjs and a disposable local Chrome DevTools endpoint.
import assert from 'node:assert/strict';
import {writeFile} from 'node:fs/promises';
const pages=await(await fetch(`http://127.0.0.1:${process.env.CDP_PORT||19222}/json/list`)).json();
const ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
await new Promise(r=>ws.addEventListener('open',r,{once:true}));
let id=0;const pending=new Map(),errors=[];
ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);}});
const call=(method,params={})=>new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}));});
async function evaluate(expression){const r=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function until(expression){for(let i=0;i<100;i++){if(await evaluate(expression))return;await delay(100);}throw new Error('Timed out: '+expression+' errors: '+JSON.stringify(errors));}
await fetch('http://127.0.0.1:18081/fixture/open-resume');
await call('Page.enable');await call('Runtime.enable');
await call('Emulation.setDeviceMetricsOverride',{width:1671,height:941,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:'http://127.0.0.1:18081/?view=timeline&from=2026-09-21T09%3A00%3A00Z&issue=bd-a1f'});
await until("document.getElementById('timeline-stage')?.dataset.resumeConnections==='1'");
await evaluate("document.getElementById('timeline-fit').click()");await delay(700);
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.recordedStarts"),'4','Open pause and resume remain separate recorded episodes');
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.closedEpisodes"),'1','Only actual closure returns as a closed episode');
assert.equal(errors.length,0,JSON.stringify(errors));
await writeFile('/tmp/abacus-open-resume.png',Buffer.from((await call('Page.captureScreenshot',{format:'png'})).data,'base64'));
console.log('PASS: Open pause resumes on one visual lane with a dashed gap, while recorded episodes stay separate');
await call('Browser.close');ws.close();
