// Bounded recovery for read-only ProgramGuide snapshots. Never replay a mutation.
(function(global){
  'use strict';
  const routes=new Set(['/api/reservations','/api/epg/scheduled-daily-windows','/api/chain-reservation-candidates','/api/program-guide/live-view-capability']);
  function abortError(){const e=new Error('Guide read superseded');e.name='AbortError';return e;}
  async function json(url,options,policy){
    options=options||{};policy=policy||{};
    const path=String(url).split('?')[0],method=String(options.method||'GET').toUpperCase();
    if(!routes.has(path)||(method!=='GET'&&!(method==='POST'&&(path==='/api/chain-reservation-candidates'||path==='/api/program-guide/live-view-capability')))) throw new Error('guide_read_route_not_allowed');
    const signal=options.signal,started=Date.now();
    const current=()=>!(signal&&signal.aborted)&&(!policy.isCurrent||policy.isCurrent());
    for(let attempt=0;attempt<2;attempt++){
      if(!current())throw abortError();
      const controller=new AbortController();let timedOut=false;
      const abort=()=>controller.abort();
      if(signal)signal.addEventListener('abort',abort,{once:true});
      const timer=setTimeout(()=>{timedOut=true;controller.abort();},policy.timeoutMs||5000);
      try{
        const response=await fetch(url,Object.assign({},options,{signal:controller.signal,cache:'no-store'}));
        if(!response.ok){const error=new Error('guide_read_http_'+response.status);error.status=response.status;throw error;}
        const value=await response.json();
        if(!current())throw abortError();
        if(attempt&&typeof global.uiTrace==='function')global.uiTrace('GUIDE_READ_RECOVERED','route='+path+' attempts=2 elapsedMs='+(Date.now()-started),'poll');
        return value;
      }catch(error){
        if(!current())throw abortError();
        const network=timedOut||(!error.status&&(error.name==='TypeError'||/failed to fetch|networkerror|load failed/i.test(String(error.message||error))));
        if(attempt||!network)throw error;
      }finally{
        clearTimeout(timer);if(signal)signal.removeEventListener('abort',abort);
      }
      await new Promise(resolve=>setTimeout(resolve,250));
    }
  }
  global.TvAirGuideReadRequest=Object.freeze({json});
})(window);
