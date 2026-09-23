// Requires fixture-server.mjs and a disposable local Chrome DevTools endpoint.
import assert from 'node:assert/strict';
const pages=await(await fetch(`http://127.0.0.1:${process.env.CDP_PORT||19222}/json/list`)).json();
const ws=new WebSocket(pages.find(p=>p.type==='page').webSocketDebuggerUrl);
await new Promise(r=>ws.addEventListener('open',r,{once:true}));
let id=0;const pending=new Map(),errors=[];
ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(new Error(JSON.stringify(m.error))):p.resolve(m.result);}});
const call=(method,params={})=>new Promise((resolve,reject)=>{const n=++id;pending.set(n,{resolve,reject});ws.send(JSON.stringify({id:n,method,params}));});
async function evaluate(expression){const r=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw new Error(JSON.stringify(r.exceptionDetails));return r.result.value;}
const delay=ms=>new Promise(r=>setTimeout(r,ms));
await call('Page.enable');await call('Runtime.enable');
for(const width of [1024,800,390]){
 await call('Emulation.setDeviceMetricsOverride',{width,height:900,deviceScaleFactor:1,mobile:width<720});
 await call('Page.navigate',{url:'http://127.0.0.1:18081/?view=timeline&issue=bd-a1f&from=2026-09-21T09%3A00%3A00Z'});
 for(let i=0;i<80&&!await evaluate("document.getElementById('connection').textContent==='● Live'");i++)await delay(100);
 const layout=await evaluate("(()=>{const w=document.querySelector('.workspace').getBoundingClientRect(),a=document.querySelector('aside').getBoundingClientRect(),s=document.getElementById('timeline-stage').getBoundingClientRect(),c=document.getElementById('timeline-counts').getBoundingClientRect(),o=document.querySelector('.timeline-options').getBoundingClientRect(),f=document.querySelector('.workspace>footer').getBoundingClientRect();return {overflow:document.documentElement.scrollWidth>innerWidth,header:document.querySelector('header').getBoundingClientRect().height,workspace:{left:w.left,right:w.right,bottom:w.bottom},aside:{left:a.left,top:a.top,right:a.right},stageWidth:s.width,countsTop:c.top,countsBottom:c.bottom,optionsBottom:o.bottom,footerTop:f.top,footerBottom:f.bottom,context:getComputedStyle(document.getElementById('project')).display}})()");
 assert.equal(layout.overflow,false,`${width}px has no document overflow`);
 assert.ok(layout.optionsBottom<=layout.footerTop+1,`${width}px disclosures do not overlap the footer`);
 assert.ok(layout.countsTop>=layout.footerTop-1&&layout.countsBottom<=layout.footerBottom+1,`${width}px lane summary belongs in the footer`);
 if(width===1024){assert.ok(layout.aside.left>=layout.workspace.right-1,'1024px keeps a side inspector');assert.ok(layout.stageWidth>600);}
 if(width===800){assert.ok(layout.aside.top>=layout.workspace.bottom-1,'800px stacks inspector below the scene');assert.ok(layout.stageWidth>700);assert.equal(layout.context,'none');assert.ok(layout.header<80);}
 if(width===390){assert.ok(layout.aside.top>=layout.workspace.bottom-1,'390px stacks inspector below the scene');assert.ok(layout.stageWidth>300);}
}
assert.equal(errors.length,0,JSON.stringify(errors));
console.log('PASS: desktop/tablet/phone scene and inspector layout, separated lower controls, and no document overflow');
await call('Browser.close');ws.close();
