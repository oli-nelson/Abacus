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
await call('Page.enable');
await call('Page.addScriptToEvaluateOnNewDocument',{source:"localStorage.removeItem('abacus.timeline.mode');localStorage.removeItem('abacus.motion');"});
await call('Network.enable');await call('Runtime.enable');await call('Emulation.setDeviceMetricsOverride',{width:1671,height:941,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09%3A00%3A00Z&issue=bd-a1f'});
await until('document.querySelectorAll(".lane-card").length===3 && document.getElementById("timeline-stage").dataset.frames && !document.getElementById("activity-section").hidden');
assert.match(await evaluate('document.getElementById("timeline-renderer").textContent'),/WebGL/);
for(const issue of ['bd-a1f','bd-b7c','bd-c3e']){
 await evaluate(`[...document.querySelectorAll('.lane-card')].find(n=>n.dataset.issueId==='${issue}').click()`);
 await evaluate("document.getElementById('load-activity').click()");
 await until("document.querySelectorAll('#activity article').length>0 && !document.getElementById('load-activity').disabled");
}
await evaluate("document.querySelector('#activity .show-timeline-event').click()");
assert.equal(await evaluate("document.getElementById('issue-form').hidden"),true);
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/snapshot/);
assert.equal(await evaluate("document.activeElement.id"),'timeline-stage');
await evaluate("document.getElementById('timeline-live').click()");
await until("!document.getElementById('issue-form').hidden");
await evaluate("document.querySelector('#comments .show-timeline-event').click()");
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/comment/);
assert.equal(await evaluate("document.activeElement.id"),'timeline-stage');
await evaluate("document.getElementById('timeline-live').click()");
await until("!document.getElementById('issue-form').hidden");
// Selection rebuilds the timeline overlays and event list; focus must follow
// logical identity rather than falling back to body when old nodes are removed.
await evaluate("{document.querySelector('.timeline-options').open=true;const b=document.querySelector('#timeline-event-list>li>button');b.focus();window.expectedTimelineFocus=b.dataset.timelineFocus;b.click()}");
assert.equal(await evaluate("document.activeElement.dataset.timelineFocus"),await evaluate("window.expectedTimelineFocus"));
await evaluate("{const b=[...document.querySelectorAll('.event-list-item')].find(n=>n.textContent.includes('Confirmed: drag'));b.focus();window.expectedTimelineFocus=b.dataset.timelineFocus;b.click()}");
assert.equal(await evaluate("document.activeElement.dataset.timelineFocus"),await evaluate("window.expectedTimelineFocus"));
await evaluate("{document.querySelector('.timeline-options').open=false;const b=[...document.querySelectorAll('.lane-card')].find(n=>getComputedStyle(n).visibility==='visible');b.focus();window.expectedTimelineFocus=b.dataset.timelineFocus;b.click()}");
await delay(100);
assert.equal(await evaluate("document.activeElement.dataset.timelineFocus"),await evaluate("window.expectedTimelineFocus"));
await evaluate("[...document.querySelectorAll('.lane-card')].find(n=>n.dataset.issueId==='bd-a1f').click()");
await evaluate("document.querySelector('.timeline-options').open=true;[...document.querySelectorAll('.event-list-item')].find(n=>n.textContent.includes('Confirmed: drag')).click();document.querySelector('.timeline-options').open=false");
assert.equal(await evaluate("document.getElementById('timeline-callout').hidden"),false);
// Deliberate selections animate once; identical selections never replay effects.
await evaluate("document.getElementById('timeline-motion').value='full';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));document.querySelector('.timeline-options').open=true;document.querySelector('.event-list-item').click()");
assert.equal(await evaluate("document.getElementById('timeline-callout').getAnimations().length"),1);
await until("document.getElementById('timeline-callout').getAnimations().length===0");
await evaluate("document.querySelector('.event-list-item').click()");
assert.equal(await evaluate("document.getElementById('timeline-callout').getAnimations().length"),0);
await evaluate("document.querySelectorAll('.event-list-item')[1].click();document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
assert.equal(await evaluate("document.getElementById('timeline-callout').getAnimations().length"),0);
await evaluate("document.querySelector('.callout-close').click()");
assert.equal(await evaluate("document.activeElement.id"),'timeline-stage');
await evaluate("[...document.querySelectorAll('.event-list-item')].find(n=>n.textContent.includes('Confirmed: drag')).click();document.querySelector('.timeline-options').open=false");

await evaluate("document.getElementById('timeline-motion').value='system';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));document.getElementById('timeline-3d').click();document.getElementById('timeline-fit').click()");
await delay(700);
assert.equal(await evaluate("JSON.parse(document.getElementById('timeline-stage').dataset.camera).perspective"),1);
const shot=await call('Page.captureScreenshot',{format:'png'});await writeFile('/tmp/abacus-timeline-3d.png',Buffer.from(shot.data,'base64'));
const stats=()=>evaluate("({...document.getElementById('timeline-stage').dataset})");
const beforeApi=apiCalls;const before=await stats();await delay(800);assert.equal((await stats()).frames,before.frames,'Settled scene must stop drawing');
const box=await evaluate("(()=>{const r=document.getElementById('timeline-stage').getBoundingClientRect();return {x:r.x+500,y:r.y+80};})()");
await call('Input.dispatchMouseEvent',{type:'mousePressed',x:box.x,y:box.y,button:'left',clickCount:1});
await call('Input.dispatchMouseEvent',{type:'mouseMoved',x:box.x+100,y:box.y+35,button:'left',buttons:1});
await call('Input.dispatchMouseEvent',{type:'mouseReleased',x:box.x+100,y:box.y+35,button:'left',clickCount:1});
await delay(100);const orbit=await stats();assert.notEqual(orbit.camera,before.camera);assert.equal(orbit.uploads,before.uploads,'Orbit must reuse geometry');
await evaluate("document.getElementById('timeline-2d').click()");await delay(650);
assert.equal(JSON.parse((await stats()).camera).perspective,0);
await evaluate("document.getElementById('write-text').value='Draft survives playback';document.getElementById('write-text').dispatchEvent(new Event('input',{bubbles:true}));");
await evaluate("document.getElementById('timeline-scrub').value=100;document.getElementById('timeline-scrub').dispatchEvent(new Event('input'))");
assert.equal(await evaluate("document.getElementById('issue-form').hidden"),true);
assert.match(await evaluate("document.getElementById('details').textContent"),/Unknown at this time/);
assert.match(await evaluate("document.getElementById('timeline-counts').textContent"),/unknown/);
assert.equal(apiCalls,beforeApi,'Camera and playback must not request source data');
await evaluate("document.getElementById('timeline-live').click()");
await until("!document.getElementById('issue-form').hidden");
assert.equal(await evaluate("document.getElementById('write-text').value"),'Draft survives playback');
await evaluate("document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));document.getElementById('timeline-3d').click()");
await delay(50);assert.equal(JSON.parse((await stats()).camera).perspective,1);
assert.equal(await evaluate("getComputedStyle(document.getElementById('timeline-fit')).transitionDuration"),'0s');
await evaluate("document.getElementById('timeline-canvas').getContext('webgl').getExtension('WEBGL_lose_context').loseContext()");
await until("!document.getElementById('timeline-fallback').hidden");
assert.equal(await evaluate("document.getElementById('timeline-2d').getAttribute('aria-pressed')"),'true');
await call('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
await delay(250);
assert.ok(await evaluate("document.getElementById('timeline-stage').clientWidth<=390"));
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('WebGL render, three lanes, lazy snapshots, orbit buffer reuse, settled idle, 2D, playback safety, reduced motion, context-loss fallback and narrow layout passed');
await call('Browser.close');ws.close();
