import {matchesIssueMetadata} from './issue-filters.js';
// Pure current-snapshot table projection. No network or history inference.
const fields=new Set(['id','title','status','priority','assignee']);
const collator=new Intl.Collator('en',{numeric:true,sensitivity:'base'});
export function issuePage(issues,{query='',status='',sort='id',direction='asc',page=0,pageSize=50,metadata={},searchMatches=null}={}){
  if(!fields.has(sort))sort='id';
  direction=direction==='desc'?'desc':'asc';
  pageSize=[25,50,100].includes(pageSize)?pageSize:50;
  const q=query.toLowerCase();
  const matches=issues.filter(i=>matchesIssueMetadata(i,metadata)&&(!status||i.status===status)&&(searchMatches?searchMatches(i):`${i.id} ${i.title}`.toLowerCase().includes(q)));
  const value=i=>sort==='status'&&i.status==='closed'?'Completed':i[sort];
  matches.sort((a,b)=>{
    const x=value(a),y=value(b),emptyX=x===null||x===undefined||x==='',emptyY=y===null||y===undefined||y==='';
    if(emptyX!==emptyY)return emptyX?1:-1; // Unknown/unassigned always last.
    const order=emptyX?0:sort==='priority'?x-y:collator.compare(String(x),String(y));
    return order*(direction==='desc'?-1:1)||(a.id<b.id?-1:a.id>b.id?1:0);
  });
  const pages=Math.max(1,Math.ceil(matches.length/pageSize));
  page=Number.isFinite(page)?Math.max(0,Math.min(pages-1,Math.trunc(page))):0;
  return {rows:matches.slice(page*pageSize,(page+1)*pageSize),total:matches.length,pages,page,pageSize,sort,direction};
}
