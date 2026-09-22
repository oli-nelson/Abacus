// Local layout preference only: no repository reads or mutations.
export function bindInspectorResize(){
  const main=document.querySelector('main'),handle=document.getElementById('inspector-resizer');
  const desktop=matchMedia('(min-width: 721px)');
  let preferred=null,drag=null;
  try{const stored=Number(localStorage.getItem('abacus.inspector.width'));if(Number.isFinite(stored)&&stored>=280)preferred=stored;}catch{}
  const bounds=()=>({min:280,max:Math.max(280,Math.min(main.clientWidth*.6,main.clientWidth-366))});
  function layout(){
    const {min,max}=bounds(),width=Math.round(Math.max(min,Math.min(max,preferred??main.clientWidth/3.8)));
    main.style.setProperty('--inspector-width',width+'px');
    handle.setAttribute('aria-valuemin',min);handle.setAttribute('aria-valuemax',Math.round(max));
    handle.setAttribute('aria-valuenow',width);handle.setAttribute('aria-valuetext',width+' pixels wide');
    if(!desktop.matches){drag=null;main.classList.remove('resizing-inspector');}
  }
  function change(width){const {min,max}=bounds();preferred=Math.max(min,Math.min(max,width));layout();}
  function persist(){try{localStorage.setItem('abacus.inspector.width',String(preferred));}catch{}}
  handle.addEventListener('pointerdown',e=>{
    if(e.button!==0||!desktop.matches)return;
    e.preventDefault();handle.focus();handle.setPointerCapture(e.pointerId);
    drag={x:e.clientX,width:Number(handle.getAttribute('aria-valuenow'))};main.classList.add('resizing-inspector');
  });
  handle.addEventListener('pointermove',e=>{if(drag)change(drag.width+drag.x-e.clientX);});
  const finish=()=>{if(drag){drag=null;persist();}main.classList.remove('resizing-inspector');};
  handle.addEventListener('pointerup',finish);handle.addEventListener('pointercancel',finish);handle.addEventListener('lostpointercapture',finish);
  handle.addEventListener('keydown',e=>{
    if(!['ArrowLeft','ArrowRight','Home','End'].includes(e.key))return;
    e.preventDefault();const {min,max}=bounds();
    change(e.key==='Home'?min:e.key==='End'?max:Number(handle.getAttribute('aria-valuenow'))+(e.key==='ArrowLeft'?16:-16));persist();
  });
  handle.addEventListener('dblclick',()=>{preferred=null;try{localStorage.removeItem('abacus.inspector.width');}catch{}layout();});
  new ResizeObserver(layout).observe(main);desktop.addEventListener('change',layout);layout();
}
