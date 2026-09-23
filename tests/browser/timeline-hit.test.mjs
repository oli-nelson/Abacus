import test from 'node:test';
import assert from 'node:assert/strict';
import {TimelineRenderer,pickScreenMarker,projectPoint,screenMarkerLayout} from '../../src/Abacus/Dashboard/Assets/timeline-gl.js';

const camera={yaw:0,pitch:0,distance:30,target:[0,0,0],perspective:1,axisScale:[1,1,1]};
for(const perspective of [1,0])test(`${perspective?'3D':'2D'} timeline markers use a fixed screen-space hit radius`,()=>{
  const view={...camera,perspective};
  for(const pos of [[-2,0,0],[2,0,-20]]){
    const marker={pos},point=projectPoint(pos,view,800,500);
    assert.equal(pickScreenMarker([marker],view,800,500,point.x+20,point.y,24),marker);
    assert.equal(pickScreenMarker([marker],view,800,500,point.x+25,point.y,24),null);
  }
});
test('overlapping targets choose the nearest center, then the foreground marker',()=>{
  const near={pos:[0,0,0]},far={pos:[0,0,-20]};
  assert.equal(pickScreenMarker([far,near],camera,800,500,400,250,24),near);
});
test('visible marker dots stay large at distance and shrink in dense clusters',()=>{
  const far={pos:[-5,0,-20]},alone=screenMarkerLayout([far],camera,800,500,100);
  assert.equal(alone[0].radius,7.5);
  const crowded=screenMarkerLayout(Array.from({length:1000},()=>({pos:[0,0,0]})),camera,800,500,100);
  assert.equal(crowded.length,1000);
  assert.ok(crowded.every(point=>point.radius===3.75));
  assert.deepEqual(screenMarkerLayout([{pos:[1,0,0]}],camera,800,500,0),[],'future markers stay hidden');
});
test('WebGL overlay renders a distant marker at its screen-space minimum',()=>{
  const circles=[],ctx={setTransform(){},clearRect(){},beginPath(){},arc(_x,_y,r){circles.push(r);},fill(){},stroke(){}};
  const renderer=Object.create(TimelineRenderer.prototype);
  const marker={pos:[0,0,-20],color:'#ff646c'};
  renderer.overlay={getContext:()=>ctx};renderer.scene={markers:[marker]};
  renderer.drawScreenMarkers(camera,100,800,500,1);
  assert.deepEqual(circles,[7.5]);
  assert.equal(ctx.fillStyle,'rgb(255,100,108)');
  circles.length=0;
  renderer.drawScreenMarkers(camera,100,800,500,1,marker);
  assert.deepEqual(circles,[7.5,11],'Pinned event gets a separate ring');
});
