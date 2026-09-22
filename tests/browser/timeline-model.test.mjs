import {test} from 'node:test';
import assert from 'node:assert/strict';
import {brushTimeRange,issueWorkEpisodes,episodePosition,workEpisodes,eventArrivalStart,eventBubblePlacement,authorInitials,savedTimelineCamera,timelineLocation,eventsFor,stateAt,clusterEvents,TimelineProjection,nonOverlappingLabels,gitLaneSummary,currentGitTopology,smoothConnection,recordedStartEvent,simplifyStraightSegments,branchCurvePosition} from '../../src/Abacus/Dashboard/Assets/timeline-model.js';
import {TimelineRenderer,cameraBasis,projectPoint} from '../../src/Abacus/Dashboard/Assets/timeline-gl.js';
const issue={id:'a',revision:'one',title:'Current title',status:'in_progress',createdAt:'2026-09-21T09:00:00Z',comments:[]};
test('current content never invents dated notes, claims or historical states',()=>{
  const events=eventsFor({...issue,notes:'undated',assignee:'Agent 1',comments:[{id:'x',text:'undated'}]});
  assert.equal(events.length,1);assert.equal(events[0].kind,'created');
  assert.equal(stateAt(issue,events,Date.parse('2026-09-21T11:00:00Z')).status,'unknown');
  assert.equal(stateAt(issue,events,0,true).status,'in_progress');
});
test('recorded snapshots preserve provenance; equal-time conflicts remain unknown',()=>{
  const versions=[{id:'v1',recordedAt:'2026-09-21T10:00:00Z',status:'blocked',title:'Then',notes:'snapshot note',committer:'root',sourceRevision:'hash'}];
  const events=eventsFor(issue,versions);
  const state=stateAt(issue,events,Date.parse('2026-09-21T11:00:00Z'));
  assert.equal(state.status,'blocked');assert.equal(state.title,'Then');assert.match(state.certainty,/intervening changes unknown/);
  assert.equal(events[1].author,'root');assert.equal(events[1].kind,'snapshot');
  const conflict=eventsFor(issue,[...versions,{...versions[0],id:'v2',status:'open'}]);
  assert.equal(stateAt(issue,conflict,Date.parse('2026-09-21T11:00:00Z')).status,'unknown');
});
test('closed field is not a merge and reopened issues retain committed closure snapshots',()=>{
  const closed={...issue,status:'closed',closedAt:'2026-09-21T11:00:00Z'};
  assert.match(eventsFor(closed).at(-1).text,/integration unverified/);
  assert.equal(eventsFor({...closed,status:'open'}).length,1);
});
test('lane identity and unchanged projection survive updates and removals',()=>{
  const p=new TimelineProjection();const first=p.update([issue,{...issue,id:'b'}]);
  p.update([issue,{...issue,id:'b'}]);assert.equal(p.rebuilds,2);assert.equal(p.cache.get('a'),first[0]);
  p.update([{...issue,id:'b'}, {...issue,id:'new'}]);assert.equal(p.cache.get('b').slot,1);assert.equal(p.cache.get('new').slot,2);
  p.update([issue,{...issue,id:'b'}]);assert.equal(p.cache.get('a').slot,0);
});
test('dense clustering stays lane-local, bounded and lossless',()=>{
  const events=Array.from({length:10000},(_,i)=>({id:String(i),issueId:i%2?'a':'b',time:i,text:'comment'}));
  const clusters=clusterEvents(events,0,10000,80);
  assert.equal(clusters.length,160);assert.equal(clusters.reduce((n,c)=>n+(c.members?.length||1),0),10000);
  assert.ok(clusters.every(c=>c.members.every(e=>e.issueId===c.issueId)));
});
test('camera is genuinely perspective and 2D projection removes depth scaling',()=>{
  const camera={yaw:0,pitch:0,distance:30,target:[0,0,0],perspective:1};
  const near=projectPoint([4,0,5],camera,1000,600),far=projectPoint([4,0,-5],camera,1000,600);
  assert.ok(near.x>far.x);
  const a=projectPoint([4,0,5],{...camera,perspective:0},1000,600),b=projectPoint([4,0,-5],{...camera,perspective:0},1000,600);
  assert.equal(a.x,b.x);
  assert.notDeepEqual(cameraBasis({...camera,yaw:.5}).eye,cameraBasis(camera).eye);
});

