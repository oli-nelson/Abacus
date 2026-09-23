import {bindInspectorResize} from './inspector-resize.js';
bindInspectorResize();
const forceRequests=new Map();
import {Timeline} from './timeline.js';
import {eventKindLabel,prioritizeTimelineHistory} from './timeline-model.js';
import {issuePage} from './issue-table.js';
import {IssueRelations} from './issue-relations.js';
import {DependencyTree} from './dependency-tree.js';
const relations=new IssueRelations();
let relationsKey=null;
const tableUrl=new URL(location.href);
let tableState={sort:tableUrl.searchParams.get('sort')||'id',direction:tableUrl.searchParams.get('order')||'asc',page:Number(tableUrl.searchParams.get('page'))||0,pageSize:Number(tableUrl.searchParams.get('size'))||50};
const $ = id => document.getElementById(id);
async function mutationResponse(response) {
  if (!response.headers.get('content-type')?.includes('application/json'))
    throw new Error(`HTTP ${response.status} without an action result`);
  const result=await response.json();
  if (!result || typeof result.outcome!=='string')
    throw new Error(`HTTP ${response.status} returned an invalid action result`);
  return result;
}
let issuesLoaded=false;
let issues = new Map(), rows = new Map(), source, selected = new URL(location.href).searchParams.get('issue');
let currentInspectorRevision = null, issueRevision = 0;
const workerRequests=new Map();
let stopRunRequest=null,stopRunBusy=false,stopRunAccepted=false;
let runtimeOnline=false,runtimeSession=null,canControlClaims=false,currentRuntime=null,claimRequest=null,claimBusy=false;
let canEdit=false, canResolveAttention=false, operator='Git user', editorId=null;
const drafts=new Map();
const descriptionOpen=new Map();
let labelChoicesKey=null;
let projectLabelsRevision=null,projectLabelChoices=[];
const creation={context:null,populated:false,request:null,pending:false,outcome:null,createdId:null,result:''};
function creationFeedback(){
  const offline=$('source').classList.contains('stale')||!timeline.live||liveReadPending;
  $('create-issue').disabled=offline;
  $('create-actor').textContent=`Stored as ${operator} · no automatic remote push`;
  $('create-fields').disabled=creation.pending||!!creation.request||!creation.context;
  $('create-result').textContent=creation.result;
  $('create-submit').disabled=offline||creation.pending||!creation.context||creation.outcome==='completed';
  $('create-submit').textContent=creation.request?'Retry same request':'Create blocked draft';
  $('create-review').disabled=creation.pending||!!creation.request&&creation.outcome!=='rejected';
  $('create-show').hidden=!creation.createdId;
  $('create-show').disabled=!issues.has(creation.createdId);
  $('create-show').textContent=creation.createdId?'View '+creation.createdId:'View created issue';
  $('create-new').hidden=!creation.request||creation.pending;
}
async function loadCreationPolicy(){
  creation.pending=true;creation.result='Reading target and reasoning policy…';creationFeedback();
  try{
    const response=await fetch('/api/v1/issues/drafts/context');if(!response.ok)throw new Error();
    const policy=await response.json(),previous=creation.populated;
    for(const [id,values,initial] of [['create-type',policy.issueTypes,'task'],['create-target',policy.targets,policy.defaultTarget||''],['create-reasoning',['',...policy.reasoningLabels],'']]){
      const field=$(id),value=previous?field.value:initial;
      field.replaceChildren(...values.map(v=>{const option=text('option',v||'No reasoning label');option.value=v;return option;}));
      if(!values.includes(value)){const option=text('option',value?value+' · not in current policy':'Choose a target');option.value=value;field.prepend(option);}
      field.value=value;
    }
    $('create-reasoning').required=policy.requireReasoning;
    creation.context=policy;creation.populated=true;creation.request=null;creation.outcome=null;
    creation.result='Review the policy and fields. This creates only a blocked draft, not ready work.';
  }catch{creation.context=null;creation.result='Creation policy unavailable. No request was sent. Your input is preserved.';}
  finally{creation.pending=false;creationFeedback();}
}
$('create-issue').addEventListener('click',()=>{
  $('create-dialog').showModal();creationFeedback();
  if(!creation.context&&!creation.pending&&!creation.request)loadCreationPolicy();
});
$('create-close').addEventListener('click',()=>$('create-dialog').close());
$('create-dialog').addEventListener('close',()=>$('create-issue').focus());
$('create-review').addEventListener('click',()=>{
  if(creation.pending||creation.request&&creation.outcome!=='rejected')return;
  loadCreationPolicy();
});
$('create-new').addEventListener('click',()=>{
  if(creation.pending||!confirm('Inspect current issues first. A partial or unknown result may already have created your issue. Start a DIFFERENT draft with empty fields, without retrying the old creation?'))return;
  $('create-form').reset();creation.request=null;creation.context=null;creation.populated=false;creation.createdId=null;creation.outcome=null;
  loadCreationPolicy();
});
$('create-show').addEventListener('click',()=>{
  if(!issues.has(creation.createdId))return;
  $('create-dialog').close();changeView('issues');select(creation.createdId);
});
$('create-form').addEventListener('submit',async event=>{
  event.preventDefault();
  if(creation.pending||!creation.context||!timeline.live||liveReadPending||$('source').classList.contains('stale')||creation.outcome==='completed')return;
  creation.pending=true;creation.result='Pending · creating and verifying a non-ready draft…';creationFeedback();
  try{
    if(!creation.request){
      const response=await fetch('/api/v1/mutations/context');if(!response.ok)throw new Error();const session=await response.json();
      const labels=$('create-labels').value.split('\n').map(v=>v.trim()).filter(Boolean);
      if($('create-reasoning').value)labels.push($('create-reasoning').value);
      const random=[...crypto.getRandomValues(new Uint8Array(16))].map(v=>v.toString(16).padStart(2,'0')).join('');
      creation.request={requestId:`${session.session}:${session.serverUnixMilliseconds}:${random}`,expectedRevision:creation.context.revision,
        title:$('create-name').value,description:$('create-description').value,type:$('create-type').value,
        priority:Number($('create-priority').value),labels,target:$('create-target').value};
    }
    const response=await fetch('/api/v1/issues/drafts',{method:'POST',headers:{'Content-Type':'application/json','X-Abacus-Request':'1'},body:JSON.stringify(creation.request)});
    const result=await response.json();
    if(!['completed','rejected','partially-applied','outcome-unknown'].includes(result.outcome))throw new Error();
    creation.outcome=result.outcome;creation.createdId=result.createdIssueId||null;
    creation.result=`${result.outcome}: ${result.message}`;
    if(creation.createdId)creation.result+=` Issue: ${creation.createdId}. Review this issue rather than creating a duplicate.`;
  }catch{
    creation.result=creation.request?'Result unavailable. Creation may already have succeeded. Retry only this SAME request or inspect current issues before starting a different draft.':'Could not obtain a mutation session; no creation request was sent. Your input is preserved.';
  }finally{creation.pending=false;creationFeedback();}
});
let canReadHistory=false, canReadProjectHistory=false, historyState=null, activityRequest=null, activityKey=null, activityCursor=null, activityLoaded=false;
const timelineHistoryRequests=new Map(),timelineHistoryAttempts=new Map();
let timelineHistoryGeneration='',timelineHistoryPage=0,timelineHistoryRange='';
let projectHistoryRequest=null,projectHistoryAttempt=null,projectHistoryResult=null,projectHistoryFailed=false;
function historyProgress(loading,loaded,total,wholeProject=false){
  const panel=$('history-progress');
  panel.hidden=!loading||view!=='timeline';
  if(panel.hidden)return;
  $('history-progress-label').textContent=`Loading timeline history · ${loaded}/${total} issues`;
  const bar=$('history-progress-bar');
  bar.max=Math.max(1,total);
  // A whole-project response arrives atomically; don't imply that zero means stalled.
  if(wholeProject)bar.removeAttribute('value');
  else bar.value=loaded;
}
// One read covers every issue, so no lane is hidden behind a batch it has not
// loaded yet. The paged reader below stays as the fallback for sources that
// cannot answer a whole-project query.
function scheduleProjectHistory(generation){
  timeline.historyScope=null;
  $('timeline-history-pages').hidden=true;
  for(const r of timelineHistoryRequests.values())r.abort();
  const ordered=[...issues.values()].sort((a,b)=>a.id.localeCompare(b.id));
  // Both revisions matter: the recorded history and the issue snapshot it is fenced
  // against. If either has moved this is a different read; if neither has, a finished
  // read must not be requested again, or a failure would retry in a tight loop.
  const attempt=generation+':'+issueRevision;
  const describe=()=>{
    const loaded=ordered.filter(i=>{const c=timeline.histories.get(i.id);return c?.revision===generation&&c.issueRevision===i.revision;}).length;
    $('timeline-history-state').textContent=`Status history: ${loaded}/${ordered.length} issues loaded${projectHistoryRequest?' · loading…':''}${projectHistoryResult?' · '+projectHistoryResult:''}. Every issue is read together; overlap is confirmed by history. Missing transitions are not invented.`;
    historyProgress(!!projectHistoryRequest,loaded,ordered.length,true);
    $('timeline-history-retry').hidden=!projectHistoryFailed;
  };
  if(projectHistoryRequest||projectHistoryAttempt===attempt){describe();return;}
  const request=new AbortController();projectHistoryRequest=request;projectHistoryResult=null;projectHistoryFailed=false;
  describe();
  (async()=>{
    try{
      const response=await fetch('/api/v1/issues/activity',{signal:request.signal});
      // 503 means this source cannot answer a whole-project query at all (older bd,
      // other storage, schema drift). Fall back to the per-issue reader rather than
      // leaving the timeline with no recorded history.
      if(response.status===503){canReadProjectHistory=false;return;}
      if(!response.ok)throw new Error('history could not be read');
      const page=await response.json();
      if(request.signal.aborted||historyState?.revision!==generation)return;
      if(page.historyRevision!==generation)throw new Error('history changed while loading');
      let stale=0;
      for(const entry of page.issues){
        // An issue edited since the read is left for the next read, never merged
        // against the revision it no longer matches.
        if(issues.get(entry.issueId)?.revision!==entry.issueRevision){stale++;continue;}
        timeline.setHistory(entry.issueId,{historyRevision:page.historyRevision,issueRevision:entry.issueRevision,versions:entry.versions});
      }
      projectHistoryAttempt=attempt;
      projectHistoryResult=page.coverage?.limitReached?'older history beyond the read limit is missing':stale?stale+' issues changed during the read; they reload shortly':null;
      timeline.setData([...issues.values()],selected,$('search').value,$('status').value,metadataFilters());
    }catch(error){
      if(error.name==='AbortError')return;
      projectHistoryAttempt=attempt;projectHistoryResult=error.message;projectHistoryFailed=true;
    }
    finally{if(projectHistoryRequest===request){projectHistoryRequest=null;scheduleTimelineHistory();}}
  })();
}
function scheduleTimelineHistory(){
  const allowed=view==='timeline'&&!document.hidden&&canReadHistory&&!historyState?.stale&&!!historyState?.revision;
  if(!allowed){projectHistoryRequest?.abort();for(const r of timelineHistoryRequests.values())r.abort();historyProgress(false,0,0);if(view==='timeline')$('timeline-history-state').textContent=historyState?.stale?'Status history unavailable; recorded transitions cannot be refreshed.':'Status history is not connected yet.';return;}
  if(canReadProjectHistory){scheduleProjectHistory(historyState.revision);return;}
  const generation=historyState.revision;
  if(timelineHistoryGeneration!==generation){
    for(const r of timelineHistoryRequests.values())r.abort();
    timelineHistoryAttempts.clear();timelineHistoryGeneration=generation;
  }
  const rangeKey=JSON.stringify(timeline.live?['live',timeline.liveHours,timeline.liveHours===null?timeline.from:null]:[timeline.from,timeline.to]);
  if(rangeKey!==timelineHistoryRange){timelineHistoryRange=rangeKey;timelineHistoryPage=0;}
  const ordered=prioritizeTimelineHistory([...issues.values()],timeline.from,timeline.to);
  const pages=Math.max(1,Math.ceil(ordered.length/64));timelineHistoryPage=Math.min(timelineHistoryPage,pages-1);
  const candidates=ordered.slice(timelineHistoryPage*64,(timelineHistoryPage+1)*64);
  timeline.historyScope=new Set(candidates.map(i=>i.id));
  for(const [id,request] of timelineHistoryRequests)if(!timeline.historyScope.has(id))request.abort();
  $('timeline-history-pages').hidden=pages===1;
  $('timeline-history-page').textContent=`History batch ${timelineHistoryPage+1}/${pages} · issues ${ordered.length?timelineHistoryPage*64+1:0}–${Math.min(ordered.length,(timelineHistoryPage+1)*64)} of ${ordered.length}`;
  $('timeline-history-prev').disabled=timelineHistoryPage===0;$('timeline-history-next').disabled=timelineHistoryPage===pages-1;
  const keyOf=i=>i.id+':'+i.revision+':'+generation;
  const currentKeys=new Set(candidates.map(keyOf));
  for(const key of timelineHistoryAttempts.keys())if(!currentKeys.has(key))timelineHistoryAttempts.delete(key);
  for(const issue of candidates){
    const key=keyOf(issue),cached=timeline.histories.get(issue.id);
    if(cached?.issueRevision===issue.revision&&cached.revision===generation){timelineHistoryAttempts.set(key,'loaded');continue;}
    if(timelineHistoryAttempts.has(key)||timelineHistoryRequests.has(issue.id))continue;
    if(timelineHistoryRequests.size>=2)break;
    const request=new AbortController();timelineHistoryRequests.set(issue.id,request);
    timelineHistoryAttempts.set(key,'loading');
    (async()=>{
      try{
        let continuation=null,versions=[],page;
        do{
          const response=await fetch('/api/v1/issues/'+encodeURIComponent(issue.id)+'/activity?limit=100'+(continuation?'&after='+encodeURIComponent(continuation):''),{signal:request.signal});
          if(!response.ok)throw new Error('History unavailable');
          page=await response.json();
          if(page.issueRevision!==issue.revision||page.historyRevision!==generation)throw new Error('History changed');
          versions.push(...page.versions);continuation=page.continuation;
        }while(continuation&&versions.length<1000);
        if(request.signal.aborted||issues.get(issue.id)?.revision!==issue.revision||historyState?.revision!==generation||!timeline.historyScope.has(issue.id))return;
        timeline.setHistory(issue.id,{...page,versions});
        timeline.setData([...issues.values()],selected,$('search').value,$('status').value,metadataFilters());
        timelineHistoryAttempts.set(key,'loaded');
      }catch(error){if(error.name==='AbortError')timelineHistoryAttempts.delete(key);else timelineHistoryAttempts.set(key,'failed');}
      finally{
        if(timelineHistoryRequests.get(issue.id)===request)timelineHistoryRequests.delete(issue.id);
        scheduleTimelineHistory();
      }
    })();
  }
  const loaded=candidates.filter(i=>timelineHistoryAttempts.get(keyOf(i))==='loaded').length;
  const failed=candidates.filter(i=>timelineHistoryAttempts.get(keyOf(i))==='failed').length;
  $('timeline-history-state').textContent=`Status history: ${loaded}/${candidates.length} issues loaded${timelineHistoryRequests.size?' · loading…':''}${failed?' · '+failed+' unavailable':''}${issues.size>64?' · current history batch':''}. Range-relevant issues load first; overlap is confirmed by history. Missing transitions are not invented.`;
  historyProgress(timelineHistoryRequests.size>0,loaded,candidates.length);
  $('timeline-history-retry').hidden=!failed;
}
for(const [id,delta] of [['timeline-history-prev',-1],['timeline-history-next',1]])$(id).addEventListener('click',()=>{
  timelineHistoryPage=Math.max(0,timelineHistoryPage+delta);
  for(const r of timelineHistoryRequests.values())r.abort();
  timelineHistoryAttempts.clear();timeline.offset=0;timeline.dataKey=null;
  scheduleTimelineHistory();render();timeline.fit();
});
$('timeline-history-retry').addEventListener('click',()=>{projectHistoryAttempt=null;projectHistoryResult=null;projectHistoryFailed=false;for(const [key,state] of timelineHistoryAttempts)if(state==='failed')timelineHistoryAttempts.delete(key);scheduleTimelineHistory();});
document.addEventListener('visibilitychange',scheduleTimelineHistory);
document.addEventListener('timeline-range-change',()=>queueMicrotask(()=>{
  scheduleTimelineHistory();timeline.dataKey=null;
  timeline.setData([...issues.values()],selected,$('search').value,$('status').value,metadataFilters());
}));
function resetActivity() {
  activityRequest?.abort(); activityRequest=null; activityKey=null; activityCursor=null; activityLoaded=false;
  $('activity').replaceChildren();$('activity-snapshots').replaceChildren();$('activity-evidence').open=false;$('activity-evidence').hidden=true; $('more-activity').hidden=true;
  $('activity-section').hidden=!canReadHistory || !issues.has(selected);
  $('load-activity').disabled=!!historyState?.stale;
  $('activity-state').textContent=historyState?.stale ? historyState.error : 'Load history to see status, label and note changes. Comments are listed below.';
}
async function loadActivity(more=false) {
  const issue=issues.get(selected); if(!issue || !canReadHistory || historyState?.stale)return;
  activityRequest?.abort(); const request=new AbortController();activityRequest=request;
  const key=`${issue.id}:${issue.revision}:${historyState?.revision}`;
  activityKey=key;activityLoaded=true;
  if(!more)activityCursor=null;
  $('load-activity').disabled=true;$('more-activity').disabled=true;
  $('activity-state').textContent='Loading issue changes…';
  try {
    const response=await fetch(`/api/v1/issues/${encodeURIComponent(issue.id)}/activity?limit=25${more && activityCursor ? '&after='+encodeURIComponent(activityCursor) : ''}`,{signal:request.signal});
    if(!response.ok)throw new Error(response.status===409 ? 'History changed while loading. Reload history to start a fresh page.' : 'History unavailable. Current issue fields are not a substitute.');
    const data=await response.json();if(activityRequest!==request || selected!==issue.id || activityKey!==key)return;
    // Merge into the revision-fenced cache: inspector paging must not replace
    // a fuller timeline history or duplicate changes across page boundaries.
    timeline.setHistory(issue.id,data,true);timeline.setData([...issues.values()],selected,$('search').value,$('status').value,metadataFilters());
    const lane=timeline.projection.cache.get(issue.id),events=(lane?.displayEvents||[]).filter(e=>e.kind!=='comment').sort((a,b)=>b.time-a.time||a.id.localeCompare(b.id));
    const onTimeline=new Set(lane?timeline.episodeEvents(lane).map(e=>e.id):[]);
    $('activity').replaceChildren();
    for(const event of events){
      const card=text('article','');card.className='comment';
      card.append(text('small','Recorded at · '+new Date(event.time).toLocaleString()),text('p',event.text));
      appendEventEvidence(card,event);
      if(onTimeline.has(event.id))card.append(timelineEventButton(issue.id,event.id));
      $('activity').append(card);
    }
    if(!events.length)$('activity').append(text('p','No status, label or note changes found in the loaded history. Unchanged snapshots are hidden.'));
    $('activity-snapshots').replaceChildren();
    const versions=timeline.histories.get(issue.id)?.versions||[];
    for(const version of versions){
      const card=text('article','');card.className='comment';
      card.append(text('small','Recorded snapshot · '+new Date(version.recordedAt).toLocaleString()),text('p',version.title+' · '+statusLabel(version.status)),text('small','Committer: '+version.committer+' (not necessarily edit author) · '+version.sourceRevision));
      $('activity-snapshots').append(card);
    }
    $('activity-evidence').hidden=!versions.length;
    $('activity-coverage').textContent=data.coverage.explanation;
    activityCursor=data.continuation;
    $('more-activity').hidden=!activityCursor;
    $('activity-state').textContent=events.length+' changes in loaded history. Exact edit times may be unknown.'+(activityCursor?' Load older history for earlier changes.':'')+(data.coverage.limitReached?' History is limited; older changes may be missing.':'');
    render();
  } catch(error) {if(activityRequest===request && error.name!=='AbortError'){$('activity-state').textContent=error.message;$('more-activity').hidden=true;}}
  finally {if(activityRequest===request){$('load-activity').disabled=false;$('more-activity').disabled=false;}}
}
$('load-activity').addEventListener('click',()=>loadActivity());
$('more-activity').addEventListener('click',()=>loadActivity(true));
function updateHistory(state) {
  if(JSON.stringify(historyState)===JSON.stringify(state))return;
  const reload=activityLoaded;historyState=state;timeline.invalidateHistory(state);scheduleTimelineHistory();
  if(state?.stale) {
    activityRequest?.abort();activityRequest=null;
    $('load-activity').disabled=true;$('more-activity').disabled=true;
    $('activity-state').textContent=state.error+' Showing previously loaded snapshots, if any.';
    return;
  }
  if(reload)loadActivity();else resetActivity();
}
let view = ['dependency','branches','issues','attention','workers','worktrees'].includes(new URL(location.href).searchParams.get('view')) ? new URL(location.href).searchParams.get('view') : 'timeline';
let gitState = null, branch = new URL(location.href).searchParams.get('branch'), gitRenderKey = null, comparisonRequest, comparisonKey = null;
let gitHistoryRequest=null,gitHistoryKey=null;
const metadataFields=['assignee','label','priority','attention','type','target'];
function metadataFilters(){return Object.fromEntries(metadataFields.map(key=>[key,$('filter-'+key).value.trim()]));}
function restoreMetadataFilters(){const url=new URL(location.href);for(const key of metadataFields)$('filter-'+key).value=url.searchParams.get('filter-'+key)||'';}
restoreMetadataFilters();
const statusLabel = value => ({closed:'Completed',in_progress:'In progress',blocked:'Blocked',open:'Open'})[value]||value;
function text(tag, value, className) { const node = document.createElement(tag); node.textContent = value ?? 'Unknown'; if (className) node.className = className; return node; }
function updateTabBadges(){
  const count=[...issues.values()].filter(issue=>issue.labels?.includes('abacus:needs-user-attention')).length;
  $('attention-badge').hidden=!count;
  $('attention-badge').textContent=count>99?'99+':String(count);
  $('attention-view').setAttribute('aria-label',count?`Attention Center, ${count} issues require attention`:'Attention Center');
  const claims=currentRuntime?.claims;
  const state=claims?.manualEnabled===false?'paused':claims?.gateAllows===true?'active':null;
  $('workers-badge').hidden=!state;
  $('workers-badge').dataset.state=state||'';
  $('workers-badge').textContent=state==='active'?'✓':'!';
  $('workers-view').setAttribute('aria-label',state==='paused'?'Workers, claims paused':state==='active'?'Workers, claims active':'Workers');
}
function renderAttention(){
  const list=$('attention-list');list.replaceChildren();
  const pending=[...issues.values()].filter(issue=>issue.labels?.includes('abacus:needs-user-attention'));
  const recent=issue=>issue.comments?.at(-1)?.createdAt||issue.createdAt||'';
  pending.sort((a,b)=>recent(b).localeCompare(recent(a))||a.id.localeCompare(b.id));
  $('attention-summary').textContent=issuesLoaded?`${pending.length} issue${pending.length===1?'':'s'} waiting for your attention · latest comments from the current Beads snapshot`:'Loading attention issues…';
  $('attention-empty').hidden=!issuesLoaded||pending.length>0;
  for(const issue of pending){
    const card=text('article','', 'attention-card');card.classList.toggle('selected',selected===issue.id);
    const heading=text('div','', 'attention-heading');
    const open=text('button',`${issue.id} · ${issue.title}`,'attention-issue');open.type='button';open.addEventListener('click',()=>select(issue.id));
    const badge=text('span',statusLabel(issue.status),'status');badge.dataset.status=issue.status;
    heading.append(open,badge);card.append(heading);
    const comments=text('div','', 'attention-comments');
    const latest=(issue.comments||[]).slice(-3);
    if(!latest.length)comments.append(text('p','No comments recorded for this issue. Open it to review its description and notes.','attention-no-comments'));
    for(const comment of latest){
      const entry=text('article','', 'comment');
      const when=comment.createdAt?new Date(comment.createdAt).toLocaleString():'Time not recorded';
      entry.append(text('small',`${comment.author||'Author not recorded'} · ${when}`),text('p',comment.text));comments.append(entry);
    }
    card.append(comments);
    const actions=text('div','', 'attention-actions');
    for(const [action,label] of [['attention-resolve','Resolve attention'],['attention-resolve-reopen','Resolve attention & reopen']]){
      const button=text('button',label);button.type='button';
      button.disabled=!canEdit||!canResolveAttention||!timeline.live||liveReadPending||$('source').classList.contains('stale');
      button.title=button.disabled?'Return to live with a healthy issue source to edit':'Review and submit in the issue inspector';
      button.addEventListener('click',()=>openAttentionAction(issue.id,action));actions.append(button);
    }
    card.append(actions);list.append(card);
  }
}
function openAttentionAction(id,action){
  if(!canEdit||!canResolveAttention||!timeline.live||liveReadPending||$('source').classList.contains('stale'))return;
  const existing=drafts.get(id);
  if(existing?.pending||existing?.request){select(id);$('issue-form').scrollIntoView({block:'start'});return;}
  select(id);
  const draft=drafts.get(id);if(!draft)return;
  draft.action=action;draft.result='Review the latest comments, optionally add a response, then submit.';
  $('write-action').value=action;editorFeedback(draft);
  $('issue-form').scrollIntoView({block:'start'});$('write-text').focus();
}
let issueGitRequest=null,issueGitInspectorKey=null;
let selectedEvent=null, inspectorSourceKey=null, liveReadPending=false, liveReadError=false;
const inspectorTabs=['overview','activity','git'];
let inspectorTab=readInspectorTab();
const inspectorTabBar=document.querySelector('.inspector-tabs'),inspectorIndicator=document.createElement('span');
inspectorIndicator.className='inspector-tab-indicator';inspectorIndicator.setAttribute('aria-hidden','true');inspectorTabBar.append(inspectorIndicator);
const inspectorMotionMedia=matchMedia('(prefers-reduced-motion: reduce)');
let inspectorPanelAnimation=null;
const inspectorReduced=()=>document.documentElement.dataset.motion==='reduce'||(document.documentElement.dataset.motion!=='full'&&inspectorMotionMedia.matches);
function cancelInspectorMotion(){inspectorPanelAnimation?.cancel();inspectorPanelAnimation=null;inspectorTabBar.classList.remove('animate-tabs');}
inspectorMotionMedia.addEventListener('change',()=>{if(inspectorReduced())cancelInspectorMotion();});
new MutationObserver(()=>{if(inspectorReduced())cancelInspectorMotion();}).observe(document.documentElement,{attributes:true,attributeFilter:['data-motion']});
document.addEventListener('visibilitychange',()=>{if(document.hidden)cancelInspectorMotion();});
function readInspectorTab(){const value=new URL(location.href).searchParams.get('inspector');return inspectorTabs.includes(value)?value:'overview';}
function showInspectorTab(next,updateUrl=false,focus=false){
  const changed=inspectorTab!==next;
  if(changed)inspectorPanelAnimation?.cancel();
  inspectorTab=next;
  inspectorTabBar.classList.toggle('animate-tabs',updateUrl&&!inspectorReduced()&&!document.hidden);
  inspectorIndicator.style.transform=`translateX(${inspectorTabs.indexOf(next)*100}%)`;
  for(const name of inspectorTabs){const active=name===next,button=$('inspector-tab-'+name);button.setAttribute('aria-selected',String(active));button.tabIndex=active?0:-1;$('inspector-panel-'+name).hidden=!active;}
  if(updateUrl){const url=new URL(location.href);url.searchParams.set('inspector',next);history.replaceState(null,'',url);}
  if(focus)$('inspector-tab-'+next).focus();
  if(changed&&updateUrl&&!inspectorReduced()&&!document.hidden&&!$('issue-inspector').hidden){
    const style=getComputedStyle(document.documentElement);
    inspectorPanelAnimation=$('inspector-panel-'+next).animate(
      [{opacity:.35,transform:'translateY(4px)'},{opacity:1,transform:'translateY(0)'}],
      {duration:parseFloat(style.getPropertyValue('--motion-panel'))||240,easing:style.getPropertyValue('--ease-out').trim()||'ease-out'});
  }
}
for(const [index,name] of inspectorTabs.entries()){
  const button=$('inspector-tab-'+name);
  button.addEventListener('click',()=>showInspectorTab(name,true));
  button.addEventListener('keydown',event=>{
    const next=event.key==='ArrowRight'?(index+1)%3:event.key==='ArrowLeft'?(index+2)%3:event.key==='Home'?0:event.key==='End'?2:null;
    if(next!==null){event.preventDefault();showInspectorTab(inspectorTabs[next],true,true);}
  });
}
showInspectorTab(inspectorTab);
$('copy-issue-id').addEventListener('click',async()=>{
  const id=selected;if(!issues.has(id))return;
  try{await navigator.clipboard.writeText(id);if(selected===id)$('copy-issue-result').textContent='Issue ID copied.';}
  catch{if(selected===id)$('copy-issue-result').textContent='Clipboard unavailable. Select and copy the issue ID above.';}
});
function invalidateIssueGit(message='Git evidence changed; load again to verify current binding.'){
  issueGitRequest?.abort();issueGitRequest=null;$('issue-git-facts').replaceChildren();clearIssueGitDetails();
  $('issue-git-state').textContent=message;
  $('integration-confirmation-state').textContent='Status closure is recorded; Git integration is not currently confirmed.';
  $('load-issue-git').disabled=!issues.has(selected)||(view==='timeline'&&!timeline.live);
  $('check-integration').disabled=$('load-issue-git').disabled;
}
function clearIssueGitDetails(){
  $('load-issue-patch').hidden=true;$('load-issue-history').hidden=true;
  $('issue-git-detail-state').textContent='';$('issue-git-patch').textContent='';$('issue-git-patch').hidden=true;$('issue-git-history').replaceChildren();
}
async function loadIssueGit(detail=''){

  const issue=issues.get(selected);if(!issue||(view==='timeline'&&!timeline.live))return;
  issueGitRequest?.abort();const request=new AbortController();issueGitRequest=request;
  const gitRevision=gitState?.revision;
  $('check-integration').disabled=true;$('integration-confirmation-state').textContent='Checking current Git integration…';
  $('load-issue-git').disabled=true;$('issue-git-state').textContent='Validating recorded binding against target policy and Git…';$('issue-git-facts').replaceChildren();clearIssueGitDetails();
  try{
    const response=await fetch('/api/v1/issues/'+encodeURIComponent(issue.id)+'/git'+(detail?'?'+detail+'=true':''),{signal:request.signal});
    if(!response.ok)throw new Error(response.status===409?'Issue changed during inspection; load again.':'Git evidence unavailable or incomplete; no integration claim can be made.');
    const result=await response.json();
    if(issueGitRequest!==request||selected!==issue.id||issues.get(selected)?.revision!==issue.revision||gitRevision!==gitState?.revision)return;
    if(result.issueRevision!==issue.revision)throw new Error('Issue changed during inspection; load again.');
    timeline.setGitEvidence(issue.id,result);
    $('integration-confirmation-state').textContent=result.state==='validated'&&result.comparison?.containedInTarget?
      'Git confirms integration: issue tip is contained in '+result.binding.targetRef+' now. Exact merge time is unknown; the curve ends at status closure.':
      'Git integration is unconfirmed. The curve still ends at status closure.';
    $('issue-git-state').textContent=result.state+' · '+result.explanation;
    const fields=[['Effective target',result.effectiveTarget||'Unknown']];
    if(result.binding)fields.push(['Recorded target ref',result.binding.targetRef],['Recorded issue branch',result.binding.issueBranch],['Recorded start commit',result.binding.startCommit]);
    if(result.state==='validated'){
      fields.push(['Collected registered checkouts (not worker ownership)',result.worktrees?.length?result.worktrees.map(w=>`${w.path} · ${w.dirty===true?'dirty':w.dirty===false?'clean':'dirty state unknown'}${w.statusError?' · '+w.statusError:''}`).join('\n'):'No attached checkout in the collected registration snapshot. Detached worker association requires ownership evidence.']);
    }
    const comparison=result.comparison;
    if(comparison)fields.push(['Target tip',comparison.targetTip],['Issue tip',comparison.issueTip],['Integration evidence',comparison.containedInTarget?'Issue tip is contained in target now; exact integration time unknown':'Issue tip is not contained in target in available history'],['Comparison basis',comparison.basis],['Ahead / behind',comparison.ahead+' / '+comparison.behind],['Files',comparison.files.map(f=>f.path+': '+(f.binary?'binary':'+'+f.additions+' / −'+f.deletions)).join('\n')||'No file changes'],['History warning',comparison.warning||'None reported']);
    for(const [label,value] of fields)$('issue-git-facts').append(text('dt',label),text('dd',value));
    $('load-issue-patch').hidden=result.state!=='validated';$('load-issue-history').hidden=result.state!=='validated';
    if(result.patch){$('issue-git-detail-state').textContent=result.patch.warning||'Triple-dot patch at the displayed tips; working-tree changes are not included.';$('issue-git-patch').hidden=!result.patch.available;$('issue-git-patch').textContent=result.patch.text;}
    if(result.history){
      const h=result.history;$('issue-git-detail-state').textContent=`${h.coverage} ${h.limitReached?'Latest 100 commits only; older commits omitted.':''} ${h.clockSkew?'Commit timestamps disagree with parent order; topology is authoritative.':''} Reachable branch history includes shared ancestors, not only work authored for this issue.`;
      for(const commit of h.commits){const card=document.createElement('article');card.className='comment';card.append(text('small',`${commit.id} · ${commit.author} · ${new Date(commit.committedAt).toLocaleString()}`),text('p',commit.message),text('small','Parents: '+(commit.parents.join(' · ')||'none recorded')));$('issue-git-history').append(card);}
    }

  }catch(error){if(issueGitRequest===request&&error.name!=='AbortError'){timeline.setGitEvidence(issue.id,null);$('issue-git-state').textContent=error.message;$('integration-confirmation-state').textContent='Git integration could not be checked. Status closure is unchanged.';}}
  finally{if(issueGitRequest===request){issueGitRequest=null;$('load-issue-git').disabled=false;$('check-integration').disabled=false;}}
}
$('load-issue-git').addEventListener('click',()=>loadIssueGit());
$('check-integration').addEventListener('click',()=>loadIssueGit());
$('load-issue-patch').addEventListener('click',()=>loadIssueGit('patch'));
$('load-issue-history').addEventListener('click',()=>loadIssueGit('history'));
$('issue-browse-branches').addEventListener('click',()=>changeView('branches'));

