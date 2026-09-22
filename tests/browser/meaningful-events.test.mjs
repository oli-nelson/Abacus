import test from 'node:test';
import assert from 'node:assert/strict';
import {commentPreview,eventsFor,meaningfulEvents,clusterEvents,workEpisodes} from '../../src/Abacus/Dashboard/Assets/timeline-model.js';
const issue={id:'x',status:'closed',closedAt:'2026-09-21T11:00:00Z',comments:[]};
const version=(id,time,fields={})=>({id,recordedAt:'2026-09-21T'+time+':00Z',status:'in_progress',labels:['one'],notes:'Old note',title:'Title',committer:'Committer',...fields});
const baseline=version('a','09:00');
test('unchanged snapshots and unrelated metadata do not create events',()=>{
 const rows=Array.from({length:12},(_,i)=>version('v'+i,'09:'+String(i+1).padStart(2,'0'),{title:'Title '+i,assignee:'a'+i}));
 assert.deepEqual(meaningfulEvents(eventsFor({...issue,status:'in_progress'},[baseline,...rows])),[]);
});
test('only status, label and note deltas plus distinct comments are counted',()=>{
 const raw=eventsFor({...issue,comments:[{id:'c',createdAt:'2026-09-21T10:00:00Z',text:'Hello',author:'Ollie'}]},[baseline,version('b','10:00',{status:'blocked',labels:['two'],notes:'New note'}),version('c','11:00',{status:'closed',labels:['two'],notes:'New note'})]);
 const events=meaningfulEvents(raw);
 assert.deepEqual(events.map(e=>e.kind).sort(),['comment','labels','notes','status','status']);
 assert.equal(events.filter(e=>e.kind==='status'&&e.status==='closed').length,1);
 assert.match(events.find(e=>e.kind==='labels').text,/Added labels: two · Removed labels: one/);
 const note=events.find(e=>e.kind==='notes');assert.equal(note.before,'Old note');assert.equal(note.after,'New note');assert.equal(note.author,undefined);
 assert.equal(clusterEvents(events,Date.parse('2026-09-21T09:00Z'),Date.parse('2026-09-21T12:00Z')).find(e=>e.members?.length===4)?.members.length,4);
 assert.equal(workEpisodes(raw)[0].endStatus,'closed');
});
test('first snapshot is a baseline, not a fabricated change',()=>{
 assert.deepEqual(meaningfulEvents(eventsFor({...issue,status:'in_progress'},[baseline])),[]);
});
test('label order, null notes and absent history do not invent edits',()=>{
 assert.deepEqual(meaningfulEvents(eventsFor({...issue,status:'in_progress'},[version('a','09:00',{labels:['a','b'],notes:null}),version('b','10:00',{labels:['b','a','a'],notes:''})])),[]);
});
test('equal-time conflicting snapshots break the baseline',()=>{
 const events=meaningfulEvents(eventsFor({...issue,status:'in_progress'},[baseline,version('b','10:00',{status:'blocked'}),version('c','10:00',{status:'open'}),version('d','10:30')]));
 assert.equal(events.length,0);
});
test('notes clearing retains before and after; chronological input order is normalized',()=>{
 const events=meaningfulEvents(eventsFor({...issue,status:'in_progress'},[version('b','10:00',{notes:''}),baseline]));
 assert.equal(events[0].text,'Notes cleared');assert.equal(events[0].after,'');
});

test('comment previews truncate with ellipsis without splitting Unicode characters',()=>{
 assert.equal(commentPreview('Short comment'),'Short comment');
 assert.equal(commentPreview('😀'.repeat(241)),'😀'.repeat(240)+'…');
 assert.equal(commentPreview(null),'');
});