test('label LOD hides overlaps without mutating candidates and prioritizes selection/focus',()=>{
  const card=(id,y,priority=1)=>({id,x:10,y,width:220,height:68,priority});
  const input=[card('ordinary',0),card('selected',15,2),card('separate',100)];
  assert.deepEqual(nonOverlappingLabels(input).map(c=>c.id),['selected','separate']);
  assert.deepEqual(input.map(c=>c.id),['ordinary','selected','separate']);
  assert.deepEqual(nonOverlappingLabels([...input,card('focused',20,3)]).map(c=>c.id),['focused','separate']);
  assert.deepEqual(nonOverlappingLabels([card('first',0),card('second',0)]).map(c=>c.id),['first']);
  assert.equal(nonOverlappingLabels([card('first',0),card('touching',68)]).length,1);
  assert.equal(nonOverlappingLabels([card('first',0),card('spaced',74)]).length,2);
  assert.equal(nonOverlappingLabels([{...card('bad',0),width:NaN}]).length,0);
});

test('lane Git summaries require matching live validated evidence and never invent merge times',()=>{
  const evidence={issueId:issue.id,issueRevision:issue.revision,state:'validated',binding:{issueBranch:'abacus/a'},comparison:{files:[{additions:3,deletions:2},{binary:true}],basis:'target...issue',targetTip:'abc',issueTip:'def',containedInTarget:true}};
  const summary=gitLaneSummary(issue,evidence);
  assert.equal(summary.branch,'abacus/a');assert.equal(summary.changes,'2 files · +3 / −2 · includes binary');
  assert.match(summary.integration,/merge time unknown/);assert.match(summary.basis,/target\.\.\.issue/);
  assert.equal(gitLaneSummary(issue,evidence,false),null);
  assert.equal(gitLaneSummary({...issue,revision:'changed'},evidence),null);
  assert.equal(gitLaneSummary({...issue,id:'other'},evidence),null);
  assert.equal(gitLaneSummary(issue,{...evidence,state:'unbound'}),null);
  assert.equal(gitLaneSummary(issue,{...evidence,comparison:null}),null);
  assert.equal(gitLaneSummary(issue,{...evidence,comparison:{...evidence.comparison,containedInTarget:false}}).integration,'Not proven integrated');
});

test('loaded Git history projects dated commits without inferring Beads state or authorship',()=>{
  const commit={id:'sha',committedAt:'2026-09-21T10:00:00Z',author:'Recorded author',message:'Commit message',parents:['parent']};
  const git=new Map([[issue.id,{issueRevision:issue.revision,key:'tip-one',commits:[commit,commit,{...commit,id:'undated',committedAt:null}]}]]);
  const projection=new TimelineProjection();
  const lane=projection.update([issue],new Map(),git)[0];
  const commits=lane.events.filter(e=>e.kind==='git');assert.equal(commits.length,1);
  assert.equal(commits[0].author,'Recorded author');assert.deepEqual(commits[0].parents,['parent']);
  assert.match(commits[0].provenance,/shared ancestor/);
  assert.equal(stateAt(issue,lane.events,Date.parse('2026-09-21T11:00:00Z')).status,'unknown');
  const rebuilds=projection.rebuilds;projection.update([issue],new Map(),git);assert.equal(projection.rebuilds,rebuilds);
  assert.equal(projection.update([{...issue,revision:'changed'}],new Map(),git)[0].events.filter(e=>e.kind==='git').length,0);
  assert.equal(projection.update([issue])[0].events.filter(e=>e.kind==='git').length,0);
});

test('current topology requires validated evidence and never backfills playback or closure',()=>{
  const closed={...issue,status:'closed'};
  assert.equal(currentGitTopology(closed,null),null);
  const evidence={issueId:issue.id,issueRevision:issue.revision,state:'validated',binding:{targetRef:'refs/heads/main',startCommit:'start'},comparison:{files:[],containedInTarget:false}};
  assert.equal(currentGitTopology(closed,evidence).contained,false);
  assert.equal(currentGitTopology(closed,evidence).issueBranch,evidence.binding.issueBranch);
  evidence.comparison.containedInTarget=true;
  assert.equal(currentGitTopology(closed,evidence).contained,true);
  assert.equal(currentGitTopology(closed,evidence,false),null);
  assert.equal(currentGitTopology({...closed,revision:'new'},evidence),null);
  const points=smoothConnection([0,1,2],[4,5,6]);
  assert.deepEqual(points[0],[0,1,2]);assert.deepEqual(points.at(-1),[4,5,6]);
  assert.ok(points.every(p=>p.every(Number.isFinite)));
});

