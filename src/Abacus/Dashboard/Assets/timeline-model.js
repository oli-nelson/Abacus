// Source-only projection: no current-field backfill into historical playback.
export const laneColors = ['#06d9ff','#ffc34a','#b486ff','#65dcc6','#ff9ccc'];
export const statusColors = {open:'#92b2cc',in_progress:'#06d9ff',blocked:'#ff646c',closed:'#32e6ad',unknown:'#637b91'};
export const stamp = value => { const t=value ? Date.parse(value) : NaN; return Number.isFinite(t) ? t : null; };
export const clamp = (n,a,b) => Math.max(a,Math.min(b,n));
// Pure URL interpretation shared by initial load and browser history navigation.
export function timelineLocation(url,now) {
  const day=new Date(now);day.setHours(0,0,0,0);
  let from=stamp(url.searchParams.get('from'))??day.getTime();
  let to=stamp(url.searchParams.get('to'))??now;
  if(to<=from){from=now-3600000;to=now;}
  const live=!url.searchParams.has('at')&&!url.searchParams.has('to');
  const requested=Number(url.searchParams.get('hours'));
  const liveHours=Number.isFinite(requested)&&requested>0&&requested<=87600?requested:(!url.searchParams.has('from')?24:null);
  if(live&&liveHours!==null){from=now-liveHours*3600000;to=now;}
  return {from,to,live,liveHours,playhead:clamp(stamp(url.searchParams.get('at'))??now,from,to),pendingEvent:url.searchParams.get('event')};
}
export function savedTimelineCamera(value,mode){
  try {
    const c=JSON.parse(value);
    if(!c||!['yaw','pitch','distance','perspective'].every(k=>Number.isFinite(c[k]))||
      Math.abs(c.yaw)>1.2||Math.abs(c.pitch)>1.15||c.distance<5||c.distance>250||
      c.perspective!==(mode==='2d'?0:1)||!Array.isArray(c.target)||c.target.length!==3||
      !c.target.every(n=>Number.isFinite(n)&&Math.abs(n)<=100000))return null;
    return {yaw:c.yaw,pitch:c.pitch,distance:c.distance,perspective:c.perspective,target:[...c.target]};
  }catch{return null;}
}
export function authorInitials(author){
  const parts=typeof author==='string'?author.trim().split(/\s+/u).filter(Boolean):[];
  if(!parts.length)return '?';
  return [...new Set([0,parts.length-1])].map(i=>Array.from(parts[i])[0]).join('').toLocaleUpperCase().slice(0,4);
}
export function eventsFor(issue, versions=[]) {
  const events=[];
  const created=stamp(issue.createdAt);
  if(created!==null)events.push({id:'created:'+issue.id,issueId:issue.id,time:created,kind:'created',source:'beads',certainty:'recorded',text:'Created · recorded issue field',status:'unknown'});
  for(const c of issue.comments || []) {
    const time=stamp(c.createdAt);
    if(time!==null)events.push({id:'comment:'+issue.id+':'+c.id,issueId:issue.id,time,kind:'comment',source:'beads',sourceRevision:c.id,certainty:'recorded',author:c.author,text:c.text});
  }
  for(const v of versions) {
    const time=stamp(v.recordedAt);
    if(time!==null)events.push({id:v.id,issueId:issue.id,time,kind:'snapshot',source:'beads',sourceRevision:v.sourceRevision,certainty:'recorded',author:v.committer,text:v.notes || v.title,notes:v.notes??'',status:v.status,title:v.title,assignee:v.assignee,priority:v.priority,labels:Array.isArray(v.labels)?[...v.labels].sort():null,issueType:v.issueType,target:v.target});
  }
  // Current closed_at is evidence of this recorded closure only, not every prior
  // closure, a Git merge, or a reliable reconstruction of intermediate states.
  const closed=stamp(issue.closedAt);
  if(issue.status==='closed' && closed!==null)events.push({id:'closure:'+issue.id+':'+closed,issueId:issue.id,time:closed,kind:'closure',source:'beads',certainty:'recorded',text:'Closed · current recorded closure; integration unverified',status:'closed'});
  return events.sort((a,b)=>a.time-b.time || a.id.localeCompare(b.id));
}
// Snapshots reconstruct state; only differences become user-facing events.
export const eventKindLabel=kind=>({status:'Status change',labels:'Label change',notes:'Note change',comment:'New comment',cluster:'Issue changes',current:'Current state'}[kind]||kind);
export function meaningfulEvents(events) {
  const result=events.filter(e=>e.kind==='comment'),groups=new Map();
  for(const e of events.filter(e=>['snapshot','closure'].includes(e.kind)).sort((a,b)=>a.time-b.time||a.id.localeCompare(b.id))){
    if(!groups.has(e.time))groups.set(e.time,[]);groups.get(e.time).push(e);
  }
  let previous={};
  for(const group of groups.values()){
    const snapshots=group.filter(e=>e.kind==='snapshot'),source=snapshots[0]||group[0];
    const consensus=(rows,key)=>{
      const values=rows.map(e=>key==='labels'?(Array.isArray(e.labels)?[...new Set(e.labels)].sort():undefined):e[key]);
      return values.length&&values.every(v=>v!==undefined&&JSON.stringify(v)===JSON.stringify(values[0]))?values[0]:undefined;
    };
    const next={status:consensus(group,'status'),labels:consensus(snapshots,'labels'),notes:consensus(snapshots,'notes')};
    for(const kind of ['status','labels','notes']){
      const before=previous[kind],after=next[kind];
      if(before===undefined||after===undefined||JSON.stringify(before)===JSON.stringify(after))continue;
      let text;
      if(kind==='status')text='Status: '+before+' → '+after;
      if(kind==='labels'){
        const added=after.filter(v=>!before.includes(v)),removed=before.filter(v=>!after.includes(v));
        text=[added.length?'Added labels: '+added.join(', '):'',removed.length?'Removed labels: '+removed.join(', '):''].filter(Boolean).join(' · ');
      }
      if(kind==='notes')text=!after?'Notes cleared':!before?'Notes added':'Notes updated';
      result.push({id:source.id+':'+kind,issueId:source.issueId,time:source.time,kind,text,before,after,
        status:kind==='status'?after:undefined,source:'beads',sourceRevision:source.sourceRevision,
        committer:source.author,certainty:'Observed between recorded states; exact edit time and author may be unknown.'});
    }
    // Equal-time conflicts and missing fields break the baseline: do not invent a delta.
    previous=next;
  }
  return result.sort((a,b)=>a.time-b.time||a.id.localeCompare(b.id));
}
export function stateAt(issue,events,time,live=false) {
  if(live)return {status:issue.status,title:issue.title,assignee:issue.assignee,priority:issue.priority,labels:issue.labels,issueType:issue.issueType,target:issue.target,certainty:'current',at:null};
  const known=events.filter(e=>e.time<=time && (e.kind==='snapshot'||e.kind==='closure'));
  const last=known.at(-1);
  if(!last)return {status:'unknown',title:issue.id,certainty:'no recorded state at this time',at:null};
  const simultaneous=known.filter(e=>e.time===last.time);
  const snapshots=simultaneous.filter(e=>e.kind==='snapshot');
  let ambiguous=false;
  function consensus(rows,key){
    const values=rows.map(e=>e[key]??null);
    if(values.some(v=>JSON.stringify(v)!==JSON.stringify(values[0]))){ambiguous=true;return null;}
    return values[0]??null;
  }
  const status=consensus(simultaneous,'status')||'unknown';
  const metadata=Object.fromEntries(['title','assignee','priority','labels','issueType','target'].map(key=>[key,consensus(snapshots,key)]));
  return {...metadata,status,title:metadata.title||issue.id,
    certainty:ambiguous?'ambiguous equal-time snapshots; conflicting fields unknown':'last recorded snapshot; intervening changes unknown',at:last.time};
}
export function clusterEvents(events,from,to,buckets=100) {
  const groups=new Map(),span=Math.max(1,to-from);
  for(const event of events) {
    if(event.time<from || event.time>to)continue;
    const bucket=Math.floor((event.time-from)/span*buckets),key=event.issueId+':'+bucket;
    if(!groups.has(key))groups.set(key,[]);
    groups.get(key).push(event);
  }
  return [...groups.values()].map(group=>{
    if(group.length===1)return group[0];
    const statuses=[...new Set(group.map(e=>e.status).filter(Boolean))];
    return {id:'cluster:'+group[0].id,issueId:group[0].issueId,time:group[0].time,kind:'cluster',
      status:statuses.length===1?statuses[0]:undefined,text:group.length+' changes · '+[...new Set(group.map(e=>e.kind))].map(kind=>group.filter(e=>e.kind===kind).length+' '+({status:'status',labels:'label',notes:'note',comment:'comment'}[kind]||kind)).join(' · '),members:group};
  });
}
export class TimelineProjection {
  constructor(){this.slots=new Map();this.cache=new Map();this.rebuilds=0;}
  update(issues,histories=new Map(),gitHistories=new Map()){
    for(const issue of [...issues].sort((a,b)=>a.id.localeCompare(b.id))){
      if(!this.slots.has(issue.id))this.slots.set(issue.id,this.slots.size);
      const history=histories.get(issue.id),git=gitHistories.get(issue.id),key=issue.revision+':'+(history?.key || '')+':'+(git?.key||'');
      if(this.cache.get(issue.id)?.key===key)continue;
      this.cache.set(issue.id,{key,issue,events:[...eventsFor(issue,history?.versions),...gitCommitEvents(issue,git)].sort((a,b)=>a.time-b.time||a.id.localeCompare(b.id)),slot:this.slots.get(issue.id),color:laneColors[this.slots.get(issue.id)%laneColors.length]});
      const lane=this.cache.get(issue.id);lane.displayEvents=meaningfulEvents(lane.events);
      this.rebuilds++;
    }
    const ids=new Set(issues.map(i=>i.id));
    for(const id of this.cache.keys())if(!ids.has(id))this.cache.delete(id);
    return [...this.cache.values()].sort((a,b)=>a.slot-b.slot);
  }
}

