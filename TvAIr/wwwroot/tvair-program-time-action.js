'use strict';
(function(global){
  function timestampOf(value){
    const time=new Date(value).getTime();
    return Number.isFinite(time) ? time : NaN;
  }
  function resolve(event, defaultSource, nowValue){
    const now=Number.isFinite(Number(nowValue)) ? Number(nowValue) : Date.now();
    const start=timestampOf(event && (event.start ?? event.startTime));
    const end=timestampOf(event && (event.end ?? event.endTime));
    if(!Number.isFinite(start) || !Number.isFinite(end) || end<=start){
      return Object.freeze({action:'none', label:'', source:'', status:'', reason:'invalid-time'});
    }
    if(now>=end){
      return Object.freeze({action:'none', label:'', source:'', status:'', reason:'ended'});
    }
    if(now>=start){
      return Object.freeze({action:'record-now', label:'今すぐ録画', source:'immediate', status:'scheduled', reason:'on-air'});
    }
    return Object.freeze({action:'reserve', label:'予約', source:String(defaultSource||'manual'), status:'scheduled', reason:'future'});
  }
  global.TvAirProgramTimeAction=Object.freeze({resolve});
})(window);