test('recorded start connectors require the exact dated commit and matching inspected tip',()=>{
  const evidence={issueId:issue.id,issueRevision:issue.revision,state:'validated',binding:{targetRef:'refs/heads/main',startCommit:'start'},comparison:{files:[],issueTip:'tip'}};
  const history={issueRevision:issue.revision,tip:'tip',commits:[{id:'start',committedAt:'2026-09-21T09:00:00Z',parents:[],message:'Base'}]};
  assert.match(recordedStartEvent(issue,evidence,history).provenance,/not the branch creation or claim time/);
  assert.equal(recordedStartEvent(issue,evidence,history,false),null);
  assert.equal(recordedStartEvent(issue,evidence,{...history,tip:'changed'}),null);
  assert.equal(recordedStartEvent(issue,evidence,{...history,commits:[]}),null);
  assert.equal(recordedStartEvent(issue,evidence,{...history,commits:[{id:'start',committedAt:null}]}),null);
});

test('glow geometry simplification keeps curves and reversals, drops straight interiors',()=>{
  assert.deepEqual(simplifyStraightSegments([[0,0,0],[1,0,0],[2,0,0]]),[[0,0,0],[2,0,0]]);
  const turn=[[0,0,0],[1,0,0],[1,1,0]];assert.deepEqual(simplifyStraightSegments(turn),turn);
  const reverse=[[0,0,0],[1,0,0],[0,0,0]];assert.deepEqual(simplifyStraightSegments(reverse),reverse);
  const curve=smoothConnection([0,0,0],[4,6,0]);assert.ok(simplifyStraightSegments(curve).length>20);
});

test('continuous issue curves leave and return to the target only with evidence',()=>{
  const topology={startX:-10,targetY:0,contained:true};
  assert.deepEqual(branchCurvePosition(-10,4,1,topology),[-10,0,0]);
  assert.deepEqual(branchCurvePosition(0,4,1,topology),[0,4,1]);
  assert.deepEqual(branchCurvePosition(12,4,1,topology),[12,0,0]);
  assert.deepEqual(branchCurvePosition(12,4,1,{...topology,contained:false}),[12,4,1]);
  assert.deepEqual(branchCurvePosition(-10,4,1,{...topology,startX:null}),[-10,4,1]);
  assert.deepEqual(branchCurvePosition(0,4,1,null),[0,4,1]);
});

test('timeline URLs restore bounded playback and distinguish live from fixed ranges',()=>{
  const now=Date.parse('2026-09-21T12:00:00Z');
  const url=new URL('http://localhost/?from=2026-09-21T09:00:00Z&to=2026-09-21T11:00:00Z&at=2026-09-21T14:00:00Z&event=comment:a:c');
  const state=timelineLocation(url,now);
  assert.equal(state.live,false);assert.equal(state.playhead,state.to);
  assert.equal(state.pendingEvent,'comment:a:c');
  url.searchParams.delete('at');url.searchParams.delete('to');
  assert.equal(timelineLocation(url,now).live,true);
  url.searchParams.set('from','2026-09-22T09:00:00Z');
  assert.equal(timelineLocation(url,now).from,now-3600000);
  url.searchParams.set('from','not a date');url.searchParams.set('at','not a date');
  const invalid=timelineLocation(url,now);
  assert.ok(Number.isFinite(invalid.from));assert.equal(invalid.playhead,now);
  assert.equal(invalid.live,false,'Explicit malformed playback must not silently enable editing');
});

test('saved cameras reject malformed, nonfinite, out-of-range and mismatched projection data',()=>{
  const camera={yaw:.3,pitch:.4,distance:20,perspective:1,target:[-2,-3,0]};
  assert.deepEqual(savedTimelineCamera(JSON.stringify(camera),'3d'),camera);
  for(const value of ['invalid','null','{}',JSON.stringify({...camera,target:[0,0]}),JSON.stringify({...camera,distance:0}),JSON.stringify({...camera,yaw:8}),JSON.stringify({...camera,target:[0,'1',0]})])assert.equal(savedTimelineCamera(value,'3d'),null);
  assert.equal(savedTimelineCamera(JSON.stringify(camera),'2d'),null);
});

