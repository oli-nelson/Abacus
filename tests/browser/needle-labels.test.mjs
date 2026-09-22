import test from 'node:test';
import assert from 'node:assert/strict';
import {needleLabels} from '../../src/Abacus/Dashboard/Assets/timeline-model.js';
// Entries are opaque to the rule; only the span and the issue they belong to matter.
const span=(id,start,end)=>({entry:id,id,start,end});
const ids=set=>[...set].sort();

test('every episode open at the needle is captioned, because parallel work is the point',()=>{
 const rows=[span('a',0,Infinity),span('b',5,Infinity),span('c',0,8)];
 assert.deepEqual(ids(needleLabels(rows,10,null)),['a','b']);
});
test('a selection narrows the captions to that issue, open at the needle or not',()=>{
 const rows=[span('a',0,Infinity),span('b',5,Infinity),span('c',0,8)];
 assert.deepEqual(ids(needleLabels(rows,10,'c')),['c']);
 assert.deepEqual(ids(needleLabels(rows,10,'a')),['a']);
});
test('a selection with nothing at the needle falls back to the whole scene',()=>{
 // Selecting an issue whose work has not started yet must not blank the captions.
 const rows=[span('a',0,Infinity),span('later',20,Infinity)];
 assert.deepEqual(ids(needleLabels(rows,10,'later')),['a']);
 assert.deepEqual(ids(needleLabels(rows,10,'absent')),['a']);
});
test('with nothing open the most recently ended work is what the needle reports',()=>{
 const rows=[span('old',0,2),span('recent',0,7),span('also-recent',3,7)];
 assert.deepEqual(ids(needleLabels(rows,10,null)),['also-recent','recent']);
});
test('work that has not started by the needle is never captioned',()=>{
 assert.deepEqual(ids(needleLabels([span('future',20,30)],10,null)),[]);
 assert.deepEqual(ids(needleLabels([],10,null)),[]);
});
test('an episode ending exactly at the needle still counts as open',()=>{
 assert.deepEqual(ids(needleLabels([span('a',0,10),span('b',0,4)],10,null)),['a']);
});
