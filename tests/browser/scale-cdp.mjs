// Requires fixture-server.mjs and a local Chrome DevTools endpoint (default 19222).
import assert from 'node:assert/strict';
import {readFile,writeFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import os from 'node:os';
const pages=await (await fetch(`http://127.0.0.1:${process.env.CDP_PORT||19222}/json/list`)).json();
const ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
await new Promise(r=>ws.addEventListener('open',r,{once:true}));
let id=0;const pending=new Map(),errors=[];let apiCalls=0;
ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.method==='Network.requestWillBeSent'&&m.params.request.url.includes('/api/v1/'))apiCalls++;if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);}});
function call(method,params={}){return new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}));});}
async function evaluate(expression){const r=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function until(expression){for(let i=0;i<100;i++){if(await evaluate(expression))return;await delay(100);}throw new Error('Timed out: '+expression+' errors: '+JSON.stringify(errors));}
const base='http://127.0.0.1:18081';
assert.deepEqual(await (await fetch(base+'/fixture/scale')).json(),{issues:1000,recordedEvents:10000});
const streams=[],readers=[];
try{
 for(let i=0;i<9;i++){
  const controller=new AbortController();streams.push(controller);
  const response=await fetch(base+'/api/v1/events',{signal:controller.signal});assert.ok(response.ok);
  const reader=response.body.getReader();readers.push(reader);const first=await reader.read();assert.ok(first.value.length>0);
 }
 await call('Page.enable');await call('Runtime.enable');await call('Network.enable');await call('Performance.enable');
 await call('Emulation.setDeviceMetricsOverride',{width:1671,height:941,deviceScaleFactor:1,mobile:false});
 await call('Page.navigate',{url:base+'/?view=issues&from=2026-09-21T09%3A00%3A00Z'});
 await until("document.getElementById('count').textContent.includes('1000')");
 await until("document.getElementById('connection').textContent.includes('Live')");
 const warm=await evaluate(`new Promise(resolve=>{
  const start=performance.now();document.getElementById('timeline-view').click();
  function check(){if(Number(document.getElementById('timeline-stage').dataset.frames)>0&&document.querySelectorAll('.lane-card').length>0)resolve(performance.now()-start);else requestAnimationFrame(check)}check();
 })`);
 await delay(700);
 const snapshot=await (await fetch(base+'/api/v1/snapshot')).json();
 assert.equal(snapshot.issues.length,1000);assert.equal(snapshot.issues.reduce((sum,issue)=>sum+issue.comments.length,0),10000);
 const displayedEvents=await evaluate("document.querySelectorAll('.event-list-item').length");assert.ok(displayedEvents>0,'Benchmark must contain visible recorded events, not empty lanes');
 const labelLayout=await evaluate(`(()=>{
  const labels=[...document.querySelectorAll('#timeline-labels>*')].filter(n=>getComputedStyle(n).visibility==='visible');
  const rects=labels.map(n=>n.getBoundingClientRect());let overlaps=0;
  for(let i=0;i<rects.length;i++)for(let j=i+1;j<rects.length;j++){const a=rects[i],b=rects[j];if(a.left<b.right&&a.right>b.left&&a.top<b.bottom&&a.bottom>b.top)overlaps++;}
  return {visible:labels.length,overlaps,accessibleLanes:document.querySelectorAll('#timeline-event-list>li').length,culled:Number(document.getElementById('timeline-stage').dataset.culledLabels)};
 })()`);
 assert.equal(labelLayout.overlaps,0,'Visible labels must not obscure each other');
 assert.ok(labelLayout.visible>0);assert.ok(labelLayout.culled>0);assert.equal(labelLayout.accessibleLanes,24);
 await writeFile('/tmp/abacus-scene-scale.png',Buffer.from((await call('Page.captureScreenshot',{format:'png'})).data,'base64'));
 assert.match(await evaluate("document.getElementById('timeline-renderer').textContent"),/WebGL/);
 assert.equal((await (await fetch(base+'/fixture/stream-clients')).json()).count,10);
 const before=await call('Performance.getMetrics'),networkBefore=apiCalls;
 const motion=await evaluate(`new Promise(resolve=>{
  const stage=document.getElementById('timeline-stage'),start=performance.now(),initial=Number(stage.dataset.frames),uploads=Number(stage.dataset.uploads);let frames=[];
  let last=initial,lastCamera=stage.dataset.camera,cameraChanges=0,direction=1;
  function move(){
   const now=performance.now(),count=Number(stage.dataset.frames);
   if(count!==last){frames.push(now);last=count;if(stage.dataset.camera!==lastCamera)cameraChanges++;lastCamera=stage.dataset.camera;}
   if(now-start>=8000){resolve({elapsedMs:now-start,renderedFrames:count-initial,frameTimes:frames,cameraChanges,uploadsBefore:uploads,uploadsAfter:Number(stage.dataset.uploads)});return;}
   const yaw=JSON.parse(stage.dataset.camera).yaw;if(yaw>.8)direction=-1;else if(yaw<-.8)direction=1;
   stage.dispatchEvent(new KeyboardEvent('keydown',{key:direction>0?'ArrowRight':'ArrowLeft',bubbles:true}));requestAnimationFrame(move);
  }requestAnimationFrame(move);
 })`);
 const after=await call('Performance.getMetrics');
 assert.equal((await (await fetch(base+'/fixture/stream-clients')).json()).count,10);
 const intervals=motion.frameTimes.slice(1).map((time,i)=>time-motion.frameTimes[i]).sort((a,b)=>a-b);
 const metric=(data,name)=>data.metrics.find(m=>m.name===name)?.value;
 const frameRate=motion.renderedFrames/(motion.elapsedMs/1000);
 assert.ok(motion.cameraChanges>=motion.renderedFrames*.8,'Benchmark must move the camera rather than sit against an orbit limit');
 assert.equal(motion.uploadsAfter,motion.uploadsBefore,'Camera motion must reuse scene buffers');
 assert.equal(apiCalls,networkBefore,'Camera motion must not query source APIs');
 const browser=await call('Browser.getVersion');
 await delay(700);
 const idleFrames=await evaluate("Number(document.getElementById('timeline-stage').dataset.frames)");
 await delay(1000);
 const idleFrameDelta=await evaluate("Number(document.getElementById('timeline-stage').dataset.frames)")-idleFrames;
 assert.equal(idleFrameDelta,0,'Settled large scene must stop rendering');
 const sourceFiles=['tests/browser/scale-cdp.mjs','tests/browser/fixture-server.mjs','src/Abacus/Dashboard/Assets/timeline.js','src/Abacus/Dashboard/Assets/timeline-model.js','src/Abacus/Dashboard/Assets/timeline-gl.js','src/Abacus/Dashboard/Assets/dashboard.js','src/Abacus/Dashboard/Assets/dashboard.css','src/Abacus/Dashboard/Assets/index.html','src/Abacus/Dashboard/Assets/inspector-resize.js'];
 const sourceHashes=Object.fromEntries(await Promise.all(sourceFiles.map(async path=>[path,createHash('sha256').update(await readFile(path)).digest('hex')])));
 const report={recordedAt:new Date().toISOString(),sourceHashes,idle:{observedMs:1000,frameDelta:idleFrameDelta},scope:'Synthetic browser scene only: one rendered client plus nine stream-only clients. Not real Beads/Git, ten rendered clients, isolated CPU, peak memory or native-GPU acceptance.',
  environment:{platform:os.platform(),architecture:os.arch(),cpu:os.cpus()[0]?.model,logicalCpus:os.cpus().length,totalMemoryBytes:os.totalmem(),browser,graphics:'Headless Chrome with SwiftShader software rendering'},
  dataset:{issues:1000,recordedEvents:10000,concurrentStreams:10},viewport:{width:1671,height:941},
  warmViewportMs:warm,labelLayout,displayedEventListItems:displayedEvents,motion:{elapsedMs:motion.elapsedMs,cameraChanges:motion.cameraChanges,renderedFrames:motion.renderedFrames,framesPerSecond:frameRate,p95ObservedFrameIntervalMs:intervals[Math.floor(intervals.length*.95)]??null,bufferUploads:motion.uploadsAfter-motion.uploadsBefore,sourceRequests:apiCalls-networkBefore},
  rendererTaskSeconds:metric(after,'TaskDuration')-metric(before,'TaskDuration'),jsHeapUsedBytes:metric(after,'JSHeapUsedSize'),
  visibleLaneCards:await evaluate("document.querySelectorAll('.lane-card').length"),domNodes:metric(after,'Nodes'),
  thresholds:{warmViewportUnder2s:warm<=2000,motionAtLeast30fps:frameRate>=30},errors};
 await writeFile('/tmp/abacus-scene-scale.json',JSON.stringify(report,null,2)+'\n');
 console.log(JSON.stringify(report,null,2));assert.deepEqual(errors,[]);
 assert.ok(report.thresholds.warmViewportUnder2s,'Warm scene activation must stay below two seconds');
 assert.ok(report.thresholds.motionAtLeast30fps,'Large-scene camera motion must sustain at least 30 FPS');
}finally{for(const stream of streams)stream.abort();await call('Browser.close');ws.close();}
