import test from 'node:test';
import assert from 'node:assert/strict';
import {timelineAnnotations,nonOverlappingLabels} from '../../src/Abacus/Dashboard/Assets/timeline-model.js';
const event=(id,kind,time,after)=>({id,kind,time,after});
test('closing status inside a mixed cluster receives an annotation',()=>{
 const closed=event('closed','status',10,'closed'),comment=event('c','comment',10);
 const markers=[{issueId:'a',pos:[1,2,0],event:{kind:'cluster',members:[comment,closed]}},{issueId:'b',event:event('other','status',20,'closed')}];
 assert.deepEqual(timelineAnnotations(markers,'a').map(m=>m.event.id),['c','closed']);
});
test('later comments cannot crowd status labels out of the annotation budget',()=>{
 const markers=Array.from({length:30},(_,i)=>({issueId:'a',event:event('c'+i,'comment',i+1)}));
 markers.unshift({issueId:'a',event:event('closure','status',0,'closed')});
 const annotations=timelineAnnotations(markers,'a');assert.equal(annotations.length,7);assert.ok(annotations.some(m=>m.event.id==='closure'));
});
test('annotations try alternate placement before being hidden',()=>{
 const placed=nonOverlappingLabels([{id:'closure',x:0,y:0,width:100,height:20,priority:4},{id:'entry',x:0,y:0,width:100,height:20,priority:3,alternatives:[{x:120,y:0}]}]);
 assert.equal(placed.length,2);assert.equal(placed[1].x,120);
});
