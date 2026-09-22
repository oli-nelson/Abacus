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
await fetch('http://127.0.0.1:18081/fixture/reference');
await call('Page.enable');await call('Runtime.enable');await call('Network.enable');
await call('Emulation.setTimezoneOverride',{timezoneId:'UTC'});
await call('Emulation.setDeviceMetricsOverride',{width:1671,height:941,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:'http://127.0.0.1:18081/?view=timeline&issue=bd-a1f&from=2026-09-21T09%3A00%3A00Z'});
await until("document.querySelectorAll('.lane-card').length===3");
assert.equal(await evaluate("document.getElementById('workspace-tools').open"),false);
await evaluate("document.querySelector('#workspace-tools>summary').focus()");
await call('Input.dispatchKeyEvent',{type:'keyDown',key:' ',code:'Space',windowsVirtualKeyCode:32});
await call('Input.dispatchKeyEvent',{type:'keyUp',key:' ',code:'Space',windowsVirtualKeyCode:32});
await until("document.getElementById('workspace-tools').open");
assert.ok(await evaluate("document.getElementById('status').getBoundingClientRect().height>0"));
await evaluate("document.querySelector('#workspace-tools>summary').click()");

for(const issue of ['bd-a1f','bd-b7c','bd-c3e']){
 await evaluate(`[...document.querySelectorAll('.lane-card')].find(n=>n.dataset.issueId==='${issue}').click();document.getElementById('inspector-tab-git').click();document.getElementById('load-issue-git').click()`);
 await until("!document.getElementById('load-issue-history').hidden");
 await evaluate("document.getElementById('load-issue-history').click()");
 await until("document.querySelectorAll('#issue-git-history article').length===2");
 await evaluate("document.getElementById('inspector-tab-activity').click();document.getElementById('load-activity').click()");
 await until("document.querySelectorAll('#activity article').length==="+(issue==='bd-a1f'?4:1));
}
await evaluate("[...document.querySelectorAll('.lane-card')].find(n=>n.dataset.issueId==='bd-a1f').click();document.getElementById('inspector-tab-overview').click();document.querySelector('.timeline-options').open=true;[...document.querySelectorAll('.event-list-item')].find(n=>n.textContent.includes('Confirmed: drag')).click();document.querySelector('.timeline-options').open=false;document.getElementById('timeline-fit').click()");
await delay(800);
assert.equal(await evaluate("document.querySelector('#timeline-callout .author-initials').textContent"),'O');
assert.equal(await evaluate("document.querySelector('#timeline-callout .callout-heading strong').textContent"),'Oliver');
assert.equal(await evaluate("document.querySelector('#selected-event details').open"),false);
await evaluate("document.querySelector('#selected-event summary').focus()");
await call('Input.dispatchKeyEvent',{type:'keyDown',key:' ',code:'Space',windowsVirtualKeyCode:32});
await call('Input.dispatchKeyEvent',{type:'keyUp',key:' ',code:'Space',windowsVirtualKeyCode:32});
await until("document.querySelector('#selected-event details').open");
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/Confirmed: drag/);
await evaluate("document.querySelector('#selected-event summary').click();document.getElementById('timeline-stage').focus()");
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.recordedStarts"),'3');
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.currentConnections"),'1');
assert.equal(await evaluate("document.getElementById('timeline-callout').dataset.anchored"),'true');
const pointerBefore=await evaluate("document.querySelector('#timeline-callout-leader path').getAttribute('d')");
const requestsBefore=apiCalls,uploadsBefore=await evaluate("document.getElementById('timeline-stage').dataset.uploads");
await call('Input.dispatchKeyEvent',{type:'keyDown',key:'ArrowRight',code:'ArrowRight',windowsVirtualKeyCode:39});
await call('Input.dispatchKeyEvent',{type:'keyUp',key:'ArrowRight',code:'ArrowRight',windowsVirtualKeyCode:39});
await delay(150);
assert.notEqual(await evaluate("document.querySelector('#timeline-callout-leader path').getAttribute('d')"),pointerBefore,'Bubble pointer follows camera projection');
assert.equal(apiCalls,requestsBefore);assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.uploads"),uploadsBefore);
assert.ok(await evaluate("(()=>{const b=document.getElementById('timeline-callout').getBoundingClientRect(),s=document.getElementById('timeline-stage').getBoundingClientRect();return b.left>=s.left&&b.right<=s.right&&b.top>=s.top&&b.bottom<=s.bottom})()"),'Bubble stays inside scene');
await call('Input.dispatchKeyEvent',{type:'keyDown',key:'ArrowLeft',code:'ArrowLeft',windowsVirtualKeyCode:37});
await call('Input.dispatchKeyEvent',{type:'keyUp',key:'ArrowLeft',code:'ArrowLeft',windowsVirtualKeyCode:37});
await delay(150);
assert.equal(await evaluate("document.querySelectorAll('.observation-list-item').length"),3,'Every live current/containment marker has an accessible list entry');
assert.match(await evaluate("[...document.querySelectorAll('.observation-list-item')].map(n=>n.textContent).join(' ')"),/Exact integration time unknown/);
assert.equal(await evaluate("document.querySelectorAll('.waiting-marker').length"),1,'Live blocked lane has a waiting glyph');
assert.match(await evaluate("document.querySelector('.waiting-marker').getAttribute('aria-label')"),/bd-b7c.*blocked.*waiting now/);
const visible=await evaluate("[...document.querySelectorAll('.lane-card')].filter(n=>getComputedStyle(n).visibility==='visible').length");
await writeFile('/tmp/abacus-reference-scene.png',Buffer.from((await call('Page.captureScreenshot',{format:'png'})).data,'base64'));
assert.ok(await evaluate("[...document.querySelectorAll('.event-annotation')].some(n=>getComputedStyle(n).visibility==='visible')"),'Selected branch has readable event captions');
assert.equal(visible,3,'Three-lane reference scene must retain three readable lane cards');
assert.equal(await evaluate("document.documentElement.scrollWidth<=1671"),true);

