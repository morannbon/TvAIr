(function(global){
  'use strict';
  const memory=new Map();
  function key(id){return 'tvair:reservation-cancel:'+String(id);}
  function read(id){try{return global.sessionStorage.getItem(key(id));}catch(_){return null;}}
  function newId(){const bytes=new Uint8Array(16);global.crypto.getRandomValues(bytes);return Array.from(bytes,b=>b.toString(16).padStart(2,'0')).join('');}
  function headers(id){
    const name=key(id);let value=memory.get(name)||read(id);
    if(!/^[0-9a-f]{32}$/i.test(value||''))value=newId();
    memory.set(name,value);try{global.sessionStorage.setItem(name,value);}catch(_){}
    return {'X-TvAIr-Reservation-Operation':value,'X-TvAIr-Reservation-Action':'cancel'};
  }
  function complete(id,response,body){
    if(body&&body.pending===true)return;
    if(response&&response.ok||body&&(body.changed===true||body.changed===false)){
      memory.delete(key(id));try{global.sessionStorage.removeItem(key(id));}catch(_){}
    }
  }
  async function send(id){
    const operationHeaders=headers(id);
    // A pending result is neither a failed cancellation nor canonical completion.
    // Repeat only the same operation identity; the Host remains the sole mutation owner.
    for(;;){
      const response=await global.fetch('/api/reservations/'+id,{method:'DELETE',headers:operationHeaders});
      let body={};try{body=await response.json();}catch(_){}
      complete(id,response,body);
      if(response.status!==503 || body.pending!==true) return {response,body};
      await new Promise(resolve=>global.setTimeout(resolve,500));
    }
  }
  global.TvAirReservationCancelRequest=Object.freeze({headers,complete,send});
})(window);
