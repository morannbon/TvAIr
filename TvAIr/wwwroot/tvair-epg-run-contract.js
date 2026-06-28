/* TvAIr Manual EPG Run Contract v1.0.0
   Owns the manual EPG run request shape for Web surfaces.
   API boundary uses scope. targetScope remains server/internal state terminology only. */
(function(){
  'use strict';
  if(window.TvAIrManualEpgRunContract && window.TvAIrManualEpgRunContract.version === '1.0.0') return;

  const VERSION = '1.0.0';
  const DEFAULT_SCOPE = 'All';
  const SURFACES = Object.freeze({
    hamburger:'hamburger',
    page:'page',
    context:'context',
    epgPanel:'epgPanel',
    tray:'tray'
  });

  function normalizeScope(scope){
    const v = String(scope || DEFAULT_SCOPE).trim().toUpperCase();
    if(v === 'GR') return 'GR';
    if(v === 'BS') return 'BS';
    if(v === 'CS') return 'CS';
    if(v === 'BSCS' || v === 'BS/CS') return 'BSCS';
    return DEFAULT_SCOPE;
  }

  function normalizeSurface(surface){
    const v = String(surface || SURFACES.epgPanel).trim();
    if(v === SURFACES.hamburger || v === 'menu') return SURFACES.hamburger;
    if(v === SURFACES.page) return SURFACES.page;
    if(v === SURFACES.context || v === 'webContextMenu') return SURFACES.context;
    if(v === SURFACES.tray) return SURFACES.tray;
    return SURFACES.epgPanel;
  }

  function defaultSilent(surface){
    return normalizeSurface(surface) === SURFACES.tray;
  }

  function sourceFor(surface, silent){
    const s = normalizeSurface(surface);
    if(s === SURFACES.hamburger) return 'WebMenu.HamburgerEpg';
    if(s === SURFACES.page) return 'WebMenu.PageEpg';
    if(s === SURFACES.context) return 'WebContextMenu.Epg';
    if(s === SURFACES.tray) return 'TrayMenu.SilentEpg';
    return silent ? 'WebApi.SilentEpg' : 'WebEpgPanel.Epg';
  }

  function create(input){
    input = input || {};
    const surface = normalizeSurface(input.surface);
    const silent = typeof input.silent === 'boolean' ? input.silent : defaultSilent(surface);
    return Object.freeze({
      scope: normalizeScope(input.scope),
      surface,
      silent,
      source: sourceFor(surface, silent),
      contract: 'ManualEpgRunContract/v1.0.0'
    });
  }

  function toQuery(request){
    const req = create(request);
    const qs = new URLSearchParams();
    qs.set('scope', req.scope);
    qs.set('source', req.source);
    if(req.silent) qs.set('silent', 'true');
    return qs.toString();
  }

  async function run(request){
    const req = create(request);
    const res = await fetch('/api/epg/run?' + toQuery(req), { method:'POST', cache:'no-store' });
    let body = {};
    try{ body = await res.json(); }catch(_){ }
    body.manualEpgRunContract = req.contract;
    body.manualEpgRunSurface = req.surface;
    body.manualEpgRunSource = req.source;
    body.manualEpgRunSilent = req.silent;
    body.manualEpgRunScope = req.scope;
    return { response:res, body, request:req };
  }

  window.TvAIrManualEpgRunContract = Object.freeze({
    version: VERSION,
    create,
    toQuery,
    run,
    normalizeScope,
    normalizeSurface,
    defaultSilent,
    sourceFor
  });
})();