// Screen-space label LOD only: never remove lanes/events from source projections.
// Stable input order breaks equal-priority ties; focused/selected cards win space.
export function nonOverlappingLabels(candidates,gap=6) {
  const accepted=[];
  for(const candidate of [...candidates].sort((a,b)=>(b.priority||0)-(a.priority||0))){
    for(const placement of [candidate,...(candidate.alternatives||[]).map(pos=>({...candidate,...pos}))]){
      const {x,y,width,height}=placement;
      if(![x,y,width,height].every(Number.isFinite)||width<=0||height<=0)continue;
      if(accepted.some(other=>x<other.x+other.width+gap&&x+width+gap>other.x&&y<other.y+other.height+gap&&y+height+gap>other.y))continue;
      accepted.push(placement);break;
    }
  }
  return accepted;
}

// This is a current comparison summary, never a dated merge event.
export function gitLaneSummary(issue,evidence,live=true) {
  if(!live||evidence?.issueId!==issue.id||evidence.issueRevision!==issue.revision||
      evidence.state!=='validated'||!evidence.binding||!evidence.comparison)return null;
  const {binding,comparison}=evidence;
  const files=comparison.files||[];
  const additions=files.reduce((sum,file)=>sum+(Number.isFinite(file.additions)?file.additions:0),0);
  const deletions=files.reduce((sum,file)=>sum+(Number.isFinite(file.deletions)?file.deletions:0),0);
  return {branch:binding.issueBranch,
    changes:`${files.length} files · +${additions} / −${deletions}${files.some(file=>file.binary)?' · includes binary':''}`,
    integration:comparison.containedInTarget===true?'Contained now · merge time unknown':'Not proven integrated',
    basis:`${comparison.basis} · ${comparison.targetTip}…${comparison.issueTip}${comparison.warning?' · '+comparison.warning:''}`};
}


