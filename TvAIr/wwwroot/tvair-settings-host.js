(function(){
  'use strict';
  const FRAME_URL='/?settingsHost=1';
  let overlay=null;
  let frame=null;
  let opener=null;

  function ensureHost(){
    if(overlay) return overlay;
    overlay=document.createElement('div');
    overlay.className='tvair-settings-host-overlay';
    overlay.id='tvair-settings-host-overlay';
    overlay.setAttribute('role','dialog');
    overlay.setAttribute('aria-modal','true');
    overlay.setAttribute('aria-label','TvAIr 設定');
    frame=document.createElement('iframe');
    frame.className='tvair-settings-host-frame';
    frame.id='tvair-settings-host-frame';
    frame.title='TvAIr 設定';
    overlay.appendChild(frame);
    document.body.appendChild(overlay);
    return overlay;
  }

  function open(options){
    ensureHost();
    opener=document.activeElement instanceof HTMLElement ? document.activeElement : null;
    frame.src=FRAME_URL+'&ts='+Date.now();
    overlay.classList.add('open');
    document.body.classList.add('tvair-settings-host-open');
    overlay.dataset.entrySource=(options&&options.source)||'web';
    return true;
  }

  function close(){
    if(!overlay) return;
    overlay.classList.remove('open');
    document.body.classList.remove('tvair-settings-host-open');
    if(opener && document.contains(opener)) opener.focus({preventScroll:true});
    opener=null;
  }

  window.addEventListener('message',function(e){
    if(e.origin!==location.origin || !frame || e.source!==frame.contentWindow) return;
    if(!e.data || e.data.contract!=='tvair-settings-host') return;
    if(e.data.action==='saved'){
      const detail=(e.data.detail && typeof e.data.detail==='object') ? e.data.detail : {};
      close();
      const syncTheme=window.TvAIrTheme && typeof window.TvAIrTheme.syncRuntime==='function'
        ? window.TvAIrTheme.syncRuntime('settings-host-save')
        : Promise.resolve();
      Promise.resolve(syncTheme).catch(()=>{}).finally(()=>{
        window.dispatchEvent(new CustomEvent('tvair-settings-saved', { detail }));
      });
      return;
    }
    if(e.data.action==='close') close();
  });

  window.TvAIrSettingsHost={version:'1.2.0',open,close,contract:'shared-non-navigating-settings-host'};
})();
