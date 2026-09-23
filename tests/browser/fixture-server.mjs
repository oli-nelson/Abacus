// Disposable, loopback-only visual fixture. Never contacts Beads or starts workers.
import http from 'node:http';
import {readFile} from 'node:fs/promises';
const root=new URL('../../src/Abacus/Dashboard/Assets/',import.meta.url);
const day='2026-09-21',at=t=>day+'T'+t+':00Z';
const specs=[['bd-a1f','Timeline renderer','in_progress','#06d9ff'],['bd-b7c','Event ingestion','blocked','#ffc34a'],['bd-c3e','Issue inspector','closed','#b486ff']];
const issues=specs.map(([id,title,status],i)=>({
 id,title,status,revision:'fixture-'+i,description:'Render issue history as a navigable 3D timeline. Keep status snapshots and recorded comments on independent issue lanes.',
 issueType:'task',target:'main',notes:'Current notes have no claimed edit timestamp.',assignee:'Agent '+(i+1),priority:1,labels:['visualisation','frontend'],createdAt:at('09:00'),closedAt:i===2?at('11:35'):null,
 comments:[{id:'c'+i,author:i===0?'Oliver':'Agent '+(i+1),text:['Confirmed: drag to orbit, Shift-drag to pan. The camera controls can proceed.','Awaiting schema decision','Inspector wired. Integration remains unverified.'][i],createdAt:at(['11:00','11:05','10:10'][i])}],
}));
const history=new Map(issues.map((issue,i)=>[issue.id,[
 {id:issue.id+':v1',sourceRevision:'commit-fixture-1',recordedAt:at(['09:20','09:40','09:15'][i]),committer:'fixture committer',title:issue.title,status:'in_progress',notes:null},
 {id:issue.id+':v2',sourceRevision:'commit-fixture-2',recordedAt:at(['10:20','10:30','11:35'][i]),committer:'fixture committer',title:issue.title,status:i===2?'closed':'blocked',notes:i===0?'User attention requested: confirm camera interaction':null},
 ...(i===0?[{id:issue.id+':v3',sourceRevision:'commit-fixture-3',recordedAt:at('11:00'),committer:'fixture committer',title:issue.title,status:'in_progress',notes:'Attention cleared in recorded snapshot'}]:[]),
]]));
const draftRequests=[],draftReceipts=new Map();let draftRevision='a'.repeat(64),draftDropped=false;
const worktreeClients=new Set();
const clients=new Set(),requests=[],results=new Map();let revision=1,dropped=false,gitBound=false,gitContained=false,gitBranchSuffix='',referenceMode=false,worktreeMode=false,branchMode=false,worktreeReads=0,historyFailure=false,historyRevision='fixture',projectHistoryMode='off';
const branchGit=()=>({revision:'branch-fixture',stale:false,targets:['main','release'],defaultTarget:'main',facts:{branches:[{ref:'refs/heads/main',tip:'a'.repeat(40)},{ref:'refs/heads/abacus/bd-a1f',tip:'b'.repeat(40)},{ref:'refs/heads/feature/visualisation',tip:'c'.repeat(40)}],worktrees:[]}});
function publish(stale=false){revision++;for(const client of clients)client.write('event: change\ndata: '+JSON.stringify({revision,upserts:issues,removals:[],beads:{stale,error:stale?'Synthetic source read failure':null}})+'\n\n');}
const server=http.createServer(async(req,res)=>{
 res.setHeader('Date',new Date(at('12:00')).toUTCString());
 const url=new URL(req.url,'http://127.0.0.1'),path=url.pathname;
 function json(value){res.setHeader('Content-Type','application/json');res.end(JSON.stringify(value));}
 if(path==='/api/v1/project')return json({name:'abacus / visualiser fixture',actor:'fixture operator',capabilities:{editIssues:true,history:true,projectHistory:projectHistoryMode!=='off',createDrafts:true}});
 if(path==='/api/v1/snapshot')return json({revision,cursor:'fixture:'+revision,issues,beads:{stale:false},history:{revision:historyRevision,stale:false},git:branchMode?branchGit():worktreeMode?{revision:1,stale:false,targets:['main'],defaultTarget:'main',facts:{branches:[],worktrees:[{id:'f'.repeat(64),path:'/fixture/worktree',head:'a'.repeat(40),branch:'refs/heads/main',dirty:true}]}}:null});
 if(path==='/api/v1/events'){res.setHeader('Content-Type','text/event-stream');res.write(': connected\n\n');clients.add(res);res.on('close',()=>clients.delete(res));return;}
 if(path==='/api/v1/issues/drafts/context')return json({session:'fixture-session',serverUnixMilliseconds:Date.now(),revision:draftRevision,targets:['main','release'],defaultTarget:'main',requireReasoning:true,reasoningLabels:['abacus:low_reasoning','abacus:high_reasoning'],issueTypes:['task','bug','epic'],publicationAvailable:false});
 if(path==='/fixture/draft-policy'){draftRevision='b'.repeat(64);return json({ok:true});}
 if(path==='/fixture/draft-requests')return json(draftRequests);
 if(path==='/api/v1/issues/drafts'&&req.method==='POST'){
  let raw='';for await(const part of req)raw+=part;const body=JSON.parse(raw);draftRequests.push(body);
  if(draftReceipts.has(body.requestId)){res.statusCode=201;return json(draftReceipts.get(body.requestId));}
  if(body.expectedRevision!==draftRevision){res.statusCode=409;return json({outcome:'rejected',message:'Creation policy changed; review it.'});}
  const issue={...issues[0],id:'bd-created',title:body.title,description:body.description,issueType:body.type,status:'blocked',assignee:null,labels:body.labels,priority:body.priority,target:body.target,comments:[],revision:'created-draft'};
  issues.push(issue);publish();const receipt={outcome:'completed',message:'Blocked draft verified. Not published.',createdIssueId:issue.id};draftReceipts.set(body.requestId,receipt);
  if(!draftDropped){draftDropped=true;res.setHeader('Content-Type','application/json');res.end('{\"outcome\":');return;}res.statusCode=201;return json(receipt);
 }
 if(path==='/api/v1/mutations/context')return json({session:'fixture-session',serverUnixMilliseconds:Date.now(),retryMinutes:15});
 if(path==='/fixture/many'){for(let i=0;i<120;i++)issues.push({...issues[0],id:'many-'+String(i).padStart(3,'0'),title:'Generated '+i,revision:'many-'+i,status:i%2?'open':'closed',priority:i%5,comments:[],labels:[]});publish();return json({ok:true});}
 if(path==='/fixture/relations'){issues[0].dependencies=url.searchParams.has('clear')?[]:[{issueId:issues[0].id,dependsOnId:issues[1].id,type:'blocks'},{issueId:issues[0].id,dependsOnId:'missing-child',type:'parent-child'}];issues[0].revision='relations-'+revision;issues[1].dependencies=[];publish();return json({ok:true});}
 if(path==='/fixture/reopened'){
  const issue=issues[2];issue.closedAt=at('11:55');issue.revision='reopened-history';
  history.get(issue.id).push({id:issue.id+':v3',sourceRevision:'reopen',recordedAt:at('11:45'),title:issue.title,status:'in_progress'},
   {id:issue.id+':v4',sourceRevision:'reclose',recordedAt:at('11:55'),title:issue.title,status:'closed'});
  issue.comments.push({id:'inactive-gap',author:'Fixture',text:'Hidden inactive gap comment',createdAt:at('11:40')});return json({ok:true});
 }
 if(path==='/fixture/history-many'){
  for(let i=0;i<70;i++){
   const issue={...issues[0],id:'work-'+String(i).padStart(3,'0'),revision:'work-'+i,status:'closed',closedAt:at('11:30'),comments:[]};issues.push(issue);
   history.set(issue.id,[{id:issue.id+':start',sourceRevision:'fixture',recordedAt:at('09:30'),title:issue.title,status:'in_progress'},{id:issue.id+':end',sourceRevision:'fixture',recordedAt:at('11:30'),title:issue.title,status:'closed'}]);
  }return json({ok:true});
 }
 // Earlier work with late-sorting IDs and later work with early-sorting IDs. In
 // issue-ID order the later work inserts ahead of lanes already drawn and pushes
 // them off the 24-lane page as the playhead reaches it.
 if(path==='/fixture/staggered-work'){
  const add=(id,title,startAt,endAt)=>{
   issues.push({...issues[0],id,title,revision:id,status:'closed',createdAt:at('09:00'),closedAt:endAt,comments:[],labels:[]});
   history.set(id,[{id:id+':start',sourceRevision:'fixture',recordedAt:startAt,title,status:'in_progress'},
    {id:id+':end',sourceRevision:'fixture',recordedAt:endAt,title,status:'closed'}]);
  };
  const early=Number(url.searchParams.get('early')||30),late=Number(url.searchParams.get('late')||20);
  for(let i=0;i<early;i++)add('zzz-'+String(i).padStart(3,'0'),'Early work '+i,at('09:30'),at('11:50'));
  for(let i=0;i<late;i++)add('aaa-'+String(i).padStart(3,'0'),'Later work '+i,at('10:30'),at('11:50'));
  publish();return json({ok:true});
 }
 if(path==='/fixture/entry-event'){
  const issue=issues[0],rows=history.get(issue.id);rows.unshift({id:'entry-baseline',recordedAt:at('09:10'),status:'open',title:issue.title,notes:null,labels:[]});
  issue.comments.push({id:'entry-comment',author:'Ollie',text:'Comment at first entry',createdAt:at('09:20')});
  return json({ok:true});
 }
 if(path==='/fixture/closing-cluster'){
  const issue=issues[0],rows=history.get(issue.id);rows.at(-1).notes='Closing note';
  issue.comments.push({id:'closing-comment',author:'Ollie',text:'All done',createdAt:at('11:35')});return json({ok:true});
 }
 if(path==='/fixture/semantic-events'){
  issues.splice(1);const issue=issues[0];issue.status='closed';issue.closedAt=at('11:35');
  issue.comments=[{id:'semantic-comment',author:'Ollie',text:'Ready for review',createdAt:day+'T09:50:01Z'}];
  const row=(id,time,extra={})=>({id,sourceRevision:id,recordedAt:at(time),committer:'DB committer',title:'Repeated issue title',status:'in_progress',notes:'Original note',labels:['old'],...extra});
  history.set(issue.id,[row('baseline','09:20'),...Array.from({length:12},(_,i)=>row('noise-'+i,'09:'+String(30+i))),
   row('changed','09:50',{status:'blocked',labels:['review'],notes:'Updated note'}),
   row('cleared','10:20',{status:'blocked',labels:['review'],notes:''}),
   row('closed','11:35',{status:'closed',labels:['review'],notes:''})]);return json({ok:true});
 }
 if(path==='/fixture/history-priority'){
  for(const issue of issues){issue.status='closed';issue.createdAt=at('01:00');issue.closedAt=at(issue.id==='work-069'?'11:30':'03:00');}
  return json({ok:true});
 }
 if(path==='/fixture/history-failure'){historyFailure=url.searchParams.get('enabled')!=='false';return json({ok:true});}
 // Whole-project history: 'on' serves it, 'unsupported' advertises it and then
 // refuses, standing in for a source whose bd/storage cannot answer the query.
 // Set either before the page loads; capabilities are read once.
 if(path==='/fixture/project-history'){projectHistoryMode='on';return json({ok:true});}
 if(path==='/fixture/project-history-unsupported'){projectHistoryMode='unsupported';return json({ok:true});}
 if(path==='/api/v1/issues/activity'){
  if(projectHistoryMode!=='on'){res.statusCode=503;return json({error:'Whole-project history is unavailable from this fixture'});}
  return json({historyRevision,coverage:{complete:false,limitReached:false,explanation:'Fixture committed snapshots only; intervening working-set changes unknown.'},
   issues:issues.map(i=>({issueId:i.id,issueRevision:i.revision,versions:history.get(i.id)||[]}))});
 }
 if(path==='/fixture/runtime'){for(const client of clients)client.write('event: runtime\ndata: '+JSON.stringify({workers:[{name:'Fixture worker',activity:'Working',issueId:'bd-a1f',exitCode:null,runActive:true}],workerControlsAvailable:true})+'\n\n');return json({ok:true});}
 if(path==='/fixture/worktree'){worktreeMode=true;return json({ok:true});}
 if(path==='/api/v1/worktrees/events'){
  res.setHeader('Content-Type','text/event-stream');worktreeClients.add(res);worktreeReads++;
  res.write('event: worktree\ndata: '+JSON.stringify({stale:false,diff:{revision:'content-'+worktreeReads,staged:'staged snapshot',unstaged:'+edit '+worktreeReads+' <script>literal</script>',untracked:'+new file '+worktreeReads+' <img src=x>',coverage:'Synthetic worktree content.'}})+'\n\n');
  res.on('close',()=>worktreeClients.delete(res));return;
 }
 if(path==='/fixture/worktree-edit'){
  worktreeReads++;for(const client of worktreeClients)client.write('event: worktree\ndata: '+JSON.stringify(url.searchParams.has('stale')?{stale:true,error:'Synthetic source unavailable',diff:null}:{stale:false,diff:{revision:'content-'+worktreeReads,staged:'staged snapshot',unstaged:'+edit '+worktreeReads,untracked:'+new file '+worktreeReads+' <img src=x>',coverage:'Synthetic worktree content.'}})+'\n\n');return json({clients:worktreeClients.size});
 }
 if(path==='/fixture/worktree-clients')return json({count:worktreeClients.size});
 if(path==='/api/v1/worktrees/diff')return json({revision:'content-'+(++worktreeReads),staged:'staged snapshot',unstaged:'+edit '+worktreeReads+' <script>literal</script>',untracked:'+new file '+worktreeReads+' <img src=x>',coverage:'Synthetic bounded staged, unstaged and untracked snapshot.'});
 if(path==='/fixture/reference'){gitBound=true;referenceMode=true;return json({ok:true});}
 if(path==='/fixture/branches'){branchMode=true;for(const client of clients)client.write('event: git\ndata: '+JSON.stringify(branchGit())+'\n\n');return json({ok:true});}
 if(path==='/api/v1/branches/compare')return json({comparison:{targetTip:'a'.repeat(40),issueTip:'b'.repeat(40),mergeBase:'a'.repeat(40),containedInTarget:false,basis:'target...issue',ahead:2,behind:0,files:[{path:'src/renderer.js',additions:42,deletions:11,binary:false},{path:'docs/guide.md',additions:5,deletions:0,binary:false}],warning:null},patch:url.searchParams.get('patch')==='true'?{available:true,text:'diff --git a/src/renderer.js b/src/renderer.js\n+const scene = true;',warning:null}:null});
 if(path==='/fixture/git-refresh'){
  gitContained=url.searchParams.get('contained')==='true';
  if(url.searchParams.has('branch'))gitBranchSuffix=url.searchParams.get('branch');
  for(const client of clients)client.write('event: git\ndata: '+JSON.stringify({revision:'git-'+(++revision),stale:false,branches:[]})+'\n\n');
  return json({ok:true});
 }
 if(path==='/fixture/integrated'){gitContained=true;return json({ok:true});}
 if(path==='/fixture/git'){gitBound=true;return json({ok:true});}
 if(path==='/fixture/requests')return json(requests);
 if(path==='/fixture/stream-clients')return json({count:clients.size});
 if(path==='/fixture/scale'){
  issues.splice(0,issues.length,...Array.from({length:1000},(_,i)=>({
   id:'scale-'+String(i).padStart(4,'0'),title:'Scale issue '+i,status:['open','in_progress','blocked','closed'][i%4],
   revision:'scale-'+i,description:'Deterministic large-scene fixture',notes:null,assignee:null,priority:i%5,
   issueType:'task',target:'main',labels:['scale'],dependencies:[],createdAt:null,closedAt:null,
   comments:Array.from({length:10},(_,j)=>({id:'event-'+i+'-'+j,author:'fixture',text:'Recorded scale event '+j,
    createdAt:new Date(Date.parse(at('09:00'))+(j*1000+i)*1000).toISOString()}))
  })));history.clear();publish();return json({issues:1000,recordedEvents:10000});
 }
 if(path==='/fixture/live-close'){
  const issue=issues[0];issue.status='closed';issue.closedAt=url.searchParams.has('undated')?null:at('11:50');issue.revision='live-close';publish();return json({ok:true});
 }
 if(path==='/fixture/live-reopen'){
  const issue=issues[0];issue.status='in_progress';issue.closedAt=null;issue.revision='live-reopen';
  history.get(issue.id).push({id:issue.id+':closed-live',sourceRevision:'closed-live',recordedAt:at('11:50'),title:issue.title,status:'closed'});
  if(url.searchParams.has('recorded'))history.get(issue.id).push({id:issue.id+':reopened-live',sourceRevision:'reopened-live',recordedAt:at('11:55'),title:issue.title,status:'in_progress'});
  publish();if(url.searchParams.has('recorded')){historyRevision='fixture-reopened';for(const client of clients)client.write('event: history\ndata: '+JSON.stringify({revision:historyRevision,stale:false})+'\n\n');}return json({ok:true});
 }
 if(path==='/fixture/status-transition'){
  issues[0].status=url.searchParams.get('status')||'blocked';issues[0].revision='status-'+revision;publish();return json({ok:true});
 }
 if(path==='/fixture/lane-arrival'){
  const id=url.searchParams.get('id')||'new-lane';
  if(!issues.some(i=>i.id===id))issues.push({...issues[0],id,title:'New live lane '+id,revision:id,status:'in_progress',createdAt:at('11:45'),closedAt:null,comments:[]});
  publish();return json({ok:true});
 }
 if(path==='/fixture/lane-burst'){
  for(let i=0;i<1000;i++)issues.push({...issues[0],id:'new-burst-'+i,title:'New burst lane '+i,revision:'new-burst-'+i,status:'open',createdAt:at('11:45'),closedAt:null,comments:[]});
  publish();return json({count:1000});
 }
 if(path==='/fixture/arrival-burst'){
  for(const issue of issues){issue.comments.push({id:'burst',author:'Fixture author',text:'Live burst event',createdAt:at('11:59')});issue.revision='burst-'+issue.id+'-'+revision;}
  publish();return json({count:issues.length});
 }
 if(path==='/fixture/reconnect-arrival'){
  if(url.searchParams.has('lane'))issues.push({...issues[0],id:'missed-lane',title:'Lane received during disconnect',revision:'missed-lane',comments:[]});
  issues[0].comments.push({id:'missed',author:'Fixture author',text:'Recorded while disconnected',createdAt:at('11:20')});
  issues[0].revision='missed-'+revision;revision++;
  for(const client of clients)client.end();return json({ok:true});
 }
 if(path==='/fixture/comment-arrival'){
  const id=url.searchParams.get('id')||'arrival';
  if(!issues[0].comments.some(c=>c.id===id))issues[0].comments.push({id,author:'Fixture author',text:'New live recorded event '+id,createdAt:at(url.searchParams.get('time')||'11:30')});
  issues[0].revision='arrival-'+revision;publish(url.searchParams.has('stale'));return json({ok:true});
 }
 if(path==='/fixture/cluster-many'){
  issues[0].comments=Array.from({length:Math.max(2,Math.min(121,Number(url.searchParams.get('count'))||121))},(_,i)=>({id:'dense-'+i,author:'Author '+i,text:'Dense member '+i,createdAt:at('11:00')}));
  issues[0].revision='dense-cluster-'+revision;publish();return json({ok:true});
 }
 if(path==='/fixture/comment-refresh'){
  if(url.searchParams.has('remove'))issues[0].comments=[];
  else issues[0].comments[0].text='Updated recorded comment text';
  issues[0].revision='comment-refresh-'+revision;publish();return json({ok:true});
 }
 if(path==='/fixture/race'){issues[0].title='Externally updated title';issues[0].revision='fixture-raced';publish();return json({ok:true});}
 const write=issues.find(i=>path==='/api/v1/issues/'+i.id+'/actions');
 if(req.method==='POST'&&write){
  let body='';for await(const part of req)body+=part;const action=JSON.parse(body);requests.push(action);
  if(results.has(action.requestId))return json(results.get(action.requestId));
  if(action.expectedRevision!==write.revision){res.statusCode=409;return json({outcome:'rejected',message:'Review changed issue',issue:write,revision:write.revision});}
  for(const key of ['title','description','priority'])if(key in action)write[key]=action[key];
  write.labels=[...new Set([...write.labels,...(action.addLabels||[])])].filter(l=>!(action.removeLabels||[]).includes(l));
  if(action.action==='reasoning-set'){
   write.labels=write.labels.filter(l=>!['abacus:high_reasoning','abacus:medium_reasoning','abacus:low_reasoning'].includes(l));
   if(action.reasoningLevel!=='none')write.labels.push('abacus:'+action.reasoningLevel+'_reasoning');
  }
  if(action.action.startsWith('attention-request'))write.labels=[...new Set([...write.labels,'abacus:needs-user-attention'])];
  if(action.action.startsWith('attention-resolve'))write.labels=write.labels.filter(l=>l!=='abacus:needs-user-attention');
  if(action.action==='status')write.status=action.status;
  if(action.action==='attention-request-block')write.status='blocked';
  if(action.action==='attention-resolve-reopen'){write.status='open';write.assignee=null;}
  let recordedCommentId=null;
  if((action.action==='comment'||action.action.startsWith('attention-'))&&action.text){recordedCommentId='fixture-comment-'+requests.length;write.comments.push({id:recordedCommentId,text:action.text,author:'fixture operator',createdAt:at('12:00')});}
  if(action.appendNotes)write.notes+='\n'+action.appendNotes;
  write.revision='fixture-written-'+revision;publish();
  const result={outcome:'completed',message:'Verified fixture edit',issue:write,revision:write.revision,recordedCommentId};results.set(action.requestId,result);
  if(!dropped){dropped=true;res.setHeader('Content-Type','application/json');res.end('{"outcome":');return;}
  return json(result);
 }
 const gitIssue=issues.find(i=>path==='/api/v1/issues/'+i.id+'/git');if(gitIssue){
  const comparison={targetTip:'a'.repeat(40),issueTip:'b'.repeat(40),containedInTarget:referenceMode?gitIssue.id==='bd-c3e':gitContained,basis:'target...issue',ahead:1,behind:0,files:[{path:'fixture.txt',additions:1,deletions:0,binary:false}],warning:null};
  return json({issueId:gitIssue.id,issueRevision:gitIssue.revision,state:gitBound?'validated':'unbound',explanation:gitBound?'Synthetic binding evidence for browser checks.':'No recorded execution binding; no issue branch is inferred.',effectiveTarget:'main',binding:gitBound?{targetRef:'refs/heads/main',issueBranch:'abacus/'+gitIssue.id+gitBranchSuffix,startCommit:'a'.repeat(40)}:null,comparison:gitBound?comparison:null,worktrees:gitBound?[{path:'/fixture/attached-checkout',dirty:true,statusError:null},{path:'/fixture/unavailable-checkout',dirty:null,statusError:'Status unavailable'}]:[],
   patch:gitBound&&url.searchParams.get('patch')==='true'?{available:true,text:'diff --git a/fixture.txt b/fixture.txt\n+<img src=x onerror=alert(1)>',warning:null}:null,
   history:gitBound&&url.searchParams.get('history')==='true'?{tip:'b'.repeat(40),coverage:'Synthetic recorded history.',limitReached:true,clockSkew:true,commits:[{id:'a'.repeat(40),parents:[],author:'Fixture base author',committedAt:at('09:00'),message:'Recorded base commit'},{id:'b'.repeat(40),parents:['a'.repeat(40)],author:'Fixture author',committedAt:at('11:00'),message:'<script>not executable</script>'}]}:null});
 }
 const current=issues.find(i=>path==='/api/v1/issues/'+i.id);if(current)return json(current);
 const match=path.match(/^\/api\/v1\/issues\/([^/]+)\/activity$/);
 if(match&&historyFailure){res.statusCode=503;return json({error:'Fixture unavailable'});}
 if(match&&history.has(match[1]))return json({issueId:match[1],issueRevision:issues.find(i=>i.id===match[1]).revision,historyRevision,versions:history.get(match[1]),coverage:{complete:false,limitReached:false,explanation:'Fixture committed snapshots only; intervening working-set changes unknown.'},continuation:null});
 const asset=path==='/'?'index.html':path.slice(1);
 if(!['index.html','dashboard.css','dashboard.js','timeline.js','inspector-resize.js','timeline-model.js','timeline-gl.js','dependency-tree.js','issue-relations.js','issue-table.js','issue-filters.js'].includes(asset)){res.statusCode=404;res.end();return;}
 res.setHeader('Content-Type',asset.endsWith('.js')?'text/javascript':asset.endsWith('.css')?'text/css':'text/html');
 try{res.end(await readFile(new URL(asset,root)));}catch{res.statusCode=500;res.end();}
});
server.listen(Number(process.env.PORT||18081),'127.0.0.1',()=>console.log('Timeline fixture http://127.0.0.1:'+server.address().port));
