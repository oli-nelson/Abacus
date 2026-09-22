import test from 'node:test';
import assert from 'node:assert/strict';
import {prioritizeTimelineHistory} from '../../src/Abacus/Dashboard/Assets/timeline-model.js';
const at=h=>'2026-09-21T'+h+':00:00Z',time=h=>Date.parse(at(h));
const issues=[
 {id:'a-old',status:'closed',createdAt:at('01'),closedAt:at('03')},
 {id:'b-open',status:'open',createdAt:at('01')},
 {id:'z-current',status:'in_progress',createdAt:at('01')},
 {id:'z-closed',status:'closed',createdAt:at('01'),closedAt:at('11')},
 {id:'c-spanning',status:'closed',createdAt:at('01'),closedAt:at('15')},
 {id:'a-future',status:'in_progress',createdAt:at('16')},
];
test('range candidates precede ID order; spanning work and unknowns remain eligible',()=>{
 assert.deepEqual(prioritizeTimelineHistory(issues,time('09'),time('12')).map(i=>i.id),['z-closed','z-current','c-spanning','b-open','a-old','a-future']);
 assert.equal(issues[0].id,'a-old');
});
test('changing range prioritizes historical closure',()=>{
 assert.equal(prioritizeTimelineHistory(issues,time('02'),time('04'))[0].id,'a-old');
});
test('priority precedes batch limits, including high IDs',()=>{
 const old=Array.from({length:70},(_,i)=>({...issues[0],id:'a-'+i}));
 assert.equal(prioritizeTimelineHistory([...old,issues[2]],time('09'),time('12'))[0].id,'z-current');
});
