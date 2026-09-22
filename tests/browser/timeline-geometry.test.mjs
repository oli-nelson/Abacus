import test from 'node:test';
import assert from 'node:assert/strict';
import {TimelineRenderer} from '../../src/Abacus/Dashboard/Assets/timeline-gl.js';
function geometry(scale,paths,markers){
 let data;
 const renderer=Object.create(TimelineRenderer.prototype);
 Object.assign(renderer,{geometryScale:scale,uploads:0,gl:{bindBuffer(){},bufferData(_target,value){data=value;}},buffer:{}});
 renderer.setScene({paths,markers,floor:-4,ceiling:4,nowX:100});
 return {data,vertices:renderer.counts[0]};
}
test('spherical markers retain equal world radii at 10x time and compressed vertical scale',()=>{
 const scale=[10,.6,1],{data,vertices}=geometry(scale,[],[{pos:[0,0,0],shape:'sphere',color:'#00ffff'}]);
 const extents=[0,0,0];
 for(let i=0;i<vertices;i++){
  const p=[0,1,2].map(k=>data[i*11+k]*scale[k]);
  assert.ok(Math.abs(Math.hypot(...p)-.36)<1e-6);
  p.forEach((v,k)=>extents[k]=Math.max(extents[k],Math.abs(v)));
 }
 for(const extent of extents)assert.ok(Math.abs(extent-.36)<1e-6);
});
test('stretched tubes retain their radius and perpendicular cross-sections',()=>{
 const scale=[10,.6,1],{data,vertices}=geometry(scale,[{points:[[0,0,0],[1,1,0]],color:'#00ffff'}],[]);
 const tangent=[10,.6,0],length=Math.hypot(...tangent);
 for(let i=0;i<vertices;i++){
  const p=[0,1,2].map(k=>data[i*11+k]*scale[k]),along=p.reduce((sum,v,k)=>sum+v*tangent[k],0)/(length*length);
  const radial=p.map((v,k)=>v-along*tangent[k]);
  assert.ok(Math.abs(Math.hypot(...radial)-.048)<1e-5);
 }
});
