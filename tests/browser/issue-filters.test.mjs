import {test} from 'node:test';
import assert from 'node:assert/strict';
import {matchesIssueMetadata} from '../../src/Abacus/Dashboard/Assets/issue-filters.js';
import {issuePage} from '../../src/Abacus/Dashboard/Assets/issue-table.js';
import {stateAt,eventsFor} from '../../src/Abacus/Dashboard/Assets/timeline-model.js';
const issue={id:'x',title:'Issue',status:'open',assignee:'alice',priority:1,labels:['team:ui','abacus:needs-user-attention']};
test('table and live timeline match the same metadata conjunction',()=>{
 const filters={assignee:'alice',label:'team:ui',priority:'1',attention:'requested'};
 assert.ok(matchesIssueMetadata(stateAt(issue,[],0,true),filters));
 assert.equal(issuePage([issue],{metadata:filters}).total,1);
 for(const mismatch of [{assignee:'Alice'},{label:'team'},{priority:'2'},{attention:'clear'}]){
  const filter={...filters,...mismatch};assert.equal(issuePage([issue],{metadata:filter}).total,0);assert.equal(matchesIssueMetadata(stateAt(issue,[],0,true),filter),false);
 }
});
test('unknown historical metadata never inherits current owner, labels or priority',()=>{
 const events=eventsFor(issue,[{id:'v1',recordedAt:'2026-09-21T12:00:00Z',title:'Old',status:'open'}]);
 const historic=stateAt(issue,events,Date.parse('2026-09-21T13:00:00Z'));
 for(const filter of [{assignee:'alice'},{label:'team:ui'},{priority:'1'},{attention:'requested'},{attention:'clear'}])assert.equal(matchesIssueMetadata(historic,filter),false);
 assert.ok(matchesIssueMetadata(historic,{attention:'unknown',priority:'unknown'}));
 assert.ok(matchesIssueMetadata(historic,{}));
});
test('empty labels differ from unknown labels and zero priority is known',()=>{
 assert.ok(matchesIssueMetadata({labels:[],priority:0},{attention:'clear',priority:'0'}));
 assert.equal(matchesIssueMetadata({labels:[]},{attention:'unknown'}),false);
 assert.ok(matchesIssueMetadata({},{attention:'unknown',priority:'unknown'}));
 assert.equal(matchesIssueMetadata({priority:0},{priority:'unknown'}),false);
});

test('recorded metadata filters use that snapshot rather than current fields',()=>{
 const versions=[{id:'old',recordedAt:'2026-09-21T12:00:00Z',title:'Old',status:'blocked',assignee:'bob',priority:3,labels:[],issueType:'bug',target:'release'}];
 const state=stateAt(issue,eventsFor(issue,versions),Date.parse('2026-09-21T13:00:00Z'));
 assert.ok(matchesIssueMetadata(state,{assignee:'bob',priority:'3',attention:'clear',type:'bug',target:'release'}));
 assert.equal(matchesIssueMetadata(state,{assignee:'alice'}),false);
 assert.equal(matchesIssueMetadata(state,{attention:'requested'}),false);
});
test('equal-time metadata disagreement makes only conflicting fields unknown',()=>{
 const version={recordedAt:'2026-09-21T12:00:00Z',title:'Old',status:'open',assignee:'bob',priority:1,labels:['b','a'],target:'main'};
 const state=stateAt(issue,eventsFor(issue,[{...version,id:'a'},{...version,id:'b',priority:2,labels:['a','b'],target:'release'}]),Date.parse('2026-09-21T13:00:00Z'));
 assert.equal(state.priority,null);assert.equal(state.target,null);assert.equal(state.status,'open');assert.equal(state.assignee,'bob');assert.deepEqual(state.labels,['a','b']);
 assert.match(state.certainty,/ambiguous/);assert.ok(matchesIssueMetadata(state,{priority:'unknown',assignee:'bob'}));
});

test('shared search bounds recorded text and never leaks future or undated text into playback',async()=>{
 const {matchesIssueText}=await import('../../src/Abacus/Dashboard/Assets/issue-filters.js');
 const current={...issue,title:'Current title',notes:'Undated current note',comments:[{text:'Undated comment',createdAt:null}]};
 const events=[{kind:'comment',time:10,text:'Earlier comment'},{kind:'snapshot',time:20,text:'Recorded note'},{kind:'comment',time:30,text:'Future comment'}];
 const options={from:5,to:40,playhead:25,live:false,title:'Recorded title'};
 assert.ok(matchesIssueText(current,events,'RECORDED NOTE',options));
 for(const q of ['Current title','Undated current note','Undated comment','Future comment'])assert.equal(matchesIssueText(current,events,q,options),false);
 assert.equal(matchesIssueText(current,events,'Earlier comment',{...options,from:15}),false);
 for(const q of ['Undated current note','Undated comment','Current title'])assert.ok(matchesIssueText(current,events,q,{...options,live:true,title:current.title}));
 assert.equal(matchesIssueText(current,events,'Future comment',{...options,live:true,to:25}),false);
 const table=issuePage([current],{query:'Recorded note',searchMatches:i=>matchesIssueText(i,events,'Recorded note',{...options,live:true,title:i.title})});
 assert.equal(table.total,1);
});
