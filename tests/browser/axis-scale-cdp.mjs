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
await call('Emulation.setTimezoneOverride',{timezoneId:'UTC'});
await call('Emulation.setDeviceMetricsOverride',{width:1671,height:1000,deviceScaleFactor:1,mobile:false});
await call('Page.navigate',{url:'http://127.0.0.1:18081/?from=2026-09-21T09:00:00Z'});
await until("document.readyState==='complete'&&document.getElementById('timeline-history-state')?.textContent.includes('3/3 issues loaded')");

await evaluate("document.getElementById('timeline-motion').value='reduce';document.getElementById('timeline-motion').dispatchEvent(new Event('change'))");
const requests=apiCalls;
const scaleIs=(t,v)=>`(()=>{const s=JSON.parse(document.getElementById('timeline-stage').dataset.camera).axisScale;return s[0]===${t}&&s[1]===${v};})()`;
// Software rendering can miss a fixed delay, so wait for the scene to publish the
// new scale rather than assuming a frame has landed.
const change=async(axis,value)=>{await evaluate(`document.getElementById('timeline-scale-${axis}').value='${value}';document.getElementById('timeline-scale-${axis}').dispatchEvent(new Event('input',{bubbles:true}))`);await until(`JSON.parse(document.getElementById('timeline-stage').dataset.camera).axisScale[${axis==='time'?0:1}]===${value}`);};
const camera=()=>evaluate("JSON.parse(document.getElementById('timeline-stage').dataset.camera)");
const beforeUrl=await evaluate('location.href');
await change('time',10);await change('vertical',10);
assert.deepEqual((await camera()).axisScale,[10,10,1]);
assert.equal(await evaluate('location.href'),beforeUrl);
await evaluate("document.getElementById('timeline-2d').click()");
await until("JSON.parse(document.getElementById('timeline-stage').dataset.camera).perspective===0");
assert.deepEqual((await camera()).axisScale,[10,10,1]);
await evaluate("document.getElementById('timeline-fit').click()");await delay(300);
assert.deepEqual((await camera()).axisScale,[10,10,1]);
await evaluate("document.getElementById('timeline-3d').click()");
await until("JSON.parse(document.getElementById('timeline-stage').dataset.camera).perspective===1");
assert.deepEqual((await camera()).axisScale,[10,10,1]);
assert.equal(apiCalls,requests,'Stretching must not fetch sources');
await call('Page.reload');
await until("document.readyState==='complete'&&document.getElementById('timeline-stage')?.dataset.camera");
await until(scaleIs(10,10));
assert.deepEqual((await camera()).axisScale,[10,10,1]);
// Full stretch: the slider accepts it and the camera can actually reach the scene.
await change('time',40);await change('vertical',40);
assert.deepEqual((await camera()).axisScale,[40,40,1]);
await evaluate("document.getElementById('timeline-fit').click()");await delay(300);
const stretched=(await camera()).distance;
assert.ok(stretched>250,'Fitting a 40x scene needs more distance than the 1x limit');
await until("(()=>{const gl=document.getElementById('timeline-canvas').getContext('webgl');return gl&&gl.getParameter(gl.CURRENT_PROGRAM);})()");
const clip=await evaluate("(()=>{const gl=document.getElementById('timeline-canvas').getContext('webgl'),program=gl.getParameter(gl.CURRENT_PROGRAM);return [gl.getUniform(program,gl.getUniformLocation(program,'u_clipA')),gl.getUniform(program,gl.getUniformLocation(program,'u_clipB'))];})()");
assert.ok(clip[0]>1&&clip[1]>.1,'WebGL must upload a valid depth projection');
assert.ok(.1*(clip[0]+1)/(clip[0]-1)>stretched,'Far clip must extend past the stretched camera distance');
await evaluate("document.getElementById('timeline-stage').focus();");
await call('Input.dispatchKeyEvent',{type:'keyDown',key:'-',code:'Minus',windowsVirtualKeyCode:189});
await call('Input.dispatchKeyEvent',{type:'keyUp',key:'-',code:'Minus',windowsVirtualKeyCode:189});
await delay(150);
assert.ok((await camera()).distance>=stretched,'Zooming out at 40x is not clamped back to the 1x limit');
await evaluate("{const stage=document.getElementById('timeline-stage');for(let i=0;i<12;i++)stage.dispatchEvent(new WheelEvent('wheel',{deltaY:200,ctrlKey:true,bubbles:true,cancelable:true}));}");
await until("JSON.parse(document.getElementById('timeline-stage').dataset.camera).distance===10000");
const farClip=await evaluate("(()=>{const gl=document.getElementById('timeline-canvas').getContext('webgl'),program=gl.getParameter(gl.CURRENT_PROGRAM),a=gl.getUniform(program,gl.getUniformLocation(program,'u_clipA'));return .1*(a+1)/(a-1);})()");
assert.ok(farClip>10000,'Paths must remain inside the depth range at maximum zoom-out');
// Reducing the stretch pulls a far camera back instead of stranding it outside the scene.
await evaluate("document.getElementById('timeline-scale-reset').click()");
await until(scaleIs(1,1));
assert.deepEqual((await camera()).axisScale,[1,1,1]);
assert.ok((await camera()).distance<=250,'Reset brings the camera back within the 1x limit');
// Exercise the same projection through canvas fallback.
await evaluate("document.getElementById('timeline-canvas').getContext('webgl').getExtension('WEBGL_lose_context').loseContext()");
await delay(300);await change('vertical',2);
assert.equal((await camera()).perspective,0);assert.deepEqual((await camera()).axisScale,[1,2,1]);
assert.deepEqual(errors,[]);
console.log('PASS: independent axis controls, 2D/3D, fit, persisted scales, reset, fallback, no source requests');
await call('Browser.close');ws.close();