export function gitCommitEvents(issue,history){
  if(!history||history.issueRevision!==issue.revision)return [];
  const seen=new Set(),events=[];
  for(const commit of (history.commits||[]).slice(0,100)){
    const time=stamp(commit.committedAt);
    if(time===null||!commit.id||seen.has(commit.id))continue;
    seen.add(commit.id);
    events.push({id:'git:'+issue.id+':'+commit.id,issueId:issue.id,time,kind:'git',source:'git',
      certainty:'recorded commit; current reachability only',sourceRevision:commit.id,
      author:commit.author,text:commit.message,parents:commit.parents||[],
      provenance:'Reachable from the inspected issue tip now; may be a shared ancestor. Commit time is not issue work time or integration time.'});
  }
  return events;
}


// Current topology observations are separate from historical merge events.
export function currentGitTopology(issue,evidence,live=true){
  if(!gitLaneSummary(issue,evidence,live))return null;
  return {target:evidence.binding.targetRef,issueBranch:evidence.binding.issueBranch,startCommit:evidence.binding.startCommit,
    contained:evidence.comparison.containedInTarget===true,
    issueTip:evidence.comparison.issueTip,targetTip:evidence.comparison.targetTip};
}
export function smoothConnection(from,to,steps=32){
  return Array.from({length:steps+1},(_,i)=>{
    const t=i/steps,s=t*t*(3-2*t);
    return [from[0]+(to[0]-from[0])*t,from[1]+(to[1]-from[1])*s,from[2]+(to[2]-from[2])*s];
  });
}

