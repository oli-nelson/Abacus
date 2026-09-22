import {clamp,simplifyStraightSegments} from './timeline-model.js';
const sub=(a,b)=>a.map((v,i)=>v-b[i]);
const dot=(a,b)=>a.reduce((s,v,i)=>s+v*b[i],0);
const cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
const unit=a=>{const n=Math.hypot(...a)||1;return a.map(x=>x/n);};
export const rgb=hex=>[1,3,5].map(i=>parseInt(hex.slice(i,i+2),16)/255);
export function cameraBasis(camera) {
  const {yaw,pitch,distance,target}=camera;
  const eye=[target[0]+Math.sin(yaw)*Math.cos(pitch)*distance,target[1]+Math.sin(pitch)*distance,target[2]+Math.cos(yaw)*Math.cos(pitch)*distance];
  const forward=unit(sub(target,eye)),right=unit(cross(forward,[0,1,0])),up=cross(right,forward);
  return {eye,forward,right,up};
}
export function projectPoint(point,camera,width,height) {
  const b=cameraBasis(camera),p=sub(point.map((v,i)=>camera.target[i]+(v-camera.target[i])*(camera.axisScale?.[i]??1)),b.eye),depth=dot(p,b.forward);
  if(depth<=.1)return null;
  const scale=1.9/((1-camera.perspective)*camera.distance+camera.perspective*depth);
  return {x:width/2+dot(p,b.right)*scale*height/2,y:height/2-dot(p,b.up)*scale*height/2,depth};
}
const vertex=`
attribute vec3 a_position;
attribute vec3 a_normal;
attribute vec4 a_color;
attribute float a_timed;
uniform vec3 u_eye,u_right,u_up,u_forward,u_target,u_axisScale;
uniform float u_aspect,u_distance,u_perspective;
varying vec4 v_color;
varying float v_time,v_timed,v_light;
void main(){
 vec3 p=u_target+(a_position-u_target)*u_axisScale-u_eye;
 float depth=dot(p,u_forward);
 float w=mix(u_distance,depth,u_perspective);
 gl_Position=vec4(dot(p,u_right)*1.9/u_aspect,dot(p,u_up)*1.9, (1.0004*depth-0.20004)*w/depth,w);
 float light=length(a_normal)<0.1 ? 1.0 : 0.85+0.3*max(0.0,dot(normalize(a_normal),normalize(vec3(-0.2,0.8,1.0))));
 v_color=vec4(a_color.rgb*light,a_color.a);v_time=a_position.x;v_timed=a_timed;v_light=light;
}`;
const fragment=`
precision mediump float;
varying vec4 v_color;
varying float v_time,v_timed,v_light;
uniform float u_playhead,u_fade,u_colorMix;
uniform vec3 u_fromColor;
void main(){if(v_timed>0.5 && v_time>u_playhead)discard;gl_FragColor=vec4(mix(v_color.rgb,u_fromColor*v_light,u_colorMix),v_color.a*u_fade);}
`;
export class TimelineRenderer {
  constructor(canvas,fallback,onFailure) {
    this.canvas=canvas;this.fallback=fallback;this.onFailure=onFailure;this.frames=0;this.uploads=0;this.scene=null;
    canvas.addEventListener('webglcontextlost',e=>{e.preventDefault();this.gl=null;this.onFailure('WebGL context lost · using 2D');});
    canvas.addEventListener('webglcontextrestored',()=>{this.initialize();if(this.scene)this.setScene(this.scene);this.onFailure(this.gl ? null : 'WebGL unavailable · using 2D');});
    this.initialize();
  }
  initialize(){
    try {
      const gl=this.canvas.getContext('webgl',{alpha:false,antialias:true,preserveDrawingBuffer:false});
      if(!gl)throw new Error();
      const compile=(type,source)=>{const shader=gl.createShader(type);gl.shaderSource(shader,source);gl.compileShader(shader);if(!gl.getShaderParameter(shader,gl.COMPILE_STATUS))throw new Error('Shader unavailable');return shader;};
      this.program=gl.createProgram();
      const vs=compile(gl.VERTEX_SHADER,vertex),fs=compile(gl.FRAGMENT_SHADER,fragment);
      gl.attachShader(this.program,vs);gl.attachShader(this.program,fs);gl.linkProgram(this.program);gl.deleteShader(vs);gl.deleteShader(fs);
      if(!gl.getProgramParameter(this.program,gl.LINK_STATUS))throw new Error();
      this.gl=gl;this.buffer=gl.createBuffer();this.locations={};
      for(const name of ['eye','right','up','forward','target','axisScale','aspect','distance','perspective','playhead','fade','colorMix','fromColor'])this.locations[name]=gl.getUniformLocation(this.program,'u_'+name);
    }catch{this.gl=null;}
  }
  setScene(scene) {
    this.scene=scene;
    if(!this.gl)return;
    const scale=this.geometryScale||[1,1,1];
    const solid=[],lines=[],planes=[],glow=[];this.arrivalRanges=[];this.glowArrivalRanges=[];
    const vertex=(into,p,n,c,timed=1)=>into.push(...p,...n,...c,timed);
    const triangle=(into,a,b,c,color,timed=1)=>{const normal=unit(cross(sub(b,a),sub(c,a)));for(const p of [a,b,c])vertex(into,p,normal,color,timed);};
    const line=(a,b,c,timed=0)=>{vertex(lines,a,[0,0,0],c,timed);vertex(lines,b,[0,0,0],c,timed);};
    const tube=(a,b,color,r=.032,timed=1,into=solid,startDirection=sub(b,a),endDirection=startDirection)=>{
      // Shared tangent rings meet exactly at bends; radial normals avoid faceted lighting.
      const ring=(p,direction,j)=>{
        const dir=unit(direction.map((v,k)=>v*scale[k])),side=unit(cross(dir,Math.abs(dir[1])>.9?[1,0,0]:[0,1,0])),up=cross(dir,side);
        const normal=side.map((v,k)=>v*Math.cos(j*Math.PI/4)+up[k]*Math.sin(j*Math.PI/4));
        return {pos:p.map((v,k)=>v+r*normal[k]/scale[k]),normal};
      };
      for(let j=0;j<8;j++){
        const a0=ring(a,startDirection,j),a1=ring(a,startDirection,j+1),b0=ring(b,endDirection,j),b1=ring(b,endDirection,j+1);
        for(const v of [a0,b0,b1,a0,b1,a1])vertex(into,v.pos,v.normal,color,timed);
      }
    };
    for(let x=-12;x<=12;x+=.5)line([x,scene.floor,-5],[x,scene.floor,5],[.08,.20,.30,x%2===0?.65:.25]);
    for(let z=-5;z<=5;z+=.5)line([-12,scene.floor,z],[12,scene.floor,z],[.08,.20,.30,.35]);
    for(let x=-12;x<=12;x+=4)line([x,scene.floor,-4],[x,scene.ceiling,-4],[.11,.25,.38,.35]);
    // Selected recorded events have quiet drop lines to the time grid, not new events.
    for(const guide of scene.guides||[]){
      const [x,y,z]=guide.pos,color=[...rgb(guide.color),.28];
      for(let i=0;i<24;i+=2){
        const at=t=>[x,scene.floor+(y-scene.floor)*t,z];
        line(at(i/24),at((i+1)/24),color,1);
      }
    }
    for(const path of scene.paths){
      const first=solid.length/11,glowFirst=glow.length/11;
      const color=[...rgb(path.color),path.alpha ?? 1],points=path.dashed?path.points:simplifyStraightSegments(path.points);
      const radius=path.radius??(path.selected?.075:.048),core=[...color.slice(0,3).map(v=>v*.72+.28),color[3]];
      for(let i=1;i<points.length;i++){
        if(path.dashed && i%4>=2)continue;
        const startDirection=sub(points[i],points[Math.max(0,i-2)]),endDirection=sub(points[Math.min(points.length-1,i+1)],points[i-1]);
        tube(points[i-1],points[i],core,radius,1,solid,startDirection,endDirection);
        tube(points[i-1],points[i],[...color.slice(0,3),.12],radius*2.2,1,glow,startDirection,endDirection);
        tube(points[i-1],points[i],[...color.slice(0,3),.035],radius*4,1,glow,startDirection,endDirection);
      }
      if(path.arrival!=null){
        this.arrivalRanges.push({first,end:solid.length/11,marker:path});
        this.glowArrivalRanges.push({first:glowFirst,end:glow.length/11,marker:path});
      }
    }

    for(const marker of scene.markers){
      const first=solid.length/11;
      const p=marker.pos,r=marker.selected?.46:.36,color=[...rgb(marker.color),1];
      if(marker.shape==='diamond'){
        const top=[p[0],p[1]+r*1.6,p[2]],bottom=[p[0],p[1]-r*1.6,p[2]];
        const ring=[[p[0]+r,p[1],p[2]],[p[0],p[1],p[2]+r],[p[0]-r,p[1],p[2]],[p[0],p[1],p[2]-r]];
        for(let j=0;j<4;j++){triangle(solid,top,ring[j],ring[(j+1)%4],color);triangle(solid,bottom,ring[(j+1)%4],ring[j],color);}
      }else{
        const point=(a,b)=>[p[0]+r*Math.sin(a)*Math.cos(b)/scale[0],p[1]+r*Math.cos(a)/scale[1],p[2]+r*Math.sin(a)*Math.sin(b)/scale[2]];
        for(let a=0;a<8;a++)for(let b=0;b<12;b++){
          const a0=a*Math.PI/8,a1=(a+1)*Math.PI/8,b0=b*Math.PI/6,b1=(b+1)*Math.PI/6;
          for(const pos of [point(a0,b0),point(a1,b0),point(a1,b1),point(a0,b0),point(a1,b1),point(a0,b1)])
            vertex(solid,pos,unit(sub(pos,p).map((v,k)=>v*scale[k])),color);
        }
      }
      if(marker.arrival!=null||marker.colorTransition)this.arrivalRanges.push({first,end:solid.length/11,marker});
    }
    for(const range of [...this.arrivalRanges,...this.glowArrivalRanges])range.count=range.end-range.first;
    if(scene.nowX>=-12 && scene.nowX<=12){
      const x=scene.nowX,a=[x,scene.floor,-4],b=[x,scene.ceiling,-4],c=[x,scene.ceiling,4],d=[x,scene.floor,4],color=[.04,.40,.85,.13];
      triangle(planes,a,b,c,color,0);triangle(planes,a,c,d,color,0);
      for(const [u,v] of [[a,b],[b,c],[c,d],[d,a]])line(u,v,[.12,.55,1,.6]);
    }
    this.counts=[solid.length/11,lines.length/11,planes.length/11,glow.length/11];
    const data=new Float32Array(solid.length+lines.length+planes.length+glow.length);data.set(solid);data.set(lines,solid.length);data.set(planes,solid.length+lines.length);data.set(glow,solid.length+lines.length+planes.length);
    const gl=this.gl;gl.bindBuffer(gl.ARRAY_BUFFER,this.buffer);gl.bufferData(gl.ARRAY_BUFFER,data,gl.STATIC_DRAW);this.uploads++;
  }
  arrivalOpacity(marker){const floor=marker.arrivalFloor||0;return marker.arrival==null?1:floor+(1-floor)*clamp((performance.now()-marker.arrival)/350,0,1);}
  finishArrivals(){for(const marker of [...(this.scene?.markers||[]),...(this.scene?.paths||[])]){delete marker.arrival;delete marker.arrivalFloor;delete marker.colorTransition;}this.arrivalRanges=[];this.glowArrivalRanges=[];}
  colorMix(marker){return marker.colorTransition?1-clamp((performance.now()-marker.colorTransition.start)/350,0,1):0;}
  markerColor(marker){const to=rgb(marker.color),mix=this.colorMix(marker);return to.map((v,i)=>v*(1-mix)+(marker.colorTransition?.from[i]??v)*mix);}
  markerAnimating(marker){return this.arrivalOpacity(marker)<1||this.colorMix(marker)>0;}
  activeArrivals(){return [...(this.scene?.markers||[]),...(this.scene?.paths||[])].some(marker=>this.markerAnimating(marker));}
  draw(camera,playheadX) {
    if(!this.scene)return;
    const scale=camera.axisScale||[1,1,1];
    if(JSON.stringify(this.geometryScale)!==JSON.stringify(scale)){this.geometryScale=[...scale];this.setScene(this.scene);}
    this.frames++;
    this.arrivalRanges=(this.arrivalRanges||[]).filter(range=>this.markerAnimating(range.marker));
    this.glowArrivalRanges=(this.glowArrivalRanges||[]).filter(range=>this.markerAnimating(range.marker));
    const rect=this.canvas.parentElement.getBoundingClientRect(),width=Math.max(1,rect.width),height=Math.max(1,rect.height),dpr=Math.min(2,devicePixelRatio||1);
    for(const canvas of [this.canvas,this.fallback])if(canvas.width!==Math.round(width*dpr)||canvas.height!==Math.round(height*dpr)){canvas.width=Math.round(width*dpr);canvas.height=Math.round(height*dpr);}
    this.canvas.hidden=!this.gl;this.fallback.hidden=!!this.gl;
    if(!this.gl){this.drawFallback({...camera,yaw:0,pitch:0,perspective:0},playheadX,width,height,dpr);return;}
    const gl=this.gl,b=cameraBasis(camera);
    gl.viewport(0,0,this.canvas.width,this.canvas.height);gl.clearColor(.018,.045,.075,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);
    gl.enable(gl.DEPTH_TEST);gl.enable(gl.BLEND);gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);gl.useProgram(this.program);gl.bindBuffer(gl.ARRAY_BUFFER,this.buffer);
    for(const [name,size,offset] of [['position',3,0],['normal',3,3],['color',4,6],['timed',1,10]]){
      const loc=gl.getAttribLocation(this.program,'a_'+name);gl.enableVertexAttribArray(loc);gl.vertexAttribPointer(loc,size,gl.FLOAT,false,44,offset*4);
    }
    for(const name of ['eye','right','up','forward'])gl.uniform3fv(this.locations[name],b[name]);
    gl.uniform3fv(this.locations.target,camera.target);gl.uniform3fv(this.locations.axisScale,camera.axisScale||[1,1,1]);
    gl.uniform1f(this.locations.aspect,width/height);gl.uniform1f(this.locations.distance,camera.distance);gl.uniform1f(this.locations.perspective,camera.perspective);gl.uniform1f(this.locations.playhead,playheadX);
    gl.depthMask(true);gl.uniform1f(this.locations.fade,1);gl.uniform1f(this.locations.colorMix,0);gl.uniform3fv(this.locations.fromColor,[0,0,0]);
    let cursor=0;
    for(const range of this.arrivalRanges||[]){
      if(range.first>cursor)gl.drawArrays(gl.TRIANGLES,cursor,range.first-cursor);
      gl.uniform1f(this.locations.fade,this.arrivalOpacity(range.marker));
      gl.uniform1f(this.locations.colorMix,this.colorMix(range.marker));gl.uniform3fv(this.locations.fromColor,range.marker.colorTransition?.from||[0,0,0]);
      gl.drawArrays(gl.TRIANGLES,range.first,range.count);gl.uniform1f(this.locations.fade,1);gl.uniform1f(this.locations.colorMix,0);cursor=range.end;
    }
    if(cursor<this.counts[0])gl.drawArrays(gl.TRIANGLES,cursor,this.counts[0]-cursor);
    gl.depthMask(false);gl.drawArrays(gl.LINES,this.counts[0],this.counts[1]);gl.drawArrays(gl.TRIANGLES,this.counts[0]+this.counts[1],this.counts[2]);
    if(this.glow!==false){
      gl.blendFunc(gl.SRC_ALPHA,gl.ONE);
      const base=this.counts[0]+this.counts[1]+this.counts[2];let cursor=0;
      for(const range of this.glowArrivalRanges){
        if(range.first>cursor)gl.drawArrays(gl.TRIANGLES,base+cursor,range.first-cursor);
        gl.uniform1f(this.locations.fade,this.arrivalOpacity(range.marker));
        gl.drawArrays(gl.TRIANGLES,base+range.first,range.count);gl.uniform1f(this.locations.fade,1);cursor=range.end;
      }
      if(cursor<this.counts[3])gl.drawArrays(gl.TRIANGLES,base+cursor,this.counts[3]-cursor);
    }
    gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);gl.depthMask(true);
  }
  drawFallback(camera,playheadX,width,height,dpr) {
    const ctx=this.fallback.getContext('2d');ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,width,height);ctx.fillStyle='#050d16';ctx.fillRect(0,0,width,height);
    const p=point=>projectPoint(point,camera,width,height);
    for(let x=-12;x<=12;x+=2){const a=p([x,this.scene.floor,0]),b=p([x,this.scene.ceiling,0]);ctx.strokeStyle='#18354a';ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.stroke();}
    ctx.lineWidth=1;ctx.shadowBlur=0;ctx.setLineDash([3,5]);ctx.globalAlpha=.28;
    for(const guide of this.scene.guides||[]){
      if(guide.pos[0]>playheadX)continue;
      const a=p(guide.pos),b=p([guide.pos[0],this.scene.floor,guide.pos[2]]);
      if(!a||!b)continue;
      ctx.strokeStyle=guide.color;ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.stroke();
    }
    ctx.globalAlpha=1;ctx.setLineDash([]);
    for(const path of this.scene.paths){
      ctx.globalAlpha=this.arrivalOpacity(path);ctx.strokeStyle=path.color;ctx.shadowColor=path.color;ctx.shadowBlur=this.glow===false?0:path.selected?10:6;ctx.lineWidth=path.selected?3:2;ctx.setLineDash(path.dashed?[6,5]:[]);ctx.beginPath();
      let previous=null;
      for(const pos of path.points){
        const beyond=pos[0]>playheadX;
        if(beyond&&!previous)break;
        // Match WebGL's exact time-plane clipping instead of stepping between samples.
        const point=beyond?previous.map((v,k)=>v+(pos[k]-v)*(playheadX-previous[0])/(pos[0]-previous[0])):pos;
        const q=p(point);
        if(q){if(previous)ctx.lineTo(q.x,q.y);else ctx.moveTo(q.x,q.y);}
        if(beyond)break;
        previous=pos;
      }
      ctx.stroke();ctx.globalAlpha=1;
    }
    ctx.shadowBlur=0;ctx.setLineDash([]);
    for(const m of this.scene.markers){if(m.pos[0]>playheadX)continue;const q=p(m.pos);ctx.globalAlpha=this.arrivalOpacity(m);ctx.fillStyle='rgb('+this.markerColor(m).map(v=>Math.round(v*255)).join(',')+')';ctx.beginPath();ctx.arc(q.x,q.y,m.selected?8:6,0,Math.PI*2);ctx.closePath();ctx.fill();ctx.strokeStyle='#d9faff';ctx.lineWidth=1.5;ctx.stroke();ctx.globalAlpha=1;}
    const n=p([this.scene.nowX,this.scene.floor,0]);ctx.strokeStyle='#3389c9';ctx.beginPath();ctx.moveTo(n.x,0);ctx.lineTo(n.x,height);ctx.stroke();
  }
}
