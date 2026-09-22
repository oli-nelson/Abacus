import test from 'node:test';
import assert from 'node:assert/strict';
import {concurrentEpisodeLayout} from '../../src/Abacus/Dashboard/Assets/timeline-model.js';
const row=(key,start,end)=>({key,start:start*60000,end:end*60000});
test('sequential issues reuse the same positive offset regardless of ID',()=>{
 const layout=concurrentEpisodeLayout([row('z',0,10),row('a',10,20)]);
 assert.equal(layout.offset('z',5*60000),2.4);assert.equal(layout.offset('a',15*60000),2.4);
});
test('two overlapping issues are opposite; survivor keeps its established side',()=>{
 const layout=concurrentEpisodeLayout([row('a',0,10),row('b',5,20)]);
 assert.equal(layout.offset('a',7*60000),2.4);assert.equal(layout.offset('b',7*60000),-2.4);
 assert.equal(layout.offset('b',10*60000),-2.4);assert.equal(layout.offset('b',14*60000),-2.4);
 assert.equal(layout.offset('b',11.25*60000),-2.4);
});
test('concurrent offsets alternate outward and input order does not change placement',()=>{
 const rows=[row('d',0,20),row('b',0,20),row('a',0,20),row('c',0,20)];
 const a=concurrentEpisodeLayout(rows),b=concurrentEpisodeLayout([...rows].reverse());
 assert.deepEqual(['a','b','c','d'].map(k=>a.offset(k,60000)),[2.4,-2.4,4.8,-4.8]);
 for(const r of rows)assert.equal(a.offset(r.key,60000),b.offset(r.key,60000));
});

test('ending an inner lane does not swap surviving issues across each other',()=>{
 const layout=concurrentEpisodeLayout([row('a',0,10),row('b',1,20),row('c',2,20)]);
 assert.equal(layout.offset('b',7*60000),-2.4);assert.equal(layout.offset('c',7*60000),4.8);
 assert.equal(layout.offset('b',14*60000),-2.4);assert.equal(layout.offset('c',14*60000),2.4);
});

test('departure cannot pull a positive closing branch below the main line',()=>{
 const layout=concurrentEpisodeLayout([row('a',0,10),row('b',1,4),row('c',2,10)]);
 for(let t=0;t<10;t+=.1)assert.ok(layout.offset('a',t*60000)>0);
 for(let t=2;t<10;t+=.1)assert.ok(layout.offset('c',t*60000)>0);
});
