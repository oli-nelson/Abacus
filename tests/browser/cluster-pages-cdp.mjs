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
await fetch(base+'/fixture/cluster-many');
await until("document.getElementById('timeline-event-list').textContent.includes('Dense member 120')");
const point=await evaluate("(()=>{const r=document.getElementById('timeline-minimap').getBoundingClientRect();return {x:r.left+r.width*2/3,y:r.top+r.height*(8+.5*38/3)/54}})()");
await call('Input.dispatchMouseEvent',{type:'mousePressed',...point,button:'left',clickCount:1});await call('Input.dispatchMouseEvent',{type:'mouseReleased',...point,button:'left',clickCount:1});
await until("document.querySelectorAll('#selected-event article').length===50");
const requests=apiCalls,seen=new Set();
async function collect(){for(const value of await evaluate("[...document.querySelectorAll('#selected-event article p')].map(n=>n.textContent)"))seen.add(value);}
await collect();
await evaluate("document.querySelector('#selected-event details').open=true");
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/1–50 of 121/);
await evaluate("[...document.querySelectorAll('#selected-event button')].find(n=>n.textContent==='Next events').click()");
assert.equal(await evaluate("document.querySelectorAll('#selected-event article').length"),50);await collect();
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/51–100 of 121/);
await evaluate("[...document.querySelectorAll('#selected-event button')].find(n=>n.textContent==='Next events').click()");
assert.equal(await evaluate("document.querySelectorAll('#selected-event article').length"),21);
await collect();assert.deepEqual([...seen].sort(),Array.from({length:121},(_,i)=>'Dense member '+i).sort());
await evaluate("[...document.querySelectorAll('#selected-event button')].find(n=>n.textContent==='Previous events').click()");
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/51–100 of 121/);
await evaluate("[...document.querySelectorAll('#selected-event button')].find(n=>n.textContent==='Next events').focus()");
await fetch(base+'/fixture/comment-arrival?id=zz-member&time=11:00');
await until("document.getElementById('selected-event').textContent.includes('51–100 of 122')");
assert.equal(await evaluate("document.activeElement.textContent"),'Next events','Source refresh preserves pager focus');
assert.equal(await evaluate("document.querySelector('#selected-event details').open"),true);
await evaluate("document.activeElement.click()");
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/101–122 of 122/);
await fetch(base+'/fixture/cluster-many?count=55');
await until("document.getElementById('selected-event').textContent.includes('51–55 of 55')");
assert.equal(await evaluate("document.querySelectorAll('#selected-event article').length"),5,'Shrinking cluster clamps retained page');
assert.equal(apiCalls,requests);assert.deepEqual(errors,[]);
console.log('PASS: 121 cluster members remain reachable with at most 50 rendered entries and no paging source requests');
await call('Browser.close');ws.close();
