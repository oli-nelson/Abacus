import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const source=await readFile(new URL('../../src/Abacus/Dashboard/Assets/dependency-tree.js',import.meta.url),'utf8');
const {layoutDependencyTree,zoomScrollOffset}=await import('data:text/javascript,'+encodeURIComponent(source));
const issue=(id,deps=[],status='open')=>({id,title:id,status,dependencies:deps.map(dependsOnId=>({issueId:id,dependsOnId,type:'blocks'}))});

const tree=layoutDependencyTree([issue('root'),issue('a',['root']),issue('b',['root']),issue('leaf',['a','b'])]);
assert.equal(tree.edges.length,4);
for(const edge of tree.edges)assert.ok(tree.positions.get(edge.from).x<tree.positions.get(edge.to).x,`${edge.from} should precede ${edge.to}`);
assert.notEqual(tree.positions.get('a').y,tree.positions.get('b').y);
for(const [id,p] of tree.positions)for(const [other,q] of tree.positions)if(id!==other)
  assert.ok(p.x+246<=q.x||q.x+246<=p.x||p.y+76<=q.y||q.y+76<=p.y,`${id} and ${other} overlap`);
const crossed=layoutDependencyTree([issue('a'),issue('b'),issue('c',['b']),issue('d',['a'])]);
assert.ok(crossed.positions.get('c').y>crossed.positions.get('d').y,'barycenter sweep should uncross opposite dependencies');
const missing=layoutDependencyTree([issue('a',['not-in-export']),issue('b')]);
assert.equal(missing.unresolved,1);assert.equal(missing.edges.length,0);assert.equal(missing.positions.size,2);
const hierarchy=layoutDependencyTree([
  issue('epic'),
  {...issue('child'),dependencies:[{issueId:'child',dependsOnId:'epic',type:'parent-child'}]},
  {...issue('related'),dependencies:[{issueId:'related',dependsOnId:'epic',type:'relates-to'}]},
]);
assert.equal(hierarchy.edges.length,0,'organizational and related links are not blocking prerequisites');
const cyclic=layoutDependencyTree([issue('a',['b']),issue('b',['a'])]);
assert.equal(cyclic.positions.size,2);assert.ok(cyclic.cycles>0);
const many=layoutDependencyTree(Array.from({length:20},(_,i)=>issue(`solo-${i}`)));
assert.equal(many.positions.size,20);assert.ok(many.width>800&&many.height>350);
const zoomed=zoomScrollOffset(500,1,1.5,200);
assert.equal((500+200)/1,(zoomed+200)/1.5,'zoom keeps the cursor on the same graph point');
console.log('Dependency tree layout checks passed');
