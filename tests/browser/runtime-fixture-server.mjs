// Disposable UI contract fixture: no CLI tools, worker processes or repository writes.
import http from 'node:http';
import {readFile} from 'node:fs/promises';
const root=new URL('../../src/Abacus/Dashboard/Assets/',import.meta.url);
const session='123456781234123412341234567890ab',clients=new Set(),requests=[];
const runtime={revision:1,workers:[{name:'maintenance',supervisor:true,activity:'Idle',issueId:null,branch:null,dirty:null,retryCount:0,exitCode:null,runActive:false}],claims:{manualEnabled:true,scheduleAllows:true,gateAllows:true,reason:'Fixture'},workerControlsAvailable:true,supervisorControlsAvailable:true,stopRunAvailable:true,workerActions:[],supervisorActions:[]};
let dropped=false;
function publish(){runtime.revision++;for(const res of clients)res.write('event: runtime\ndata: '+JSON.stringify(runtime)+'\n\n');}
http.createServer(async(req,res)=>{
 const path=new URL(req.url,'http://127.0.0.1').pathname;
 const json=value=>{res.setHeader('Content-Type','application/json');res.end(JSON.stringify(value));};
 if(path==='/api/v1/project')return json({name:'Runtime UI fixture',actor:'fixture operator',runtimeSession:session,capabilities:{claimControl:true}});
 if(path==='/api/v1/snapshot')return json({revision:1,cursor:'fixture:1',issues:[],beads:{stale:false},history:null,git:null,runtime});
 if(path==='/api/v1/events'){res.setHeader('Content-Type','text/event-stream');res.write(': connected\n\n');clients.add(res);res.on('close',()=>clients.delete(res));return;}
 if(path==='/fixture/requests')return json(requests);
 if(path==='/fixture/complete'){for(const a of runtime.supervisorActions)if(a.command==='force-run')a.outcome='completed';publish();return json({ok:true});}
 if(path==='/fixture/disconnect'){for(const client of clients){client.write('event: disconnected\ndata: {}\n\n');client.end();}clients.clear();return json({ok:true});}
 if(req.method==='POST'&&path==='/api/v1/runtime/supervisors/actions'){
  let body='';for await(const part of req)body+=part;
  const request=JSON.parse(body);requests.push(request);
  let action=runtime.supervisorActions.find(a=>a.requestId===request.requestId);
  if(!action){action={requestId:request.requestId,worker:request.worker,command:request.command,outcome:request.command==='force-run'?'accepted':'completed'};runtime.supervisorActions.push(action);}
  publish();
  if(request.command==='force-run'&&!dropped){dropped=true;res.statusCode=202;res.setHeader('Content-Type','application/json');res.end('{\"outcome\":');return;}
  // Deliberately deliver stale accepted HTTP after completed SSE for Stop.
  await new Promise(r=>setTimeout(r,100));res.statusCode=202;return json({outcome:'accepted',message:'Fixture accepted',requestId:request.requestId});
 }
 const asset=path==='/'?'index.html':path.slice(1);
 if(!['index.html','dashboard.css','dashboard.js','timeline.js','inspector-resize.js','timeline-model.js','timeline-gl.js','dependency-tree.js','issue-relations.js','issue-table.js','issue-filters.js'].includes(asset)){res.statusCode=404;return res.end();}
 res.setHeader('Content-Type',asset.endsWith('.js')?'text/javascript':asset.endsWith('.css')?'text/css':'text/html');
 res.end(await readFile(new URL(asset,root)));
}).listen(18082,'127.0.0.1',()=>console.log('Runtime UI fixture http://127.0.0.1:18082'));
