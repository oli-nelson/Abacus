// Requires fixture-server.mjs and a local Chrome DevTools endpoint (default 19222).
// Playing the timeline must not drop what the operator is looking at: advancing the
// playhead is not a change of playback context.
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
const playhead=()=>evaluate("document.getElementById('timeline-asof').textContent");
const captioned="[...document.querySelectorAll('.lane-card')].filter(n=>getComputedStyle(n).visibility==='visible').map(n=>n.dataset.issueId)";
await call('Page.enable');await call('Runtime.enable');
await call('Emulation.setTimezoneOverride',{timezoneId:'UTC'});
await call('Emulation.setDeviceMetricsOverride',{width:1671,height:941,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09%3A00%3A00Z&to=2026-09-21T12%3A00%3A00Z&at=2026-09-21T10%3A30%3A00Z'});
await until("document.querySelectorAll('.lane-card').length>1&&document.getElementById('timeline-history-state').textContent.includes('issues loaded')");
await evaluate("document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));document.getElementById('timeline-speed').value='3600';document.getElementById('timeline-speed').dispatchEvent(new Event('change'))");

// A pinned recorded event survives playback, in the inspector, the callout and the URL.
await evaluate("[...document.querySelectorAll('.lane-card')].find(n=>n.dataset.issueId==='bd-a1f').click()");
await evaluate("document.querySelector('.timeline-options').open=true");
// Pin an event belonging to the selected lane, not whichever lane is listed first.
const laneEvent="[...document.querySelectorAll('#timeline-event-list>li')].find(li=>li.firstElementChild.textContent.startsWith('bd-a1f'))?.querySelector('.event-list-item')";
await until(`!!(${laneEvent})`);
await evaluate(`(${laneEvent}).click()`);
await until("!!new URL(location.href).searchParams.get('event')");
const pinnedEvent=await evaluate("new URL(location.href).searchParams.get('event')");
const pinnedText=await evaluate("document.getElementById('selected-event').textContent");
assert.ok(pinnedText.length,'An event is pinned before playback');
const startedAt=await playhead();
await evaluate("document.getElementById('timeline-play').click()");
for(let i=0;i<6;i++){
  await delay(250);
  assert.equal(await evaluate("new URL(location.href).searchParams.get('event')"),pinnedEvent,'Playback keeps the pinned event in the URL');
  assert.equal(await evaluate("document.getElementById('selected-event').textContent"),pinnedText,'Playback keeps the pinned event in the inspector');
  assert.equal(await evaluate("document.getElementById('selected-id').textContent"),'bd-a1f','Playback keeps the selected issue');
}
assert.notEqual(await playhead(),startedAt,'The playhead actually advanced');
await evaluate("if(document.getElementById('timeline-play').textContent==='Pause')document.getElementById('timeline-play').click()");
assert.equal(await evaluate("new URL(location.href).searchParams.get('event')"),pinnedEvent,'Pausing keeps the pinned event');

// Returning to live is a real change of context and still drops the pinned event.
await evaluate("document.getElementById('timeline-live').click()");
await until("document.getElementById('timeline-asof').textContent.startsWith('Live')");
assert.equal(await evaluate("new URL(location.href).searchParams.get('event')"),null,'Return to live clears the pinned event');

// A focused lane caption is never culled by the needle moving past its work. This
// needs a scene with no selected issue, because a selection scopes captions to it.
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09%3A00%3A00Z&to=2026-09-21T12%3A00%3A00Z&at=2026-09-21T10%3A00%3A00Z'});
await until("document.querySelectorAll('.lane-card').length>1&&document.getElementById('timeline-history-state').textContent.includes('issues loaded')");
await evaluate("document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));document.getElementById('timeline-speed').value='3600';document.getElementById('timeline-speed').dispatchEvent(new Event('change'))");
assert.equal(await evaluate("document.getElementById('selected-id').textContent"),'','No issue is selected in this scene');
await until(`${captioned}.includes('bd-c3e')`);
await evaluate("[...document.querySelectorAll('.lane-card')].find(n=>n.dataset.issueId==='bd-c3e'&&getComputedStyle(n).visibility==='visible').focus()");
assert.equal(await evaluate("document.activeElement.dataset.issueId"),'bd-c3e');
await evaluate("document.getElementById('timeline-play').click()");
// bd-c3e closes at 11:35 while the other two lanes stay open, so the needle stops
// reporting on it: without the focus guard the caption is culled and focus is lost.
await until("document.getElementById('timeline-asof').textContent.includes('11:4')||document.getElementById('timeline-asof').textContent.includes('12:00')");
assert.equal(await evaluate("document.activeElement.dataset?.issueId"),'bd-c3e','Focus stays on the focused lane caption');
assert.ok(await evaluate(`${captioned}.includes('bd-c3e')`),'The focused caption stays readable');
await evaluate("if(document.getElementById('timeline-play').textContent==='Pause')document.getElementById('timeline-play').click()");
assert.deepEqual(errors,[]);
console.log('PASS: playback keeps the pinned event, URL and focused lane caption; return to live still clears them');
await call('Browser.close');ws.close();
