import {matchesIssueMetadata,matchesIssueText} from './issue-filters.js';
import {needleLabels,timelineAnnotations,isInitialWorkEntry,eventAwareEpisodePosition,episodeVisualRuns,resumesOpenEpisode,concurrentEpisodeLayout,commentPreview,eventKindLabel,eventStatusChanges,brushTimeRange,issueWorkEpisodes,workEpisodes,episodePosition,eventArrivalStart,eventBubblePlacement,TimelineProjection,eventsFor,authorInitials,timelineLocation,savedTimelineCamera,clusterEvents,stateAt,statusColors,stamp,clamp,nonOverlappingLabels,gitLaneSummary,currentGitTopology,smoothConnection,recordedStartEvent,branchCurvePosition} from './timeline-model.js';
import {TimelineRenderer,projectPoint,pickScreenMarker,rgb} from './timeline-gl.js';
const $=id=>document.getElementById(id);
const node=(tag,value,className)=>{const n=document.createElement(tag);n.textContent=value ?? '';if(className)n.className=className;return n;};
const localInput=t=>{const d=new Date(t);return new Date(t-d.getTimezoneOffset()*60000).toISOString().slice(0,16);};
const display=t=>new Date(t).toLocaleString(undefined,{month:'short',day:'numeric',hour:'2-digit',minute:'2-digit'});
const readPreference=(key,fallback)=>{try{return localStorage.getItem(key)||fallback;}catch{return fallback;}};
const savePreference=(key,value)=>{try{localStorage.setItem(key,value);}catch{}};
// Axis stretch bounds, shared by the range inputs in index.html.
const MAXIMUM_STRETCH=40,BASE_MAXIMUM_DISTANCE=250;
// Lanes drawn at once. Vertical spread follows peak simultaneous work, not this
// number, because lanes that do not overlap in time share an offset; the cost of a
// larger page is geometry, not height. A small page combined with start ordering
// would otherwise crowd every drawn lane into the earliest part of the range and
// leave the rest of the width looking empty.
const LANE_PAGE=120;
export class Timeline {
  constructor({onSelect,onPlayback}) {
    this.onSelect=onSelect;this.onPlayback=onPlayback;this.projection=new TimelineProjection();this.connectorArrivals=new Map();this.topologyObservations=new Map();this.laneArrivals=new Map();this.statusChanges=new Map();this.arrivals=new Map();this.histories=new Map();this.gitEvidence=new Map();this.gitHistories=new Map();this.lanes=[];this.selected=null;this.visible=false;
    this.clock={server:Date.now(),local:performance.now()};this.frame=null;this.transition=null;this.playing=false;this.lastPlayback=0;this.newEvents=0;this.offset=0;
    Object.assign(this,timelineLocation(new URL(location.href),this.now()));
    this.axisScale=['time','vertical'].map(axis=>{const n=Number(readPreference('abacus.timeline.scale.'+axis,'1'));return Number.isFinite(n)&&n>=.25&&n<=MAXIMUM_STRETCH?n:1;});this.axisScale.push(1);
    this.camera={yaw:.15,pitch:.28,distance:30,target:[0,0,0],perspective:1};
    const urlMode=new URL(location.href).searchParams.get('camera');
    this.mode=['2d','3d'].includes(urlMode)?urlMode:readPreference('abacus.timeline.mode','3d');
    if(!['2d','3d'].includes(this.mode))this.mode='3d';
    this.motion=readPreference('abacus.motion','system');
    if(!['system','reduce','full'].includes(this.motion))this.motion='system';
    this.media=matchMedia('(prefers-reduced-motion: reduce)');
    this.media.addEventListener('change',()=>{if(this.reduced()){this.finishCameraTransition();this.calloutAnimation?.cancel();this.cancelFilterMotion();this.invalidate();}});
    $('timeline-motion').value=this.motion;document.documentElement.dataset.motion=this.motion;
    this.renderer=new TimelineRenderer($('timeline-canvas'),$('timeline-fallback'),$('timeline-markers'),message=>{
      $('timeline-renderer').textContent=message||'WebGL · perspective camera';
      if(message){this.mode='2d';this.applyMode(false);}this.invalidate();
    });
    if(!this.renderer.gl)this.mode='2d';
    $('timeline-renderer').textContent=this.renderer.gl?'WebGL · perspective camera':'WebGL unavailable · accessible 2D fallback';
    this.renderer.glow=readPreference('abacus.timeline.glow','on')!=='off';
    $('timeline-glow').setAttribute('aria-pressed',String(this.renderer.glow));document.documentElement.dataset.glow=this.renderer.glow?'on':'off';
    this.applyMode(false);
    const saved=savedTimelineCamera(readPreference('abacus.timeline.camera',''),this.mode,this.maxDistance());
    if(saved&&readPreference('abacus.timeline.layout','')==='concurrent-v1'){this.camera=saved;this.fitted=true;}
    savePreference('abacus.timeline.layout','concurrent-v1');
    $('timeline-camera').value=readPreference('abacus.timeline.control','orbit')==='pan'?'pan':'orbit';
    this.lastEventKind=$('timeline-kind').value;this.bindControls();this.syncTimeInputs();
    new ResizeObserver(()=>this.invalidate()).observe($('timeline-stage'));
    document.addEventListener('visibilitychange',()=>{if(document.hidden){this.cancelRangeBrush();this.pausePlayback();cancelAnimationFrame(this.frame);this.frame=null;this.finishCameraTransition();this.calloutAnimation?.cancel();this.cancelFilterMotion();}else{this.lastPlayback=performance.now();this.tickNow();this.invalidate();}});
    setInterval(()=>{if(!document.hidden)this.tickNow();},30000);
  }
  now(){return this.clock.server+performance.now()-this.clock.local;}
  setClock(value){const server=stamp(value);if(server!==null){this.clock={server,local:performance.now()};if(this.live){this.to=this.now();this.playhead=this.to;this.rebuild();this.syncTimeInputs();}}}
  reduced(){return this.motion==='reduce'||(this.motion==='system'&&this.media.matches);}
  historicalState(id){const lane=this.projection.cache.get(id);return lane?stateAt(lane.issue,lane.events,this.playhead,this.live):null;}
  matchesSearch(id,live=true){
    const lane=this.projection.cache.get(id);if(!lane)return false;
    return matchesIssueText(lane.issue,lane.events,this.query,{from:this.from,to:this.to,playhead:this.playhead,live,title:live?lane.issue.title:stateAt(lane.issue,lane.events,this.playhead,false).title});
  }
  updateSearchCoverage(){
    const end=this.visible&&!this.live?Math.min(this.to,this.playhead):this.to;
    $('search-coverage').textContent=`Search: IDs/titles and loaded recorded notes/comments from ${display(this.from)} to ${display(end)}. ${this.histories.size} issue histories loaded; older/unloaded history is not searched. Live views also search current undated notes/comments; playback does not.`;
  }
  queueLiveArrivals(issues){
    if(!this.live||!this.visible||document.hidden||this.reduced())return;
    const start=performance.now(),visible=new Set((this.visibleLanes||[]).map(l=>l.issue.id));
    for(const issue of issues){
      if(this.arrivals.size>=64)break;
      const previous=this.projection.cache.get(issue.id);
      if(!previous){if(this.laneArrivals.size<24)this.laneArrivals.set(issue.id,start);continue;}
      if(!visible.has(issue.id))continue;
      if(previous.issue.status!==issue.status){
        const marker=this.scene?.markers.find(m=>m.event.id==='current:'+issue.id);
        this.statusChanges.set(issue.id,{start,from:marker?this.renderer.markerColor(marker):rgb(statusColors[previous.issue.status]||statusColors.unknown)});
      }
      const known=new Set(previous.events.map(e=>e.id));
      for(const event of eventsFor(issue))if(!known.has(event.id)&&event.time>=this.from&&event.time<=this.now()){
        if(this.arrivals.size>=64)break;this.arrivals.set(event.id,start);
      }
    }
  }
  cancelArrivals(){this.connectorArrivals.clear();this.laneArrivals.clear();this.statusChanges.clear();this.arrivals.clear();this.renderer?.finishArrivals();}
  cancelFilterMotion(){for(const animation of this.filterAnimations||[])animation.cancel();this.filterAnimations=[];}
  fadeFilteredScene(){
    this.cancelFilterMotion();
    if(!this.visible||document.hidden||this.reduced())return;
    // Stable lane slots and event timestamps do not need positional interpolation.
    this.filterAnimations=['timeline-canvas','timeline-fallback','timeline-markers','timeline-labels'].map(id=>
      $(id).animate([{opacity:.45},{opacity:1}],{duration:180,easing:'cubic-bezier(.2,.75,.25,1)'}));
  }
  setData(issues,selected,query,status,metadata=this.metadata||{}){
    const filtersChanged=this.dataKey!=null&&(this.query!==query||this.status!==status||JSON.stringify(this.metadata)!==JSON.stringify(metadata));
    const before=this.projection.rebuilds;
    const revisions=new Map(issues.map(issue=>[issue.id,issue.revision]));
    for(const [id,evidence] of this.gitEvidence)if(revisions.get(id)!==evidence.issueRevision)this.gitEvidence.delete(id);
    for(const [id,history] of this.gitHistories)if(revisions.get(id)!==history.issueRevision)this.gitHistories.delete(id);
    this.lanes=this.projection.update(issues,this.histories,this.gitHistories);this.selected=selected;this.query=query;this.status=status;this.metadata=metadata;
    const key=this.lanes.map(l=>l.key).join('|')+':'+selected+':'+query+':'+status+':'+JSON.stringify(metadata);
    if(key===this.dataKey)return;this.dataKey=key;
    if(!this.live && this.projection.rebuilds!==before)this.newEvents+=this.projection.rebuilds-before;
    this.rebuild();
    this.reconcilePinnedEvent();
    this.restoreSharedEvent();
    if(filtersChanged)this.fadeFilteredScene();
  }
  reconcilePinnedEvent(){
    if(!this.pinnedEvent)return;
    if(this.pinnedIssue!==this.selected){
      this.pinnedEvent=null;this.calloutAnimation?.cancel();this.cancelFilterMotion();$('timeline-callout').hidden=true;return;
    }
    const previous=this.pinnedEvent;
    const event=this.projection.cache.get(this.selected)?.displayEvents.find(e=>e.id===previous.id)
      ??this.scene?.markers.find(m=>m.issueId===this.selected&&m.event.id===previous.id)?.event;
    // A pinned overlay is not an independent cache of source evidence. Remove
    // invalidated facts, and refresh changed text without replaying its entrance.
    const comparable=e=>e?.kind==='current'?{...e,time:0}:e;
    if(JSON.stringify(comparable(event))!==JSON.stringify(comparable(previous)))this.pick(this.selected,event||null);
  }
  restoreLocation(url){
    const wasLive=this.live;
    Object.assign(this,timelineLocation(url,this.now()));
    this.playing=false;this.transition=null;this.dataKey=null;
    this.pinnedEvent=null;this.calloutAnimation?.cancel();this.cancelFilterMotion();$('timeline-callout').hidden=true;
    const mode=url.searchParams.get('camera');
    if(['2d','3d'].includes(mode)&&mode!==this.mode){this.mode=this.renderer.gl?mode:'2d';this.applyMode(false);}
    this.syncTimeInputs();
    // History traversal must use the same current-issue reread gate as Return to
    // live, but must not erase the destination URL's pending event selection.
    this.onPlayback(!wasLive&&this.live,true);
  }
  restoreSharedEvent(){
    if(!this.pendingEvent)return;
    const event=this.projection.cache.get(this.selected)?.displayEvents.find(e=>e.id===this.pendingEvent);
    // Never fetch history implicitly or substitute a current observation for a
    // missing recorded event. A later explicit history load can resolve the link.
    if(!event||!Number.isFinite(event.time)||event.time>this.playhead)return;
    this.pendingEvent=null;this.pick(this.selected,event);
  }
  refreshEvidence(){
    this.dataKey=null;this.setData(this.lanes.map(l=>l.issue),this.selected,this.query,this.status);
  }
  setGitEvidence(id,evidence){
    if(!evidence){this.topologyObservations.delete(id);this.connectorArrivals.delete(id);const changed=this.gitEvidence.delete(id)|this.gitHistories.delete(id);if(changed)this.refreshEvidence();return;}
    const {issueId,issueRevision,state,binding,comparison,history}=evidence;
    const previous=this.gitEvidence.get(id);
    const issue=this.lanes.find(l=>l.issue.id===id)?.issue;
    const topology=issue&&currentGitTopology(issue,evidence,this.live),observed=this.topologyObservations.get(id);
    if(topology){
      if(!topology.contained)this.connectorArrivals.delete(id);
      if(observed&&!observed.contained&&topology.contained&&observed.target===topology.target&&observed.issueBranch===topology.issueBranch&&observed.startCommit===topology.startCommit&&this.visible&&!document.hidden&&!this.reduced()&&this.visibleLanes?.some(l=>l.issue.id===id))this.connectorArrivals.set(id,performance.now());
      if(!this.topologyObservations.has(id)&&this.topologyObservations.size>=64)this.topologyObservations.delete(this.topologyObservations.keys().next().value);
      this.topologyObservations.set(id,topology);
    }else{this.topologyObservations.delete(id);this.connectorArrivals.delete(id);}

    let changed=false;
    if(history&&state==='validated'&&history.tip===comparison?.issueTip){
      const entry={issueRevision,tip:history.tip,commits:history.commits.slice(0,100)};
      entry.key=JSON.stringify(entry);
      if(entry.key!==this.gitHistories.get(id)?.key){
        if(!this.gitHistories.has(id)&&this.gitHistories.size>=16)this.gitHistories.delete(this.gitHistories.keys().next().value);
        this.gitHistories.set(id,entry);changed=true;
      }
    }else if(previous?.comparison?.issueTip!==comparison?.issueTip||state!=='validated')changed=this.gitHistories.delete(id);
    // Keep patch bodies out of the timeline; loaded commit events have a separate bound.
    evidence={issueId,issueRevision,state,binding,comparison};
    if(!this.gitEvidence.has(id)&&this.gitEvidence.size>=64)this.gitEvidence.delete(this.gitEvidence.keys().next().value);
    if(JSON.stringify(previous)!==JSON.stringify(evidence)){this.gitEvidence.set(id,evidence);changed=true;}
    if(changed)this.refreshEvidence();
  }
  clearGitEvidence(preserveObservations=false){
    this.connectorArrivals.clear();
    if(!preserveObservations)this.topologyObservations.clear();
    if(!this.gitEvidence.size&&!this.gitHistories.size)return;
    this.gitEvidence.clear();this.gitHistories.clear();this.refreshEvidence();
  }
  invalidateHistory(state){
    let changed=false;
    for(const [id,history] of this.histories)if(state?.stale || history.revision!==state?.revision){this.histories.delete(id);changed=true;}
    if(changed){this.dataKey=null;this.setData(this.lanes.map(l=>l.issue),this.selected,this.query,this.status);}
  }
  setHistory(id,data,append=false){
    const previous=this.histories.get(id),same=previous?.revision===data.historyRevision && previous?.issueRevision===data.issueRevision;
    const versions=append&&same?[...previous.versions,...data.versions]:data.versions;
    const unique=[...new Map(versions.map(v=>[v.id,v])).values()];
    // A whole-project read holds one collapsed entry per issue, so the bound is on
    // issue count rather than on a page of them. Versions are already deduplicated.
    if(!this.histories.has(id)&&this.histories.size>=1024)this.histories.delete(this.histories.keys().next().value);
    this.histories.set(id,{versions:unique,revision:data.historyRevision,issueRevision:data.issueRevision,key:data.historyRevision+':'+unique.map(v=>v.id).join(',')});
    this.dataKey=null;
  }
  pausePlayback(){
    if(!this.playing)return;
    this.playing=false;this.saveRange();this.updateTransport();
  }
  show(visible){this.visible=visible;this.syncPanelSize();if(!visible){this.cancelRangeBrush();cancelAnimationFrame(this.frame);this.frame=null;this.pausePlayback();this.finishCameraTransition();this.calloutAnimation?.cancel();this.cancelFilterMotion();}else this.invalidate();}
  syncPanelSize(){
    const workspace=$('timeline-pane').closest('.workspace');
    workspace.classList.toggle('timeline-maximized',Boolean(this.visible&&this.panelMaximized));
    workspace.style.setProperty('--timeline-panel-top',Math.max(0,workspace.getBoundingClientRect().top)+'px');
    $('timeline-maximize').setAttribute('aria-pressed',String(Boolean(this.panelMaximized)));
    $('timeline-maximize').textContent=this.panelMaximized?'Restore panel':'Maximize view';
  }
  timeX(time){return -12+24*(time-this.from)/Math.max(1,this.to-this.from);}
  episodes(lane){return issueWorkEpisodes(lane.issue,lane.events,Math.min(this.now(),this.to,this.live?Infinity:this.playhead),this.live);}
  episodeEvents(lane){const episodes=this.episodes(lane);return lane.displayEvents.filter(e=>episodes.some(p=>e.time>=p.start&&e.time<=(p.end??this.now())));}
  // Lanes are ordered by when their work starts, not by issue ID. A bounded page is
  // drawn at a time, and advancing the playhead reveals later work: in ID order a
  // newly revealed lane could insert itself ahead of lanes already on the page and
  // push one off it, which reads as recorded history vanishing from the past. Work
  // revealed later always starts later, so in start order it can only be appended.
  filteredLanes(){
    const until=Math.min(this.to,this.playhead),now=this.now(),q=(this.query||'').toLowerCase();
    const rows=[];
    for(const lane of this.lanes){
      if(this.historyScope&&!this.historyScope.has(lane.issue.id))continue;
      const episodes=this.episodes(lane).filter(e=>e.start<=until&&(e.end??now)>=this.from);
      if(!episodes.length&&!(this.live&&lane.issue.status==='in_progress'))continue;
      const state=stateAt(lane.issue,lane.events,this.playhead,this.live);
      if(!matchesIssueMetadata(state,this.metadata))continue;
      if(this.status&&state.status!==this.status)continue;
      if(!matchesIssueText(lane.issue,lane.events,q,{from:this.from,to:this.to,playhead:this.playhead,live:this.live,title:state.title}))continue;
      // Episodes are already ascending, so the first is the earliest recorded start.
      rows.push({lane,start:episodes.length?episodes[0].start:Infinity});
    }
    return rows.sort((a,b)=>a.start-b.start||a.lane.issue.id.localeCompare(b.lane.issue.id)).map(row=>row.lane);
  }
  rebuild(){
    if(!this.lanes)return;
    const filter=$('timeline-kind').value,now=this.now();
    if(this.live){this.to=now;this.playhead=now;if(this.liveHours!==null)this.from=now-this.liveHours*3600000;if(this.from>=this.to)this.from=this.to-3600000;}
    this.nextEpisodeBoundary=this.lanes.reduce((next,lane)=>lane.events.reduce((n,e)=>['snapshot','closure'].includes(e.kind)&&e.time>this.playhead?Math.min(n,e.time):n,next),Infinity);
    const visible=this.filteredLanes();this.filteredCount=visible.length;
    this.offset=clamp(this.offset,0,Math.max(0,visible.length-1));
    this.visibleLanes=visible.slice(this.offset,this.offset+LANE_PAGE);
    const visibleIds=new Set(this.visibleLanes.map(l=>l.issue.id));
    for(const map of [this.laneArrivals,this.connectorArrivals])for(const id of map.keys())if(!visibleIds.has(id))map.delete(id);
    const paths=[],markers=[],labels=[],targetLabels=[];
    const allEpisodeMap=new Map(this.visibleLanes.map(lane=>[lane.issue.id,this.episodes(lane)]));
    const episodeMap=new Map(this.visibleLanes.map(lane=>[lane.issue.id,allEpisodeMap.get(lane.issue.id).filter(e=>e.start<=this.to&&(e.end??now)>=this.from)]));
    const key=(lane,episode)=>lane.issue.id+':'+episode.start;
    const layoutKeys=new Map(),rows=[];
    for(const lane of this.visibleLanes)for(const run of episodeVisualRuns(episodeMap.get(lane.issue.id))){
      const runKey=key(lane,run[0]);
      for(const episode of run)layoutKeys.set(key(lane,episode),runKey);
      rows.push({key:runKey,start:run[0].start,end:run.at(-1).end??now+1});
    }
    for(const lane of this.visibleLanes)if(this.live&&lane.issue.status==='in_progress'&&!episodeMap.get(lane.issue.id).some(e=>e.end===null))rows.push({key:lane.issue.id+':unknown',start:now,end:now+1});
    const layout=concurrentEpisodeLayout(rows),floor=layout.min-1.5,ceiling=layout.max+1.5,spineY=0;
    const offset=(lane,episode,time)=>layout.offset(layoutKeys.get(key(lane,episode)),time);
    const entry=(lane,event)=>isInitialWorkEntry(event,allEpisodeMap.get(lane.issue.id));
    const positions=new Map();
    let episodeCount=0,closedCount=0,resumeConnections=0;
    for(const lane of this.visibleLanes){
      const episodes=episodeMap.get(lane.issue.id);
      for(let episodeIndex=0;episodeIndex<episodes.length;episodeIndex++){
        const episode=episodes[episodeIndex],previous=episodes[episodeIndex-1];
        const continued=resumesOpenEpisode(previous,episode);
        episodeCount++;if(episode.endStatus==='closed')closedCount++;
        const start=this.timeX(episode.start),end=this.timeX(episode.end??now);
        const events=lane.displayEvents.filter(e=>e.time>=episode.start&&e.time<=(episode.end??now));
        const branchEvents=events.filter(e=>!entry(lane,e)),eventXs=branchEvents.map(e=>this.timeX(e.time));
        if(this.live&&episode.end===null&&['in_progress','blocked'].includes(lane.issue.status))eventXs.push(end);
        const eventXSet=new Set(eventXs);
        const first=eventXs.length?Math.min(...eventXs):Infinity,last=eventXs.length?Math.max(...eventXs):-Infinity;
        const position=x=>{
          let y=offset(lane,episode,this.from+(x+12)/24*(this.to-this.from));
          // A moving lane may cross the spine; its event must still be distinct.
          if(eventXSet.has(x)&&Math.abs(y)<.8)y=y<0?-.8:.8;
          return eventAwareEpisodePosition(x,start,end,y,episode.endStatus==='closed',first,last,continued);
        };
        positions.set(key(lane,episode),position);
        const left=Math.max(-12,start),right=Math.min(12,end),points=[],span=right-left;
        // Sample by on-screen length, not a fixed count: a few-minute episode drawn a
        // few pixels wide needed as many points as one spanning the whole range. The
        // floor still resolves its fork and return curves. Full width keeps ~100.
        const steps=clamp(Math.round(span*4.2),16,100);
        const samples=[...new Set([...Array.from({length:steps+1},(_,i)=>left+span*i/steps),...layout.sampleTimes.map(t=>this.timeX(t)).filter(x=>x>left&&x<right),...eventXs.filter(x=>x>=left&&x<=right),...([first,last].filter(Number.isFinite).filter(x=>x>=left&&x<=right))])].sort((a,b)=>a-b);
        if(continued&&previous.end<episode.start){
          const pauseStart=Math.max(-12,this.timeX(previous.end)),pauseEnd=Math.min(12,start);
          if(pauseEnd>pauseStart){
            const pausedPoints=Array.from({length:33},(_,i)=>{
              const x=pauseStart+(pauseEnd-pauseStart)*i/32;
              return [x,offset(lane,episode,this.from+(x+12)/24*(this.to-this.from)),0];
            });
            paths.push({issueId:lane.issue.id,points:pausedPoints,color:lane.color,alpha:.55,radius:.055,dashed:true,selected:lane.issue.id===this.selected});
            resumeConnections++;
          }
        }
        if(!continued&&left===start&&Math.abs(position(start)[1])>.001)points.push([start,spineY,0]);
        for(const x of samples)points.push(position(x));
        if(right===end&&episode.endStatus==='closed'&&Math.abs(position(end)[1])>.001)points.push([end,spineY,0]);
        paths.push({issueId:lane.issue.id,points,color:lane.color,selected:lane.issue.id===this.selected,dashed:false});
        for(let i=0;i<episode.transitions.length;i++){
          const transition=episode.transitions[i];if(transition.status!=='blocked')continue;
          const a=Math.max(left,this.timeX(transition.time)),b=Math.min(right,this.timeX(episode.transitions[i+1]?.time??episode.end??now));
          if(b<=a)continue;
          paths.push({issueId:lane.issue.id,points:[...new Set([...Array.from({length:33},(_,j)=>a+(b-a)*j/32),...samples.filter(x=>x>a&&x<b)])].sort((x,y)=>x-y).map(position),color:statusColors.blocked,dashed:true,selected:true});
        }
        const candidates=events.filter(e=>filter==='all'||e.kind===filter);
        // Never let a comment/label/note share a spine-entry cluster.
        const displayed=[...candidates.filter(e=>entry(lane,e)),...clusterEvents(candidates.filter(e=>!entry(lane,e)),this.from,this.to,70)];
        for(const e of displayed)if(e.time>=this.from&&e.time<=this.to)markers.push({pos:entry(lane,e)?[this.timeX(e.time),spineY,0]:position(this.timeX(e.time)),color:e.status?statusColors[e.status]||statusColors.unknown:lane.color,shape:'sphere',event:e,issueId:lane.issue.id,selected:lane.issue.id===this.selected});
        labels.push({lane,pos:[left,offset(lane,episode,Math.max(this.from,episode.start)),0],end:right,episode});
      }
      const latest=episodes.at(-1),active=latest&&latest.end===null;
      if(this.live&&(lane.issue.status==='in_progress'||active&&lane.issue.status==='blocked')){
        const pos=active?positions.get(key(lane,latest))(12):[12,layout.offset(lane.issue.id+':unknown',now),0];
        markers.push({pos,color:statusColors[lane.issue.status],shape:'sphere',issueId:lane.issue.id,event:{id:'current:'+lane.issue.id,kind:'current',source:'beads',sourceRevision:lane.issue.revision,time:now,status:lane.issue.status,text:active?'Current working state':'Current working state · start time not recorded',provenance:'Current Beads observation; not a dated status transition.'}});
        if(!active)labels.push({lane,pos,end:12});
      }
    }
    paths.push({points:[[-12,spineY,0],[12,spineY,0]],color:'#adbed4',radius:.12});
    targetLabels.push({text:'Project history',pos:[-12,spineY+.5,0]});
    for(const path of paths)if(this.laneArrivals.has(path.issueId))path.arrival=this.laneArrivals.get(path.issueId);
    for(const marker of markers){
      if(this.laneArrivals.has(marker.issueId))marker.arrival=this.laneArrivals.get(marker.issueId);
      const arrival=eventArrivalStart(marker.event,this.arrivals);
      if(arrival!==undefined){marker.arrival=arrival;if(marker.event.kind==='cluster')marker.arrivalFloor=.55;}
      if(marker.event.id==='current:'+marker.issueId)marker.colorTransition=this.statusChanges.get(marker.issueId);
    }
    this.targetLabels=targetLabels;
    $('timeline-stage').dataset.recordedStarts=String(episodeCount);
    $('timeline-stage').dataset.resumeConnections=String(resumeConnections);
    $('timeline-stage').dataset.currentConnections='0';
    $('timeline-stage').dataset.closedEpisodes=String(closedCount);
    $('timeline-stage').dataset.spineEvents=JSON.stringify(markers.filter(m=>Math.abs(m.pos[1]-spineY)<1e-6).map(m=>({kind:m.event.kind,before:m.event.before,after:m.event.after})));
    $('timeline-stage').dataset.clusters=String(markers.filter(m=>m.event.kind==='cluster').length);
    const guides=markers.filter(m=>m.selected&&['comment','status','labels','notes'].includes(m.event.kind)).slice(-6).map(m=>({pos:m.pos,color:m.color}));
    this.scene={paths,markers,guides,floor,ceiling,nowX:Math.abs(now-this.to)<1000?clamp(this.timeX(now),-12,12):this.timeX(now)};
    this.labels=labels;this.renderer.setScene(this.scene);this.buildLabels();this.updateTransport();
    $('timeline-page').textContent=visible.length?`${this.offset+1}–${Math.min(this.offset+LANE_PAGE,visible.length)} / ${visible.length} lanes`:'No matching lanes in this range';
    $('timeline-prev').disabled=this.offset===0;$('timeline-next').disabled=this.offset+LANE_PAGE>=visible.length;
    if(!this.fitted && this.visibleLanes.length){this.fitted=true;this.fit(false);}
    this.invalidate();
  }
  // A card with no episode is work in flight whose start was never recorded, so it
  // is open at the needle by definition rather than by a timestamp it does not have.
  featuredLabels(){
    const needle=Math.min(this.playhead,this.to);
    return needleLabels((this.labels||[]).map(entry=>({entry,id:entry.lane.issue.id,
      start:entry.episode?entry.episode.start:needle,
      end:entry.episode?(entry.episode.end??Infinity):Infinity})),needle,this.selected);
  }
  buildLabels(){
    const focused=document.activeElement;
    const focusKey=focused?.dataset.timelineFocus;
    const restoreFocus=focusKey&&($('timeline-labels').contains(focused)||$('timeline-event-list').contains(focused));
    const focusTargets=new Map();
    const remember=(element,key)=>{element.dataset.timelineFocus=key;focusTargets.set(key,element);};
    const overlay=$('timeline-labels');overlay.replaceChildren();this.labelNodes=[];
    const featured=this.featuredLabels();
    for(const entry of this.labels){
      const {lane,pos,episode}=entry;
      const card=node('button','', 'lane-card');card.style.setProperty('--lane-color',lane.color);
      remember(card,JSON.stringify(['lane',lane.issue.id,episode?.start??'current']));
      const state=stateAt(lane.issue,lane.events,this.playhead,this.live);
      const git=gitLaneSummary(lane.issue,this.gitEvidence.get(lane.issue.id),this.live);
      card.dataset.issueId=lane.issue.id;card.setAttribute('aria-label',state.title+' · '+lane.issue.id);
      card.append(node('strong',state.title===lane.issue.id?'Title unavailable':state.title),node('span',episode?'Work episode · '+display(episode.start):'Working now · start unknown'),node('small',episode?.endUnknown?episode.currentStatus+' now · end time unknown; last recorded working state':episode?.end!==null&&episode?.end!==undefined?'Ended '+display(episode.end)+' · '+episode.endStatus:(this.live?lane.issue.status+' · current state':state.status+' · as of playhead')));
      if(git){card.append(node('small',git.changes),node('small',git.integration));card.title=git.basis;card.dataset.gitEvidence='validated';}
      card.addEventListener('click',()=>this.onSelect(lane.issue.id,null));card.addEventListener('focus',()=>this.invalidate());overlay.append(card);this.labelNodes.push({node:card,issueId:lane.issue.id,featured:featured.has(entry),pos:[Math.max(-12,pos[0]),pos[1]+.5,pos[2]],time:Math.max(this.from,stamp(lane.issue.createdAt)??this.from)});
    }
    // A small selected-lane annotation budget keeps the reference-style captions
    // informative without filling dense scenes with overlapping text.
    const annotated=timelineAnnotations(this.scene?.markers||[],this.selected);
    for(const marker of annotated){
      const event=marker.event,caption=eventKindLabel(event.kind);
      const label=node('button',(event.kind==='comment'?'◌ ':'◇ ')+new Date(event.time).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})+' · '+caption,'event-annotation');
      label.style.setProperty('--event-color',marker.color);label.title=new Date(event.time).toISOString()+' · '+(event.text?.slice(0,500)||caption);
      label.setAttribute('aria-label',caption+' at '+display(event.time)+'. '+(event.text||'').slice(0,160));
      remember(label,JSON.stringify(['marker',marker.issueId,event.id]));
      label.addEventListener('click',()=>this.pick(marker.issueId,event));label.addEventListener('focus',()=>this.invalidate());
      overlay.append(label);this.labelNodes.push({node:label,pos:[marker.pos[0],marker.pos[1]+.55,marker.pos[2]],time:event.time,annotation:true,priority:event.kind==='status'?(event.after==='closed'?4:3):1.5});
    }
    for(const marker of this.scene?.markers||[]){
      if(marker.event.kind!=='current'||marker.event.status!=='blocked')continue;
      const label=node('button','Ⅱ','waiting-marker');
      label.setAttribute('aria-label',marker.issueId+' is blocked · waiting now. Inspect current state.');
      label.title='Blocked · waiting now. Transition time unknown.';
      remember(label,JSON.stringify(['waiting',marker.issueId]));
      label.addEventListener('click',()=>this.pick(marker.issueId,marker.event));label.addEventListener('focus',()=>this.invalidate());
      overlay.append(label);this.labelNodes.push({node:label,pos:[marker.pos[0],marker.pos[1]+.5,marker.pos[2]],time:marker.event.time,priority:1.8});
    }
    for(const target of this.targetLabels||[]){
      const label=node('span',target.text,'time-label');label.title='Issue status activity spine. Returning on closure does not itself prove a Git merge.';overlay.append(label);
      this.labelNodes.push({node:label,pos:target.pos,time:null});
    }
    for(let i=0;i<=6;i++){
      const time=this.from+(this.to-this.from)*i/6,label=node('span',new Date(time).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'}),'time-label');
      label.title=new Date(time).toISOString();
      overlay.append(label);this.labelNodes.push({node:label,pos:[-12+i*4,this.scene.floor,2],time:null});
    }
    const now=node('span','NOW','now-label');overlay.append(now);this.labelNodes.push({node:now,pos:[this.scene.nowX,this.scene.ceiling,0],time:null});
    const list=$('timeline-event-list');list.replaceChildren();
    for(const lane of this.visibleLanes){
      const item=node('li',''),button=node('button',lane.issue.id+' · '+stateAt(lane.issue,lane.events,this.playhead,this.live).title);
      remember(button,JSON.stringify(['issue',lane.issue.id]));
      button.addEventListener('click',()=>this.onSelect(lane.issue.id,null));item.append(button);
      const events=this.episodeEvents(lane).filter(e=>e.time>=this.from&&e.time<=Math.min(this.to,this.playhead)&&($('timeline-kind').value==='all'||e.kind===$('timeline-kind').value));
      for(const e of events.slice(-100)){
        const b=node('button',display(e.time)+' · '+eventKindLabel(e.kind)+' · '+e.text.slice(0,160),'event-list-item');
        b.title=new Date(e.time).toISOString()+' · '+e.source+' · '+(e.sourceRevision||'revision not recorded');
        remember(b,JSON.stringify(['event',lane.issue.id,e.id]));
        b.addEventListener('click',()=>this.pick(lane.issue.id,e));item.append(b);
      }
      for(const marker of this.scene.markers.filter(m=>m.issueId===lane.issue.id&&m.event.kind==='current')){
        const e=marker.event,b=node('button','Current observation · '+(e.status?e.status+' · ':'')+e.text,'observation-list-item');
        b.title='Displayed '+new Date(e.time).toISOString()+'; not a source observation or recorded transition time.';
        remember(b,JSON.stringify(['observation',lane.issue.id,e.id]));
        b.addEventListener('click',()=>this.pick(lane.issue.id,e));item.append(b);
      }
      if(events.length>100)item.append(node('p','Showing the latest 100 events for this lane; narrow the range for older events.'));
      list.append(item);
    }
    // Source refreshes and selection rebuild these lists. Restore the same logical
    // control, or the camera when it disappeared; never steal unrelated focus.
    if(restoreFocus)(focusTargets.get(focusKey)||$('timeline-stage')).focus({preventScroll:true});
  }
  revealEvent(id,eventId){
    const lane=this.projection.cache.get(id),event=lane?.displayEvents.find(e=>e.id===eventId);
    if(!event||!Number.isFinite(event.time)||event.time>this.now())return false;
    this.playing=false;
    this.from=Math.min(this.from,event.time-60000);this.to=Math.max(this.to,event.time+60000);
    this.selected=id;this.seek(event.time);
    // Page to the lane by its place in the displayed order, which seek() has just
    // settled: an unfiltered ID-order index would point at a different lane.
    const order=this.filteredLanes().findIndex(l=>l.issue.id===id);
    if(order>=0&&Math.floor(order/LANE_PAGE)*LANE_PAGE!==this.offset){this.offset=Math.floor(order/LANE_PAGE)*LANE_PAGE;this.rebuild();}
    this.syncTimeInputs();this.pick(id,event);this.fit(true,true);
    $('timeline-stage').focus({preventScroll:true});return true;
  }
  pick(id,event){
    const changed=this.pinnedEvent?.id!==event?.id;
    if(changed){this.calloutAnimation?.cancel();this.cancelFilterMotion();}
    this.pinnedIssue=id;this.pinnedEvent=event;this.onSelect(id,event);if(event)$('timeline-tooltip').hidden=true;
    const callout=$('timeline-callout');callout.hidden=!event;callout.replaceChildren();
    this.invalidate();
    if(!event)return;
    const statusChanges=eventStatusChanges(event);
    const target=statusChanges[0]?.after;
    const statusColor=target&&statusChanges.every(change=>change.after===target)?statusColors[target]||statusColors.unknown:null;
    const calloutColor=statusColor||'#23c7ff';
    callout.style.setProperty('--callout-color',calloutColor);
    $('timeline-callout-leader').style.setProperty('--callout-color',calloutColor);
    const close=node('button','×','callout-close');close.setAttribute('aria-label','Dismiss pinned event');
    close.addEventListener('click',()=>{this.calloutAnimation?.cancel();this.cancelFilterMotion();this.pinnedEvent=null;callout.hidden=true;this.invalidate();this.onSelect(id,null);$('timeline-stage').focus({preventScroll:true});});
    const identity=node('div',null,'callout-identity'),badge=node('span',event.kind==='comment'?authorInitials(event.author):({status:'↔',labels:'#',notes:'≡',cluster:String(event.members?.length||0)}[event.kind]||'•'),'author-initials');
    badge.setAttribute('aria-hidden','true');
    const heading=node('div',null,'callout-heading');
    const timestamp=node('time',display(event.time));timestamp.dateTime=new Date(event.time).toISOString();timestamp.title=timestamp.dateTime;
    const metadata=node('small','');metadata.append(timestamp,document.createTextNode(' · '+id+' · '+eventKindLabel(event.kind)));
    heading.append(node('strong',event.kind==='current'?(event.source==='git'?'Git containment':'Current issue state'):event.kind==='comment'?(event.author||'Author not recorded'):eventKindLabel(event.kind)),metadata);
    identity.append(badge,heading);
    callout.append(close,node('span','Event details','callout-kicker'),identity);
    if(event.kind==='cluster'&&statusChanges.length){
      const changes=node('section',null,'callout-status-changes');
      changes.append(node('strong',statusChanges.length===1?'Status change':'Status changes'));
      for(const change of statusChanges){
        const row=node('p',`${change.before} → ${change.after}`);
        row.style.setProperty('--status-color',statusColors[change.after]||statusColors.unknown);
        changes.append(row);
      }
      callout.append(changes);
    }
    const comments=event.members?.filter(member=>member.kind==='comment')||[];
    const summary=event.kind==='cluster'?`${event.members?.length||0} changes · ${comments.length} comment${comments.length===1?'':'s'} · ${statusChanges.length} status`:commentPreview(event.text,180);
    callout.append(node('p',summary),node('small',event.kind==='git'?event.provenance:['status','labels','notes'].includes(event.kind)?'Recorded at this time; exact edit time and author may be unknown':event.kind==='current'?(event.provenance||'Current state; transition time unknown'):event.kind==='cluster'?'Grouped issue changes':'New comment'));
    if(comments.length){
      const previews=node('section',null,'callout-comments');previews.setAttribute('aria-label','Comment previews');
      previews.tabIndex=0;
      for(const comment of comments.slice(0,1)){
        const preview=node('article',null,'callout-comment');
        preview.append(node('strong',comment.author||'Author not recorded'),node('p',commentPreview(comment.text,120)));
        previews.append(preview);
      }
      callout.append(previews,node('small',comments.length>1?`${comments.length-1} more comments · full text in Activity`:'Full text in Activity'));
    }
    if(changed&&!this.reduced()&&!document.hidden&&this.visible){
      this.calloutAnimation=callout.animate([{opacity:0,transform:'translateY(7px)'},{opacity:1,transform:'translateY(0)'}],{duration:240,easing:'cubic-bezier(.2,.75,.25,1)'});
    }

  }
  updateTransport(){
    this.updateSearchCoverage();
    $('timeline-range-mode').textContent=this.live?(this.liveHours!==null?'Rolling window · follows now':'Fixed start → now'):'Historical range · paused';
    if(document.activeElement!==$('timeline-hours'))$('timeline-hours').value=this.liveHours??24;
    $('timeline-play').textContent=this.playing?'Pause':'Play';
    $('timeline-play').dataset.playing=String(this.playing);
    $('timeline-live').textContent=this.live?'● Live':`Return to live${this.newEvents?' · '+this.newEvents+' issue updates':''}`;
    $('timeline-asof').textContent=(this.live?'Live · ':'As of · ')+display(this.playhead)+' · '+Intl.DateTimeFormat().resolvedOptions().timeZone;
    $('timeline-scrub').value=clamp((this.playhead-this.from)/Math.max(1,this.to-this.from)*1000,0,1000);
    const counts={};for(const lane of this.visibleLanes||[]){const status=stateAt(lane.issue,lane.events,this.playhead,this.live).status;counts[status]=(counts[status]||0)+1;}
    const countsText='Visible lanes · '+Object.entries(counts).map(([s,n])=>n+' '+s).join(' · ');
    const incomplete=!this.live&&Object.values(this.metadata||{}).some(Boolean);
    $('timeline-counts').textContent=(incomplete?'Historical metadata: unknown values · ':'')+countsText;
    $('timeline-counts').title=incomplete?'Historical metadata uses loaded snapshots only; missing or conflicting values are unknown, never copied from today. '+countsText:countsText;
    this.drawMinimap();
  }
  drawMinimap(){
    const canvas=$('timeline-minimap'),rect=canvas.getBoundingClientRect(),ctx=canvas.getContext('2d'),w=Math.max(1,Math.round(rect.width)),h=54;
    if(canvas.width!==w)canvas.width=w;canvas.height=h;ctx.clearRect(0,0,w,h);
    const lanes=this.visibleLanes||[],kind=$('timeline-kind').value;this.minimapEvents=[];
    lanes.forEach((lane,i)=>{const y=8+(i+.5)*38/Math.max(1,lanes.length);ctx.strokeStyle=lane.color;ctx.globalAlpha=.45;ctx.beginPath();for(const episode of this.episodes(lane)){const left=Math.max(this.from,episode.start),right=Math.min(this.to,episode.end??this.now());if(right<left)continue;ctx.moveTo((left-this.from)/(this.to-this.from)*w,y);ctx.lineTo((right-this.from)/(this.to-this.from)*w,y);}ctx.stroke();ctx.globalAlpha=1;
      const episodes=this.episodes(lane),candidates=this.episodeEvents(lane).filter(e=>e.time>=this.from&&e.time<=this.to&&e.time<=this.now()&&(kind==='all'||e.kind===kind)),isEntry=e=>isInitialWorkEntry(e,episodes);
      for(const e of [...candidates.filter(isEntry),...clusterEvents(candidates.filter(e=>!isEntry(e)),this.from,this.to,80)]){const x=(e.time-this.from)/(this.to-this.from)*w;ctx.fillStyle=e.status?statusColors[e.status]||lane.color:lane.color;ctx.fillRect(x-2,y-2,4,4);this.minimapEvents.push({x:x/w,y:y/h,issueId:lane.issue.id,event:e});}});
    ctx.strokeStyle='#e3f5ff';const x=clamp((this.playhead-this.from)/(this.to-this.from)*w,0,w);ctx.beginPath();ctx.moveTo(x,0);ctx.lineTo(x,h);ctx.stroke();
  }
  syncTimeInputs(){$('timeline-from').value=localInput(this.from);$('timeline-to').value=localInput(this.to);}
  saveRange(){
    const url=new URL(location.href);if(this.liveHours!==null)url.searchParams.set('hours',String(this.liveHours));else url.searchParams.delete('hours');url.searchParams.set('from',new Date(this.from).toISOString());url.searchParams.set('to',new Date(this.to).toISOString());
    if(this.live){url.searchParams.delete('at');url.searchParams.delete('to');}else url.searchParams.set('at',new Date(this.playhead).toISOString());history.replaceState(null,'',url);
    const rangeKey=JSON.stringify(this.live?['live',this.liveHours,this.liveHours===null?this.from:null]:[this.from,this.to]);
    if(rangeKey!==this.lastNotifiedRange){this.lastNotifiedRange=rangeKey;document.dispatchEvent(new Event('timeline-range-change'));}
  }
  seek(time){
    this.topologyObservations.clear();this.cancelArrivals();
    this.live=false;this.playhead=clamp(time,this.from,this.to);this.transition=null;this.rebuild();this.onPlayback();this.saveRange();
  }
  returnLive(){
    this.live=true;this.playing=false;this.newEvents=0;this.playhead=this.now();this.to=this.playhead;
    if(this.from>=this.to)this.from=this.to-3600000;
    this.rebuild();this.syncTimeInputs();this.onPlayback(true);this.saveRange();
  }
  tickNow(){if(this.live&&!this.minimapBrush){this.to=this.now();this.playhead=this.to;this.rebuild();this.syncTimeInputs();}}
  cancelRangeBrush(){
    const brush=this.minimapBrush;this.minimapBrush=null;
    $('timeline-range-brush').hidden=true;
    if(brush){this.ignoreMinimapClick=true;const map=$('timeline-minimap');if(map.hasPointerCapture(brush.id))map.releasePointerCapture(brush.id);}
  }
  applyRange(from,to){
    const pinned=this.pinnedEvent,issueId=this.pinnedIssue,changed=from!==this.from||to!==this.to;
    this.liveHours=null;this.from=from;this.to=to;this.playing=false;this.seek(Math.min(to,this.now()));
    if(pinned&&pinned.kind!=='current'&&pinned.time>=from&&pinned.time<=this.playhead){
      const event=this.projection.cache.get(issueId)?.displayEvents.find(e=>e.id===pinned.id)||this.scene.markers.find(m=>m.issueId===issueId&&m.event.id===pinned.id)?.event;
      if(event){this.pick(issueId,event);this.calloutAnimation?.cancel();}
    }
    this.syncTimeInputs();this.fit();if(changed)this.fadeFilteredScene();
  }
  // Stretching multiplies world size, so the reachable camera distance stretches with
  // it. At 1x this is the original bound, and Fit view at full stretch stays inside it.
  maxDistance(){return BASE_MAXIMUM_DISTANCE*Math.max(1,this.axisScale[0],this.axisScale[1]);}
  viewCamera(){return {...this.camera,...(this.renderer.gl?{}:{yaw:0,pitch:0,perspective:0}),axisScale:this.axisScale};}
  saveCamera(){
    const value=JSON.stringify(this.camera);
    if(value===this.cameraSaveValue)return;
    this.cameraSaveValue=value;clearTimeout(this.cameraSaveTimer);
    this.cameraSaveTimer=setTimeout(()=>{
      if(savedTimelineCamera(value,this.mode,this.maxDistance()))savePreference('abacus.timeline.camera',value);
    },250);
  }
  shareMode(){
    const url=new URL(location.href);url.searchParams.set('camera',this.mode);history.replaceState(null,'',url);
  }
  applyMode(animate=true){
    $('timeline-3d').setAttribute('aria-pressed',String(this.mode==='3d'));
    $('timeline-2d').setAttribute('aria-pressed',String(this.mode==='2d'));
    const destination={...this.camera,perspective:this.mode==='3d'?1:0,yaw:this.mode==='3d'?.15:0,pitch:this.mode==='3d'?.28:0};
    $('timeline-renderer').textContent=this.renderer.gl?(this.mode==='3d'?'WebGL · perspective camera':'WebGL · orthographic camera'):'WebGL unavailable · accessible 2D fallback';
    this.moveCamera(destination,animate);savePreference('abacus.timeline.mode',this.mode);
  }
  finishCameraTransition(){
    this.cancelArrivals();
    if(!this.transition)return;
    this.camera={...this.transition.to,target:[...this.transition.to.target]};this.transition=null;
  }
  moveCamera(destination,animate=true){
    if(!animate||this.reduced()){this.camera=destination;this.transition=null;}
    else this.transition={from:{...this.camera,target:[...this.camera.target]},to:destination,start:performance.now(),duration:450};
    this.invalidate();
  }
  fit(animate=true,selectedOnly=false){
    const lanes=selectedOnly?this.visibleLanes.filter(l=>l.issue.id===this.selected):this.visibleLanes;
    if(!lanes?.length)return;
    const points=(this.scene?.paths||[]).filter(p=>!selectedOnly||p.issueId===this.selected).flatMap(p=>p.points);
    points.push(...(this.scene?.markers||[]).filter(m=>!selectedOnly||m.issueId===this.selected).map(m=>m.pos));const ys=points.map(p=>p[1]);
    const low=Math.min(0,...ys),high=Math.max(2.4,...ys),mid=(low+high)/2,height=high-low+5;
    const rect=$('timeline-stage').getBoundingClientRect(),aspect=Math.max(.4,rect.width/Math.max(1,rect.height));
    this.moveCamera({...this.camera,target:[-2,mid,0],distance:Math.max(12,height*this.axisScale[1],36*this.axisScale[0]/aspect)},animate);
  }
  invalidate(){if(!this.visible||document.hidden||this.frame!==null)return;this.frame=requestAnimationFrame(t=>this.draw(t));}
  draw(time){
    this.frame=null;if(!this.visible||document.hidden)return;
    if(this.transition){
      const a=this.transition,t=clamp((time-a.start)/a.duration,0,1),e=t*t*(3-2*t);
      for(const k of ['yaw','pitch','distance','perspective'])this.camera[k]=a.from[k]+(a.to[k]-a.from[k])*e;
      this.camera.target=a.from.target.map((v,i)=>v+(a.to.target[i]-v)*e);if(t===1)this.transition=null;
    }
    if(this.playing){
      const delta=Math.min(100,time-(this.lastPlayback||time));this.lastPlayback=time;
      this.playhead=Math.min(this.to,this.playhead+delta*Number($('timeline-speed').value));
      if(this.playhead>=this.to)this.playing=false;
      if(!this.playing||this.playhead>=this.nextEpisodeBoundary||!this.lastTransport||time-this.lastTransport>200){
        this.lastTransport=time;
        const filtered=this.filteredLanes(),offset=clamp(this.offset,0,Math.max(0,filtered.length-1));
        const ids=lanes=>lanes.map(l=>l.issue.id).join('|');
        if(this.playhead>=this.nextEpisodeBoundary||filtered.length!==this.filteredCount||ids(filtered.slice(offset,offset+LANE_PAGE))!==ids(this.visibleLanes))this.rebuild();
        else{this.updateTransport();this.buildLabels();}
        // Advancing the playhead is not a change of playback context: the pinned
        // event and its URL survive so a selected node can be watched through it.
        this.onPlayback(false,false,true);if(!this.playing)this.saveRange();
      }
    }
    const camera=this.viewCamera();
    $('timeline-callout').hidden=!this.pinnedEvent||(!this.live&&(this.pinnedEvent.kind==='current'||this.pinnedEvent.time>this.playhead));
    const marker=this.pinnedEvent&&this.scene.markers.find(m=>m.issueId===this.pinnedIssue&&(m.event.id===this.pinnedEvent.id||m.event.members?.some(e=>e.id===this.pinnedEvent.id)));
    this.renderer.draw(camera,this.live?100:this.timeX(this.playhead),marker);
    $('timeline-stage').dataset.frames=String(this.renderer.frames);$('timeline-stage').dataset.uploads=String(this.renderer.uploads);
    $('timeline-stage').dataset.arrivalDrawRanges=String(this.renderer.arrivalRanges?.length||0);
    $('timeline-stage').dataset.glowArrivalDrawRanges=String(this.renderer.glowArrivalRanges?.length||0);
    $('timeline-stage').dataset.camera=JSON.stringify(camera);
    this.saveCamera();
    const rect=$('timeline-stage').getBoundingClientRect();
    const callout=$('timeline-callout'),leader=$('timeline-callout-leader');
    const anchor=marker?projectPoint(marker.pos,camera,rect.width,rect.height):null;
    const bubble=callout.hidden?null:eventBubblePlacement(anchor,callout.offsetWidth,callout.offsetHeight,rect.width,rect.height);
    leader.style.display=bubble?'block':'none';
    callout.dataset.anchored=String(!!bubble);
    callout.style.left=bubble?`${bubble.x}px`:'';callout.style.top=bubble?`${bubble.y}px`:'';
    if(bubble){leader.setAttribute('viewBox',`0 0 ${rect.width} ${rect.height}`);leader.firstElementChild.setAttribute('d',bubble.path);}
    // Read sizes before writes. Visibility (rather than display:none) keeps
    // culled dimensions measurable after resizing without per-frame layout churn.
    const candidates=[];
    for(const label of this.labelNodes||[]){
      // A lane caption the needle is not reporting on never competes for space,
      // unless it holds keyboard focus: culling the focused card would move focus
      // off it, so a needle that drifts past a lane would silently steal focus.
      const focused=label.node===document.activeElement;
      if(!focused&&label.featured===false)continue;
      const p=projectPoint(label.pos,camera,rect.width,rect.height);
      if(!p||p.x<0||p.x>rect.width||p.y<0||p.y>rect.height||(label.time!==null&&label.time>this.playhead))continue;
      const width=label.node.offsetWidth,height=label.node.offsetHeight;
      const x=clamp(p.x-(label.issueId?width+18:0),8,Math.max(8,rect.width-width-8)),y=clamp(p.y-(label.issueId?height/2:0),0,Math.max(0,rect.height-height));
      const alternatives=label.annotation?[[p.x-width-12,y],[p.x,p.y+22],[p.x-width-12,p.y+22],[p.x,p.y-height-14],[p.x-width-12,p.y-height-14]].map(([ax,ay])=>({x:clamp(ax,8,Math.max(8,rect.width-width-8)),y:clamp(ay,0,Math.max(0,rect.height-height))})):[];
      // A captioned lane is already a deliberate, scarce choice, so it outranks the
      // event annotations on its own branch; those have alternative placements and
      // it does not. Otherwise selecting an issue could caption nothing at all.
      candidates.push({label,x,y,width,height,alternatives,priority:focused?5:label.featured?4.5:label.priority??0});
    }
    const placed=new Map(nonOverlappingLabels(candidates).map(c=>[c.label,c]));
    for(const label of this.labelNodes||[]){
      const placement=placed.get(label);
      label.node.style.visibility=placement?'visible':'hidden';
      label.node.style.opacity=String(this.renderer.arrivalOpacity({arrival:this.laneArrivals.get(label.issueId)}));
      if(label.issueId)label.node.tabIndex=placement?0:-1;
      if(placement)label.node.style.transform=`translate(${placement.x}px,${placement.y+(label.issueId?placement.height:0)}px)`;
    }
    $('timeline-stage').dataset.culledLabels=String((this.labelNodes?.length||0)-placed.size);
    for(const [id,start] of this.connectorArrivals)if(performance.now()-start>=350)this.connectorArrivals.delete(id);
    $('timeline-stage').dataset.connectorArrivals=String(this.connectorArrivals.size);
    for(const [id,start] of this.laneArrivals)if(performance.now()-start>=350)this.laneArrivals.delete(id);
    $('timeline-stage').dataset.laneArrivals=String(this.laneArrivals.size);
    for(const [id,start] of this.arrivals)if(performance.now()-start>=350)this.arrivals.delete(id);
    for(const [id,change] of this.statusChanges)if(performance.now()-change.start>=350)this.statusChanges.delete(id);
    $('timeline-stage').dataset.statusTransitions=String(this.scene.markers.filter(m=>this.renderer.colorMix(m)>0).length);
    $('timeline-stage').dataset.clusterArrivals=String(this.scene.markers.filter(m=>m.event.kind==='cluster'&&this.renderer.arrivalOpacity(m)<1).length);
    $('timeline-stage').dataset.arrivals=String(this.scene.markers.filter(m=>this.renderer.arrivalOpacity(m)<1).length);
    if(this.playing||this.transition||this.renderer.activeArrivals())this.invalidate();
  }
  bindControls(){
    const options=document.querySelector('.view-options'),menu=options.querySelector('.view-options-content'),workspace=$('timeline-pane').closest('.workspace');
    const positionOptions=()=>{
      if(!options.open)return;
      const anchor=options.getBoundingClientRect(),bounds=workspace.getBoundingClientRect(),width=menu.getBoundingClientRect().width;
      let left=bounds.left+8,right=bounds.right-8;
      if(right-left<width){left=16;right=innerWidth-16;}
      menu.style.left=Math.min(Math.max(0,left-anchor.left),right-anchor.left-width)+'px';
    };
    options.addEventListener('toggle',positionOptions);
    window.addEventListener('resize',positionOptions);
    workspace.addEventListener('scroll',positionOptions,{passive:true});
    new ResizeObserver(positionOptions).observe(workspace);
    $('timeline-maximize').addEventListener('click',()=>{this.panelMaximized=!this.panelMaximized;this.cancelRangeBrush();this.syncPanelSize();this.invalidate();});
    window.addEventListener('resize',()=>this.syncPanelSize());
    $('timeline-pane').addEventListener('keydown',e=>{if(e.key==='Escape'&&this.panelMaximized){this.panelMaximized=false;this.syncPanelSize();$('timeline-maximize').focus();this.invalidate();}});
    $('timeline-glow').addEventListener('click',()=>{
      this.renderer.glow=!this.renderer.glow;const value=this.renderer.glow?'on':'off';
      $('timeline-glow').setAttribute('aria-pressed',String(this.renderer.glow));document.documentElement.dataset.glow=value;
      savePreference('abacus.timeline.glow',value);this.invalidate();
    });
    $('timeline-3d').addEventListener('click',()=>{if(!this.renderer.gl){$('timeline-renderer').textContent='WebGL unavailable · 2D and event list remain usable';return;}this.mode='3d';this.applyMode();this.shareMode();});
    $('timeline-2d').addEventListener('click',()=>{this.mode='2d';this.applyMode();this.shareMode();});
    const syncScale=()=>['time','vertical'].forEach((axis,i)=>{
      $('timeline-scale-'+axis).value=this.axisScale[i];
      $('timeline-scale-'+axis+'-value').textContent=this.axisScale[i].toFixed(2)+'×';
    });
    syncScale();
    ['time','vertical'].forEach((axis,i)=>$('timeline-scale-'+axis).addEventListener('input',()=>{
      const value=Number($('timeline-scale-'+axis).value);
      if(!Number.isFinite(value)||value<.25||value>MAXIMUM_STRETCH)return;
      this.finishCameraTransition();this.axisScale[i]=value;
      // Shrinking the scene must not strand the camera beyond its new reach.
      this.camera.distance=clamp(this.camera.distance,5,this.maxDistance());
      savePreference('abacus.timeline.scale.'+axis,String(value));syncScale();this.invalidate();
    }));
    $('timeline-scale-reset').addEventListener('click',()=>{
      this.finishCameraTransition();this.axisScale=[1,1,1];
      this.camera.distance=clamp(this.camera.distance,5,this.maxDistance());
      ['time','vertical'].forEach(axis=>savePreference('abacus.timeline.scale.'+axis,'1'));
      syncScale();this.invalidate();
    });
    $('timeline-camera').addEventListener('change',()=>savePreference('abacus.timeline.control',$('timeline-camera').value));
    $('timeline-fit').addEventListener('click',()=>this.fit());$('timeline-focus').addEventListener('click',()=>this.fit(true,true));
    $('timeline-motion').addEventListener('change',()=>{this.motion=$('timeline-motion').value;document.documentElement.dataset.motion=this.motion;savePreference('abacus.motion',this.motion);if(this.reduced()){this.finishCameraTransition();this.calloutAnimation?.cancel();this.cancelFilterMotion();}this.invalidate();});
    $('timeline-kind').addEventListener('change',()=>{const kind=$('timeline-kind').value;if(kind===this.lastEventKind)return;this.lastEventKind=kind;this.rebuild();this.fadeFilteredScene();});
    $('timeline-prev').addEventListener('click',()=>{this.offset=Math.max(0,this.offset-LANE_PAGE);this.rebuild();this.fit();});
    $('timeline-next').addEventListener('click',()=>{this.offset+=LANE_PAGE;this.rebuild();this.fit();});
    $('timeline-live').addEventListener('click',()=>this.returnLive());
    $('timeline-play').addEventListener('click',()=>{if(this.playing)this.pausePlayback();else{if(this.live||this.playhead>=this.to)this.seek(this.from);this.playing=true;this.lastPlayback=performance.now();}this.updateTransport();this.invalidate();});
    $('timeline-scrub').addEventListener('input',()=>{this.playing=false;this.seek(this.from+(this.to-this.from)*Number($('timeline-scrub').value)/1000);});
    const minimap=$('timeline-minimap');
    minimap.addEventListener('pointerdown',e=>{
      if(e.button!==0||this.minimapBrush)return;
      const rect=minimap.getBoundingClientRect();this.ignoreMinimapClick=false;
      this.minimapBrush={id:e.pointerId,start:e.clientX-rect.left,left:rect.left,width:rect.width,from:this.from,to:this.to};
      minimap.setPointerCapture(e.pointerId);
    });
    minimap.addEventListener('pointermove',e=>{
      const b=this.minimapBrush;if(!b||b.id!==e.pointerId)return;
      const x=clamp(e.clientX-b.left,0,b.width),start=clamp(b.start,0,b.width),overlay=$('timeline-range-brush');
      overlay.hidden=Math.abs(x-start)<6;
      overlay.style.left=100*Math.min(x,start)/b.width+'%';overlay.style.width=100*Math.abs(x-start)/b.width+'%';
      if(!overlay.hidden)this.pausePlayback();
    });
    minimap.addEventListener('pointerup',e=>{
      const b=this.minimapBrush;if(!b||b.id!==e.pointerId)return;
      const range=brushTimeRange(b.from,b.to,b.start,e.clientX-b.left,b.width);
      this.minimapBrush=null;$('timeline-range-brush').hidden=true;
      if(minimap.hasPointerCapture(e.pointerId))minimap.releasePointerCapture(e.pointerId);
      this.ignoreMinimapClick=!!range;if(range)this.applyRange(range.from,range.to);
    });
    minimap.addEventListener('pointercancel',()=>this.cancelRangeBrush());
    minimap.addEventListener('lostpointercapture',()=>this.cancelRangeBrush());
    document.addEventListener('keydown',e=>{if(e.key==='Escape'&&this.minimapBrush){e.preventDefault();this.cancelRangeBrush();}});
    $('timeline-minimap').addEventListener('click',e=>{
      if(this.ignoreMinimapClick){this.ignoreMinimapClick=false;return;}
      const rect=e.currentTarget.getBoundingClientRect(),x=e.clientX-rect.left,y=e.clientY-rect.top;
      // The compact displayed map is shorter than its backing canvas; keep its
      // event hit area usable across the visible lane thickness.
      let picked=null,distance=Math.max(8,rect.height*.4);
      for(const marker of this.minimapEvents||[]){const d=Math.hypot(x-marker.x*rect.width,y-marker.y*rect.height);if(d<distance){picked=marker;distance=d;}}
      this.playing=false;
      this.seek(picked?picked.event.time:this.from+(this.to-this.from)*clamp(x/rect.width,0,1));
      if(picked)this.pick(picked.issueId,picked.event);
    });
    $('timeline-live-range').addEventListener('submit',e=>{
      e.preventDefault();const hours=Number($('timeline-hours').value);
      if(!Number.isFinite(hours)||hours<.01||hours>87600)return;
      this.liveHours=hours;this.returnLive();this.fit();
    });
    $('timeline-range').addEventListener('submit',e=>{e.preventDefault();const from=stamp($('timeline-from').value),to=stamp($('timeline-to').value);
      if(from===null||to===null||to<=from){$('timeline-renderer').textContent='Choose a valid increasing time range.';return;}
      this.applyRange(from,to);
    });
    // Range shortcuts use all loaded lanes, not only the filtered/paged viewport.
    const availableTimes=()=>{const now=this.now();return this.lanes.flatMap(l=>l.events.map(e=>e.time)).filter(t=>Number.isFinite(t)&&t<=now);};
    $('timeline-expand').addEventListener('click',()=>{
      const now=this.now(),times=availableTimes();
      if(!times.length){$('timeline-renderer').textContent='No dated events are available to set the range.';return;}
      this.from=times.reduce((min,t)=>Math.min(min,t),now);
      if(this.from>=now){$('timeline-renderer').textContent='The first event is at now; there is no elapsed range yet.';return;}
      this.liveHours=null;this.to=now;this.returnLive();this.fit();this.fadeFilteredScene();
    });
    $('timeline-event-span').addEventListener('click',()=>{
      const times=availableTimes();
      if(!times.length){$('timeline-renderer').textContent='No dated events are available to set the range.';return;}
      const from=times.reduce((min,t)=>Math.min(min,t),Infinity),to=times.reduce((max,t)=>Math.max(max,t),-Infinity);
      if(from>=to){$('timeline-renderer').textContent='Only one dated event time is available; choose a wider range.';return;}
      this.applyRange(from,to);
    });
    $('timeline-fullscreen').addEventListener('click',async()=>{try{if(document.fullscreenElement)await document.exitFullscreen();else await document.querySelector('main').requestFullscreen();}catch{$('timeline-renderer').textContent='Fullscreen unavailable in this browser.';}});
    const stage=$('timeline-stage');let drag=null;const pointers=new Map();
    stage.addEventListener('pointerdown',e=>{if(e.target.closest('button, #timeline-callout'))return;this.transition=null;stage.setPointerCapture(e.pointerId);pointers.set(e.pointerId,[e.clientX,e.clientY]);drag={x:e.clientX,y:e.clientY,startX:e.clientX,startY:e.clientY,moved:false};});
    stage.addEventListener('pointermove',e=>{
      if(!pointers.has(e.pointerId)){if(!e.target.closest('#timeline-callout'))this.hover(e);return;}
      const old=[...pointers.values()];pointers.set(e.pointerId,[e.clientX,e.clientY]);
      if(pointers.size===2){const next=[...pointers.values()],distance=a=>Math.hypot(a[0][0]-a[1][0],a[0][1]-a[1][1]);this.camera.distance=clamp(this.camera.distance*distance(old)/Math.max(1,distance(next)),5,this.maxDistance());drag.moved=true;this.invalidate();return;}
      if(!drag)return;
      // Let the browser own vertical touch scrolling without tilting the camera first.
      if(e.pointerType==='touch'&&Math.abs(e.clientY-drag.startY)>=Math.abs(e.clientX-drag.startX))return;
      const dx=e.clientX-drag.x,dy=e.clientY-drag.y;drag.moved ||= Math.hypot(e.clientX-drag.startX,e.clientY-drag.startY)>4;
      if(e.shiftKey||e.buttons===4||$('timeline-camera').value==='pan'||this.mode==='2d'){
        this.camera.target[0]-=dx*this.camera.distance/stage.clientHeight/this.axisScale[0];this.camera.target[1]+=dy*this.camera.distance/stage.clientHeight/this.axisScale[1];
      }else{this.camera.yaw=clamp(this.camera.yaw-dx*.005,-1.2,1.2);this.camera.pitch=clamp(this.camera.pitch+dy*.005,-1.15,1.15);}
      drag.x=e.clientX;drag.y=e.clientY;this.invalidate();
    });
    const release=e=>{pointers.delete(e.pointerId);if(drag&&!drag.moved){const pick=this.hit(e);if(pick)this.pick(pick.issueId,pick.event);}drag=null;};
    stage.addEventListener('pointerup',release);stage.addEventListener('pointercancel',()=>{pointers.clear();drag=null;});
    stage.addEventListener('wheel',e=>{if(!e.ctrlKey&&!e.metaKey)return;e.preventDefault();this.transition=null;this.camera.distance=clamp(this.camera.distance*Math.exp(clamp(e.deltaY,-200,200)*.002),5,this.maxDistance());this.invalidate();},{passive:false});
    stage.addEventListener('keydown',e=>{
      if(e.target!==stage)return;
      if(e.key==='Escape'){$('timeline-tooltip').hidden=true;return;}
      if(e.key.toLowerCase()==='f'){e.preventDefault();this.fit();return;}
      if(!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','+','-','='].includes(e.key))return;
      e.preventDefault();this.transition=null;
      const direction=e.key==='ArrowLeft'||e.key==='ArrowDown'?-1:1;
      if(['+','-','='].includes(e.key))this.camera.distance=clamp(this.camera.distance*(e.key==='-'?1.1:.9),5,this.maxDistance());
      else if(e.shiftKey||this.mode==='2d')this.camera.target[e.key==='ArrowLeft'||e.key==='ArrowRight'?0:1]+=direction*.6;
      else if(e.key==='ArrowLeft'||e.key==='ArrowRight')this.camera.yaw=clamp(this.camera.yaw+direction*.06,-1.2,1.2);
      else this.camera.pitch=clamp(this.camera.pitch+direction*.06,-1.15,1.15);
      this.invalidate();
    });
    stage.addEventListener('pointerleave',()=>{$('timeline-tooltip').hidden=true;stage.style.cursor='';});
  }
  hit(e){
    if(!this.scene)return null;
    const rect=$('timeline-stage').getBoundingClientRect(),camera=this.viewCamera(),x=e.clientX-rect.left,y=e.clientY-rect.top;
    // Keep the hit area in CSS pixels, not world units: distant 3D spheres and
    // 2D fallback dots are equally easy to select without drawing larger nodes.
    // Nearest-center ownership prevents overlapping targets from blocking one another.
    const hitRadius=e.pointerType==='touch'?34:28;
    let best=pickScreenMarker(this.scene.markers.filter(marker=>this.live||marker.event.time<=this.playhead),camera,rect.width,rect.height,x,y,hitRadius);
    if(best)return best;
    let distance=8;
    for(const path of this.scene.paths){
      for(let i=1;i<path.points.length;i++){
        if(!this.live&&path.points[i][0]>this.timeX(this.playhead))break;
        const a=projectPoint(path.points[i-1],camera,rect.width,rect.height),b=projectPoint(path.points[i],camera,rect.width,rect.height);
        if(!a||!b)continue;
        const dx=b.x-a.x,dy=b.y-a.y,t=clamp(((x-a.x)*dx+(y-a.y)*dy)/(dx*dx+dy*dy||1),0,1);
        const d=Math.hypot(x-a.x-dx*t,y-a.y-dy*t);
        if(d<distance){distance=d;best={issueId:path.issueId,event:null};}
      }
    }
    return best;
  }
  hover(e){
    const picked=this.hit(e),tip=$('timeline-tooltip');tip.hidden=!picked;
    $('timeline-stage').style.cursor=picked?'pointer':'';
    if(picked){const event=picked.event;tip.textContent=event?`${picked.issueId} · ${event.kind} · ${display(event.time)} · ${new Date(event.time).toISOString()} · ${event.text?.slice(0,240)}`:`${picked.issueId} · independent issue lane`;
      const rect=$('timeline-stage').getBoundingClientRect();tip.style.left=clamp(e.clientX-rect.left+12,8,Math.max(8,rect.width-290))+'px';tip.style.top=clamp(e.clientY-rect.top+12,8,Math.max(8,rect.height-90))+'px';}
  }
}