const timeline=new Timeline({
  onSelect:(id,event)=>{currentInspectorRevision=null;select(id,event);},
  // `advancing` is a playhead step inside a running playback. Returning to live,
  // seeking elsewhere and restoring a location all change which events are even
  // reachable, so those drop the pinned event; simply moving the playhead does not,
  // or a selected node would be dropped several times a second while it plays.
  onPlayback:(returnedLive=false,restoringLocation=false,advancing=false)=>{
    currentInspectorRevision=null;
    if(!advancing){
      selectedEvent=null;
      if(!restoringLocation)timeline.pendingEvent=null;
      timeline.pinnedEvent=null;timeline.calloutAnimation?.cancel();$('timeline-callout').hidden=true;
      if(!restoringLocation){const url=new URL(location.href);url.searchParams.delete('event');history.replaceState(null,'',url);}
    }
    if(returnedLive && selected){
      const id=selected;liveReadPending=true;liveReadError=false;
      fetch('/api/v1/issues/'+encodeURIComponent(id)).then(r=>{if(!r.ok)throw new Error();return r.json();})
        .then(issue=>{issues.set(id,issue);projectLabelsRevision=null;liveReadPending=false;currentInspectorRevision=null;render();})
        .catch(()=>{liveReadError=true;currentInspectorRevision=null;inspect();});
    }
    inspect();creationFeedback();
  },
});
$('inspector-edit-live').addEventListener('click',()=>timeline.returnLive());
$('inspector-comments').addEventListener('click',()=>{showInspectorTab('activity',true,true);$('comments-coverage').scrollIntoView({block:'nearest'});});
function inspectRelations(issue,historical){
  relations.update(issues);
  const key=`${selected}:${historical}:${relations.revision}`;
  if(key===relationsKey)return;
  relationsKey=key;
  $('relations-section').hidden=!issue;
  $('relations-section').open=false;
  $('relations-summary').textContent='Relationships';
  $('relations').replaceChildren();
  $('ongoing-section').hidden=true;$('ongoing-issues').replaceChildren();
  if(!issue)return;
  if(historical){$('relations-state').textContent='Relationships at this playhead are unknown. Return to live for the current export.';return;}
  const result=relations.forIssue(issue.id);
  $('relations-summary').textContent=`Relationships${result.edges.length?` · ${result.edges.length}`:''}`;
  $('relations-state').textContent=`Current export only; not dispatch readiness. ${result.outgoingKnown?'Outgoing recorded.':'Outgoing unknown.'} ${result.incomingComplete?'Incoming links limited to exported issues.':'Incoming coverage incomplete.'}`;
  if(!result.edges.length)$('relations').append(text('li','No recorded links in the available data.'));
  const ongoing=new Set();
  for(const edge of result.edges){
    const row=document.createElement('li');
    row.append(text('span',`${edge.direction} · ${edge.type} · `));
    const other=issues.get(edge.otherId);
    if(other){const link=text('button',`${other.id} · ${other.title}`);link.type='button';link.onclick=()=>select(other.id);row.append(link);}
    else row.append(text('span',`${edge.otherId} · not present in current export`));
    $('relations').append(row);
    if(other&&other.id!==issue.id&&['in_progress','blocked'].includes(other.status)&&!ongoing.has(other.id)){
      ongoing.add(other.id);const item=document.createElement('li'),button=text('button',`${other.id} · ${other.title} · ${statusLabel(other.status)}`);button.type='button';button.onclick=()=>select(other.id);item.append(button);$('ongoing-issues').append(item);
    }
  }
  $('ongoing-section').hidden=ongoing.size===0;
}
function inspect() {
  const issue = issues.get(selected);
  $('issue-inspector').classList.toggle('inspector-empty',!issue);
  $('inspector-empty-state').hidden=!!issue;
  const historical=view==='timeline'&&!timeline.live;
  inspectRelations(issue,historical);
  const key = issue ? `${issue.id}:${issue.revision}:${historical?Math.floor(timeline.playhead):'live'}` : null;
  if (key === currentInspectorRevision) return;
  currentInspectorRevision = key;
  const sourceKey=issue?issue.id+':'+issue.revision:null;
  if(sourceKey!==inspectorSourceKey){
    const reloadHistory=activityLoaded && activityKey?.startsWith(issue?.id+':');
    inspectorSourceKey=sourceKey;resetActivity();if(reloadHistory)loadActivity();
  }
  $('inspector-mode').hidden=!issue||(timeline.live&&!liveReadPending);
  $('inspector-mode-message').textContent=!timeline.live
    ? 'Playback is read-only. Return to live to edit; your drafts are saved.'
    : liveReadError ? 'Could not refresh the current issue. Retry before editing.' : 'Refreshing the current issue before enabling edits…';
  $('inspector-edit-live').textContent=liveReadError?'Retry current issue':canEdit?'Return to live to edit':'Return to live';
  $('inspector-edit-live').disabled=timeline.live&&liveReadPending&&!liveReadError;
  renderComments(issue,historical);
  $('selected-title').textContent = issue?.title ?? 'Select an issue';
  $('selected-id').textContent = issue?.id ?? '';
  $('copy-issue-id').disabled=!issue;$('copy-issue-result').textContent='';
  $('selected-current-status').textContent=issue?(historical?'Current: ':'')+statusLabel(issue.status):'';
  $('selected-current-status').dataset.status=issue?.status||'unknown';
  $('integration-confirmation').hidden=!issue||issue.status!=='closed';
  const gitInspectorKey=JSON.stringify([issue?.id,issue?.revision,historical]);
  if(gitInspectorKey!==issueGitInspectorKey){
    issueGitInspectorKey=gitInspectorKey;
    invalidateIssueGit();
    $('issue-git-state').textContent=!issue?'Select an issue to inspect Git evidence.':historical?'Git binding and integration evidence at this playhead are unknown.':'Load recorded binding evidence to validate target policy, start ancestry and current tips. Branch names and assignees alone are not proof of ownership or integration.';
    if(issue&&!historical)$('issue-git-facts').append(text('dt','Declared target (not validated binding)'),text('dd',issue.target||'Not recorded'));
    if(issue?.status==='closed'&&!historical){const id=issue.id;queueMicrotask(()=>{if(selected===id&&issueGitInspectorKey===gitInspectorKey&&!issueGitRequest)loadIssueGit();});}
  }

  $('details').replaceChildren();
  $('details-extra').replaceChildren();
  $('inspector-more').hidden=true;
  $('historical-coverage').hidden=!issue||!historical;
  $('historical-facts').replaceChildren();
  $('selected-event').hidden=!selectedEvent&&!timeline.pendingEvent;
  const eventFocused=$('selected-event').contains(document.activeElement);
  const memberFocus=eventFocused?document.activeElement.dataset.memberPage:null;
  const memberPage=$('selected-event').dataset.eventId===selectedEvent?.id?Number($('selected-event').dataset.memberPage)||0:0;
  $('selected-event').dataset.memberPage='0';
  const eventExpanded=$('selected-event').dataset.eventId===selectedEvent?.id?($('selected-event').querySelector('details')?.open??false):false;
  $('selected-event').dataset.eventId=selectedEvent?.id||'';
  $('selected-event').replaceChildren();
  if(!selectedEvent&&timeline.pendingEvent)$('selected-event').append(text('p','Linked event is not available in the loaded history at this playhead. Load the relevant activity or Git history; no event has been inferred.'));
  if(selectedEvent){
    const e=selectedEvent;
    const eventDetails=document.createElement('details');eventDetails.open=Boolean(eventExpanded);
    eventDetails.append(text('summary',eventKindLabel(e.kind)+' · '+new Date(e.time).toLocaleTimeString()+' · Details'));
    $('selected-event').append(eventDetails);
    eventDetails.append(text('h3',eventKindLabel(e.kind)),
      text('p',e.kind==='current'?'Current observation · transition time unknown':new Date(e.time).toLocaleString()),
      text('p',e.text),text('p',e.kind==='comment'?(e.author||'Author not recorded'):e.kind==='cluster'?'Meaningful issue changes only':e.certainty||'' ));
    const utc=text('time',(e.kind==='current'?'Displayed at: ':'Recorded at: ')+new Date(e.time).toISOString());utc.dateTime=new Date(e.time).toISOString();eventDetails.append(utc);
    if(e.kind==='current'&&e.provenance)eventDetails.append(text('small',e.provenance),text('small','Source revision: '+(e.sourceRevision||'unknown')));
    if(e.kind==='git')eventDetails.append(text('small',e.sourceRevision+' · '+e.provenance),text('small','Parents: '+(e.parents.join(' · ')||'none recorded')));
    appendEventEvidence(eventDetails,e);
    if(e.members){
      const members=text('div',''),pager=text('nav',''),count=text('span','');
      pager.setAttribute('aria-label','Cluster member pages');count.setAttribute('aria-live','polite');
      const previous=text('button','Previous events'),next=text('button','Next events');previous.type=next.type='button';
      previous.dataset.memberPage='previous';next.dataset.memberPage='next';
      const size=50;let page=Math.max(0,Math.min(memberPage,Math.ceil(e.members.length/size)-1));
      const renderMembers=()=>{
        $('selected-event').dataset.memberPage=String(page);
        members.replaceChildren();const start=page*size;
        for(const member of e.members.slice(start,start+size)){
          const entry=text('article',''),timestamp=text('time',new Date(member.time).toISOString());timestamp.dateTime=new Date(member.time).toISOString();
          entry.append(text('h4',eventKindLabel(member.kind)),timestamp,text('p',member.text));
          if(member.kind==='comment')entry.append(text('p',member.author||'Author not recorded'));
          appendEventEvidence(entry,member);
          members.append(entry);
        }
        count.textContent=` ${start+1}–${Math.min(start+size,e.members.length)} of ${e.members.length} events `;
        previous.disabled=page===0;next.disabled=start+size>=e.members.length;
        if(document.activeElement===previous&&previous.disabled&&!next.disabled)next.focus();
        else if(document.activeElement===next&&next.disabled&&!previous.disabled)previous.focus();
      };
      previous.addEventListener('click',()=>{page--;renderMembers();});next.addEventListener('click',()=>{page++;renderMembers();});
      pager.append(previous,count,next);eventDetails.append(members);if(e.members.length>size)eventDetails.append(pager);renderMembers();
    }
    if(eventFocused){const control=memberFocus&&eventDetails.querySelector(`[data-member-page="${memberFocus==='previous'?'previous':'next'}"]:not(:disabled)`);(control||eventDetails.querySelector('summary')).focus({preventScroll:true});}
  }
  if(issue && historical){
    const state=timeline.historicalState(issue.id);
    $('selected-title').textContent=state?.title || issue.id;
    for(const [label,value] of [['As of',new Date(timeline.playhead).toLocaleString()],['Recorded status',statusLabel(state?.status||'unknown')],['Assignee',state?.assignee==null?null:state.assignee||'Unassigned'],['Priority',Number.isInteger(state?.priority)?`P${state.priority}`:null]]){
      if(value===null)continue;
      const row=document.createElement('div');row.className='detail-row';row.dataset.field=label;
      row.append(text('dt',label),text('dd',value));$('details').append(row);
    }
    // Snapshot fields can lag the last status observation. Keep the full
    // provenance available without letting it dominate the default overview.
    for(const [label,value] of [['Fields recorded at',state?.recordedAt?new Date(state.recordedAt).toLocaleString():'No recorded snapshot at this time'],['Coverage',state?.certainty||'Unknown'],['Labels',Array.isArray(state?.labels)?state.labels.join(' · ')||'None':'Unknown'],['Type',state?.issueType||'Unknown'],['Declared target',state?.target||'Unknown'],['Other fields','Not recorded in history. Return to live to inspect current data.']])$('historical-facts').append(text('dt',label),text('dd',value));
    $('issue-form').hidden=true;return;
  }
  if (!issue) { $('issue-form').hidden=true; $('comments').replaceChildren(); return; }
  for (const [label,value] of [['Description',issue.description || 'No description'],['Assignee',issue.assignee || 'Unassigned'],['Priority',Number.isInteger(issue.priority)?`P${issue.priority} · ${['Critical','High','Medium','Low','Backlog'][issue.priority]||'Unknown'}`:'Unknown'],['Type',issue.issueType||'Unknown'],['Target',issue.target||'Not recorded'],['Labels',issue.labels.join(' · ') || 'None'],['Notes',issue.notes || 'No notes'],['Attention',issue.labels.includes('abacus:needs-user-attention')?'Requested':'None']]) {
    if((label==='Notes'&&!issue.notes)||(label==='Attention'&&value==='None')||(label==='Labels'&&!issue.labels.length))continue;
    const row=document.createElement('div');row.className='detail-row';row.dataset.field=label;
    if(label==='Description'){
      const disclosure=document.createElement('details');disclosure.id='description-disclosure';disclosure.open=descriptionOpen.get(issue.id)??false;
      disclosure.append(text('summary','Show description'),text('p',value));
      disclosure.addEventListener('toggle',()=>descriptionOpen.set(issue.id,disclosure.open));
      const definition=document.createElement('dd');definition.append(disclosure);
      row.append(text('dt','Description'),definition);$('details').append(row);continue;
    }
    const term=text('dt',label),definition=text('dd',value);
    if(label==='Assignee')term.title='Recorded assignee; not proof of a currently verified worker.';
    if(label==='Target')term.title='Declared target; not proof of an execution binding.';
    if(label==='Labels'&&issue.labels.length)definition.replaceChildren(...issue.labels.map(label=>text('span',label,'label-chip')));
    if(label==='Priority')definition.dataset.priority=String(issue.priority);
    if(label==='Attention')definition.dataset.attention='requested';
    row.append(term,definition);
    (['Type','Target','Notes'].includes(label)?$('details-extra'):$('details')).append(row);
  }
  $('inspector-more').hidden=!$('details-extra').childElementCount;
  setupEditor(issue);
}
function renderComments(issue,historical){
  const all=issue?.comments||[];
  const visible=historical?all.filter(c=>Number.isFinite(Date.parse(c.createdAt))&&Date.parse(c.createdAt)<=timeline.playhead):all;
  $('inspector-recent').hidden=!issue||visible.length===0;
  $('inspector-comments').hidden=!issue||visible.length===0;
  $('inspector-comments').textContent=`View all (${visible.length}) →`;
  const recent=[...visible].sort((a,b)=>(Date.parse(b.createdAt)||0)-(Date.parse(a.createdAt)||0)).slice(0,2);
  $('inspector-recent-list').replaceChildren(...recent.map(c=>{
    const card=text('article','', 'recent-comment');
    const author=text('strong',c.author||'Unknown author');
    const time=text('time',c.createdAt&&Number.isFinite(Date.parse(c.createdAt))?new Date(c.createdAt).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'}):'Time unknown');
    if(c.createdAt&&Number.isFinite(Date.parse(c.createdAt)))time.dateTime=c.createdAt;
    const preview=c.text.length>100?c.text.slice(0,99).trimEnd()+'…':c.text;
    card.append(author,time,text('p',preview));return card;
  }));
  $('comments-coverage').textContent=!issue?'':historical
    ? `Comments recorded at or before the playhead (${visible.length}). Return to live for all current comments; later or undated comments are not shown in playback.`
    : visible.length?'Current issue comments.':'No comments on this issue yet.';
  $('comments').replaceChildren(...visible.map(c=>{
    const card=document.createElement('article');card.className='comment';
    card.append(text('small',`${c.author||'Unknown author'} · ${c.createdAt?new Date(c.createdAt).toLocaleString():'Time unknown'}`),text('p',c.text));
    if(c.createdAt&&Number.isFinite(Date.parse(c.createdAt)))card.append(timelineEventButton(issue.id,'comment:'+issue.id+':'+c.id));
    return card;
  }));
}
function appendEventEvidence(parent,event){
  if(event.kind==='notes'){
    const changes=text('details','');changes.append(text('summary','Show note changes'),text('h4','Before'),text('pre',event.before||'(empty)'),text('h4','After'),text('pre',event.after||'(empty)'));parent.append(changes);
  }
  if(!event.source)return;
  const evidence=text('details','');evidence.className='event-evidence';
  evidence.append(text('summary','Technical evidence'),text('p','Source: '+event.source),text('p','Revision: '+(event.sourceRevision||'not recorded')));
  if(event.committer)evidence.append(text('p','Committer: '+event.committer+' (not necessarily edit author)'));
  if(event.certainty)evidence.append(text('p',event.certainty));parent.append(evidence);
}
function timelineEventButton(issueId,eventId){
  const button=text('button','Show on timeline','show-timeline-event');
  button.title='Show this recorded event in playback; clears view filters to reveal its lane.';
  button.addEventListener('click',()=>{
    // This is explicit navigation, not a source write or a guessed transition.
    const event=timeline.projection.cache.get(issueId)?.displayEvents.find(e=>e.id===eventId);
    if(!event||event.time>timeline.now()){button.textContent='Event unavailable in loaded history';return;}
    $('search').value='';$('status').value='';
    for(const key of metadataFields)$('filter-'+key).value='';
    $('timeline-kind').value='all';
    const url=new URL(location.href);url.searchParams.delete('search');url.searchParams.delete('status');
    for(const key of metadataFields)url.searchParams.delete('filter-'+key);
    history.replaceState(null,'',url);changeView('timeline');
    timeline.revealEvent(issueId,eventId);
  });
  return button;
}
function select(id,event=null) {
  if(!event){timeline.pinnedEvent=null;timeline.calloutAnimation?.cancel();$('timeline-callout').hidden=true;}
  selectedEvent=event;currentInspectorRevision=null;timeline.pendingEvent=null;
  selected = id;
  const url = new URL(location.href); url.searchParams.set('issue', id);
  if(event?.id&&event.kind!=='current'&&event.kind!=='cluster')url.searchParams.set('event',event.id);else url.searchParams.delete('event');
  history.replaceState(null,'',url);
  render();
}
const dependencyTree=new DependencyTree({pane:$('dependency-pane'),scroll:$('dependency-scroll'),sizer:$('dependency-sizer'),surface:$('dependency-surface'),
  lines:$('dependency-lines'),nodes:$('dependency-nodes'),summary:$('dependency-summary'),
  zoom:$('dependency-zoom'),zoomValue:$('dependency-zoom-value'),onSelect:select});
$('dependency-focus').addEventListener('click',()=>dependencyTree.focus(selected));
$('dependency-fit').addEventListener('click',()=>dependencyTree.fit());
$('dependency-reset').addEventListener('click',()=>dependencyTree.setScale(1));
function render() {
  updateTabBadges();
  if(document.body.dataset.view!==view)$('workspace-tools').open=view!=='timeline'||$('source').classList.contains('stale');
  document.body.dataset.view=view;
  $('source-coverage').hidden=view==='branches';
  creationFeedback();
  if(view!=='worktrees')stopWorktreeWatch();
  if(['branches','workers','worktrees'].includes(view))cancelInspectorMotion();
  $('workers-pane').hidden=view!=='workers';$('worktrees-pane').hidden=view!=='worktrees';$('attention-pane').hidden=view!=='attention';
  $('workspace-tools').hidden=['workers','worktrees'].includes(view);
  $('inspector').hidden=['workers','worktrees'].includes(view);
  $('inspector-resizer').hidden=['workers','worktrees'].includes(view);
  $('timeline-pane').hidden=view!=='timeline';timeline.show(view==='timeline');scheduleTimelineHistory();
  $('dependency-pane').hidden=view!=='dependency';
  timeline.setData([...issues.values()],selected,$('search').value,$('status').value,metadataFilters());
  $('issue-pane').hidden = view !== 'issues'; $('branch-pane').hidden = view !== 'branches';
  $('issue-inspector').hidden = view === 'branches'; $('branch-inspector').hidden = view !== 'branches';
  $('issues-title').textContent = view === 'branches' ? 'Branches' : view==='timeline'?'Issue timeline':view==='dependency'?'Dependency Tree':view==='attention'?'Attention Center':'Issues';
  $('status').parentElement.hidden = view === 'branches'||view==='attention';
  $('source').hidden = view === 'branches';
  $('search-coverage').hidden=view==='branches'||view==='attention';timeline.updateSearchCoverage();
  $('issue-filters').hidden=view==='branches'||view==='attention';
  $('search').parentElement.hidden=view==='attention';
  $('search').placeholder = view === 'branches' ? 'Search local branches…' : 'Search issues…';
  $('inspector').setAttribute('aria-labelledby',view === 'branches' ? 'branch-title' : 'selected-title');
  for (const name of ['timeline','dependency','issues','attention','branches','workers','worktrees']) { if(view === name) $(name+'-view').setAttribute('aria-current','page'); else $(name+'-view').removeAttribute('aria-current'); }
  if(view === 'branches'||view==='worktrees') { renderBranches(); return; }
  if(view==='workers'){if(currentRuntime)updateRuntime(currentRuntime);return;}
  if(view==='attention'){$('count').textContent='';renderAttention();inspect();return;}
  if(view==='timeline'){$('count').textContent=issues.size+' issues';inspect();return;}
  if(view==='dependency'){
    $('count').textContent=issues.size+' issues';
    dependencyTree.render([...issues.values()],selected,$('search').value,$('status').value);
    inspect();return;
  }
  if(!issuesLoaded){$('count').textContent='Loading issues…';inspect();return;}
  const query = $('search').value.toLowerCase(), status = $('status').value;
  const projected=issuePage([...issues.values()],{...tableState,query,status,metadata:metadataFilters(),searchMatches:issue=>timeline.matchesSearch(issue.id,true)});
  const visible=projected.rows;
  tableState={sort:projected.sort,direction:projected.direction,page:projected.page,pageSize:projected.pageSize};
  updateTableUrl();
  $('issues-page').textContent=projected.total?`${projected.page*projected.pageSize+1}–${projected.page*projected.pageSize+visible.length} of ${projected.total} · Page ${projected.page+1} of ${projected.pages}`:'0 matching issues';
  $('issues-prev').disabled=projected.page===0;$('issues-next').disabled=projected.page+1>=projected.pages;$('issues-page-size').value=projected.pageSize;
  for(const button of document.querySelectorAll('[data-issue-sort]')){
    const active=button.dataset.issueSort===projected.sort;
    button.parentElement.setAttribute('aria-sort',active?(projected.direction==='asc'?'ascending':'descending'):'none');
    button.setAttribute('aria-label',`Sort by ${button.textContent}: ${active&&projected.direction==='asc'?'descending':'ascending'}`);
  }
  $('issues-selection').textContent=selected&&!visible.some(i=>i.id===selected)?'Selected issue is outside this page/filter; its inspector remains open.':'';
  const visibleIds = new Set(visible.map(i => i.id));
  for (const [id,row] of rows) if (!visibleIds.has(id)) { row.remove(); rows.delete(id); }
  let previous = null;
  for (const issue of visible) {
    let row = rows.get(issue.id);
    if (!row) { row = document.createElement('tr'); rows.set(issue.id,row); }
    if (row.dataset.revision !== issue.revision) {
      const idCell = document.createElement('td'), button = text('button',issue.id);
      button.addEventListener('click',() => select(issue.id)); idCell.append(button);
      const stateCell = document.createElement('td'), badge = text('span',statusLabel(issue.status),'status'); badge.dataset.status = issue.status; stateCell.append(badge);
      row.replaceChildren(idCell,text('td',issue.title),stateCell,text('td',issue.priority === null ? '—' : `P${issue.priority}`),text('td',issue.assignee || 'Unassigned'));
      row.dataset.revision = issue.revision;
    }
    row.classList.toggle('selected',selected === issue.id);
    const expected = previous ? previous.nextSibling : $('issues').firstChild;
    if (expected !== row) $('issues').insertBefore(row,expected);
    previous = row;
  }
  $('count').textContent = `${projected.total} issues`;
  $('empty').hidden = visible.length > 0;
  inspect();
}
function health(beads) { if(beads.stale){timeline.cancelArrivals();$('source-coverage').open=true;$('workspace-tools').open=true;} if(beads.stale){timeline.clearGitEvidence();invalidateIssueGit('Issue source is stale; current Git evidence is unavailable.');} $('source').textContent = beads.stale ? beads.error : 'Beads · current snapshot · loaded search coverage below'; $('source').classList.toggle('stale',beads.stale); }
function disconnected() { timeline.cancelArrivals();timeline.clearGitEvidence();invalidateIssueGit('Disconnected; previously loaded Git evidence is no longer current.');runtimeOnline=false;renderClaimControls();if(currentRuntime)updateRuntime(currentRuntime); $('connection').textContent = 'Disconnected'; $('connection').classList.remove('connected'); }
async function snapshot() {
  timeline.cancelArrivals();
  const response = await fetch('/api/v1/snapshot'); if (!response.ok) throw new Error('Snapshot unavailable');
  timeline.setClock(response.headers.get('Date'));
  const data = await response.json(); issueRevision=data.revision; issues = new Map(data.issues.map(i => [i.id,i])); issuesLoaded=true; currentInspectorRevision=null;health(data.beads); timeline.clearGitEvidence();invalidateIssueGit('Source snapshot refreshed; reload Git evidence.');gitState = data.git; updateHistory(data.history); updateRuntime(data.runtime); render(); return data.cursor;
}
function updateRuntime(runtime) {
  currentRuntime=runtime;updateTabBadges();renderClaimControls();
  $('runtime-panel').hidden=!runtime;
  $('workers-empty').hidden=!!runtime;
  if(!runtime)return;
  $('runtime-claims').textContent=runtime.claims?`${runtime.claims.manualEnabled?'Claims enabled':'Claims paused'} · ${runtime.claims.scheduleAllows?'Schedule open':'Schedule closed'}${runtime.claims.reason?' · '+runtime.claims.reason:''}`:'Claim gates not connected';
  $('runtime-claims').title='Gate permission is not a guarantee that work is ready or that workspace ownership is available.';
  const active=document.activeElement?.dataset.workerAction;
  const rows=runtime.workers.map(worker=>{
    const row=text('li','','worker-card'),heading=text('div','','worker-heading'),facts=text('div','','worker-facts'),actions=text('div','','worker-actions');
    heading.append(text('strong',worker.name),text('span',worker.activity,'worker-state'));
    if(worker.supervisor)heading.append(text('span','Supervisor','worker-type'));
    row.append(heading);
    const issue=worker.issueId?issues.get(worker.issueId):null;
    const context=text('p','', 'worker-context');
    if(worker.issueId){
      context.append(text('span',worker.issueId,'worker-issue-id'));
      if(issue?.title){const title=text('span',issue.title,'worker-issue-title');title.title=issue.title;context.append(title);}
    }else context.textContent='No assigned issue';
    row.append(context);
    if(worker.branch)row.append(text('p',worker.branch,'worker-branch'));
    const dirt=text('span',worker.dirty===true?'Workspace dirty':worker.dirty===false?'Workspace clean':'Workspace status unknown','worker-fact');dirt.dataset.dirty=worker.dirty===true?'true':worker.dirty===false?'false':'unknown';facts.append(dirt);
    if(worker.retryCount)facts.append(text('span',`${worker.retryCount} retries`,'worker-fact'));
    if(worker.exitCode!=null)facts.append(text('span',`Exit ${worker.exitCode}`,'worker-fact'));
    if(worker.runActive)facts.append(text('span','Execution active','worker-fact'));
    row.append(facts);
    const draft=workerRequests.get(worker.name);
    const actionStates=worker.supervisor?runtime.supervisorActions:runtime.workerActions;
    const recorded=actionStates?.find(a=>a.requestId===draft?.request?.requestId);
    if(recorded&&recorded.outcome!=='accepted'&&draft){draft.request=null;draft.result=recorded.outcome==='failed'?'Worker cleanup failed; review its state before a new action.':recorded.outcome==='outcome-unknown'?'Completion is unknown; review worker state before acting again.':recorded.command==='restart'?'Dispatch/supervision resumed; a new harness is not guaranteed to have started.':'Target acknowledged completion.';draft.uncertain=false;}
    const pending=actionStates?.some(a=>a.worker===worker.name&&a.command!=='force-run'&&a.outcome==='accepted');
    if(worker.supervisor?runtime.supervisorControlsAvailable:runtime.workerControlsAvailable) {
      for(const command of (worker.supervisor?['stop','restart']:['stop','restart','clean-workspace'])) {
        const retry=draft?.request?.command===command&&draft.uncertain;
        const button=text('button',(retry?'Retry same request: ':'')+command);
        button.dataset.workerAction=worker.name+':'+command;
        button.disabled=!runtimeOnline||!!draft?.busy||(!retry&&(pending||!!draft?.request));
        button.addEventListener('click',()=>controlWorker(worker.name,command,worker.supervisor));actions.append(button);
      }
    }
    if(worker.supervisor&&runtime.supervisorControlsAvailable){
      const force=forceRequests.get(worker.name);
      const receipt=actionStates?.find(a=>a.requestId===force?.request?.requestId);
      if(receipt&&receipt.outcome!=='accepted'&&force){force.request=null;force.uncertain=false;force.result=`Force Run: ${receipt.outcome}. Review supervisor state before a new request.`;}
      const forcePending=actionStates?.some(a=>a.worker===worker.name&&a.command==='force-run'&&a.outcome==='accepted');
      const retry=!!force?.request&&force.uncertain;
      const button=text('button',retry?'Retry same Force Run request':'Force Run');
      button.dataset.workerAction=worker.name+':force-run';
      button.disabled=!runtimeOnline||!!force?.busy||(!retry&&(pending||forcePending||!!force?.request));
      button.addEventListener('click',()=>forceSupervisor(worker.name));actions.append(button);
      if(force?.result)row.append(text('p',force.result));
    }
    row.append(actions);
    const outcomes=(actionStates||[]).filter(a=>a.worker===worker.name);
    if(outcomes.length){const latest=outcomes.find(a=>a.outcome==='accepted')||outcomes.at(-1);row.append(text('small',`${latest.command}: ${latest.outcome}`));}
    if(draft?.result)row.append(text('p',draft.result));
    return row;
  });
  $('runtime-workers').replaceChildren(...rows);
  if(active)[...$('runtime-workers').querySelectorAll('button')].find(b=>b.dataset.workerAction===active)?.focus({preventScroll:true});
}
async function controlWorker(worker,command,supervisor=false) {
  if(!runtimeOnline||!(supervisor?currentRuntime?.supervisorControlsAvailable:currentRuntime?.workerControlsAvailable))return;
  let draft=workerRequests.get(worker);
  if(draft?.busy)return;
  if(!draft?.request) {
    const warning=command==='clean-workspace'?'Discard tracked workspace changes after stopping this worker? Existing cleanup and ownership checks still apply.':`${command==='stop'?'Stop':'Restart'} ${supervisor?'supervisor':'worker'} ${worker}? This controls execution, not timeline playback.`;
    if(!confirm(`Operator: ${operator}. ${warning}`))return;
    draft={request:{session:runtimeSession,requestId:[...crypto.getRandomValues(new Uint8Array(16))].map(b=>b.toString(16).padStart(2,'0')).join(''),worker,command,confirm:command==='clean-workspace'},busy:false,uncertain:false,result:''};
    workerRequests.set(worker,draft);
  }
  const request=draft.request;draft.busy=true;draft.uncertain=false;updateRuntime(currentRuntime);
  try {
    const response=await fetch(supervisor?'/api/v1/runtime/supervisors/actions':'/api/v1/runtime/workers/actions',{method:'POST',headers:{'Content-Type':'application/json','X-Abacus-Request':'1'},body:JSON.stringify(request)});
    const result=await response.json();
    if(draft.request===request){draft.result=`${result.outcome}: ${result.message}`;if(result.outcome!=='accepted')draft.request=null;}
  } catch {if(draft.request===request){draft.uncertain=true;draft.result='Response unknown. Retry the SAME request or review the recorded outcome; do not submit a new action blindly.';}}
  finally {draft.busy=false;updateRuntime(currentRuntime);}
}
async function forceSupervisor(worker){
  if(!runtimeOnline||!currentRuntime?.supervisorControlsAvailable)return;
  let draft=forceRequests.get(worker);
  if(draft?.busy)return;
  if(!draft?.request){
    const instructions=window.prompt(`Operator: ${operator}. Instructions for one ${worker} force run (maximum 16,000 characters):`);
    if(instructions===null)return;
    if(!instructions.trim()||instructions.length>16000||instructions.includes('\0')){alert('Provide a nonempty prompt of at most 16,000 characters, without NUL characters.');return;}
    if(!confirm(`Operator: ${operator}. Force ${worker} to run with these instructions? This may bypass automatic trigger/claim-gate eligibility under existing supervisor policy. Checkout safety and configured permissions still apply. Stop/Restart remain available to interrupt it.`))return;
    draft={request:{session:runtimeSession,requestId:[...crypto.getRandomValues(new Uint8Array(16))].map(b=>b.toString(16).padStart(2,'0')).join(''),worker,command:'force-run',confirm:true,prompt:instructions},busy:false,uncertain:false,result:''};
    forceRequests.set(worker,draft);
  }
  const request=draft.request;draft.busy=true;draft.uncertain=false;updateRuntime(currentRuntime);
  try{
    const response=await fetch('/api/v1/runtime/supervisors/actions',{method:'POST',headers:{'Content-Type':'application/json','X-Abacus-Request':'1'},body:JSON.stringify(request)});
    const result=await response.json();
    if(draft.request===request){draft.result=`Force Run: ${result.outcome}. ${result.message}`;if(result.outcome!=='accepted')draft.request=null;}
  }catch{if(draft.request===request){draft.uncertain=true;draft.result='Force response unknown. Retry only this SAME request; Stop/Restart remain available.';}}
  finally{draft.busy=false;updateRuntime(currentRuntime);}
}
function renderClaimControls() {
  $('stop-run').hidden=!currentRuntime?.stopRunAvailable&&!stopRunRequest;
  $('stop-run').disabled=!runtimeOnline||stopRunBusy||stopRunAccepted;
  $('stop-run').textContent=stopRunRequest?'Retry same Stop Run request':'Stop Run';
  $('claim-controls').hidden=!canControlClaims||!currentRuntime?.claims;
  for(const command of ['pause','resume']) {
    const button=$('claims-'+command);
    button.textContent=(claimRequest?.command===command?'Retry same request: ':'')+(command==='pause'?'Pause claims':'Resume claims');
    button.disabled=!runtimeOnline||claimBusy||!!(claimRequest&&claimRequest.command!==command)||(!claimRequest&&currentRuntime?.claims?.manualEnabled===(command==='resume'));
  }
}
async function controlClaims(command) {
  if(!runtimeOnline||claimBusy||!canControlClaims||!currentRuntime?.claims)return;
  if(!claimRequest) {
    if(!confirm(command==='pause'?'Pause new claims? Running work will continue.':'Enable the manual claim gate? Schedule and ownership restrictions still apply.'))return;
    claimRequest={session:runtimeSession,requestId:[...crypto.getRandomValues(new Uint8Array(16))].map(b=>b.toString(16).padStart(2,'0')).join(''),command,expectedManualEnabled:currentRuntime.claims.manualEnabled};
  }
  claimBusy=true;renderClaimControls();
  try {
    const response=await fetch('/api/v1/runtime/actions',{method:'POST',headers:{'Content-Type':'application/json','X-Abacus-Request':'1'},body:JSON.stringify(claimRequest)});
    const result=await mutationResponse(response);$('claim-result').textContent=result.message;
    if(result.outcome==='completed'||result.outcome==='rejected')claimRequest=null;
  } catch(error) {$('claim-result').textContent=`${error.message.startsWith('HTTP ') ? error.message+'. ' : ''}Result unknown. Retry the same request; do not assume the gate changed.`;}
  finally {claimBusy=false;renderClaimControls();}
}
$('claims-pause').addEventListener('click',()=>controlClaims('pause'));
$('claims-resume').addEventListener('click',()=>controlClaims('resume'));
async function connect() {
  source?.close();
  try {
    const cursor = await snapshot();
    source = new EventSource(`/api/v1/events?after=${encodeURIComponent(cursor)}`);
    let opened=false;
    source.onopen = () => { if(opened){source.close();connect();return;}opened=true;runtimeOnline=true;renderClaimControls();if(currentRuntime)updateRuntime(currentRuntime); $('connection').textContent = '● Live'; $('connection').classList.add('connected'); };
    source.onerror = disconnected;
    source.addEventListener('change',event => { const data = JSON.parse(event.data); if(!data.beads.stale&&!$('source').classList.contains('stale'))timeline.queueLiveArrivals(data.upserts);issueRevision=data.revision; for(const id of data.removals) issues.delete(id); for(const issue of data.upserts) issues.set(issue.id,issue); health(data.beads); render(); });
    source.addEventListener('runtime',event => updateRuntime(JSON.parse(event.data)));
    source.addEventListener('history',event => updateHistory(JSON.parse(event.data)));
    source.addEventListener('git',event => { gitState = JSON.parse(event.data);timeline.clearGitEvidence(true);invalidateIssueGit(); if(view === 'branches'||view==='worktrees') renderBranches(); });
    source.addEventListener('resync',connect);
    source.addEventListener('disconnected',() => { source.close(); disconnected(); });
  } catch { disconnected(); $('source').textContent = 'Cannot load dashboard. Reload to retry; the last snapshot is retained.'; }
}
function changedMetadataFilters(){
  tableState.page=0;const url=new URL(location.href);
  for(const [key,value] of Object.entries(metadataFilters())){if(value)url.searchParams.set('filter-'+key,value);else url.searchParams.delete('filter-'+key);}
  history.replaceState(null,'',url);render();
}
for(const key of metadataFields)$('filter-'+key).addEventListener('input',changedMetadataFilters);
$('clear-issue-filters').addEventListener('click',()=>{for(const key of metadataFields)$('filter-'+key).value='';changedMetadataFilters();});
function updateTableUrl(){const url=new URL(location.href);for(const [key,value] of Object.entries({sort:tableState.sort,order:tableState.direction,page:tableState.page,size:tableState.pageSize}))url.searchParams.set(key,value);if(url.href!==location.href)history.replaceState(null,'',url);}
for(const button of document.querySelectorAll('[data-issue-sort]'))button.addEventListener('click',()=>{
  const sort=button.dataset.issueSort;tableState.direction=tableState.sort===sort&&tableState.direction==='asc'?'desc':'asc';tableState.sort=sort;tableState.page=0;render();updateTableUrl();
});
for(const [id,delta] of [['issues-prev',-1],['issues-next',1]])$(id).addEventListener('click',()=>{tableState.page+=delta;render();updateTableUrl();});
$('issues-page-size').addEventListener('change',()=>{tableState.pageSize=Number($('issues-page-size').value);tableState.page=0;render();updateTableUrl();});
$('search').addEventListener('input',()=>{tableState.page=0;render();}); $('status').addEventListener('change',()=>{tableState.page=0;render();});
window.addEventListener('popstate',() => { showInspectorTab(readInspectorTab());restoreMetadataFilters();const url=new URL(location.href);tableState={sort:url.searchParams.get('sort')||'id',direction:url.searchParams.get('order')||'asc',page:Number(url.searchParams.get('page'))||0,pageSize:Number(url.searchParams.get('size'))||50}; view = ['dependency','branches','issues','attention','workers','worktrees'].includes(new URL(location.href).searchParams.get('view')) ? new URL(location.href).searchParams.get('view') : 'timeline'; branch = new URL(location.href).searchParams.get('branch'); selected = url.searchParams.get('issue');selectedEvent=null;currentInspectorRevision=null;timeline.restoreLocation(url); render(); });
function changeView(next) {
  view = next; currentInspectorRevision=null;const url = new URL(location.href); url.searchParams.set('view',view); history.replaceState(null,'',url); render();
}
$('timeline-view').addEventListener('click',() => changeView('timeline'));
$('dependency-view').addEventListener('click',() => changeView('dependency'));
$('issues-view').addEventListener('click',() => changeView('issues'));
$('attention-view').addEventListener('click',() => changeView('attention'));
$('branches-view').addEventListener('click',() => changeView('branches'));
$('workers-view').addEventListener('click',()=>changeView('workers'));
$('worktrees-view').addEventListener('click',()=>changeView('worktrees'));
$('git-target').addEventListener('change',() => { const url=new URL(location.href);url.searchParams.set('target',$('git-target').value);history.replaceState(null,'',url);comparisonKey=null;compareBranch(); });
$('load-patch').addEventListener('click',() => compareBranch(true));
function renderBranches() {
  const worktreeHealth=$('worktree-health');
  const worktreeCount=gitState?.facts?.worktrees?.length||0;
  worktreeHealth.textContent=gitState?(gitState.error||gitState.policyError||(`${worktreeCount} registered worktree${worktreeCount===1?'':'s'}`)):'Git facts not available';
  worktreeHealth.classList.toggle('stale',!!gitState?.stale||!!gitState?.policyError);
  if (!gitState) { $('git-health').textContent='Git facts not available'; return; }
  const query = $('search').value.toLowerCase(), key = `${gitState.revision}:${issueRevision}:${query}`;
  if (gitRenderKey !== key) {
    gitRenderKey=key;
    $('git-health').textContent=gitState.error || gitState.policyError || 'Observed local refs';
    $('git-health').classList.toggle('stale',gitState.stale || !!gitState.policyError);
    const target=$('git-target').value || new URL(location.href).searchParams.get('target') || '';
    $('git-target').replaceChildren(...gitState.targets.map(t => {const option=text('option',t);option.value='refs/heads/'+t;return option;}));
    if(gitState.targets.includes(target.replace('refs/heads/',''))) $('git-target').value=target;
    else if(gitState.defaultTarget) $('git-target').value='refs/heads/'+gitState.defaultTarget;
    const visible=(gitState.facts?.branches || []).filter(b => b.ref.toLowerCase().includes(query));
    $('branches').replaceChildren(...visible.map(b => {
      const row=document.createElement('tr'), cell=document.createElement('td'), button=text('button',b.ref.replace('refs/heads/',''));
      button.addEventListener('click',() => {branch=b.ref;comparisonKey=null;const url=new URL(location.href);url.searchParams.set('branch',branch);history.replaceState(null,'',url);compareBranch();});cell.append(button);
      const possible=b.ref.startsWith('refs/heads/abacus/') ? b.ref.slice(18) : null;
      const association=text('td','Not associated');
      if(possible && issues.has(possible)) { const link=text('button',possible);link.className='branch-issue-link';link.title='Possible issue association by branch naming convention only';link.setAttribute('aria-label',`Open ${possible}; possible association by branch naming convention only`);link.addEventListener('click',()=>{changeView('issues');select(possible);});association.replaceChildren(link); }
      row.append(cell,text('td',b.tip.slice(0,12)),association);
      return row;
    }));
    $('count').textContent=`${visible.length} branches`;
    $('worktrees').replaceChildren(...(gitState.facts?.worktrees || []).map(w => {
      const row=text('li','','worktree-card'),heading=text('div','','worktree-card-heading');
      const state=text('span',w.dirty===true?'Dirty':w.dirty===false?'Clean':'Status unknown','worktree-state');state.dataset.dirty=w.dirty===true?'true':w.dirty===false?'false':'unknown';
      heading.append(text('strong',w.path),state);
      row.append(heading,text('p',w.branch?.replace('refs/heads/','')||(w.detached?'Detached':'Unborn'),'worktree-branch'));
      if(w.statusError)row.append(text('p',w.statusError,'worktree-warning'));
      const button=text('button','View live changes');button.type='button';button.disabled=!w.id||w.bare||w.prunable||!w.head||gitState.stale;button.title='Load bounded, current uncommitted worktree content';button.onclick=()=>loadWorktreeDiff(w.id);row.append(button);return row;
    }));
  }
  const historyKey=branch+':'+gitState.facts?.branches.find(b=>b.ref===branch)?.tip+':'+(gitState.facts?.historyBoundary??'');
  if(gitHistoryKey&&gitHistoryKey!==historyKey){gitHistoryRequest?.abort();gitHistoryKey=null;$('git-history').replaceChildren();$('git-history-state').textContent='Branch or history boundary changed. Reload recorded history.';}
  if(view==='branches')compareBranch();
  if(view==='worktrees'&&selectedWorktree&&!worktreeSource&&!document.hidden)loadWorktreeDiff(selectedWorktree);
}
let selectedWorktree=null,worktreeSource=null;
function clearWorktreeContent(message){
  $('worktree-detail-state').textContent=message;
  for(const key of ['staged','unstaged','untracked'])$('worktree-'+key).textContent='';
}
function stopWorktreeWatch(){
  worktreeSource?.close();worktreeSource=null;
  if(selectedWorktree)clearWorktreeContent('Live worktree observation paused while this view is hidden.');
}
function loadWorktreeDiff(id){
  selectedWorktree=id;worktreeSource?.close();worktreeSource=null;
  $('worktree-detail').hidden=false;$('worktree-refresh').disabled=false;
  if(view!=='worktrees'||document.hidden){stopWorktreeWatch();return;}
  clearWorktreeContent('Connecting to shared worktree reconciliation…');
  const active=new EventSource('/api/v1/worktrees/events?id='+encodeURIComponent(id));worktreeSource=active;
  active.addEventListener('worktree',event=>{
    if(worktreeSource!==active)return;
    const update=JSON.parse(event.data);
    if(update.stale||!update.diff){clearWorktreeContent(update.error||'Worktree content is unavailable.');return;}
    const result=update.diff;
    $('worktree-detail-state').textContent=result.coverage+' Content revision: '+result.revision+'. Live: shared server reconciliation; not an atomic filesystem snapshot.';
    $('worktree-staged').textContent=result.staged||'No staged tracked changes.';
    $('worktree-unstaged').textContent=result.unstaged||'No unstaged tracked changes.';
    $('worktree-untracked').textContent=result.untracked||'No untracked changes.';
  });
  active.onerror=()=>{if(worktreeSource===active)clearWorktreeContent('Worktree stream disconnected. Reconnecting; current contents are unknown.');};
}
$('worktree-refresh').addEventListener('click',()=>{if(selectedWorktree)loadWorktreeDiff(selectedWorktree);});
document.addEventListener('visibilitychange',()=>{
  if(document.hidden)stopWorktreeWatch();else if(view==='worktrees'&&selectedWorktree)loadWorktreeDiff(selectedWorktree);
});
window.addEventListener('pagehide',stopWorktreeWatch);
async function compareBranch(patch=false) {
  if(!branch || !gitState) return;
  const historyKey=branch+':'+gitState.facts?.branches.find(b=>b.ref===branch)?.tip+':'+(gitState.facts?.historyBoundary??'');
  if(gitHistoryKey&&gitHistoryKey!==historyKey){gitHistoryRequest?.abort();gitHistoryKey=null;$('git-history').replaceChildren();$('git-history-state').textContent='Branch or history boundary changed. Reload recorded history.';}
  const target=$('git-target').value, tip=gitState.facts?.branches.find(b => b.ref===branch)?.tip;
  const key=`${target}:${branch}:${gitState.revision}`;
  if(comparisonKey===key && !patch) return;
  comparisonKey=key; comparisonRequest?.abort(); const activeRequest=new AbortController(); comparisonRequest=activeRequest;
  $('branch-title').textContent=branch.replace('refs/heads/',''); $('comparison').replaceChildren(); $('comparison-provenance').replaceChildren(); $('patch').textContent=''; $('load-patch').hidden=true;
  if(!tip || gitState.stale || gitState.policyError || !target) { $('comparison-state').textContent='Comparison unavailable: missing branch, stale Git, or invalid target policy.'; return; }
  $('comparison-state').textContent='Reading Git comparison…';
  try {
    const response=await fetch(`/api/v1/branches/compare?target=${encodeURIComponent(target)}&branch=${encodeURIComponent(branch)}&patch=${patch}`,{signal:activeRequest.signal});
    if(!response.ok) throw new Error('Comparison unavailable or refs are updating. Select the branch again to retry.');
    const result=await response.json(), c=result.comparison;
    if(comparisonKey!==key || comparisonRequest!==activeRequest) return;
    $('comparison-state').textContent=c.warning || (c.containedInTarget ? 'Contained in target now · exact integration time unknown' : 'Integration not verified by ancestry');
    for(const [label,value] of [['Ahead / behind',`${c.ahead} / ${c.behind}`],['Files',c.files.map(f => `${f.path}: ${f.binary ? 'binary' : '+'+f.additions+' / −'+f.deletions}`).join('\n') || 'No file changes']]) $('comparison').append(text('dt',label),text('dd',value));
    for(const [label,value] of [['Target tip',c.targetTip],['Branch tip',c.issueTip],['Merge base',c.mergeBase || 'Unknown'],['Comparison basis',c.basis]]) $('comparison-provenance').append(text('dt',label),text('dd',value));
    $('load-patch').hidden=!c.mergeBase;
    if(result.patch) $('patch').textContent=result.patch.available ? (result.patch.text || 'Empty patch') : result.patch.warning;
  } catch(error) {if(error.name!=='AbortError') {$('comparison-state').textContent=error.message;comparisonKey=null;}}
}
function ordinaryLabel(label){
  return typeof label==='string'&&label.length>0&&label.length<=100&&label.trim()===label&&
    !/[\x00-\x1f\x7f,"]/.test(label)&&!/^((abacus|gt):)/i.test(label);
}
function selectedLabels(container){return [...container.querySelectorAll('input:checked')].map(input=>input.value);}
function labelPickerOptions(container,labels,selected){
  container.replaceChildren(...labels.map(label=>{
    const row=document.createElement('label');row.className='label-choice';
    const input=document.createElement('input');input.type='checkbox';input.value=label;input.checked=selected.includes(label);
    row.append(input,text('span',label));return row;
  }));
  if(!labels.length)container.append(text('p','No ordinary labels available.'));
}
function projectOrdinaryLabels(){
  if(projectLabelsRevision!==issueRevision){
    projectLabelChoices=[...new Set([...issues.values()].flatMap(issue=>issue.labels||[]).filter(ordinaryLabel))].sort();
    projectLabelsRevision=issueRevision;
  }
  return projectLabelChoices;
}
function refreshLabelChoices(issue,draft){
  if(!issue)return;
  const existing=[...new Set(issue.labels.filter(ordinaryLabel))].sort();
  const add=projectOrdinaryLabels().filter(label=>!issue.labels.includes(label));
  const key=JSON.stringify([issue.id,existing,add]);
  if(key===labelChoicesKey)return;
  labelChoicesKey=key;
  labelPickerOptions($('write-add-options'),add,draft.addSelected||[]);
  labelPickerOptions($('write-remove-options'),existing,draft.removeSelected||[]);
  $('write-add-menu').querySelector('summary').textContent=`Add existing project labels${add.length?` · ${add.length}`:''}`;
  $('write-remove-menu').querySelector('summary').textContent=`Remove labels on this issue${existing.length?` · ${existing.length}`:''}`;
}
function setupEditor(issue) {
  $('issue-form').hidden=!canEdit || !timeline.live || liveReadPending;
  if(editorId===issue.id){const draft=drafts.get(issue.id);if(draft)refreshLabelChoices(issue,draft);return;}
  editorId=issue.id;
  let draft=drafts.get(issue.id);
  if(!draft) {draft={revision:issue.revision,action:'comment',text:'',title:issue.title,description:issue.description || '',priority:issue.priority ?? 2,status:issue.status,reasoningLevel:reasoningLevel(issue),baseline:{title:issue.title,description:issue.description||'',priority:issue.priority??2},appendNotes:'',addLabels:'',removeLabels:'',addSelected:[],removeSelected:[],customMode:false,result:'',request:null,pending:false};drafts.set(issue.id,draft);}
  for(const [id,key] of [['write-action','action'],['write-text','text'],['write-title','title'],['write-description','description'],['write-priority','priority'],['write-status','status'],['write-reasoning','reasoningLevel'],['write-notes','appendNotes'],['write-add-labels','addLabels'],['write-remove-labels','removeLabels']]) $(id).value=draft[key];
  $('write-custom-mode').checked=!!draft.customMode;labelChoicesKey=null;
  editorFeedback(draft);
}
function reasoningLevel(issue){
  const labels=issue?.labels||[];
  for(const level of ['high','medium','low'])if(labels.includes(`abacus:${level}_reasoning`))return level;
  return 'none';
}
function labelDraftValues(draft){
  const lines=value=>value.split('\n').map(label=>label.trim()).filter(Boolean);
  if(draft.action==='labels')return {
    add:[...new Set([...(draft.addSelected||[]),...(draft.customMode?lines(draft.addLabels):[])])],
    remove:[...(draft.removeSelected||[])]
  };
  return {add:lines(draft.addLabels),remove:lines(draft.removeLabels)};
}
function editorFeedback(draft) {
  const attention=draft.action.startsWith('attention-');
  refreshLabelChoices(issues.get(editorId),draft);
  $('comment-fields').hidden=!attention&&draft.action!=='comment';
  $('edit-fields').hidden=!['edit','labels'].includes(draft.action);
  for(const id of ['write-title','write-description','write-priority','write-notes'])$(id).closest('label').hidden=draft.action==='labels';
  $('label-quick-picks').hidden=draft.action!=='labels';
  $('write-label-help').hidden=draft.action==='labels';
  $('write-add-labels').closest('label').hidden=draft.action==='labels'&&!draft.customMode;
  $('write-remove-labels').closest('label').hidden=draft.action==='labels';
  $('status-fields').hidden=draft.action!=='status';
  $('reasoning-fields').hidden=draft.action!=='reasoning-set';
  $('write-hint').textContent=draft.resumeCommentId?'Reviewed stored explanation '+draft.resumeCommentId+' · next submit skips the comment.':
    draft.action==='attention-request'?'Explanation required. Appends your exact comment, then flags attention; status and assignment stay unchanged.':
    draft.action==='attention-request-block'?'Explanation required. Adds attention and blocks the issue; this may conflict with active or reserved work.':
    draft.action==='attention-resolve'?'Response optional. A failed response prevents resolution. This clears only attention—not status or assignment.':
    draft.action==='attention-resolve-reopen'?'Response optional. Clears attention, sets Open, and clears assignee; this may conflict with active or reserved work.':
    draft.action==='status'?'Status changes can disrupt assigned or running work. Confirmation allows the change but does not stop a worker, release a reservation, or merge code.':
    draft.action==='reasoning-set'?'Replaces only the Abacus reasoning label; other labels are preserved.':
    draft.action==='labels'?'Add and remove ordinary labels without replacing unrelated labels.':'';
  $('write-actor').textContent=`Stored as ${operator} · no automatic remote push`;
  $('write-result').textContent=draft.result;
  $('write-submit').textContent=draft.request ? 'Retry same request' : 'Submit';
  for(const input of $('issue-form').querySelectorAll('input,textarea,select')) input.disabled=draft.pending || !!draft.request;
  $('write-submit').disabled=draft.pending;
  $('write-review').hidden=!draft.request || draft.pending;
  $('write-text').disabled=draft.pending||!!draft.request||!!draft.resumeCommentId;
  const issue=issues.get(editorId),flag=issue?.labels.includes('abacus:needs-user-attention');
  const request=draft.action.startsWith('attention-request');
  const missing=request?(!flag||draft.action==='attention-request-block'&&issue.status!=='blocked'):(flag||draft.action==='attention-resolve-reopen'&&(issue.status!=='open'||issue.assignee));
  $('write-finish-attention').hidden=!attention||!draft.recordedCommentId||draft.pending||!issue||!missing;
  $('write-finish-attention').textContent='Review and finish remaining attention step';
}
$('issue-form').addEventListener('input',event => {
  const draft=drafts.get(editorId);if(!draft || draft.pending || draft.request)return;
  draft.resumeCommentId=null;draft.recordedCommentId=null;draft.action=$('write-action').value;draft.text=$('write-text').value;draft.title=$('write-title').value;draft.description=$('write-description').value;draft.priority=Number($('write-priority').value);draft.status=$('write-status').value;draft.reasoningLevel=$('write-reasoning').value;draft.appendNotes=$('write-notes').value;draft.addLabels=$('write-add-labels').value;draft.removeLabels=$('write-remove-labels').value;draft.customMode=$('write-custom-mode').checked;
  if(event.target.closest('#write-add-options'))draft.addSelected=selectedLabels($('write-add-options'));
  if(event.target.closest('#write-remove-options'))draft.removeSelected=selectedLabels($('write-remove-options'));
  editorFeedback(draft);
});
$('write-review').addEventListener('click',() => {
  const draft=drafts.get(editorId), issue=issues.get(editorId);if(!draft || !issue)return;
  if(!confirm(`Operator: ${operator}. Review the current issue and comments first. An unknown or partial result may already have stored your append. Start a NEW operation using the displayed revision?`))return;
  draft.resumeCommentId=null;draft.recordedCommentId=null;draft.request=null;draft.revision=issue.revision;draft.result='Using the displayed revision. Review text carefully before submitting.';editorFeedback(draft);
});
$('write-finish-attention').addEventListener('click',()=>{
  const draft=drafts.get(editorId),issue=issues.get(editorId);if(!draft||!issue||!draft.recordedCommentId||draft.pending)return;
  if(!confirm('Review current comments, attention label, status and assignee. Comment '+draft.recordedCommentId+' is stored. Start a NEW operation for only the missing step, without appending another response?'))return;
  draft.request=null;draft.revision=issue.revision;draft.resumeCommentId=draft.recordedCommentId;draft.recordedCommentId=null;
  draft.text='';$('write-text').value='';draft.result='Reviewed: submit only the missing attention/status step.';editorFeedback(draft);
});
$('issue-form').addEventListener('submit',async event => {
  event.preventDefault();if(!timeline.live||liveReadPending)return;const id=editorId,draft=drafts.get(id);if(!draft || draft.pending)return;
  if(!draft.request&&draft.action==='edit'&&!['title','description','priority'].some(k=>draft[k]!==draft.baseline[k])&&!draft.appendNotes&&!draft.addLabels.trim()&&!draft.removeLabels.trim()){draft.result='No content or label changes to submit.';editorFeedback(draft);return;}
  const labels=labelDraftValues(draft);
  if(!draft.request&&draft.action==='labels'&&!labels.add.length&&!labels.remove.length){draft.result='No label changes to submit.';editorFeedback(draft);return;}
  if(!draft.request&&['status','attention-request-block','attention-resolve-reopen'].includes(draft.action)){
    const issue=issues.get(id),target=draft.action==='status'?draft.status:draft.action==='attention-request-block'?'blocked':'open';
    const warning=draft.action==='status'
      ? 'WARNING: This may interrupt an agent or conflict with reserved work. It does not stop a worker, release a reservation, or merge code. Review worker/workspace state afterward.'
      : 'WARNING: This may interrupt an agent or conflict with reserved work. It does not stop a worker, release a reservation, or merge code. Review worker/workspace state afterward.';
    if(!confirm(`Operator: ${operator}. Change ${id} from ${issue?.status||'unknown'} to ${target}? ${warning}`))return;
  }
  draft.pending=true;draft.result='Pending · verifying stored result…';editorFeedback(draft);
  try {
    if(!draft.request) {
      const contextResponse=await fetch('/api/v1/mutations/context');if(!contextResponse.ok)throw new Error('Writes unavailable');const context=await contextResponse.json();
      const random=[...crypto.getRandomValues(new Uint8Array(16))].map(b=>b.toString(16).padStart(2,'0')).join('');
      draft.request={requestId:`${context.session}:${context.serverUnixMilliseconds}:${random}`,expectedRevision:draft.revision,action:draft.action};
      if(draft.action==='comment')draft.request.text=draft.text;
      else if(draft.action.startsWith('attention-')){
        if(draft.resumeCommentId){if(draft.action.startsWith('attention-request'))draft.request.existingCommentId=draft.resumeCommentId;}
        else if(draft.text)draft.request.text=draft.text;
      }
      else if(draft.action==='status'){draft.request.status=draft.status;draft.request.confirmOwnershipRisk=true;}
      else if(draft.action==='reasoning-set')draft.request.reasoningLevel=draft.reasoningLevel;
      else {
        if(draft.action==='edit'){for(const key of ['title','description','priority'])if(draft[key]!==draft.baseline[key])draft.request[key]=draft[key];if(draft.appendNotes)draft.request.appendNotes=draft.appendNotes;}
        if(labels.add.length)draft.request.addLabels=labels.add;
        if(labels.remove.length)draft.request.removeLabels=labels.remove;
      }
    }
    const response=await fetch(`/api/v1/issues/${encodeURIComponent(id)}/actions`,{method:'POST',headers:{'Content-Type':'application/json','X-Abacus-Request':'1'},body:JSON.stringify(draft.request)});
    const result=await mutationResponse(response);
    if(result.issue){issues.set(id,result.issue);projectLabelsRevision=null;}
    draft.result=`${result.outcome}: ${result.message}`;draft.recordedCommentId=result.recordedCommentId;
    if(result.outcome==='completed') {
      const completedRequest=draft.request;
      draft.recordedCommentId=null;draft.resumeCommentId=null;draft.request=null;draft.revision=result.revision;
      if(result.issue){
        draft.status=result.issue.status;draft.reasoningLevel=reasoningLevel(result.issue);
        for(const key of ['title','description','priority']){
          const stored=key==='priority'?(result.issue[key]??2):(result.issue[key]||'');
          // Refresh untouched or submitted fields, but retain unsent edits when
          // a different composer action (such as a comment) completes.
          if(draft[key]===draft.baseline[key]||key in completedRequest)draft[key]=stored;
          draft.baseline[key]=stored;
        }
      }
      draft.text='';
      if(completedRequest.action==='edit')draft.appendNotes='';
      if(['edit','labels'].includes(completedRequest.action)){draft.addLabels='';draft.removeLabels='';draft.addSelected=[];draft.removeSelected=[];labelChoicesKey=null;}
      if(editorId===id){
        for(const [field,key] of [['write-title','title'],['write-description','description'],['write-priority','priority'],['write-status','status'],['write-reasoning','reasoningLevel'],['write-notes','appendNotes'],['write-add-labels','addLabels'],['write-remove-labels','removeLabels']])$(field).value=draft[key];
        $('write-text').value='';
      }
    }
    render();
  } catch(error) {draft.result=`${error.message.startsWith('HTTP ') ? error.message+'. ' : ''}Result unavailable. Do not create another append blindly. Retry the SAME request, or review current content first.`;}
  finally {draft.pending=false;if(editorId===id)editorFeedback(draft);}
});
fetch('/api/v1/project').then(r => { if(!r.ok) throw new Error(); return r.json(); }).then(p => { runtimeSession=p.runtimeSession;canControlClaims=!!p.capabilities.claimControl;renderClaimControls();for(const option of $('write-action').options)if(option.value.startsWith('attention-'))option.disabled=!p.capabilities.attention;canReadHistory=p.capabilities.history;canReadProjectHistory=!!p.capabilities.projectHistory;canEdit=p.capabilities.editIssues;canResolveAttention=!!p.capabilities.attention;$('create-issue').hidden=!p.capabilities.createDrafts;operator=p.actor;const avatar=$('operator-avatar');avatar.textContent=String(p.actor||'?').trim().split(/\s+/).slice(0,2).map(part=>part[0]?.toLocaleUpperCase()||'?').join('');avatar.title=`${p.actor} · self-declared display identity; not signed in`;currentInspectorRevision=null;inspectorSourceKey=null;render();$('project').textContent=p.name;$('runtime-status').textContent=p.runtimeExplanation||'Runtime state unavailable'; $('actor').textContent=`Operator attribution: ${p.actor} (self-declared, not signed in)`; }).catch(() => $('project').textContent='Project unavailable');
connect();

$('load-git-history').addEventListener('click',async()=>{
  if(!branch||gitState?.stale)return;
  gitHistoryRequest?.abort();const request=new AbortController();gitHistoryRequest=request;
  const selectedBranch=branch;gitHistoryKey=branch+':'+gitState.facts?.branches.find(b=>b.ref===branch)?.tip+':'+(gitState.facts?.historyBoundary??'');
  $('git-history-state').textContent='Reading bounded Git history…';
  try{
    const response=await fetch('/api/v1/branches/history?branch='+encodeURIComponent(selectedBranch)+'&limit=100',{signal:request.signal});
    if(!response.ok)throw new Error('History unavailable or branch moved; reload to retry.');
    const data=await response.json();if(gitHistoryRequest!==request||branch!==selectedBranch)return;
    $('git-history-state').textContent=data.coverage+(data.limitReached?' Latest 100 commits only.':'')+(data.clockSkew?' Clock skew detected; topology order is preserved.':'');
    $('git-history').replaceChildren(...data.commits.map(c=>{
      const card=document.createElement('article');card.className='comment';
      card.append(text('strong',c.id.slice(0,12)),text('small',c.author+' · commit time '+new Date(c.committedAt).toLocaleString()),
        text('p',c.message),text('small','Parents: '+(c.parents.join(', ')||'none recorded')));
      return card;
    }));
  }catch(error){if(error.name!=='AbortError'&&gitHistoryRequest===request)$('git-history-state').textContent=error.message;}
});

$('stop-run').addEventListener('click',async()=>{
  if(!runtimeOnline||stopRunBusy||stopRunAccepted)return;
  if(!stopRunRequest){
    if(!confirm(`Operator: ${operator}. Stop this run? Running work will be interrupted through normal recovery and cleanup. The dashboard will disconnect; disconnection does not prove cleanup completed.`))return;
    stopRunRequest={session:runtimeSession,requestId:[...crypto.getRandomValues(new Uint8Array(16))].map(b=>b.toString(16).padStart(2,'0')).join(''),command:'stop',confirm:true};
  }
  stopRunBusy=true;renderClaimControls();
  try{
    const response=await fetch('/api/v1/run/actions',{method:'POST',headers:{'Content-Type':'application/json','X-Abacus-Request':'1'},body:JSON.stringify(stopRunRequest)});
    const result=await response.json();$('stop-run-result').textContent=result.message;
    stopRunAccepted=result.outcome==='accepted';
    if(result.outcome==='rejected')stopRunRequest=null;
  }catch{$('stop-run-result').textContent='Shutdown result unknown. Check the owning process and cleanup logs. If still connected, retry only this same request.';}
  finally{stopRunBusy=false;renderClaimControls();}
});
