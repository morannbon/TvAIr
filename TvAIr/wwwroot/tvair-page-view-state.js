/* TvAIr Page View State contract
   Owns same-session page-view persistence only. Page modules keep their own meaning/state transitions;
   this module stores/restores registered scroll coordinates and lightweight page fields without
   changing layout, button state, navigation, or page-specific rendering contracts. */
(function(){
  'use strict';
  if(window.TvAIrPageViewState && window.TvAIrPageViewState.version === '1.0.0') return;

  const VERSION='1.0.0';
  const STORAGE_KEY='tvair_page_view_state_v1';
  const registry=new Map();

  function safeParse(raw){
    try{ const v=JSON.parse(raw||'{}'); return v && typeof v==='object' ? v : {}; }
    catch(_){ return {}; }
  }
  function loadStore(){
    try{ return safeParse(sessionStorage.getItem(STORAGE_KEY)); }
    catch(_){ return {}; }
  }
  function saveStore(store){
    try{ sessionStorage.setItem(STORAGE_KEY, JSON.stringify(store||{})); return true; }
    catch(_){ return false; }
  }
  function normalizeKey(key){
    const k=String(key||'').trim();
    if(k) return k;
    try{ return String(location.pathname||'/') || '/'; }catch(_){ return '/'; }
  }
  function read(key){
    const store=loadStore();
    const value=store[normalizeKey(key)];
    return value && typeof value==='object' ? {...value, fields:{...(value.fields||{})}, meta:{...(value.meta||{})}} : null;
  }
  function write(key, patch){
    const k=normalizeKey(key);
    const store=loadStore();
    const prev=store[k] && typeof store[k]==='object' ? store[k] : {};
    const next={...prev,...(patch||{}),updatedAt:Date.now()};
    if(patch && patch.fields) next.fields={...(prev.fields||{}),...patch.fields};
    if(patch && patch.meta) next.meta={...(prev.meta||{}),...patch.meta};
    store[k]=next;
    const keys=Object.keys(store).sort((a,b)=>(store[b]?.updatedAt||0)-(store[a]?.updatedAt||0));
    keys.slice(24).forEach(stale=>delete store[stale]);
    saveStore(store);
    return next;
  }
  function getField(key, name, fallback=null){
    const state=read(key);
    return state && state.fields && Object.prototype.hasOwnProperty.call(state.fields,name) ? state.fields[name] : fallback;
  }
  function setField(key, name, value){ return write(key,{fields:{[name]:value}}); }

  function resolveRoot(root){
    try{
      if(typeof root==='function') return root();
      if(typeof root==='string') return document.querySelector(root);
      return root || null;
    }catch(_){ return null; }
  }
  function captureRegistration(reg){
    const el=resolveRoot(reg.root);
    if(!el) return;
    let meta={};
    try{ meta=typeof reg.meta==='function' ? (reg.meta()||{}) : (reg.meta||{}); }catch(_){ meta={}; }
    write(reg.key,{top:Number(el.scrollTop)||0,left:Number(el.scrollLeft)||0,meta});
  }
  function register(options){
    const reg={
      key:normalizeKey(options && options.key),
      root:options && options.root,
      meta:options && options.meta
    };
    registry.set(reg.key,reg);
    return reg;
  }
  function capture(key){
    const reg=registry.get(normalizeKey(key));
    if(reg) captureRegistration(reg);
  }
  function captureAll(){ registry.forEach(captureRegistration); }

  function restore(key, root, options){
    const state=read(key);
    const el=resolveRoot(root);
    if(!state || !el) return false;
    const top=Number.isFinite(Number(state.top)) ? Math.max(0,Number(state.top)) : 0;
    const left=Number.isFinite(Number(state.left)) ? Math.max(0,Number(state.left)) : 0;
    const apply=()=>{
      try{
        el.scrollTop=top;
        el.scrollLeft=left;
        return true;
      }catch(_){ return false; }
    };
    apply();
    const retries=Math.max(0,Math.min(12,Number(options && options.retries)||0));
    if(retries>0){
      let remaining=retries;
      const retry=()=>{
        if(remaining--<=0) return;
        apply();
        requestAnimationFrame(retry);
      };
      requestAnimationFrame(retry);
    }
    return true;
  }

  window.addEventListener('pagehide',captureAll,{capture:true});

  window.TvAIrPageViewState=Object.freeze({version:VERSION,read,write,getField,setField,register,capture,restore});
})();
