// Current-snapshot dependency projection. Only source-authored relationships become lines.
const CARD_W=246,CARD_H=76,COL_GAP=96,ROW_GAP=24,MARGIN=32;
const compare=(a,b)=>a.localeCompare(b);
export const zoomScrollOffset=(offset,oldScale,newScale,anchor)=>
  (offset+anchor)*newScale/oldScale-anchor;

export function layoutDependencyTree(issues){
  const byId=new Map(issues.map(issue=>[issue.id,issue]));
  const edges=[],unresolved=[];
  for(const issue of issues)for(const edge of issue.dependencies||[]){
    // Only blocks links are prerequisites. Parent-child is hierarchy, while
    // relates-to can be bidirectional; neither belongs in the blocking DAG.
    if(edge.type!=='blocks')continue;
    if(!byId.has(edge.dependsOnId)){unresolved.push(edge);continue;}
    if(edge.dependsOnId!==issue.id)edges.push({from:edge.dependsOnId,to:issue.id,type:edge.type});
  }
  edges.sort((a,b)=>compare(a.from,b.from)||compare(a.to,b.to)||compare(a.type,b.type));
  const neighbors=new Map(issues.map(issue=>[issue.id,new Set()]));
  for(const edge of edges){neighbors.get(edge.from).add(edge.to);neighbors.get(edge.to).add(edge.from);}
  const seen=new Set(),components=[],isolated=[];
  for(const issue of [...issues].sort((a,b)=>compare(a.id,b.id))){
    if(seen.has(issue.id))continue;
    if(!neighbors.get(issue.id).size){seen.add(issue.id);isolated.push(issue.id);continue;}
    const group=[],queue=[issue.id];seen.add(issue.id);
    for(let i=0;i<queue.length;i++){
      const id=queue[i];group.push(id);
      for(const other of [...neighbors.get(id)].sort(compare))if(!seen.has(other)){seen.add(other);queue.push(other);}
    }
    components.push(group);
  }
  components.sort((a,b)=>b.length-a.length||compare(a[0],b[0]));
  const positions=new Map();let cursorY=MARGIN,maxX=0,cycles=0;
  for(const group of components){
    const ids=new Set(group),within=edges.filter(e=>ids.has(e.from)&&ids.has(e.to));
    const outgoing=new Map(group.map(id=>[id,[]])),incoming=new Map(group.map(id=>[id,[]]));
    for(const edge of within){outgoing.get(edge.from).push(edge.to);incoming.get(edge.to).push(edge.from);}
    const remaining=new Map(group.map(id=>[id,incoming.get(id).length]));
    const ready=group.filter(id=>remaining.get(id)===0).sort(compare),order=[];
    while(ready.length){
      const id=ready.shift();order.push(id);
      for(const next of outgoing.get(id))if(remaining.set(next,remaining.get(next)-1).get(next)===0){ready.push(next);ready.sort(compare);}
    }
    // Malformed/cyclic data must still render all issues; omit only the back edges.
    const orderSet=new Set(order);
    for(const id of group.sort(compare))if(!orderSet.has(id)){order.push(id);cycles++;}
    const index=new Map(order.map((id,i)=>[id,i]));
    const dag=within.filter(e=>index.get(e.from)<index.get(e.to));
    const rank=new Map(order.map(id=>[id,0]));
    for(const id of order)for(const edge of dag)if(edge.from===id)rank.set(edge.to,Math.max(rank.get(edge.to),rank.get(id)+1));
    const layers=[];
    for(const id of order){const r=rank.get(id);(layers[r]??=[]).push(id);}
    // Alternating median/barycenter sweeps keep related nodes close and reduce
    // crossings. Stable ID tie breaks prevent jitter on identical snapshots.
    const sortLayer=(r,forward)=>{
      const scores=new Map(layers[r].map((id,i)=>{
        const related=dag.filter(e=>forward?e.to===id:e.from===id)
          .map(e=>forward?e.from:e.to)
          .filter(other=>forward?rank.get(other)<r:rank.get(other)>r)
          .map(other=>{
            const layer=layers[rank.get(other)];
            return (layer.indexOf(other)+.5)/layer.length;
          });
        return [id,related.length?related.reduce((a,b)=>a+b,0)/related.length:(i+.5)/layers[r].length];
      }));
      layers[r].sort((a,b)=>scores.get(a)-scores.get(b)||compare(a,b));
    };
    for(let pass=0;pass<8;pass++){
      for(let r=1;r<layers.length;r++)sortLayer(r,true);
      for(let r=layers.length-2;r>=0;r--)sortLayer(r,false);
    }
    const rows=Math.max(...layers.map(layer=>layer.length));
    for(let r=0;r<layers.length;r++){
      const layer=layers[r],offset=(rows-layer.length)*(CARD_H+ROW_GAP)/2;
      layer.forEach((id,i)=>positions.set(id,{x:MARGIN+r*(CARD_W+COL_GAP),y:cursorY+offset+i*(CARD_H+ROW_GAP)}));
    }
    maxX=Math.max(maxX,MARGIN+(layers.length-1)*(CARD_W+COL_GAP)+CARD_W);
    cursorY+=rows*(CARD_H+ROW_GAP)+54;
  }
  // Unconnected issues remain visible but do not make a deep single-file column.
  if(isolated.length){const cols=Math.min(4,Math.max(1,Math.floor((maxX||1200)/(CARD_W+ROW_GAP))));
    isolated.forEach((id,i)=>positions.set(id,{x:MARGIN+(i%cols)*(CARD_W+ROW_GAP),y:cursorY+Math.floor(i/cols)*(CARD_H+ROW_GAP)}));
    maxX=Math.max(maxX,MARGIN+cols*(CARD_W+ROW_GAP));
    cursorY+=Math.ceil(isolated.length/cols)*(CARD_H+ROW_GAP)+MARGIN;
  }
  return {positions,edges:edges.filter(e=>positions.get(e.from).x<positions.get(e.to).x),
    width:Math.max(800,maxX+MARGIN),height:Math.max(350,cursorY),unresolved:unresolved.length,cycles,isolated:isolated.length};
}

