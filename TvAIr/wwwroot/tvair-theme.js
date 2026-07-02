(() => {
  const KEY = 'tvair-system-theme';
  const PREVIEW_SCOPE_KEY = 'tvair-theme-preview-scope';
  const PREVIEW_BUILD_KEY = 'tvair-theme-preview-build';
  const PREVIEW_BUILD = '1.0.8';
  const VALID = new Set(['current','light','dark']);
  const VALID_SCOPE = new Set(['off','non-program','all']);
  const DEFAULT_PREVIEW_THEME = 'dark';
  const DEFAULT_PREVIEW_SCOPE = 'all';
  const RUNTIME_THEME_SYNC_INTERVAL_MS = 1500;
  let windowsThemeCache = 'light';
  let runtimeThemeSyncRevision = null;
  let runtimeThemeSyncInFlight = false;

  function normalize(v){
    return VALID.has(v) ? v : 'current';
  }

  function normalizeScope(v){
    return VALID_SCOPE.has(v) ? v : 'off';
  }

  function ensurePreviewBuildDefaults(){
    try{
      const applied = localStorage.getItem(PREVIEW_BUILD_KEY);
      if(applied === PREVIEW_BUILD) return;

      // release_contract applies the shared dark theme token set across TvAIr host UI.
      // Apply once per build so program guide / list / plugin shell CSS use the same scope.
      localStorage.setItem(KEY, DEFAULT_PREVIEW_THEME);
      localStorage.setItem(PREVIEW_SCOPE_KEY, DEFAULT_PREVIEW_SCOPE);
      localStorage.setItem(PREVIEW_BUILD_KEY, PREVIEW_BUILD);
    }catch(_){
      // localStorage is optional; fall back to in-memory defaults below.
    }
  }

  function readUserTheme(){
    try{ return normalize(localStorage.getItem(KEY) || DEFAULT_PREVIEW_THEME); }
    catch(_){ return DEFAULT_PREVIEW_THEME; }
  }

  function readPreviewScope(){
    try{ return normalizeScope(localStorage.getItem(PREVIEW_SCOPE_KEY) || DEFAULT_PREVIEW_SCOPE); }
    catch(_){ return DEFAULT_PREVIEW_SCOPE; }
  }

  async function fetchWindowsTheme(){
    try{
      const r = await fetch('/api/system-theme', { cache:'no-store' });
      if(!r.ok) throw new Error('http');
      const j = await r.json();
      windowsThemeCache = (j && j.theme === 'dark') ? 'dark' : 'light';
    }catch(_){
      windowsThemeCache = 'light';
    }
    return windowsThemeCache;
  }

  function isNonProgramPage(){
    return !!(document.body && document.body.classList.contains('tvair-non-program-page'));
  }

  function isProgramGuidePage(){
    return !!(document.body && document.body.classList.contains('tvair-guide-page'));
  }

  function computeScopedEffective(effective, scope){
    const eff = effective === 'dark' ? 'dark' : 'light';

    // Scope rule for release_contract:
    // - all: apply the selected theme to program guide, non-program pages, and plugin shell.
    // - non-program: keep the older safe preview scope for fallback testing.
    // - off: force light.
    if(scope === 'all') return eff;
    if(scope === 'non-program'){
      if(isNonProgramPage()) return eff;
      if(isProgramGuidePage()) return 'light';
      return 'light';
    }
    return 'light';
  }

  function applyClass(effective, selected){
    const scope = readPreviewScope();
    const selectedTheme = selected || readUserTheme();
    const scopedEffective = computeScopedEffective(effective, scope);
    const root = document.documentElement;

    if(root){
      root.setAttribute('data-theme', scopedEffective);
      root.setAttribute('data-tvair-theme', selectedTheme);
      root.setAttribute('data-tvair-selected-theme', selectedTheme);
      root.setAttribute('data-tvair-effective-theme', scopedEffective);
      root.setAttribute('data-tvair-theme-scope', scope);
      root.setAttribute('data-tvair-theme-preview-build', PREVIEW_BUILD);
    }

    for(const el of [document.documentElement, document.body].filter(Boolean)){
      // release_contract ThemeStateClassAliasContract
      // Keep modern tvair-theme-* classes and legacy theme-* / data-theme selectors in sync.
      // Older foundation selectors still use body.theme-dark/body:not(.theme-dark); without these
      // aliases, dark pages can receive light token fallbacks inside body-scoped rules.
      el.classList.remove('tvair-theme-light','tvair-theme-dark','theme-light','theme-dark');
      el.classList.add(scopedEffective === 'dark' ? 'tvair-theme-dark' : 'tvair-theme-light');
      el.classList.add(scopedEffective === 'dark' ? 'theme-dark' : 'theme-light');
      el.setAttribute('data-theme', scopedEffective);
      el.setAttribute('data-tvair-theme', selectedTheme);
      el.setAttribute('data-tvair-selected-theme', selectedTheme);
      el.setAttribute('data-tvair-effective-theme', scopedEffective);
      el.setAttribute('data-tvair-theme-scope', scope);
      el.setAttribute('data-tvair-theme-preview-build', PREVIEW_BUILD);
    }

    document.querySelectorAll('input[name="cfg-system-theme"]').forEach(r=>{
      r.checked = r.value === selectedTheme;
    });
  }

  async function applyTheme(theme){
    const selected = normalize(theme || readUserTheme());
    const rawEffective = selected === 'current' ? await fetchWindowsTheme() : selected;
    applyClass(rawEffective, selected);
    return { selected, effective: computeScopedEffective(rawEffective, readPreviewScope()), rawEffective, scope: readPreviewScope() };
  }

  ensurePreviewBuildDefaults();

  function dispatchApplied(name, result, extra){
    try{
      window.dispatchEvent(new CustomEvent(name, { detail: Object.assign({}, result || {}, extra || {}) }));
    }catch(_){}
  }

  function dispatchRuntimeApplied(result, extra){
    dispatchApplied('tvair-theme-runtime-applied', result, Object.assign({
      source:'runtime-sync',
      contract:'release_contract'
    }, extra || {}));
  }

  async function hydrateFromServer(theme, reason){
    const selected = normalize(theme);
    try{ localStorage.setItem(KEY, selected); }catch(_){}
    const result = await applyTheme(selected);
    dispatchApplied('tvair-theme-applied', result, { source:'server', reason: reason || 'hydrateFromServer' });
    dispatchApplied('tvair-theme-hydrated', result, { source:'server', reason: reason || 'hydrateFromServer' });
    return result;
  }

  async function fetchRuntimeThemeState(){
    const r = await fetch('/api/settings-theme-state?ts=' + Date.now(), { cache:'no-store' });
    if(!r.ok) throw new Error('http_' + r.status);
    return await r.json();
  }

  async function syncRuntimeTheme(reason){
    if(runtimeThemeSyncInFlight) return null;
    if(window._cfgThemeTouched === true) return { skipped:true, reason:'settings_preview_in_progress' };
    runtimeThemeSyncInFlight = true;
    try{
      const state = await fetchRuntimeThemeState();
      const selected = normalize(state && (state.systemTheme || state.selectedTheme));
      const revision = state && typeof state.revision === 'number' ? state.revision : null;
      const current = readUserTheme();
      const sameRevision = revision !== null && runtimeThemeSyncRevision === revision;
      runtimeThemeSyncRevision = revision;
      if(sameRevision && selected === current) return { selected, skipped:true, reason:'same_revision' };
      if(selected !== current){
        const result = await hydrateFromServer(selected, reason || 'runtime-sync');
        dispatchApplied('tvair-theme-runtime-synced', result, { source:'runtime-sync', reason: reason || 'runtime-sync', revision });
        dispatchRuntimeApplied(result, { reason: reason || 'runtime-sync', revision });
        return result;
      }
      return { selected, skipped:true, reason:'same_theme' };
    }catch(_){
      return null;
    }finally{
      runtimeThemeSyncInFlight = false;
    }
  }

  function scheduleSemanticAuditAfterThemeSync(reason){ }

  window.TvAIrTheme = {
    key: KEY,
    previewScopeKey: PREVIEW_SCOPE_KEY,
    previewBuildKey: PREVIEW_BUILD_KEY,
    previewBuild: PREVIEW_BUILD,
    get: readUserTheme,
    getPreviewScope: readPreviewScope,
    async getWindows(){ return await fetchWindowsTheme(); },
    async apply(theme){ const result = await applyTheme(theme); dispatchApplied('tvair-theme-applied', result, { source:'apply' }); return result; },
    async hydrateFromServer(theme, reason){ return await hydrateFromServer(theme, reason); },
    async set(theme){
      const selected = normalize(theme);
      try{ localStorage.setItem(KEY, selected); }catch(_){}
      const result = await applyTheme(selected);
      window.dispatchEvent(new CustomEvent('tvair-theme-changed', { detail: result }));
      dispatchApplied('tvair-theme-applied', result, { source:'set' });
      return result;
    },
    async setPreviewScope(scope){
      const normalized = normalizeScope(scope);
      try{ localStorage.setItem(PREVIEW_SCOPE_KEY, normalized); }catch(_){}
      const result = await applyTheme(readUserTheme());
      window.dispatchEvent(new CustomEvent('tvair-theme-scope-changed', { detail: { scope: normalized, theme: result } }));
      dispatchApplied('tvair-theme-applied', result, { source:'setPreviewScope', scope: normalized });
      return { scope: normalized, theme: result };
    },
    async syncRuntime(reason){ const result = await syncRuntimeTheme(reason || 'manual-sync'); if(result && !result.skipped) dispatchRuntimeApplied(result, { reason: reason || 'manual-sync' }); scheduleSemanticAuditAfterThemeSync('theme-runtime-sync'); return result; },
    async disablePreview(){
      try{
        localStorage.setItem(PREVIEW_SCOPE_KEY, 'off');
        localStorage.setItem(KEY, 'light');
      }catch(_){}
      return await applyTheme('light');
    },
    async enableNonProgramDarkPreview(){
      try{
        localStorage.setItem(PREVIEW_SCOPE_KEY, 'non-program');
        localStorage.setItem(KEY, 'dark');
        localStorage.setItem(PREVIEW_BUILD_KEY, PREVIEW_BUILD);
      }catch(_){}
      return await applyTheme('dark');
    },
    async enableAllDarkPreview(){
      try{
        localStorage.setItem(PREVIEW_SCOPE_KEY, 'all');
        localStorage.setItem(KEY, 'dark');
        localStorage.setItem(PREVIEW_BUILD_KEY, PREVIEW_BUILD);
      }catch(_){}
      return await applyTheme('dark');
    }
  };

  applyTheme(readUserTheme()).then(result=>dispatchApplied('tvair-theme-applied', result, { source:'initial' })).catch(()=>{});
  if(document.readyState === 'loading'){
    document.addEventListener('DOMContentLoaded', () => {
      applyTheme(readUserTheme()).then(result=>dispatchApplied('tvair-theme-applied', result, { source:'domcontentloaded' })).catch(()=>{});
    }, { once:true });
  }
  window.addEventListener('storage', ev => {
    if(ev.key === KEY || ev.key === PREVIEW_SCOPE_KEY || ev.key === PREVIEW_BUILD_KEY){
      applyTheme(readUserTheme()).then(result=>dispatchApplied('tvair-theme-applied', result, { source:'storage' })).catch(()=>{});
    }
  });

  window.addEventListener('focus', () => {
    syncRuntimeTheme('window-focus').then(result=>{ if(result && !result.skipped) scheduleSemanticAuditAfterThemeSync('theme-runtime-focus-sync'); }).catch(()=>{});
  });
  document.addEventListener('visibilitychange', () => {
    if(document.visibilityState === 'visible'){
      syncRuntimeTheme('visibility').then(result=>{ if(result && !result.skipped) scheduleSemanticAuditAfterThemeSync('theme-runtime-visibility-sync'); }).catch(()=>{});
    }
  });
  setInterval(() => {
    syncRuntimeTheme('interval').then(result=>{ if(result && !result.skipped) scheduleSemanticAuditAfterThemeSync('theme-runtime-interval-sync'); }).catch(()=>{});
  }, RUNTIME_THEME_SYNC_INTERVAL_MS);
  syncRuntimeTheme('initial-runtime-sync').then(result=>{ if(result && !result.skipped) scheduleSemanticAuditAfterThemeSync('theme-runtime-initial-sync'); }).catch(()=>{});
})();
