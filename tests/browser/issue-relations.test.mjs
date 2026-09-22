import test from 'node:test';
import assert from 'node:assert/strict';
import {IssueRelations} from '../../src/Abacus/Dashboard/Assets/issue-relations.js';
const edge=(target,type='blocks')=>({issueId:'a',dependsOnId:target,type});
const issue=(id,revision,dependencies)=>({id,revision,dependencies});
test('reverse links update without changing the selected issue and disappear on source deletion',()=>{
 const index=new IssueRelations(),b=issue('b','1',[]);
 const issues=new Map([['a',issue('a','1',[edge('b'),edge('b','related')])],['b',b]]);
 index.update(issues);assert.equal(index.forIssue('b').edges.length,2);
 const revision=index.revision;index.update(issues);assert.equal(index.revision,revision);
 issues.set('a',issue('a','2',[edge('missing')]));index.update(issues);
 assert.equal(index.forIssue('b').edges.length,0);assert.equal(index.forIssue('a').edges[0].otherId,'missing');
 issues.delete('a');index.update(issues);assert.equal(index.forIssue('missing').edges.length,0);
 assert.equal(issues.get('b'),b);
});
test('unknown coverage differs from known empty and incoming coverage is export scoped',()=>{
 const index=new IssueRelations(),issues=new Map([['a',issue('a','1',null)],['b',issue('b','1',[])]]);
 index.update(issues);assert.equal(index.forIssue('a').outgoingKnown,false);
 assert.equal(index.forIssue('b').outgoingKnown,true);assert.equal(index.forIssue('b').incomingComplete,false);
 issues.delete('a');index.update(issues);assert.equal(index.forIssue('b').incomingComplete,true);
});