export function recordedStartEvent(issue,evidence,history,live=true){
  const topology=currentGitTopology(issue,evidence,live);
  if(!topology||history?.tip!==topology.issueTip)return null;
  const event=gitCommitEvents(issue,history).find(event=>event.sourceRevision===topology.startCommit);
  return event?{...event,provenance:'Recorded execution start commit on '+topology.target+'. The timestamp is the commit time, not the branch creation or claim time.'}:null;
}

// Drop only collinear interior vertices; preserve bends and direction reversals.
export function simplifyStraightSegments(points){
  if(points.length<3)return points;
  const result=[points[0]];
  for(let i=1;i<points.length-1;i++){
    const a=points[i].map((v,k)=>v-result.at(-1)[k]),b=points[i+1].map((v,k)=>v-points[i][k]);
    const cross=[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
    if(Math.hypot(...cross)>1e-8||a.reduce((s,v,k)=>s+v*b[k],0)<0)result.push(points[i]);
  }
  result.push(points.at(-1));return result;
}

export function branchCurvePosition(x,y,z,topology){
  if(!topology)return [x,y,z];
  const smooth=t=>{t=clamp(t,0,1);return t*t*(3-2*t);};
  let laneWeight=1;
  if(topology.startX!==null&&x>=topology.startX)laneWeight=smooth((x-topology.startX)/4);
  if(topology.contained)laneWeight=Math.min(laneWeight,1-smooth((x-8)/4));
  return [x,topology.targetY+(y-topology.targetY)*laneWeight,z*laneWeight];
}

// Screen-space speech bubble: above the bead when possible, below near the top.
export function eventBubblePlacement(point,width,height,viewportWidth,viewportHeight){
  if(!point||point.x<0||point.y<0||point.x>viewportWidth||point.y>viewportHeight||width>viewportWidth-16||height>viewportHeight-16)return null;
  const below=point.y-height-18<8;
  const x=clamp(point.x-width*.65,8,viewportWidth-width-8);
  const y=clamp(below?point.y+18:point.y-height-18,8,viewportHeight-height-8);
  // If there is not enough room to keep the bubble off the bead, use a detached card.
  if(point.y>=y-8&&point.y<=y+height+8)return null;
  const edge=below?y:y+height,attach=clamp(point.x,x+22,x+width-22);
  return {x,y,path:`M ${attach-9} ${edge} L ${point.x} ${point.y} L ${attach+9} ${edge} Z`};
}

export function eventArrivalStart(event,arrivals){
  let start=arrivals.get(event.id);
  for(const member of event.members||[]){const candidate=arrivals.get(member.id);if(candidate!==undefined&&(start===undefined||candidate>start))start=candidate;}
  return start;
}

// Status episodes drive the activity spine. Git evidence is an independent annotation.
// Never use creation/commit dates as a guessed in-progress transition.
export function workEpisodes(events,until=Infinity){
  const transitions=events.filter(e=>Number.isFinite(e.time)&&e.time<=until&&['snapshot','closure'].includes(e.kind))
    .sort((a,b)=>a.time-b.time||a.id.localeCompare(b.id));
  const episodes=[];let active=null;
  for(let i=0;i<transitions.length;){
    const time=transitions[i].time,group=[];
    while(i<transitions.length&&transitions[i].time===time)group.push(transitions[i++]);
    const statuses=new Set(group.map(e=>e.status));
    const status=statuses.size===1?group[0].status:'unknown';
    const event=group[0];
    if(status==='in_progress'){
      if(!active){active={start:time,end:null,endStatus:null,startEvent:event,endEvent:null,transitions:[]};episodes.push(active);}
      if(active.transitions.at(-1)?.status!==status)active.transitions.push({time,status,event});
    }else if(status==='blocked'){
      if(active&&active.transitions.at(-1)?.status!==status)active.transitions.push({time,status,event});
    }else if(active){
      active.end=time;active.endStatus=status||'unknown';active.endEvent=event;
      active=null;
    }
  }
  return episodes;
}

export function episodePosition(x,start,end,y,z,spineY,closed){
  const smooth=t=>{t=clamp(t,0,1);return t*t*(3-2*t);};
  const bend=Math.min(3,Math.max(.001,(end-start)/3));
  let weight=smooth((x-start)/bend);
  if(closed)weight=Math.min(weight,smooth((end-x)/bend));
  return [x,spineY+(y-spineY)*weight,z*weight];
}

// A terminal working-set status cannot supply a missing historical timestamp.
// Stop at the last recorded working state and make the missing endpoint explicit.
export function issueWorkEpisodes(issue,events,until,live=false){
  const episodes=workEpisodes(events,until),last=episodes.at(-1);
  if(live&&last&&last.end===null&&!['in_progress','blocked'].includes(issue.status)){
    const known=events.filter(e=>e.time>=last.start&&e.time<=until&&e.kind==='snapshot'&&['in_progress','blocked'].includes(e.status));
    last.end=known.reduce((time,e)=>Math.max(time,e.time),last.start);
    last.endStatus='unknown';last.endUnknown=true;last.currentStatus=issue.status;
  }
  return episodes;
}

export function brushTimeRange(from,to,start,end,width){
  if(![from,to,start,end,width].every(Number.isFinite)||to<=from||width<=0)return null;
  const a=clamp(start,0,width),b=clamp(end,0,width);
  if(Math.abs(a-b)<6)return null;
  return {from:from+(to-from)*Math.min(a,b)/width,to:from+(to-from)*Math.max(a,b)/width};
}

// Scheduling hints only: current fields cannot prove historical work intervals.
// Keep every issue eligible, including reopened work and unknown dates.
export function prioritizeTimelineHistory(issues,from,to) {
  const rank=issue=>{
    const created=stamp(issue.createdAt),closed=stamp(issue.closedAt);
    if(created!==null&&created>to)return 4;
    if(closed!==null&&closed>=from&&closed<=to)return 0;
    if(issue.status==='in_progress'||issue.status==='blocked')return 0;
    if(closed!==null&&closed<from&&issue.status==='closed')return 3;
    if(issue.status==='closed')return 1;
    return 2;
  };
  return [...issues].sort((a,b)=>rank(a)-rank(b)||a.id.localeCompare(b.id));
}

export function commentPreview(text,limit=240){
  const characters=Array.from(text??'');
  return characters.length>limit?characters.slice(0,limit).join('')+'…':characters.join('');
}

// Deterministic, range-local spacing by simultaneous work, never by issue ID slot.
// A lone issue is +gap; two are +gap/-gap; further issues alternate outwards.
export function concurrentEpisodeLayout(rows,gap=2.4) {
  const boundaries=[...new Set(rows.flatMap(r=>[r.start,r.end]))].sort((a,b)=>a-b),tracks=new Map(rows.map(r=>[r.key,[]]));
  for(let i=0;i<boundaries.length-1;i++){
    const start=boundaries[i],end=boundaries[i+1];
    const active=rows.filter(r=>r.start<=start&&r.end>start).sort((a,b)=>a.start-b.start||a.key.localeCompare(b.key));
    const assigned=new Map(),counts={positive:0,negative:0};
    // Keep an episode on its established side for its entire lifetime. Repacking
    // to a symmetric set after departures can otherwise drag a live branch
    // through the spine and make its closing curve overshoot and double back.
    for(const sign of [1,-1]){
      const continuing=active.filter(r=>Math.sign(tracks.get(r.key).at(-1)?.y||0)===sign)
        .sort((a,b)=>Math.abs(tracks.get(a.key).at(-1).y)-Math.abs(tracks.get(b.key).at(-1).y)||a.key.localeCompare(b.key));
      continuing.forEach((row,index)=>assigned.set(row.key,sign*(index+1)*gap));
      counts[sign===1?'positive':'negative']=continuing.length;
    }
    for(const row of active)if(!assigned.has(row.key)){
      const side=counts.positive<=counts.negative?'positive':'negative',sign=side==='positive'?1:-1;
      assigned.set(row.key,sign*(++counts[side])*gap);
    }
    active.forEach(row=>{
      const y=assigned.get(row.key),list=tracks.get(row.key),previous=list.at(-1);
      list.push({start,end,y,from:previous?.y??y});
    });
  }
  const offsets=[...tracks.values()].flat().map(s=>s.y);
  const sampleTimes=[...new Set([...tracks.values()].flat().filter(s=>s.from!==s.y).flatMap(s=>Array.from({length:9},(_,i)=>s.start+Math.min((s.end-s.start)*.25,180000)*i/8)))];
  return {sampleTimes,min:Math.min(0,...offsets),max:Math.max(gap,...offsets),offset(key,time){
    const list=tracks.get(key)||[],segment=list.find(s=>time>=s.start&&time<s.end)||list.at(-1);
    if(!segment)return gap;
    const duration=Math.min((segment.end-segment.start)*.25,180000),t=clamp((time-segment.start)/Math.max(1,duration),0,1),smooth=t*t*(3-2*t);
    return segment.from+(segment.y-segment.from)*smooth;
  }};
}

export function isInitialWorkEntry(event,episodes){
  return event.kind==='status'&&event.after==='in_progress'&&['open','blocked'].includes(event.before)&&event.time===episodes[0]?.start;
}
// Keep the branch offset through its first/last issue event. End joins are
// unmarked connectors, including vertical joins when events share an endpoint.
export function eventAwareEpisodePosition(x,start,end,y,closed,firstEvent=Infinity,lastEvent=-Infinity){
  const bend=Math.min(3,Math.max(.001,(end-start)/3));
  const forkEnd=Math.min(start+bend,firstEvent),returnStart=Math.max(end-bend,lastEvent);
  const smooth=t=>{t=clamp(t,0,1);return t*t*(3-2*t);};
  const fork=forkEnd<=start?1:smooth((x-start)/(forkEnd-start));
  const merge=!closed||returnStart>=end?1:smooth((end-x)/(end-returnStart));
  return [x,y*Math.min(fork,merge),0];
}

export function timelineAnnotations(markers,selected){
  const candidates=markers.filter(m=>m.issueId===selected).flatMap(m=>(m.event.members||[m.event]).filter(e=>['status','comment','labels','notes'].includes(e.kind)).map(event=>({...m,event})));
  const statuses=candidates.filter(m=>m.event.kind==='status').sort((a,b)=>Number(b.event.after==='closed')-Number(a.event.after==='closed')||b.event.time-a.event.time).slice(0,24);
  const others=candidates.filter(m=>m.event.kind!=='status').sort((a,b)=>b.event.time-a.event.time).slice(0,Math.min(6,24-statuses.length));
  return [...statuses,...others].sort((a,b)=>a.event.time-b.event.time||a.event.id.localeCompare(b.event.id));
}
