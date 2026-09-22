import test from 'node:test';
import assert from 'node:assert/strict';
import {projectPoint} from '../../src/Abacus/Dashboard/Assets/timeline-gl.js';
test('independent world-axis scale preserves centered target and orthographic axes',()=>{
 const camera={yaw:0,pitch:0,distance:30,target:[2,-4,0],perspective:0};
 const p=projectPoint([5,-2,0],camera,1000,600);
 const wide=projectPoint([5,-2,0],{...camera,axisScale:[2,1,1]},1000,600);
 const tall=projectPoint([5,-2,0],{...camera,axisScale:[1,3,1]},1000,600);
 assert.equal(wide.x-500,2*(p.x-500));assert.equal(wide.y,p.y);
 assert.equal(tall.x,p.x);assert.equal(300-tall.y,3*(300-p.y));
 assert.deepEqual(projectPoint(camera.target,{...camera,axisScale:[4,.25,1]},1000,600),projectPoint(camera.target,camera,1000,600));
});
test('perspective scale matches transformed world points and default remains unchanged',()=>{
 const camera={yaw:.3,pitch:.2,distance:30,target:[2,-4,0],perspective:1};
 assert.deepEqual(projectPoint([5,-2,1],{...camera,axisScale:[2,3,1]},1000,600),projectPoint([8,2,1],camera,1000,600));
 assert.deepEqual(projectPoint([5,-2,1],{...camera,axisScale:[1,1,1]},1000,600),projectPoint([5,-2,1],camera,1000,600));
});
