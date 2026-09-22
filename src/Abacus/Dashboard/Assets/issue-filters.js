// Shared metadata predicates. Missing historical fields are unknown, never
// inferred from the current issue. Filters cannot affect dispatch/ownership.
export function matchesIssueMetadata(issue,filters={}){
  if(filters.type&&issue.issueType!==filters.type)return false;
  if(filters.target&&issue.target!==filters.target)return false;
  if(filters.assignee&&issue.assignee!==filters.assignee)return false;
  if(filters.label&&(!Array.isArray(issue.labels)||!issue.labels.includes(filters.label)))return false;
  if(filters.priority){
    if(filters.priority==='unknown'){if(Number.isInteger(issue.priority))return false;}
    else if(!Number.isInteger(issue.priority)||issue.priority!==Number(filters.priority))return false;
  }
  if(filters.attention){
    const known=Array.isArray(issue.labels),requested=known&&issue.labels.includes('abacus:needs-user-attention');
    if(filters.attention==='unknown'){if(known)return false;}
    else if(!known||requested!==(filters.attention==='requested'))return false;
  }
  return true;
}

export function matchesIssueText(issue,events,query,{from=-Infinity,to=Infinity,playhead=to,live=true,title=issue.title}={}){
  const q=(query||'').trim().toLowerCase();
  if(!q)return true;
  const contains=value=>typeof value==='string'&&value.toLowerCase().includes(q);
  if(contains(issue.id)||contains(title))return true;
  // Undated content is searchable only as current content, never backfilled into
  // a historical point. Explicit UI coverage distinguishes it from ranged text.
  if(live&&(contains(issue.notes)||(issue.comments||[]).some(c=>!Number.isFinite(Date.parse(c.createdAt))&&contains(c.text))))return true;
  const end=live?to:Math.min(to,playhead);
  return events.some(e=>(e.kind==='comment'||e.kind==='snapshot')&&e.time>=from&&e.time<=end&&contains(e.text));
}