export class DependencyTree{
  constructor({pane,scroll,sizer,surface,lines,nodes,summary,zoom,zoomValue,onSelect}){
    Object.assign(this,{pane,scroll,sizer,surface,lines,nodes,summary,zoom,zoomValue,onSelect});
    this.scale=1;this.layout=null;this.buttons=new Map();this.issues=[];
    zoom.addEventListener('input',()=>this.setScale(Number(zoom.value)));
    this.bindNavigation();
  }
  setScale(scale,anchorX=this.scroll.clientWidth/2,anchorY=this.scroll.clientHeight/2){
    // Keep the point under the cursor (or viewport center) fixed while zooming.
    const oldScale=this.scale,oldLeft=this.scroll.scrollLeft,oldTop=this.scroll.scrollTop;
    this.scale=Math.max(.01,Math.min(4,scale));this.zoom.value=this.scale;
    this.zoomValue.textContent=Math.round(this.scale*100)+'%';
    if(!this.layout)return;
    this.surface.style.width=this.layout.width+'px';this.surface.style.height=this.layout.height+'px';
    this.surface.style.transform=`scale(${this.scale})`;
    this.sizer.style.width=this.layout.width*this.scale+'px';
    this.sizer.style.height=this.layout.height*this.scale+'px';
    this.scroll.scrollLeft=zoomScrollOffset(oldLeft,oldScale,this.scale,anchorX);
    this.scroll.scrollTop=zoomScrollOffset(oldTop,oldScale,this.scale,anchorY);
  }
  fit(){if(!this.layout)return;
    this.setScale(Math.min(1,(this.scroll.clientWidth-24)/this.layout.width,
      (this.scroll.clientHeight-24)/this.layout.height));
    this.scroll.scrollTo({left:0,top:0});
  }
  bindNavigation(){
    const pointers=new Map();
    this.scroll.addEventListener('pointerdown',event=>{
      if(event.target.closest('button')||![0,1].includes(event.button))return;
      this.scroll.focus({preventScroll:true});
      this.scroll.setPointerCapture(event.pointerId);
      pointers.set(event.pointerId,{x:event.clientX,y:event.clientY});
      this.scroll.classList.add('dragging');
      if(event.button===1)event.preventDefault();
    });
    this.scroll.addEventListener('pointermove',event=>{
      const previous=pointers.get(event.pointerId);if(!previous)return;
      const old=[...pointers.values()];
      pointers.set(event.pointerId,{x:event.clientX,y:event.clientY});
      if(pointers.size===2){
        const next=[...pointers.values()],distance=points=>Math.hypot(points[0].x-points[1].x,points[0].y-points[1].y);
        const rect=this.scroll.getBoundingClientRect();
        this.setScale(this.scale*distance(next)/Math.max(1,distance(old)),
          (next[0].x+next[1].x)/2-rect.left,(next[0].y+next[1].y)/2-rect.top);
      }else{
        this.scroll.scrollLeft-=event.clientX-previous.x;
        this.scroll.scrollTop-=event.clientY-previous.y;
      }
      event.preventDefault();
    });
    const release=event=>{pointers.delete(event.pointerId);if(!pointers.size)this.scroll.classList.remove('dragging');};
    this.scroll.addEventListener('pointerup',release);
    this.scroll.addEventListener('pointercancel',release);
    this.scroll.addEventListener('lostpointercapture',release);
    this.scroll.addEventListener('wheel',event=>{
      // Match the timeline: unmodified wheel scrolls; Ctrl/⌘ wheel zooms.
      if(!event.ctrlKey&&!event.metaKey)return;
      event.preventDefault();
      const rect=this.scroll.getBoundingClientRect();
      this.setScale(this.scale*Math.exp(-Math.max(-200,Math.min(200,event.deltaY))*.002),
        event.clientX-rect.left,event.clientY-rect.top);
    },{passive:false});
    this.scroll.addEventListener('keydown',event=>{
      if(event.target!==this.scroll)return;
      const key=event.key;
      if(key.toLowerCase()==='f'){event.preventDefault();this.fit();return;}
      if(['+','=','-'].includes(key)){
        event.preventDefault();this.setScale(this.scale*(key==='-'?1/1.1:1/.9));return;
      }
      if(!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(key))return;
      event.preventDefault();
      const step=60;
      if(key==='ArrowLeft')this.scroll.scrollLeft-=step;
      if(key==='ArrowRight')this.scroll.scrollLeft+=step;
      if(key==='ArrowUp')this.scroll.scrollTop-=step;
      if(key==='ArrowDown')this.scroll.scrollTop+=step;
    });
  }
  focus(id){const p=this.layout?.positions.get(id);if(!p)return;
    this.scroll.scrollTo({left:Math.max(0,(p.x+CARD_W/2)*this.scale-this.scroll.clientWidth/2),
      top:Math.max(0,(p.y+CARD_H/2)*this.scale-this.scroll.clientHeight/2),behavior:'smooth'});
    this.buttons.get(id)?.focus({preventScroll:true});
  }
  render(issues,selected,query='',status=''){
    const restoreFocus=this.nodes.contains(document.activeElement);
    this.issues=issues;this.layout=layoutDependencyTree(issues);
    const {positions,edges,width,height,unresolved,cycles,isolated}=this.layout;
    this.lines.setAttribute('viewBox',`0 0 ${width} ${height}`);this.lines.setAttribute('width',width);this.lines.setAttribute('height',height);
    this.lines.replaceChildren();this.nodes.replaceChildren();this.buttons.clear();
    const svg='http://www.w3.org/2000/svg';
    for(const edge of edges){const from=positions.get(edge.from),to=positions.get(edge.to),path=document.createElementNS(svg,'path');
      const x1=from.x+CARD_W,y1=from.y+CARD_H/2,x2=to.x,y2=to.y+CARD_H/2,mid=(x1+x2)/2;
      path.setAttribute('d',`M ${x1} ${y1} C ${mid} ${y1}, ${mid} ${y2}, ${x2} ${y2}`);
      path.setAttribute('class','dependency-line'+(selected&&(selected===edge.from||selected===edge.to)?' related':''));
      this.lines.append(path);
    }
    const q=query.trim().toLowerCase();
    for(const issue of issues){const p=positions.get(issue.id),button=document.createElement('button');
      button.type='button';button.className='dependency-card';button.dataset.status=issue.status||'unknown';
      button.style.left=p.x+'px';button.style.top=p.y+'px';
      if(issue.id===selected)button.classList.add('selected');
      if((q&&!`${issue.id} ${issue.title}`.toLowerCase().includes(q))||(status&&issue.status!==status))button.classList.add('dimmed');
      const identity=document.createElement('span');identity.className='dependency-id';identity.textContent=issue.id;
      const state=document.createElement('span');state.className='dependency-state';state.textContent=(issue.status||'unknown').replace('_',' ');
      const title=document.createElement('span');title.className='dependency-title';title.textContent=issue.title||'(untitled issue)';
      button.append(identity,state,title);button.setAttribute('aria-label',`${issue.id}, ${issue.title}, ${issue.status||'unknown'}`);
      button.onclick=()=>this.onSelect(issue.id);this.nodes.append(button);this.buttons.set(issue.id,button);
    }
    const unknown=issues.filter(i=>!Array.isArray(i.dependencies)).length;
    this.summary.textContent=`${issues.length} issues · ${edges.length} blocking links${unknown?` · ${unknown} unknown sources`:''}${unresolved?` · ${unresolved} external links`:''}${cycles?` · ${cycles} cyclic issues`:''}`;
    this.summary.title=`${isolated} unconnected issues. Unknown sources have no recorded relationship data; external links point outside this snapshot. Cyclic issues cannot be layered.`;
    this.setScale(this.scale);
    if(restoreFocus)this.buttons.get(selected)?.focus({preventScroll:true});
  }
}