test('author badges use recorded initials only and preserve Unicode characters',()=>{
  assert.equal(authorInitials(' Oliver Nelson '),'ON');assert.equal(authorInitials('Oliver'),'O');
  assert.equal(authorInitials('李 明'),'李明');assert.equal(authorInitials(''),'?');assert.equal(authorInitials(null),'?');
});

test('status colors interpolate from the supplied visual color and cancel to authoritative color',()=>{
  const renderer=Object.create(TimelineRenderer.prototype);
  const marker={color:'#ff0000',colorTransition:{start:0,from:[0,0,1]}};
  renderer.colorMix=()=>.5;
  assert.deepEqual(renderer.markerColor(marker),[.5,0,.5]);
  renderer.scene={markers:[marker]};renderer.arrivalRanges=[{marker}];
  renderer.finishArrivals();
  renderer.colorMix=TimelineRenderer.prototype.colorMix;
  assert.deepEqual(renderer.markerColor(marker),[1,0,0]);
  assert.equal(renderer.activeArrivals(),false);assert.deepEqual(renderer.arrivalRanges,[]);
});

test('curved tubes share smooth tangent rings and spherical beads use radial normals',()=>{
  const renderer=Object.create(TimelineRenderer.prototype);let data;
  renderer.gl={bindBuffer(){},bufferData(_target,vertices){data=vertices;}};renderer.uploads=0;
  const path={points:[[0,0,0],[1,1,0],[2,1,0]],color:'#00ccff'};
  renderer.setScene({paths:[path],markers:[{pos:[4,0,0],color:'#ffffff',shape:'sphere'}],floor:-3,ceiling:3,nowX:20});
  const vertex=i=>Array.from(data.slice(i*11,i*11+6));
  for(let j=0;j<8;j++){
    assert.deepEqual(vertex(j*6+1),vertex(48+j*6),'Adjacent tube rings have identical positions and normals');
    assert.ok(Math.abs(Math.hypot(...vertex(j*6).slice(3))-1)<1e-6);
  }
  for(let i=96;i<renderer.counts[0];i++){
    const v=vertex(i),radial=[v[0]-4,v[1],v[2]],length=Math.hypot(...radial);
    for(let k=0;k<3;k++)assert.ok(Math.abs(radial[k]/length-v[k+3])<2e-6);
  }
  assert.equal(renderer.uploads,1);assert.equal(renderer.counts[0],96+6*8*6,'Smoothing does not add geometry');
});

test('speech bubbles stay bounded and point above or below the actual event',()=>{
  const above=eventBubblePlacement({x:600,y:400},310,150,1000,600);
  assert.equal(above.y,232);assert.ok(above.path.includes('L 600 400'));
  const below=eventBubblePlacement({x:30,y:20},310,150,1000,600);
  assert.equal(below.x,8);assert.equal(below.y,38);assert.ok(below.path.includes('L 30 20'));
  assert.equal(eventBubblePlacement({x:-1,y:50},310,150,1000,600),null);
  assert.equal(eventBubblePlacement({x:200,y:100},310,250,400,300),null);
  assert.equal(eventBubblePlacement(null,310,150,1000,600),null);
});

test('lane arrival retains matching solid and glow ranges and cancels both',()=>{
  const renderer=Object.create(TimelineRenderer.prototype);
  renderer.gl={bindBuffer(){},bufferData(){}};renderer.uploads=0;
  const path={points:[[0,0,0],[1,0,0]],color:'#00ccff',arrival:performance.now()};
  renderer.setScene({paths:[path],markers:[],floor:-3,ceiling:3,nowX:20});
  assert.equal(renderer.arrivalRanges.length,1);assert.equal(renderer.glowArrivalRanges.length,1);
  assert.equal(renderer.arrivalRanges[0].count,48);assert.equal(renderer.glowArrivalRanges[0].count,96);
  assert.equal(renderer.activeArrivals(),true);
  renderer.finishArrivals();assert.equal(renderer.activeArrivals(),false);
  assert.equal(renderer.arrivalOpacity(path),1);assert.deepEqual(renderer.glowArrivalRanges,[]);
});

