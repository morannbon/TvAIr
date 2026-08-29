(() => {
  const KEY = 'tvair-system-theme';
  const EFFECTIVE_KEY = 'tvair-effective-theme';
  const VALID = new Set(['current','light','dark']);
  function readStored(key, fallback){
    try{ return localStorage.getItem(key) || fallback; }catch(_){ return fallback; }
  }

  const root = document.documentElement;
  const serverSelected = root.getAttribute('data-tvair-selected-theme') || root.getAttribute('data-tvair-theme') || '';
  const serverEffective = root.getAttribute('data-tvair-effective-theme') || root.getAttribute('data-theme') || '';
  let selectedTheme = normalize(serverSelected || readStored(KEY, 'current'));
  let windowsThemeCache = (serverEffective === 'dark' || serverEffective === 'light')
    ? serverEffective
    : (readStored(EFFECTIVE_KEY, 'light') === 'dark' ? 'dark' : 'light');
  let runtimeThemeSyncRevision = null;
  let runtimeThemeSyncInFlight = false;

  function normalize(value){
    return VALID.has(value) ? value : 'current';
  }

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

  async function fetchWindowsTheme(){
    try{
      const response = await fetch('/api/system-theme', { cache:'no-store' });
      if(!response.ok) throw new Error('http_' + response.status);
      const data = await response.json();
      windowsThemeCache = data && data.theme === 'dark' ? 'dark' : 'light';
    }catch(_){
      // Keep the last authoritative effective theme when the runtime endpoint is temporarily unavailable.
    }
    return windowsThemeCache;
  }

  function applyDom(effective, selected){
    const resolvedEffective = effective === 'dark' ? 'dark' : 'light';
    const resolvedSelected = normalize(selected);
    selectedTheme = resolvedSelected;

    for(const element of [document.documentElement, document.body].filter(Boolean)){
      element.classList.remove('tvair-theme-light','tvair-theme-dark','theme-light','theme-dark');
      element.classList.add(resolvedEffective === 'dark' ? 'tvair-theme-dark' : 'tvair-theme-light');
      element.classList.add(resolvedEffective === 'dark' ? 'theme-dark' : 'theme-light');
      element.setAttribute('data-theme', resolvedEffective);
      element.setAttribute('data-tvair-theme', resolvedSelected);
      element.setAttribute('data-tvair-selected-theme', resolvedSelected);
      element.setAttribute('data-tvair-effective-theme', resolvedEffective);
      // Existing CSS still consumes this attribute. It is a compatibility projection,
      // not a selectable preview state.
      element.setAttribute('data-tvair-theme-scope', 'all');
    }

    document.querySelectorAll('input[name="cfg-system-theme"]').forEach(radio => {
      radio.checked = radio.value === resolvedSelected;
    });
  }

  async function applyTheme(theme){
    const selected = normalize(theme || selectedTheme);
    const effective = selected === 'current' ? await fetchWindowsTheme() : selected;
    applyDom(effective, selected);
    return { selected, effective };
  }

  function dispatchApplied(name, result, extra){
    try{
      window.dispatchEvent(new CustomEvent(name, { detail:Object.assign({}, result || {}, extra || {}) }));
    }catch(_){ }
  }

  async function hydrateFromServer(theme, reason){
    const selected = normalize(theme);
    selectedTheme = selected;
    try{ localStorage.setItem(KEY, selected); }catch(_){ }
    const result = await applyTheme(selected);
    try{ localStorage.setItem(EFFECTIVE_KEY, result.effective); }catch(_){ }
    dispatchApplied('tvair-theme-applied', result, { source:'server', reason:reason || 'server-settings' });
    dispatchApplied('tvair-theme-hydrated', result, { source:'server', reason:reason || 'server-settings' });
    dispatchApplied('tvair-theme-state-applied', result, { source:'server', reason:reason || 'server-settings' });
    return result;
  }

  async function fetchRuntimeThemeState(){
    const response = await fetch('/api/settings-theme-state?ts=' + Date.now(), { cache:'no-store' });
    if(!response.ok) throw new Error('http_' + response.status);
    return await response.json();
  }

  async function syncRuntimeTheme(reason){
    if(runtimeThemeSyncInFlight) return null;
    if(window._cfgThemeTouched === true) return { skipped:true, reason:'settings_preview_in_progress' };
    runtimeThemeSyncInFlight = true;
    try{
      const state = await fetchRuntimeThemeState();
      const selected = normalize(state && (state.systemTheme || state.selectedTheme));
      const revision = state && typeof state.revision === 'number' ? state.revision : null;
      const sameRevision = revision !== null && runtimeThemeSyncRevision === revision;
      runtimeThemeSyncRevision = revision;
      if(sameRevision && selected === selectedTheme) return { selected, skipped:true, reason:'same_revision' };
      if(selected !== selectedTheme || !sameRevision){
        const result = await hydrateFromServer(selected, reason || 'runtime-sync');
        dispatchApplied('tvair-theme-runtime-synced', result, { source:'runtime-sync', reason:reason || 'runtime-sync', revision });
        return result;
      }
      return { selected, skipped:true, reason:'same_theme' };
    }catch(_){
      return null;
    }finally{
      runtimeThemeSyncInFlight = false;
    }
  }

  window.TvAIrTheme = {
    key:KEY,
    effectiveKey:EFFECTIVE_KEY,
    get(){ return selectedTheme; },
    async getWindows(){ return await fetchWindowsTheme(); },
    async apply(theme){
      const result = await applyTheme(theme);
      dispatchApplied('tvair-theme-applied', result, { source:'preview' });
      dispatchApplied('tvair-theme-state-applied', result, { source:'preview', reason:'settings-preview' });
      return result;
    },
    async hydrateFromServer(theme, reason){ return await hydrateFromServer(theme, reason); },
    async set(theme){
      const selected = normalize(theme);
      selectedTheme = selected;
      try{ localStorage.setItem(KEY, selected); }catch(_){ }
      const result = await applyTheme(selected);
      try{ localStorage.setItem(EFFECTIVE_KEY, result.effective); }catch(_){ }
      dispatchApplied('tvair-theme-changed', result, { source:'saved-settings' });
      dispatchApplied('tvair-theme-applied', result, { source:'saved-settings' });
      dispatchApplied('tvair-theme-state-applied', result, { source:'saved-settings', reason:'settings-save' });
      return result;
    },
    async syncRuntime(reason){ return await syncRuntimeTheme(reason || 'manual-sync'); },
    relativeLuminance,
    resolveContrast
  };

  // Establish the last authoritative theme synchronously before the first body paint.
  // Server-rendered attributes take precedence; static pages use the last saved effective theme.
  applyDom(windowsThemeCache, selectedTheme);
  if(!document.body){
    const applyBodyTheme = () => {
      if(!document.body) return false;
      applyDom(windowsThemeCache, selectedTheme);
      return true;
    };
    if(window.MutationObserver){
      const observer = new MutationObserver(() => {
        if(applyBodyTheme()) observer.disconnect();
      });
      observer.observe(document.documentElement, { childList:true });
    }
    document.addEventListener('DOMContentLoaded', applyBodyTheme, { once:true });
  }
  syncRuntimeTheme('initial-runtime-sync').catch(()=>{});

  window.addEventListener('focus', () => { syncRuntimeTheme('window-focus').catch(()=>{}); });
  document.addEventListener('visibilitychange', () => {
    if(document.visibilityState === 'visible') syncRuntimeTheme('visibility').catch(()=>{});
  });
  window.addEventListener('storage', event => {
    if(event && (event.key === KEY || event.key === EFFECTIVE_KEY)){
      syncRuntimeTheme('storage-change').catch(()=>{});
    }
  });
})();
