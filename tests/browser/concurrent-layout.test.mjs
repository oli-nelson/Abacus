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

test('a branch keeps the side and distance it was given for its whole lifetime',()=>{
 // 'a' ends at 10 and frees the inner positive slot. 'c' must not slide into it:
 // a branch that moves sideways shows motion the recorded source never contained.
 const layout=concurrentEpisodeLayout([row('a',0,10),row('b',1,20),row('c',2,20)]);
 assert.equal(layout.offset('b',7*60000),-2.4);assert.equal(layout.offset('c',7*60000),4.8);
 assert.equal(layout.offset('b',14*60000),-2.4);assert.equal(layout.offset('c',14*60000),4.8);
 for(let t=2;t<20;t+=.25)assert.equal(layout.offset('c',t*60000),4.8);
 // Nothing is left mid-move either, so no segment needs an interpolation sample.
 assert.deepEqual(layout.sampleTimes,[]);
});

test('a freed inner slot is reused instead of marching the scene outwards',()=>{
 // 'a' vacates +1 before 'd' starts, so 'd' takes +1 rather than a fourth ring.
 const layout=concurrentEpisodeLayout([row('a',0,10),row('b',1,30),row('c',2,30),row('d',12,30)]);
 assert.equal(layout.offset('a',5*60000),2.4);
 assert.equal(layout.offset('d',20*60000),2.4);
 assert.equal(layout.offset('c',20*60000),4.8);
 assert.equal(layout.max,4.8);
});

test('long sequential work never drifts away from the main line',()=>{
 const rows=Array.from({length:40},(_,i)=>row('s'+i,i*10,i*10+9));
 const layout=concurrentEpisodeLayout(rows);
 for(const r of rows)assert.equal(layout.offset(r.key,(r.start+r.end)/2),2.4);
 assert.equal(layout.max,2.4);assert.equal(layout.min,0);
});

test('departure cannot pull a positive closing branch below the main line',()=>{
 const layout=concurrentEpisodeLayout([row('a',0,10),row('b',1,4),row('c',2,10)]);
 for(let t=0;t<10;t+=.1)assert.ok(layout.offset('a',t*60000)>0);
 for(let t=2;t<10;t+=.1)assert.ok(layout.offset('c',t*60000)>0);
});

test('no branch ever moves, and the scene box never grows, across many layouts',()=>{
 // Seeded so a failure is reproducible. The invariant is the whole point of the
 // layout: a branch's only vertical movement is its own fork and return.
 let seed=12345;const rnd=()=>((seed=(seed*1103515245+12345)&0x7fffffff)/0x7fffffff);
 for(let trial=0;trial<300;trial++){
  const rows=Array.from({length:2+Math.floor(rnd()*12)},(_,i)=>{
   const start=Math.floor(rnd()*50);return{key:'r'+i,start,end:start+1+Math.floor(rnd()*30)};});
  const layout=concurrentEpisodeLayout(rows);
  for(const r of rows){
   const seen=new Set();
   for(let t=r.start;t<r.end;t+=Math.max(.5,(r.end-r.start)/40))seen.add(layout.offset(r.key,t));
   assert.equal(seen.size,1,`branch ${r.key} moved in trial ${trial}`);
  }
  // Slots are exclusive while shared, or two branches would be drawn on top of
  // each other rather than beside each other.
  for(const t of new Set(rows.flatMap(r=>[r.start,r.end-.001]))){
   const active=rows.filter(r=>r.start<=t&&r.end>t).map(r=>layout.offset(r.key,t));
   assert.equal(new Set(active).size,active.length,`overlapping branches shared a slot in trial ${trial}`);
  }
 }
});