test('clusters inherit only genuinely new member arrival times',()=>{
  const event={id:'cluster:old',members:[{id:'old'},{id:'new'},{id:'newer'}]};
  assert.equal(eventArrivalStart(event,new Map()),undefined);
  assert.equal(eventArrivalStart(event,new Map([['new',10],['newer',20]])),20);
  assert.equal(eventArrivalStart({id:'single'},new Map([['single',5]])),5);
  const renderer=Object.create(TimelineRenderer.prototype);
  assert.equal(renderer.arrivalOpacity({arrival:performance.now()+1000,arrivalFloor:.55}),.55,'Existing cluster stays visible');
});

test('recorded-event guides are bounded dashed timed lines, not event geometry',()=>{
  const renderer=Object.create(TimelineRenderer.prototype);let data;
  renderer.gl={bindBuffer(){},bufferData(_target,vertices){data=vertices;}};renderer.uploads=0;
  const scene={paths:[],markers:[],floor:-8,ceiling:2,nowX:20};
  renderer.setScene(scene);const base=renderer.counts[1];
  renderer.setScene({...scene,guides:[{pos:[3,1,.6],color:'#00ccff'}]});
  assert.equal(renderer.counts[0],0,'Guides must not create event beads');
  assert.equal(renderer.counts[1]-base,24,'Twelve bounded dash segments');
  const vertices=Array.from({length:24},(_,i)=>Array.from(data.slice((base+i)*11,(base+i+1)*11)));
  for(const v of vertices){assert.equal(v[0],3);assert.ok(v[1]>=-8&&v[1]<=1);assert.equal(v[10],1,'Playback clips guide at actual event time');}
});

test('fallback guides respect playback and restore drawing state',()=>{
  const strokes=[];let current=[],dash=[];
  const ctx={setTransform(){},clearRect(){},fillRect(){},setLineDash(value){dash=value;},beginPath(){current=[];},moveTo(x,y){current.push([x,y]);},lineTo(x,y){current.push([x,y]);},stroke(){strokes.push({points:current,dash:[...dash],alpha:this.globalAlpha,color:this.strokeStyle});}};
  const renderer=Object.create(TimelineRenderer.prototype);
  renderer.fallback={getContext:()=>ctx};
  renderer.scene={floor:-8,ceiling:2,nowX:12,paths:[],markers:[],guides:[{pos:[-2,1,.6],color:'#00ccff'},{pos:[4,0,0],color:'#ff0000'}]};
  const camera={yaw:0,pitch:0,distance:30,perspective:0,target:[0,-3,0]};
  renderer.drawFallback(camera,0,1000,600,1);
  const guides=strokes.filter(s=>s.dash.length);
  assert.equal(guides.length,1,'Future event guide is not drawn');
  assert.equal(guides[0].color,'#00ccff');assert.equal(guides[0].alpha,.28);
  assert.deepEqual(guides[0].dash,[3,5]);
  const top=projectPoint([-2,1,.6],camera,1000,600),bottom=projectPoint([-2,-8,.6],camera,1000,600);
  assert.deepEqual(guides[0].points,[[top.x,top.y],[bottom.x,bottom.y]]);
  assert.deepEqual(strokes.at(-1).dash,[]);assert.equal(ctx.globalAlpha,1);
});

test('fallback branch playback clips at the exact time plane between samples',()=>{
  const strokes=[];let points=[];
  const ctx={setTransform(){},clearRect(){},fillRect(){},setLineDash(){},beginPath(){points=[];},moveTo(x,y){points.push([x,y]);},lineTo(x,y){points.push([x,y]);},stroke(){if(this.strokeStyle==='#abcdef')strokes.push(points);}};
  const renderer=Object.create(TimelineRenderer.prototype);renderer.fallback={getContext:()=>ctx};
  renderer.scene={floor:-8,ceiling:2,nowX:12,markers:[],paths:[{points:[[-4,0,0],[4,4,0]],color:'#abcdef'}]};
  const camera={yaw:0,pitch:0,distance:30,perspective:0,target:[0,0,0]};
  renderer.drawFallback(camera,0,1000,600,1);
  const start=projectPoint([-4,0,0],camera,1000,600),end=projectPoint([0,2,0],camera,1000,600);
  assert.deepEqual(strokes[0],[[start.x,start.y],[end.x,end.y]]);
  renderer.drawFallback(camera,-5,1000,600,1);assert.deepEqual(strokes[1],[],'Entirely future path is absent');
});

