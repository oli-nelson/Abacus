import {test} from 'node:test';
import assert from 'node:assert/strict';
import {issuePage} from '../../src/Abacus/Dashboard/Assets/issue-table.js';
const issues=Array.from({length:1000},(_,i)=>({id:'issue-'+i,title:'Title '+i,status:i%2?'open':'closed',priority:i%5,assignee:i%3?'worker':null}));
test('bounded paging and deterministic ties do not mutate the source',()=>{
 const before=JSON.stringify(issues);const page=issuePage(issues,{sort:'priority',page:3,pageSize:25});
 assert.equal(page.rows.length,25);assert.equal(page.total,1000);assert.equal(page.pages,40);assert.equal(page.page,3);assert.equal(JSON.stringify(issues),before);
 const all=Array.from({length:40},(_,page)=>issuePage(issues,{sort:'priority',page,pageSize:25}).rows).flat();assert.equal(new Set(all.map(i=>i.id)).size,1000);
});
test('filters, out-of-range pages and invalid URL settings normalize',()=>{
 const result=issuePage(issues,{query:'issue-9',status:'open',page:999,pageSize:25});assert.ok(result.rows.every(i=>i.id.includes('issue-9')&&i.status==='open'));assert.equal(result.page,result.pages-1);
 const empty=issuePage(issues,{query:'absent',page:999});assert.equal(empty.page,0);assert.equal(empty.pages,1);assert.equal(empty.total,0);
 const invalid=issuePage(issues,{sort:'__proto__',direction:'wrong',page:Infinity,pageSize:0});assert.equal(invalid.sort,'id');assert.equal(invalid.direction,'asc');assert.equal(invalid.pageSize,50);assert.equal(invalid.page,0);
});
test('numeric priority, displayed status and missing values sort correctly',()=>{
 for(const direction of ['asc','desc'])assert.equal(issuePage(issues,{sort:'assignee',direction,page:19}).rows.at(-1).assignee,null);
 assert.equal(issuePage(issues,{sort:'priority',direction:'desc'}).rows[0].priority,4);
 assert.equal(issuePage(issues,{sort:'status'}).rows[0].status,'closed');
 assert.equal(issuePage([{id:'b',priority:null},{id:'a',priority:1}],{sort:'priority',direction:'desc'}).rows[1].id,'b');
});
