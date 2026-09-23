import test from 'node:test';
import assert from 'node:assert/strict';
import {isInitialWorkEntry,eventAwareEpisodePosition} from '../../src/Abacus/Dashboard/Assets/timeline-model.js';
const event=(before,after,time=10)=>({kind:'status',before,after,time});
test('only the first recorded entry from open or blocked uses the spine',()=>{
 const episodes=[{start:10},{start:30}];
 assert.equal(isInitialWorkEntry(event('open','in_progress'),episodes),true);
 assert.equal(isInitialWorkEntry(event('blocked','in_progress'),episodes),true);
 assert.equal(isInitialWorkEntry(event('blocked','in_progress',20),episodes),false);
 assert.equal(isInitialWorkEntry(event('open','in_progress',30),episodes),false);
 assert.equal(isInitialWorkEntry(event('closed','in_progress'),episodes),false);
 assert.equal(isInitialWorkEntry({kind:'comment',time:10},episodes),false);
});
test('closure events stay offset and same-time start comments stay off the spine',()=>{
 assert.deepEqual(eventAwareEpisodePosition(10,0,10,2.4,true,2,10),[10,2.4,0]);
 assert.deepEqual(eventAwareEpisodePosition(0,0,10,-2.4,true,0,10),[0,-2.4,0]);
 assert.deepEqual(eventAwareEpisodePosition(5,0,10,-2.4,true,0,10),[5,-2.4,0]);
});
test('curves reach the lane by the first event and return only after the last',()=>{
 assert.deepEqual(eventAwareEpisodePosition(0,0,10,2.4,true,1,9),[0,0,0]);
 assert.deepEqual(eventAwareEpisodePosition(1,0,10,2.4,true,1,9),[1,2.4,0]);
 assert.deepEqual(eventAwareEpisodePosition(9,0,10,2.4,true,1,9),[9,2.4,0]);
 assert.deepEqual(eventAwareEpisodePosition(10,0,10,2.4,true,1,9),[10,0,0]);
});
test('work resumed after an Open pause stays on its lane instead of forking again',()=>{
 assert.deepEqual(eventAwareEpisodePosition(4,4,10,2.4,false,4,10,true),[4,2.4,0]);
 assert.deepEqual(eventAwareEpisodePosition(4,4,10,2.4,false,5,10,false),[4,0,0]);
});
