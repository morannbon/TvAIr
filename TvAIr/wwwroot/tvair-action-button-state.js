'use strict';
(function(global){
  const PROCESSING_TEXT='処理中…';

  function begin(button){
    if(!button) return null;
    const state=Object.freeze({
      text:String(button.textContent||''),
      html:String(button.innerHTML||''),
      disabled:button.disabled===true
    });
    button.textContent=PROCESSING_TEXT;
    button.disabled=true;
    button.setAttribute('aria-busy','true');
    return state;
  }

  function finish(button, state, finalText){
    if(!button) return;
    button.removeAttribute('aria-busy');
    button.disabled=state ? state.disabled : false;
    if(finalText===undefined || finalText===null){
      if(state) button.innerHTML=state.html;
    }else{
      button.textContent=String(finalText);
    }
  }

  global.TvAirActionButtonState=Object.freeze({
    processingText:PROCESSING_TEXT,
    begin,
    finish
  });
})(window);
