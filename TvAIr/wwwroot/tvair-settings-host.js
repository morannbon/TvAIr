(function(){
  'use strict';
  const FRAME_URL='/?settingsHost=1';
  let overlay=null;
  let frame=null;
  let opener=null;
  let genrePreviewSnapshot=null;
  let genrePreviewTheme=null;

  function cloneGenrePalettes(value){
    try{ return JSON.parse(JSON.stringify(value || {})); }catch(_){ return {}; }
  }

  function captureGenrePreviewSnapshot(){
    genrePreviewSnapshot = window.TvAirGenre ? cloneGenrePalettes(window.TvAirGenre.THEME_PALETTES) : null;
    genrePreviewTheme = window.TvAirGenre ? window.TvAirGenre.CURRENT_THEME : null;
  }

  function applyGenrePreview(detail){
    const palettes=detail && detail.themeGenrePalettes;
    if(!palettes || !window.TvAirGenre || typeof window.TvAirGenre.applyThemeGenrePalettes!=='function') return;
    const effective=(detail && detail.effectiveTheme) || window.TvAirGenre.CURRENT_THEME;
    window.TvAirGenre.applyThemeGenrePalettes({themeGenrePalettes:palettes}, effective);
    if(typeof window._cfgThemeGenrePalettes!=='undefined') window._cfgThemeGenrePalettes=cloneGenrePalettes(palettes);
  }

  function restoreGenrePreviewSnapshot(){
    if(genrePreviewSnapshot && window.TvAirGenre && typeof window.TvAirGenre.applyThemeGenrePalettes==='function'){
      window.TvAirGenre.applyThemeGenrePalettes({themeGenrePalettes:genrePreviewSnapshot}, genrePreviewTheme || window.TvAirGenre.CURRENT_THEME);
      if(typeof window._cfgThemeGenrePalettes!=='undefined') window._cfgThemeGenrePalettes=cloneGenrePalettes(genrePreviewSnapshot);
    }
    genrePreviewSnapshot=null;
    genrePreviewTheme=null;
  }

  function clearGenrePreviewSnapshot(){
    genrePreviewSnapshot=null;
    genrePreviewTheme=null;
  }

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
    captureGenrePreviewSnapshot();
    frame.src=FRAME_URL+'&ts='+Date.now();
    overlay.classList.add('open');
    document.body.classList.add('tvair-settings-host-open');
    overlay.dataset.entrySource=(options&&options.source)||'web';
    return true;
  }

  function close(options){
    if(!overlay) return;
    const restorePreview=!options || options.restorePreview!==false;
    if(restorePreview) restoreGenrePreviewSnapshot();
    else clearGenrePreviewSnapshot();
    overlay.classList.remove('open');
    document.body.classList.remove('tvair-settings-host-open');
    if(opener && document.contains(opener)) opener.focus({preventScroll:true});
    opener=null;
  }

  window.addEventListener('message',function(e){
    if(e.origin!==location.origin || !frame || e.source!==frame.contentWindow) return;
    if(!e.data || e.data.contract!=='tvair-settings-host') return;
    if(e.data.action==='genre-preview'){
      const detail=(e.data.detail && typeof e.data.detail==='object') ? e.data.detail : {};
      applyGenrePreview(detail);
      return;
    }
    if(e.data.action==='saved'){
      // Save completion only owns the settings surface lifecycle. Runtime/UI synchronization is
      // Host-commit broadcast owned and must not depend on an iframe parent relationship.
      close({restorePreview:false});
      return;
    }
    if(e.data.action==='close') close();
  });


  let commitSource=null;
  let lastCommitSequence=0;

  function normalizeCommitDetail(raw){
    const value=(raw && typeof raw==='object') ? raw : {};
    return {
      sequence:Number(value.sequence||value.Sequence||0)||0,
      persistedChanged:(value.persistedChanged===true||value.PersistedChanged===true),
      themeChanged:(value.themeChanged===true||value.ThemeChanged===true),
      themeRevision:Number(value.themeRevision??value.ThemeRevision??0),
      reservationActionUiHotReloaded:(value.reservationActionUiHotReloaded===true||value.ReservationActionUiHotReloaded===true),
      requiresRestart:(value.requiresRestart===true||value.RequiresRestart===true),
      tunerTopologyRestartRequired:(value.tunerTopologyRestartRequired===true||value.TunerTopologyRestartRequired===true),
      committedAt:value.committedAt||value.CommittedAt||null,
      source:'host-settings-commit-broadcast'
    };
  }

  function applyHostCommit(raw){
    const detail=normalizeCommitDetail(raw);
    if(detail.sequence>0 && detail.sequence<=lastCommitSequence) return;
    if(detail.sequence>0) lastCommitSequence=detail.sequence;

    const syncTheme=window.TvAIrTheme && typeof window.TvAIrTheme.handleSettingsCommitted==='function'
      ? window.TvAIrTheme.handleSettingsCommitted(detail)
      : (window.TvAIrTheme && typeof window.TvAIrTheme.syncRuntime==='function'
        ? window.TvAIrTheme.syncRuntime('host-settings-commit-broadcast')
        : Promise.resolve());
    Promise.resolve(syncTheme).catch(()=>{}).finally(()=>{
      try{ window.dispatchEvent(new CustomEvent('tvair-settings-saved', { detail })); }catch(_){ }
    });
  }

  function connectCommitStream(){
    if(commitSource || typeof window.EventSource!=='function') return;
    try{
      commitSource=new EventSource('/api/settings/commit-stream');
      commitSource.addEventListener('settings-committed',event=>{
        try{ applyHostCommit(JSON.parse(event.data||'{}')); }catch(_){ }
      });
      commitSource.onerror=()=>{
        // EventSource owns reconnect. Keep the same instance so Last-Event connection state is retained.
      };
    }catch(_){ commitSource=null; }
  }

  connectCommitStream();

  window.TvAIrSettingsHost={version:'1.2.2',open,close,contract:'shared-non-navigating-settings-host'};
})();
