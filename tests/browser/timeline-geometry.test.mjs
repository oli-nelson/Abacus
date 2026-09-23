import test from 'node:test';
import assert from 'node:assert/strict';
import {TimelineRenderer,farClipDistance,clipDepthCoefficients} from '../../src/Abacus/Dashboard/Assets/timeline-gl.js';
function geometry(scale,paths,markers){
 let data;
 const renderer=Object.create(TimelineRenderer.prototype);
 Object.assign(renderer,{geometryScale:scale,uploads:0,gl:{bindBuffer(){},bufferData(_target,value){data=value;}},buffer:{}});
 renderer.setScene({paths,markers,floor:-4,ceiling:4,nowX:100});
 return {data,vertices:renderer.counts[0],counts:renderer.counts};
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
  assert.ok(Math.abs(Math.hypot(...radial)-.07)<1e-5);
 }
});
test('path tubes carry their radius for the screen-space minimum at distant zoom',()=>{
 const {data,vertices,counts}=geometry([1,1,1],[{points:[[0,0,0],[1,0,0]],color:'#00ffff'}],[]);
 assert.equal(vertices,48);
 for(let i=0;i<vertices;i++)assert.ok(Math.abs(data[i*11+10]-(2+.07))<1e-6);
 // Glow rings carry their own larger radii, so the shader need only expand
 // whichever ring would otherwise fall below the minimum screen width.
 const glowOffset=(counts[0]+counts[1]+counts[2])*11;
 assert.ok(data.length>glowOffset);
 assert.ok(Math.abs(data[glowOffset+10]-(2+.07*2.2))<1e-6);
});
test('far clip tracks zoomed-out camera and stretched scene paths',()=>{
 const bounds={min:[-12,-4,-5],max:[12,4,5]};
 const camera={distance:9000,target:[0,0,0],axisScale:[40,1,1]};
 const far=farClipDistance(bounds,camera);
 assert.ok(far>camera.distance+480);
 const [a,b]=clipDepthCoefficients(far);
 const projectedDepth=depth=>a-b/depth;
 assert.ok(projectedDepth(camera.distance+480)<1,'distant paths must remain before the far plane');
 assert.ok(Math.abs(projectedDepth(.1)+1)<1e-5,'near plane remains unchanged');
 assert.ok(farClipDistance(bounds,{...camera,distance:30,axisScale:[1,1,1]})>=500);
});