const statusEvent=(time,status,id=String(time))=>({id,time,status,kind:'snapshot'});
test('work episodes exclude backlog and blocked-only issues and end at closure',()=>{
  assert.deepEqual(workEpisodes([statusEvent(1,'open'),statusEvent(2,'blocked'),statusEvent(3,'closed')]),[]);
  const episodes=workEpisodes([statusEvent(1,'open'),statusEvent(2,'in_progress'),statusEvent(3,'blocked'),statusEvent(4,'closed'),{id:'later-comment',time:8,kind:'comment'}]);
  assert.equal(episodes.length,1);assert.equal(episodes[0].start,2);assert.equal(episodes[0].end,4);assert.equal(episodes[0].endStatus,'closed');
  assert.deepEqual(episodes[0].transitions.map(t=>t.status),['in_progress','blocked']);
});
test('reopened work creates a new episode without bridging the inactive interval',()=>{
  const episodes=workEpisodes([statusEvent(1,'in_progress'),statusEvent(2,'closed'),statusEvent(3,'open'),statusEvent(4,'in_progress'),statusEvent(5,'in_progress'),statusEvent(6,'blocked')]);
  assert.deepEqual(episodes.map(e=>[e.start,e.end]),[[1,2],[4,null]]);
  assert.equal(episodes[1].transitions.length,2);
  assert.equal(workEpisodes([statusEvent(1,'in_progress'),statusEvent(3,'closed')],2)[0].end,null,'Playback cannot see future closure');
});
test('conflicting equal-time states end known work rather than invent a closure',()=>{
  const episodes=workEpisodes([statusEvent(1,'in_progress'),statusEvent(2,'closed','a'),statusEvent(2,'blocked','b')]);
  assert.equal(episodes[0].endStatus,'unknown');
  assert.deepEqual(workEpisodes([statusEvent(1,'in_progress','a'),statusEvent(1,'blocked','b')]),[]);
});
test('live hours round-trip as a rolling window without changing historical bounds',()=>{
  const now=Date.parse('2026-09-21T12:00:00Z');
  const url=new URL('http://local/?hours=2.5');
  assert.equal(timelineLocation(url,now).from,now-2.5*3600000);
  assert.equal(timelineLocation(url,now+3600000).from,now-1.5*3600000);
  url.searchParams.set('from','2026-09-20T00:00:00Z');url.searchParams.set('to','2026-09-20T12:00:00Z');
  const past=timelineLocation(url,now);assert.equal(past.live,false);assert.equal(past.from,Date.parse('2026-09-20T00:00:00Z'));assert.equal(past.liveHours,2.5);
});

test('status episode curves leave and return to the activity spine at their own bounds',()=>{
 assert.deepEqual(episodePosition(2,2,8,4,1,0,true),[2,0,0]);
 assert.deepEqual(episodePosition(5,2,8,4,1,0,true),[5,4,1]);
 assert.deepEqual(episodePosition(8,2,8,4,1,0,true),[8,0,0]);
 assert.deepEqual(episodePosition(8,2,8,4,1,0,false),[8,4,1]);
});

test('undated terminal current state stops at last evidence without inventing a closure',()=>{
 const events=[statusEvent(1,'in_progress'),statusEvent(4,'in_progress'),{id:'later',time:6,kind:'comment'}];
 const [episode]=issueWorkEpisodes({status:'closed'},events,10,true);
 assert.equal(episode.end,4);assert.equal(episode.endStatus,'unknown');assert.equal(episode.endUnknown,true);
 assert.equal(issueWorkEpisodes({status:'closed'},events,10,false)[0].end,null,'Today’s state cannot alter historical playback');
 assert.equal(issueWorkEpisodes({status:'blocked'},events,10,true)[0].end,null);
 const [recorded]=issueWorkEpisodes({status:'closed'},[...events,statusEvent(8,'closed')],10,true);
 assert.equal(recorded.end,8);assert.equal(recorded.endStatus,'closed');assert.equal(recorded.endUnknown,undefined);
});

test('map range brush preserves precise times, either direction, and clamps edges',()=>{
 assert.deepEqual(brushTimeRange(1000,2000,20,80,100),{from:1200,to:1800});
 assert.deepEqual(brushTimeRange(1000,2000,80,20,100),{from:1200,to:1800});
 assert.deepEqual(brushTimeRange(1000,2000,-50,150,100),{from:1000,to:2000});
 assert.equal(brushTimeRange(0,1000,10,15,100),null);
 assert.equal(brushTimeRange(0,1000,0,100,0),null);
});
