// Requires fixture-server.mjs and a local Chrome DevTools endpoint (default 19222).
// A bounded number of lanes is drawn at a time. Advancing the playhead reveals later work, and
// that must never push a lane already drawn off the page: recorded history in the
// past cannot disappear because time moved forward.
import assert from 'node:assert/strict';
const pages=await (await fetch(`http://127.0.0.1:${process.env.CDP_PORT||19222}/json/list`)).json();
const ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
await new Promise(r=>ws.addEventListener('open',r,{once:true}));
let id=0;const pending=new Map(),errors=[];
ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);}});
function call(method,params={}){return new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}));});}
async function evaluate(expression){const r=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function until(expression){for(let i=0;i<150;i++){if(await evaluate(expression))return;await delay(100);}throw new Error('Timed out: '+expression+' errors: '+JSON.stringify(errors));}
const drawn=()=>evaluate("[...document.querySelectorAll('#timeline-event-list>li>button:first-child')].map(n=>n.textContent.split(' \\u00b7 ')[0])");
const page=()=>evaluate("document.getElementById('timeline-page').textContent");
await call('Page.enable');await call('Runtime.enable');
await call('Emulation.setTimezoneOverride',{timezoneId:'UTC'});
await call('Emulation.setDeviceMetricsOverride',{width:1671,height:1000,deviceScaleFactor:1,mobile:false});
// Whole-project history so every lane has episodes; more lanes than a page holds.
await fetch('http://127.0.0.1:18081/fixture/project-history');
await fetch('http://127.0.0.1:18081/fixture/staggered-work?early=140&late=40');
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09%3A00%3A00Z&to=2026-09-21T12%3A00%3A00Z&at=2026-09-21T10%3A00%3A00Z'});
await until("document.readyState==='complete'&&/Status history: (\\d+)\\/\\1 issues loaded/.test(document.getElementById('timeline-history-state')?.textContent||'')");
await evaluate("document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
await until("document.querySelectorAll('#timeline-event-list>li').length===120");
const before=await drawn(),beforePage=await page();
// Lanes are ordered by when their work starts, so the page holds the earliest work.
assert.ok(before.every(x=>x.startsWith('zzz-')||x.startsWith('bd-')),'Page 1 holds the earliest work: '+before.join(','));
assert.ok(!before.some(x=>x.startsWith('aaa-')),'Work that has not started is not drawn yet');
// Scrub past 10:30, when the late work with early-sorting IDs is revealed.
await evaluate("document.getElementById('timeline-scrub').value='667';document.getElementById('timeline-scrub').dispatchEvent(new Event('input'))");
await until("document.getElementById('timeline-page').textContent!=="+JSON.stringify(beforePage));
await delay(300);
const after=await drawn(),afterSet=new Set(after);
const lost=before.filter(x=>!afterSet.has(x));
assert.deepEqual(lost,[],'No lane already drawn may be dropped when the playhead advances');
assert.ok((await page()).includes('/ 183 lanes'),'Later work joins the lane count: '+(await page()));
assert.ok(!after.some(x=>x.startsWith('aaa-')),'Later work is appended, not inserted ahead of drawn lanes');
// Scrubbing back is symmetric: the earliest work is still the page.
await evaluate("document.getElementById('timeline-scrub').value='333';document.getElementById('timeline-scrub').dispatchEvent(new Event('input'))");
await delay(400);
assert.deepEqual(await drawn(),before,'Returning to the earlier playhead restores the same page');
assert.deepEqual(errors,[]);
console.log('PASS: advancing the playhead appends later work and never drops a drawn lane');
await call('Browser.close');ws.close();
