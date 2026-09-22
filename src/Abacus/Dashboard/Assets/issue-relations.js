// Source-authored edges only. Counts, issue names and branches are not edges.
export class IssueRelations {
  constructor(){this.sources=new Map();this.incoming=new Map();this.unknown=new Set();this.revision=0;}
  update(issues){
    for(const [id,old] of this.sources)if(!issues.has(id)||issues.get(id).revision!==old.revision){
      for(const edge of old.dependencies||[]){const entries=this.incoming.get(edge.dependsOnId);entries?.delete(id);if(!entries?.size)this.incoming.delete(edge.dependsOnId);}
      this.sources.delete(id);this.unknown.delete(id);this.revision++;
    }
    for(const [id,issue] of issues)if(!this.sources.has(id)){
      this.sources.set(id,issue);this.revision++;
      if(!Array.isArray(issue.dependencies)){this.unknown.add(id);continue;}
      for(const edge of issue.dependencies){
        if(!this.incoming.has(edge.dependsOnId))this.incoming.set(edge.dependsOnId,new Map());
        const entries=this.incoming.get(edge.dependsOnId);
        if(!entries.has(id))entries.set(id,[]);
        entries.get(id).push(edge);
      }
    }
  }
  forIssue(id){
    const issue=this.sources.get(id),outgoing=issue?.dependencies;
    return {outgoingKnown:Array.isArray(outgoing),incomingComplete:this.unknown.size===0,
      edges:[...(outgoing||[]).map(edge=>({...edge,direction:'outgoing',otherId:edge.dependsOnId})),
        ...[...(this.incoming.get(id)?.values()||[])].flat().map(edge=>({...edge,direction:'incoming',otherId:edge.issueId}))]
        .sort((a,b)=>a.direction.localeCompare(b.direction)||a.otherId.localeCompare(b.otherId)||a.type.localeCompare(b.type))};
  }
}
