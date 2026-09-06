/* TvAIr Manual EPG Run Contract release_contract
   Owns the manual EPG run request/result contract for Web surfaces, including user-visible failure notification.
   Tray EPG is Host-owned by TrayIconService and does not pass through this JavaScript contract.
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
    epgPanel:'epgPanel'
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
    return SURFACES.epgPanel;
  }

  function defaultSilent(surface){
    return false;
  }

  function sourceFor(surface, silent){
    const s = normalizeSurface(surface);
    if(s === SURFACES.hamburger) return 'WebMenu.HamburgerEpg';
    if(s === SURFACES.page) return 'WebMenu.PageEpg';
    if(s === SURFACES.context) return 'WebContextMenu.Epg';
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
      contract: 'ManualEpgRunContract/release_contract'
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

  function notifyFailure(message, guidance){
    const line1 = String(message || 'EPG取得を開始できません。').trim() || 'EPG取得を開始できません。';
    const line2 = String(guidance || '').trim();
    if(window.TvAIrNotification && typeof window.TvAIrNotification.show === 'function'){
      window.TvAIrNotification.show({ title:'TvAIr', message:line1, subMessage:line2 });
      return;
    }
    try{ console.warn('[TvAIrManualEpgRunContract] notification unavailable', line1, line2); }catch(_){ }
  }

  async function run(request){
    const req = create(request);
    let res;
    let body = {};
    try{
      res = await fetch('/api/epg/run?' + toQuery(req), { method:'POST', cache:'no-store' });
      try{ body = await res.json(); }catch(_){ }
      body.manualEpgRunContract = req.contract;
      body.manualEpgRunSurface = req.surface;
      body.manualEpgRunSource = req.source;
      body.manualEpgRunSilent = req.silent;
      body.manualEpgRunScope = req.scope;
      if(!res.ok || body.started === false){
        notifyFailure(body.message, body.guidance || (!body.blocked ? '時間をおいてお試しください。' : ''));
      }
      return { response:res, body, request:req };
    }catch(e){
      notifyFailure('EPG取得を開始できません。', '時間をおいてお試しください。');
      throw e;
    }
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
