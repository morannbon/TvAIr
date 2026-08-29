'use strict';
(function(global){
  function statusOf(item){
    return String(item && item.status || '').trim().toLowerCase();
  }
  function sameReservation(item, expectedId){
    const expected=String(expectedId || '');
    if(!expected) return true;
    return !!item && String(item.id || '')===expected;
  }
  function isStopComplete(item, expectedId){
    if(!item) return true;
    if(!sameReservation(item, expectedId)) return false;
    const status=statusOf(item);
    return status==='completed' || status==='cancelled' || status==='failed';
  }
  function isCancelComplete(item, expectedId){
    if(!item) return true;
    if(!sameReservation(item, expectedId)) return false;
    return statusOf(item)==='cancelled';
  }
  function isEnableComplete(item, expectedId){
    return sameReservation(item, expectedId) && !!item && item.isEnabled!==false;
  }
  function isDisableComplete(item, expectedId){
    return sameReservation(item, expectedId) && !!item && item.isEnabled===false;
  }
  global.TvAirReservationActionState=Object.freeze({
    statusOf,
    sameReservation,
    isStopComplete,
    isCancelComplete,
    isEnableComplete,
    isDisableComplete
  });
})(window);
