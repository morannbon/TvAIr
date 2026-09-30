(() => {
  const KEY = 'tvair-system-theme';
  const EFFECTIVE_KEY = 'tvair-effective-theme';
  const REVISION_KEY = 'tvair-theme-revision';
  const THEME_EVENT = 'tvair-runtime-theme';
  const VALID = new Set(['current','light','dark']);

  function readStored(key, fallback){
    try{ return localStorage.getItem(key) || fallback; }catch(_){ return fallback; }
  }
  function writeStored(key, value){
    try{ localStorage.setItem(key, String(value)); }catch(_){ }
  }
  function normalize(value){
    const v=String(value||'').toLowerCase();
    return VALID.has(v) ? v : 'current';
  }

  const root = document.documentElement;
  let selectedTheme = normalize(root.getAttribute('data-tvair-selected-theme') || root.getAttribute('data-tvair-theme') || readStored(KEY, 'current'));
  let effectiveTheme = ((root.getAttribute('data-tvair-effective-theme') || root.getAttribute('data-theme') || readStored(EFFECTIVE_KEY, 'light')) === 'dark') ? 'dark' : 'light';
  // Host-rendered revision is authoritative for the current process generation.
  // Never seed Plugin Page generation tracking from localStorage because it can contain
  // a revision from a previous TvAIr process (for example 6 -> 0 after restart).
  const initialGenerationText = root.getAttribute('data-tvair-theme-generation') || '';
  const initialRevisionText = root.getAttribute('data-tvair-theme-revision') || '';
  let runtimeThemeGeneration = initialGenerationText;
  let runtimeThemeRevision = /^\d+$/.test(initialRevisionText) ? Number(initialRevisionText) : null;
  let runtimeThemeSyncInFlight = false;
  let pluginPageThemeRefreshQueuedRevision = null;

  // HotApply client lifecycle is owned by this Host-supplied browser runtime because only the
  // browser can know when the Plugin listener is actually registered. Server Render completion
  // is deliberately not used as a proxy for DOM/listener readiness.
  const hotApplyClientInstanceId = 'theme-client-' + Date.now() + '-' + Math.floor(Math.random()*1000000000);
  let hotApplyClientPhase = 'awaiting-plugin-ready'; // awaiting-plugin-ready | client-ready | navigating | disposed
  let pendingHotApplyState = null;
  let pendingHotApplyReason = '';

  function parseHexColor(value){
    const text=String(value || '').trim();
    const match=/^#([0-9a-f]{6})$/i.exec(text);
    if(!match) return null;
    const n=parseInt(match[1],16);
    return { r:(n>>16)&255, g:(n>>8)&255, b:n&255 };
  }
  function linearChannel(value){
    const c=value/255;
    return c<=0.04045 ? c/12.92 : Math.pow((c+0.055)/1.055,2.4);
  }
  function relativeLuminance(value){
    const rgb=parseHexColor(value);
    if(!rgb) return null;
    return 0.2126*linearChannel(rgb.r)+0.7152*linearChannel(rgb.g)+0.0722*linearChannel(rgb.b);
  }
  function resolveContrast(value){
    const luminance=relativeLuminance(value);
    const darkSurface=luminance !== null && luminance < 0.42;
    return darkSurface
      ? { main:'#ffffff', soft:'#eef4fa', muted:'#d7e1ea', inverse:'#111820', surface:'dark' }
      : { main:'#111820', soft:'#263746', muted:'#465967', inverse:'#ffffff', surface:'light' };
  }

  function applyDom(effective, selected, revision, generation){
    const resolvedEffective = effective === 'dark' ? 'dark' : 'light';
    const resolvedSelected = normalize(selected);
    selectedTheme = resolvedSelected;
    effectiveTheme = resolvedEffective;
    if(generation!==null && generation!==undefined) runtimeThemeGeneration=String(generation||'');
    if(Number.isFinite(Number(revision))) runtimeThemeRevision=Number(revision);

    for(const element of [document.documentElement, document.body].filter(Boolean)){
      element.classList.remove('tvair-theme-light','tvair-theme-dark','theme-light','theme-dark');
      element.classList.add(resolvedEffective === 'dark' ? 'tvair-theme-dark' : 'tvair-theme-light');
      element.classList.add(resolvedEffective === 'dark' ? 'theme-dark' : 'theme-light');
      element.setAttribute('data-theme', resolvedEffective);
      element.setAttribute('data-tvair-theme', resolvedSelected);
      element.setAttribute('data-tvair-selected-theme', resolvedSelected);
      element.setAttribute('data-tvair-effective-theme', resolvedEffective);
      if(runtimeThemeGeneration) element.setAttribute('data-tvair-theme-generation', runtimeThemeGeneration);
      else element.removeAttribute('data-tvair-theme-generation');
      if(runtimeThemeRevision!==null) element.setAttribute('data-tvair-theme-revision', String(runtimeThemeRevision));
      element.setAttribute('data-tvair-theme-scope', 'all');
    }

    document.querySelectorAll('input[name="cfg-system-theme"]').forEach(radio => {
      radio.checked = radio.value === resolvedSelected;
    });
  }

  function dispatchApplied(name, result, extra){
    try{ window.dispatchEvent(new CustomEvent(name, { detail:Object.assign({}, result || {}, extra || {}) })); }catch(_){ }
  }

  async function fetchRuntimeThemeState(){
    const response = await fetch('/api/settings-theme-state?ts=' + Date.now(), { cache:'no-store' });
    if(!response.ok) throw new Error('http_' + response.status);
    const state=await response.json();
    const selected=normalize(state && (state.selectedTheme || state.systemTheme));
    const effective=state && state.effectiveTheme === 'dark' ? 'dark' : 'light';
    const windowsEffective=state && state.windowsEffectiveTheme === 'dark' ? 'dark' : 'light';
    const generation=state && state.generation ? String(state.generation) : '';
    const revision=state && typeof state.revision === 'number' ? state.revision : null;
    const themeContract=state && state.themeContract && typeof state.themeContract === 'object' ? state.themeContract : {};
    return { selected, effective, windowsEffective, generation, revision, themeContract };
  }

  function persistCommittedState(state){
    writeStored(KEY,state.selected);
    writeStored(EFFECTIVE_KEY,state.effective);
    if(state.revision!==null) writeStored(REVISION_KEY,state.revision);
  }

  function isNormalPluginPage(){
    try{
      const body=document.body;
      return !!(body && body.getAttribute('data-plugin-route') && !body.getAttribute('data-tvair-toolwindow-contract'));
    }catch(_){ return false; }
  }

  function pluginThemeUpdateMode(){
    try{
      const body=document.body;
      const value=(body&&body.getAttribute('data-tvair-theme-update-mode'))
        ||root.getAttribute('data-tvair-theme-update-mode')||'host-rerender';
      return String(value).toLowerCase()==='hot-apply' ? 'hot-apply' : 'host-rerender';
    }catch(_){ return 'host-rerender'; }
  }


  function reportPluginThemeUpdate(result, state, previousRevision, reason, fullRender){
    try{
      const hook=window.__tvairThemeDeveloperDiagnostic;
      if(typeof hook!=='function') return;
      hook('theme_update',{
        result:String(result||''),
        route:(document.body&&document.body.getAttribute('data-plugin-route'))||'',
        generation:String(state&&state.generation||''),
        revision:String(state&&state.revision!=null?state.revision:''),
        previousRevision:String(previousRevision==null?'':previousRevision),
        mode:pluginThemeUpdateMode()==='hot-apply'?'HotApply':'HostRerender',
        selected:String(state&&state.selected||''),
        effective:String(state&&state.effective||''),
        visible:document.visibilityState==='visible',
        hidden:!!document.hidden,
        fullRender:!!fullRender,
        reason:String(reason||''),
        clientPhase:String(hotApplyClientPhase||''),
        clientInstanceId:String(hotApplyClientInstanceId||'')
      });
    }catch(_){ }
  }

  function finalizeCommittedState(state, reason){
    persistCommittedState(state);
    applyDom(state.effective,state.selected,state.revision,state.generation);
    const result={ selected:state.selected, effective:state.effective, generation:state.generation||'', revision:state.revision };
    dispatchApplied('tvair-theme-applied',result,{source:'host',reason:reason||'runtime-sync'});
    dispatchApplied('tvair-theme-hydrated',result,{source:'host',reason:reason||'runtime-sync'});
    dispatchApplied('tvair-theme-state-applied',result,{source:'host',reason:reason||'runtime-sync'});
    dispatchApplied('tvair-theme-runtime-synced',result,{source:'host',reason:reason||'runtime-sync'});
    return result;
  }

  function rememberPendingHotApply(state, reason){
    if(!state) return false;
    const incomingGeneration=String(state.generation||'');
    const incomingRevision=Number(state.revision);
    if(!incomingGeneration || !Number.isFinite(incomingRevision)) return false;

    if(pendingHotApplyState){
      const pendingGeneration=String(pendingHotApplyState.generation||'');
      const pendingRevision=Number(pendingHotApplyState.revision);
      if(pendingGeneration===incomingGeneration && Number.isFinite(pendingRevision) && pendingRevision>=incomingRevision)
        return false;
    }

    pendingHotApplyState={
      selected:String(state.selected||'current'),
      effective:String(state.effective||'light'),
      generation:incomingGeneration,
      revision:incomingRevision,
      themeContract:(state.themeContract&&typeof state.themeContract==='object')?state.themeContract:{}
    };
    pendingHotApplyReason=String(reason||'client_listener_not_ready');
    reportPluginThemeUpdate('PENDING',pendingHotApplyState,runtimeThemeRevision,pendingHotApplyReason,false);
    return true;
  }

  function clearPendingHotApplyApplied(state){
    if(!pendingHotApplyState||!state) return;
    const appliedGeneration=String(state.generation||'');
    const appliedRevision=Number(state.revision);
    const pendingGeneration=String(pendingHotApplyState.generation||'');
    const pendingRevision=Number(pendingHotApplyState.revision);
    if(appliedGeneration!==pendingGeneration || !Number.isFinite(appliedRevision) || !Number.isFinite(pendingRevision)) return;
    if(appliedRevision>=pendingRevision){
      pendingHotApplyState=null;
      pendingHotApplyReason='';
    }
  }

  function tryHotApplyPluginTheme(state, previousRevision, reason){
    if(!isNormalPluginPage() || pluginThemeUpdateMode()!=='hot-apply')
      return {attempted:false,applied:false};

    // This is the single client-readiness gate. "Server render completed", DOMContentLoaded,
    // visibility and tab activation are not readiness signals. A HotApply Plugin must explicitly
    // call TvAIrTheme.registerHotApplyClientReady() after registering its tvair-runtime-theme listener.
    if(hotApplyClientPhase!=='client-ready'){
      rememberPendingHotApply(state,
        hotApplyClientPhase==='navigating' ? 'host_navigation_in_flight'
        : hotApplyClientPhase==='disposed' ? 'page_disposed'
        : 'client_listener_not_ready');
      return {attempted:true,applied:false,pending:true,reason:pendingHotApplyReason||'client_listener_not_ready'};
    }

    if(window.__tvairHostPageNavigationInFlight){
      hotApplyClientPhase='navigating';
      rememberPendingHotApply(state,'host_navigation_in_flight');
      return {attempted:true,applied:false,pending:true,reason:'host_navigation_in_flight'};
    }

    let acknowledged=false;
    let acknowledgedReason='';
    const detail={
      generation:String(state&&state.generation||''),
      revision:state&&state.revision!=null?Number(state.revision):null,
      selectedTheme:String(state&&state.selected||'current'),
      effectiveTheme:String(state&&state.effective||'light'),
      themeContract:(state&&state.themeContract)||{},
      source:'host',
      reason:String(reason||'host_theme_commit_revision'),
      acknowledge:function(success, ackReason){
        acknowledged=success===true;
        acknowledgedReason=String(ackReason||'');
      }
    };

    reportPluginThemeUpdate('QUEUED',state,previousRevision,reason||'host_theme_commit_revision',false);
    reportPluginThemeUpdate('DISPATCHED',state,previousRevision,reason||'host_theme_commit_revision',false);
    try{
      let ev=null;
      try{
        ev=new CustomEvent(THEME_EVENT,{detail:detail,bubbles:false,cancelable:false});
      }catch(_){
        ev=document.createEvent('CustomEvent');
        ev.initCustomEvent(THEME_EVENT,false,false,detail);
      }
      window.dispatchEvent(ev);
    }catch(ex){
      acknowledged=false;
      acknowledgedReason='dispatch_exception';
    }

    if(acknowledged){
      // Plugin presentation is updated synchronously before ack. Only now update the Host shell
      // attributes/classes so users never see a mixed old/new Theme.
      const result=finalizeCommittedState(state,reason);
      clearPendingHotApplyApplied(state);
      reportPluginThemeUpdate('COMPLETED',state,previousRevision,acknowledgedReason||'plugin_acknowledged',false);
      return {attempted:true,applied:true,result};
    }

    // Full rerender fallback is legal only after explicit client-ready and an actual dispatch.
    const fallbackReason=acknowledgedReason||'plugin_not_acknowledged_after_client_ready';
    reportPluginThemeUpdate('FALLBACK_FULL_RENDER',state,previousRevision,fallbackReason,true);
    const queued=queueNormalPluginPageThemeRefresh(state.revision,previousRevision,'hot_apply_failed_'+fallbackReason);
    return {attempted:true,applied:false,fallbackQueued:queued,reason:fallbackReason};
  }

  async function registerHotApplyClientReady(){
    if(!isNormalPluginPage() || pluginThemeUpdateMode()!=='hot-apply')
      return {ready:false,reason:'not_hot_apply_page'};

    if(hotApplyClientPhase==='disposed')
      return {ready:false,reason:'page_disposed'};

    if(window.__tvairHostPageNavigationInFlight){
      hotApplyClientPhase='navigating';
      return {ready:false,reason:'host_navigation_in_flight'};
    }

    hotApplyClientPhase='client-ready';
    let latest=null;
    try{ latest=await fetchRuntimeThemeState(); }catch(_){}

    if(!latest){
      reportPluginThemeUpdate('CLIENT_READY',
        {selected:selectedTheme,effective:effectiveTheme,generation:runtimeThemeGeneration,revision:runtimeThemeRevision,themeContract:{}},
        runtimeThemeRevision,'client_listener_ready_host_state_unavailable',false);
      return {ready:true,applied:false,reason:'host_state_unavailable'};
    }

    reportPluginThemeUpdate('CLIENT_READY',latest,runtimeThemeRevision,'client_listener_registered',false);

    const same=String(latest.generation||'')===String(runtimeThemeGeneration||'')
      && Number(latest.revision)===Number(runtimeThemeRevision)
      && latest.selected===selectedTheme
      && latest.effective===effectiveTheme;

    let target=latest;
    if(pendingHotApplyState){
      const pendingGeneration=String(pendingHotApplyState.generation||'');
      const pendingRevision=Number(pendingHotApplyState.revision);
      const latestGeneration=String(latest.generation||'');
      const latestRevision=Number(latest.revision);
      if(pendingGeneration===latestGeneration && Number.isFinite(pendingRevision) && pendingRevision>latestRevision)
        target=pendingHotApplyState;
    }

    if(same && !pendingHotApplyState){
      return {ready:true,applied:false,reason:'already_current'};
    }

    const applied=tryHotApplyPluginTheme(target,runtimeThemeRevision,
      pendingHotApplyState ? 'pending_latest_after_client_ready' : 'client_ready_latest_host_state');
    return {ready:true,applied:!!(applied&&applied.applied),fallbackQueued:!!(applied&&applied.fallbackQueued),reason:applied&&applied.reason||''};
  }


  function pluginThemeRefreshStateKey(){
    try{
      let search=String(location.search||'');
      search=search.replace(/([?&])_(?:tvairPageRefresh|tvairThemeRefreshRevision|tvairThemeRefreshPreviousRevision)=[^&]*&?/ig,(m,sep)=>sep==='?'?'?':'');
      search=search.replace(/\?&/g,'?').replace(/[?&]$/,'');
      return 'tvair-plugin-theme-refresh:' + location.pathname + search;
    }catch(_){ return ''; }
  }

  function reportPluginPageThemeRefresh(result, revision, previousRevision, reason, dedupe){
    try{
      const hook=window.__tvairThemeDeveloperDiagnostic;
      if(typeof hook!=='function') return;
      hook('theme_refresh',{
        result:String(result||''),
        route:(document.body&&document.body.getAttribute('data-plugin-route'))||'',
        revision:String(revision==null?'':revision),
        previousRevision:String(previousRevision==null?'':previousRevision),
        visible:document.visibilityState==='visible',
        reason:String(reason||''),
        dedupe:!!dedupe
      });
    }catch(_){ }
  }

  function clearPluginPageThemeRefreshQuery(){
    try{
      if(!isNormalPluginPage()) return;
      const u=new URL(location.href);
      let changed=false;
      for(const key of ['_tvairPageRefresh','_tvairThemeRefreshRevision','_tvairThemeRefreshPreviousRevision']){
        if(u.searchParams.has(key)){ u.searchParams.delete(key); changed=true; }
      }
      if(changed && window.history && typeof window.history.replaceState==='function'){
        window.history.replaceState(window.history.state,'',u.pathname+u.search+u.hash);
      }
    }catch(_){ }
  }

  function capturePluginPageThemeRefreshState(revision, previousRevision){
    try{
      const key=pluginThemeRefreshStateKey();
      if(!key) return;
      const active=document.activeElement;
      sessionStorage.setItem(key,JSON.stringify({
        revision:Number(revision)||0, previousRevision:previousRevision==null?null:Number(previousRevision), at:Date.now(),
        x:window.pageXOffset||document.documentElement.scrollLeft||0,
        y:window.pageYOffset||document.documentElement.scrollTop||0,
        id:active&&active.id?active.id:'',
        start:(active&&typeof active.selectionStart==='number')?active.selectionStart:null,
        end:(active&&typeof active.selectionEnd==='number')?active.selectionEnd:null
      }));
    }catch(_){ }
  }

  function restorePluginPageThemeRefreshState(){
    try{
      if(!isNormalPluginPage()) return;
      const key=pluginThemeRefreshStateKey();
      if(!key) return;
      const raw=sessionStorage.getItem(key);
      if(!raw) return;
      const state=JSON.parse(raw);
      const currentText=document.documentElement.getAttribute('data-tvair-theme-revision')||'';
      const current=/^\d+$/.test(currentText)?Number(currentText):null;
      if(!state || Date.now()-Number(state.at||0)>15000 || current===null){
        sessionStorage.removeItem(key);
        return;
      }
      // A newer ThemeRevision may have arrived while the previous Host render was still in flight.
      // Keep the preserved UI state until the page generation that actually rendered the latest
      // authoritative revision arrives; otherwise an intermediate response would consume the state
      // and the follow-up convergence render could no longer restore scroll/focus.
      if(Number(state.revision)!==current) return;
      sessionStorage.removeItem(key);
      // COMPLETED is Host-owned. The server records it only after the requested ThemeRevision
      // has been rendered into a fresh RuntimeUiRenderContext and shell successfully.
      window.scrollTo(Number(state.x)||0,Number(state.y)||0);
      if(state.id){
        const active=document.getElementById(state.id);
        if(active&&typeof active.focus==='function'){
          try{ active.focus({preventScroll:true}); }catch(_){ active.focus(); }
          if(typeof active.setSelectionRange==='function' && state.start!==null){
            try{ active.setSelectionRange(state.start,state.end===null?state.start:state.end); }catch(_){ }
          }
        }
      }
    }catch(_){ }
    finally{ clearPluginPageThemeRefreshQuery(); }
  }

  function queueNormalPluginPageThemeRefresh(revision, previousRevision, reason){
    if(!isNormalPluginPage()) return false;
    const rev=Number(revision);
    if(!Number.isFinite(rev)) return false;
    if(pluginPageThemeRefreshQueuedRevision===rev){
      reportPluginPageThemeRefresh('SKIPPED', rev, previousRevision, 'same_revision_already_queued', true);
      return false;
    }

    // HOST_PAGE_RENDER_COORDINATOR_CONTRACT:
    // Theme revision is authoritative.  A token-recovery/page navigation that is already in flight
    // must not cause a newer theme generation to be dropped.  Instead, attach this ThemeRevision to
    // the same page generation and let that navigation render the latest Host state exactly once.
    // Only an already-owned theme refresh for the same generation is a true duplicate.
    const inFlight=window.__tvairHostPageNavigationInFlight||'';
    pluginPageThemeRefreshQueuedRevision=rev;
    reportPluginPageThemeRefresh('QUEUED', rev, previousRevision,
      inFlight ? 'coalesced_with_'+String(inFlight)+'_'+String(reason||'host_theme_commit_revision') : (reason||'host_theme_commit_revision'), false);
    capturePluginPageThemeRefreshState(rev, previousRevision);

    if(inFlight){
      // The existing Host page navigation will fetch a fresh RuntimeUiRenderContext and therefore
      // consumes the newest ThemeRevision.  Do not start a competing second navigation.
      return true;
    }

    hotApplyClientPhase='navigating';
    window.__tvairHostPageNavigationInFlight='theme_refresh';
    const u=new URL(location.href);
    u.searchParams.set('_tvairPageRefresh',String(Date.now()));
    u.searchParams.set('_tvairThemeRefreshRevision',String(rev));
    if(previousRevision!==null&&previousRevision!==undefined)u.searchParams.set('_tvairThemeRefreshPreviousRevision',String(previousRevision));
    if(typeof window.__tvairThemeDeveloperDiagnostic==='function')
      setTimeout(()=>location.replace(u.pathname+u.search+u.hash),40);
    else
      location.replace(u.pathname+u.search+u.hash);
    return true;
  }

  function applyCommittedState(state, reason, allowPluginPageRefresh){
    const previousGeneration=runtimeThemeGeneration;
    const previousRevision=runtimeThemeRevision;
    const previousSelected=selectedTheme;
    const previousEffective=effectiveTheme;
    const revision=state.revision;
    const generation=String(state.generation||'');
    const generationChanged=!!(generation && previousGeneration && generation!==previousGeneration);
    const semanticThemeChanged=state.selected!==previousSelected || state.effective!==previousEffective;

    if(allowPluginPageRefresh
      && isNormalPluginPage()
      && pluginThemeUpdateMode()==='hot-apply'
      && (generationChanged || previousRevision!==revision || semanticThemeChanged)){
      const hot=tryHotApplyPluginTheme(state,previousRevision,reason||'host_theme_commit_revision');
      if(hot.attempted){
        if(hot.applied) return Object.assign({},hot.result,{hotApplied:true,refreshQueued:false});
        return {
          selected:selectedTheme,
          effective:effectiveTheme,
          generation:runtimeThemeGeneration,
          revision:runtimeThemeRevision,
          hotApplied:false,
          refreshQueued:!!hot.fallbackQueued,
          hotApplyPending:!!hot.pending,
          hotApplyFallback:!!hot.fallbackQueued,
          reason:hot.reason||'hot_apply_not_completed'
        };
      }
    }

    const result=finalizeCommittedState(state,reason);
    if(allowPluginPageRefresh && previousRevision!==null && revision!==null
      && (generationChanged || previousRevision!==revision)){
      // A normal Plugin Page can outlive the TvAIr process in the browser. When a new Host starts,
      // its generation changes and ThemeRevision restarts from 0. For HostRerender pages, accept that
      // new Host generation without treating the backward revision as a user theme commit.
      if(generationChanged){
        pluginPageThemeRefreshQueuedRevision=null;
        if(semanticThemeChanged && isNormalPluginPage()){
          const queued=queueNormalPluginPageThemeRefresh(revision,previousRevision,'host_generation_changed_theme_diff');
          return Object.assign(result,{refreshQueued:queued,hostGenerationReset:true});
        }
        return Object.assign(result,{refreshQueued:false,hostGenerationReset:true});
      }
      if(Number(previousRevision)>Number(revision)){
        pluginPageThemeRefreshQueuedRevision=null;
        return Object.assign(result,{refreshQueued:false,hostGenerationReset:true});
      }
      if(queueNormalPluginPageThemeRefresh(revision,previousRevision,reason||'host_theme_commit_revision')){
        return Object.assign(result,{refreshQueued:true});
      }
    }
    return result;
  }

  async function syncRuntimeTheme(reason, allowPluginPageRefresh=true){
    if(runtimeThemeSyncInFlight) return null;
    if(window._cfgThemeTouched === true && reason!=='settings-host-save') return { skipped:true, reason:'settings_preview_in_progress' };
    runtimeThemeSyncInFlight=true;
    try{
      const state=await fetchRuntimeThemeState();
      if(state.generation===runtimeThemeGeneration && state.revision===runtimeThemeRevision && state.selected===selectedTheme && state.effective===effectiveTheme){
        return {selected:state.selected,effective:state.effective,generation:state.generation,revision:state.revision,skipped:true,reason:'same_generation'};
      }
      return await applyCommittedState(state,reason||'runtime-sync',allowPluginPageRefresh);
    }catch(_){ return null; }
    finally{ runtimeThemeSyncInFlight=false; }
  }

  async function applyPreview(theme){
    const selected=normalize(theme||selectedTheme);
    let previewEffective=selected;
    if(selected==='current'){
      try{ previewEffective=(await fetchRuntimeThemeState()).windowsEffective; }
      catch(_){ previewEffective=effectiveTheme; }
    }
    applyDom(previewEffective,selected,runtimeThemeRevision,runtimeThemeGeneration);
    const result={selected,effective:previewEffective,generation:runtimeThemeGeneration,revision:runtimeThemeRevision};
    dispatchApplied('tvair-theme-applied',result,{source:'preview'});
    dispatchApplied('tvair-theme-state-applied',result,{source:'preview',reason:'settings-preview'});
    return result;
  }

  async function handleSettingsCommitted(detail){
    const targetRevision=detail && Number.isFinite(Number(detail.themeRevision)) ? Number(detail.themeRevision) : null;
    const themeChanged=!!(detail && detail.themeChanged===true);

    // The Host settings-commit broadcast is the canonical live-update trigger for every browser surface.
    // Theme HotApply is opt-in per RuntimeUiDefinition. Non-supporting/failing Plugin Pages retain the
    // Host full-rerender fallback. Focus, visibility, storage events and Plugin polling are not triggers.
    if(themeChanged && targetRevision!==null){
      let committedState=null;
      try{ committedState=await fetchRuntimeThemeState(); }catch(_){ }
      if(committedState){
        return await applyCommittedState(committedState,'host-settings-commit-broadcast',true);
      }

      // Host state fetch failed: do not guess ThemeContract. A normal Plugin Page falls back to the
      // existing Host rerender path using the commit revision so the next render gets canonical state.
      const previousRevision=runtimeThemeRevision;
      const queued=queueNormalPluginPageThemeRefresh(targetRevision,previousRevision,'theme_state_fetch_failed');
      return {selected:selectedTheme,effective:effectiveTheme,generation:runtimeThemeGeneration,revision:runtimeThemeRevision,refreshQueued:queued};
    }

    // Non-theme saves only converge state. They never force a Plugin Page rerender.
    return await syncRuntimeTheme('host-settings-commit-broadcast',false);
  }

  window.TvAIrTheme={
    key:KEY,effectiveKey:EFFECTIVE_KEY,revisionKey:REVISION_KEY,
    get(){return selectedTheme;}, getEffective(){return effectiveTheme;}, getGeneration(){return runtimeThemeGeneration;}, getRevision(){return runtimeThemeRevision;},
    async getWindows(){ try{return (await fetchRuntimeThemeState()).windowsEffective;}catch(_){return effectiveTheme;} },
    async apply(theme){ return await applyPreview(theme); },
    async hydrateFromServer(_theme,reason){ return await syncRuntimeTheme(reason||'server-settings',false); },
    async set(_theme){ return await syncRuntimeTheme('settings-save',false); },
    async syncRuntime(reason){ return await syncRuntimeTheme(reason||'manual-sync',true); },
    async handleSettingsCommitted(detail){ return await handleSettingsCommitted(detail); },
    async registerHotApplyClientReady(){ return await registerHotApplyClientReady(); },
    getHotApplyClientPhase(){ return hotApplyClientPhase; },
    relativeLuminance,resolveContrast
  };

  // Paint from server attributes/cache immediately, then converge to the Host state generation.
  applyDom(effectiveTheme,selectedTheme,runtimeThemeRevision,runtimeThemeGeneration);
  if(!document.body){
    const applyBodyTheme=()=>{ if(!document.body)return false; applyDom(effectiveTheme,selectedTheme,runtimeThemeRevision,runtimeThemeGeneration); return true; };
    if(window.MutationObserver){
      const observer=new MutationObserver(()=>{if(applyBodyTheme())observer.disconnect();});
      observer.observe(document.documentElement,{childList:true});
    }
    document.addEventListener('DOMContentLoaded',applyBodyTheme,{once:true});
  }
  if(document.readyState==='loading') document.addEventListener('DOMContentLoaded',restorePluginPageThemeRefreshState,{once:true});
  else setTimeout(restorePluginPageThemeRefreshState,0);

  // Initial convergence may discover that the Host theme advanced while this page was being rendered.
  // For HotApply pages, that state is retained as PENDING until the Plugin explicitly declares that
  // its Theme listener is ready. We never infer listener readiness from DOMContentLoaded/render completion.
  const startInitialRuntimeThemeSync=()=>syncRuntimeTheme('initial-runtime-sync',true).catch(()=>{});
  if(document.readyState==='loading')
    document.addEventListener('DOMContentLoaded',startInitialRuntimeThemeSync,{once:true});
  else
    startInitialRuntimeThemeSync();

  window.addEventListener('beforeunload',()=>{ hotApplyClientPhase='disposed'; },{once:true});

  // Theme refresh is never triggered by focus/visibility/storage. Existing pages react only to the
  // Host settings-commit broadcast; pages that do not yet exist render the current Host revision on first load.
})();