await evaluate("document.querySelector('.waiting-marker').focus()");
await call('Input.dispatchKeyEvent',{type:'keyDown',key:' ',code:'Space',windowsVirtualKeyCode:32});
await call('Input.dispatchKeyEvent',{type:'keyUp',key:' ',code:'Space',windowsVirtualKeyCode:32});
await until("document.getElementById('selected-id').textContent==='bd-b7c'");
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/current/i);
await evaluate("document.querySelector('.timeline-options').open=true;[...document.querySelectorAll('.observation-list-item')].find(n=>n.textContent.includes('Verified current containment')).focus()");
await call('Input.dispatchKeyEvent',{type:'keyDown',key:' ',code:'Space',windowsVirtualKeyCode:32});
await call('Input.dispatchKeyEvent',{type:'keyUp',key:' ',code:'Space',windowsVirtualKeyCode:32});
await until("document.getElementById('selected-id').textContent==='bd-c3e'");
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/Exact integration time unknown/);
assert.match(await evaluate("document.getElementById('selected-event').textContent"),/Validated current Git ancestry.*Source revision:/s);
assert.equal(await evaluate("document.querySelector('#timeline-callout .callout-heading strong').textContent"),'Git containment');
assert.match(await evaluate("document.querySelector('#selected-event time').textContent"),/^Displayed at: /);
assert.match(await evaluate("[...document.querySelectorAll('.observation-list-item')].find(n=>n.textContent.includes('Verified current containment')).title"),/^Displayed .*not a source observation or recorded transition time/);
assert.match(await evaluate("document.getElementById('timeline-callout').textContent"),/not a recorded merge event/);
await evaluate("document.querySelector('.timeline-options').open=false");

await evaluate("[...document.querySelectorAll('.event-list-item')].find(n=>n.textContent.includes('Confirmed: drag')).click()");

// A filtered or offscreen bead must not leave a misleading pointer behind.
await evaluate("document.getElementById('timeline-kind').value='closure';document.getElementById('timeline-kind').dispatchEvent(new Event('change'))");
await delay(100);
assert.equal(await evaluate("document.getElementById('timeline-callout').dataset.anchored"),'false');
assert.equal(await evaluate("document.getElementById('timeline-callout').hidden"),false,'Filtered selection remains readable');
assert.equal(await evaluate("getComputedStyle(document.getElementById('timeline-callout-leader')).display"),'none');
await evaluate("document.getElementById('timeline-kind').value='all';document.getElementById('timeline-kind').dispatchEvent(new Event('change'));document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'));document.getElementById('timeline-2d').click();document.getElementById('timeline-stage').focus()");
await until("document.getElementById('timeline-callout').dataset.anchored==='true'");
for(let i=0;i<60;i++)await call('Input.dispatchKeyEvent',{type:'keyDown',key:'ArrowRight',code:'ArrowRight',windowsVirtualKeyCode:39,modifiers:8});
await call('Input.dispatchKeyEvent',{type:'keyUp',key:'ArrowRight',code:'ArrowRight',windowsVirtualKeyCode:39});
await until("document.getElementById('timeline-callout').dataset.anchored==='false'");
assert.equal(await evaluate("getComputedStyle(document.getElementById('timeline-callout-leader')).display"),'none');
await evaluate("document.getElementById('timeline-fit').click()");
await until("document.getElementById('timeline-callout').dataset.anchored==='true'");
await evaluate("document.getElementById('timeline-canvas').getContext('webgl').getExtension('WEBGL_lose_context').loseContext()");
await until("!document.getElementById('timeline-fallback').hidden");
assert.equal(await evaluate("document.getElementById('timeline-callout').dataset.anchored"),'true','Fallback uses its actual 2D camera for anchoring');
const settledFrames=await evaluate("document.getElementById('timeline-stage').dataset.frames");
await delay(400);
assert.equal(await evaluate("document.getElementById('timeline-stage').dataset.frames"),settledFrames,'Anchored bubble does not create an idle animation loop');
assert.equal(apiCalls,requestsBefore,'Bubble edge cases do not query source data');
await evaluate("document.querySelector('#timeline-callout .callout-close').click()");
await delay(100);
assert.equal(await evaluate("getComputedStyle(document.getElementById('timeline-callout-leader')).display"),'none','Dismissal removes pointer');
await evaluate("document.getElementById('timeline-scrub').value='500';document.getElementById('timeline-scrub').dispatchEvent(new Event('input'))");
assert.equal(await evaluate("document.querySelectorAll('.waiting-marker').length"),0,'Current waiting glyph must not invent historical state');
assert.equal(await evaluate("document.querySelectorAll('.observation-list-item').length"),0,'Playback must not expose current observations as historical events');
assert.deepEqual(errors,[]);
console.log('Reference scene: three recorded starts, one verified current return, three visible cards, loaded snapshots and pinned comment passed');
await call('Browser.close');ws.close();
