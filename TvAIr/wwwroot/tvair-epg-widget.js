/*
 * TvAIr shared EPG status widget - v0.11.306
 *
 * Purpose:
 * - Unify manual EPG operation and status panel across every browser page.
 * - Hamburger/menu EPG actions are execution commands, not just a panel opener.
 * - Uses existing /api/epg/run, /api/epg/status, /api/epg/cancel so common allocation route,
 *   EpgScheduler, EpgCapture, TvAIrEpgRec worker, Wake and tuner allocation remain the authority.
 */
(function(){
  const PANEL_ID = 'epg-panel';
  const POLL_MS = 3000;
  let pollTimer = null;
  let userClosed = false;
  let lastVisiblePhase = 'idle';
  let lastRequestedScope = null;
  const POS_KEY = 'tvair.epgPanel.position.v1';
  let dragBound = false;

  function byId(id){ return document.getElementById(id); }
  function ensurePanel(){
    let panel = byId(PANEL_ID);
    if(panel){ bindPanelButtons(); bindPanelDrag(); restorePanelPosition(panel); return panel; }
    panel = document.createElement('div');
    panel.id = PANEL_ID;
    panel.innerHTML = ''+
      '<div id="epg-box">'+
        '<div id="epg-box-head"><h2 id="epg-title">EPG取得</h2><span id="epg-pct" class="epg-percent">0%</span></div>'+
        '<div id="epg-progress">'+
          '<div class="bwrap epg-progress-bar"><div class="binner" id="bar-inner"></div></div>'+
          '<div id="epg-groups" class="epg-text">待機中</div>'+
          '<div id="epg-current" class="epg-current-text">未開始</div>'+
          '<div class="epg-actions">'+
            '<button id="epg-start-btn" type="button" class="epg-btn epg-btn-start">EPG取得</button>'+
            '<button id="epg-stop-btn" type="button" class="epg-btn epg-btn-stop">キャンセル</button>'+
            '<button id="epg-close-btn" type="button" class="epg-btn epg-btn-close">閉じる</button>'+
          '</div>'+
        '</div>'+
      '</div>';
    document.body.appendChild(panel);
    bindPanelButtons();
    bindPanelDrag();
    restorePanelPosition(panel);
    return panel;
  }

  function clampPanelPosition(panel, x, y){
    const margin = 8;
    const rect = panel.getBoundingClientRect();
    const maxX = Math.max(margin, window.innerWidth - rect.width - margin);
    const maxY = Math.max(margin, window.innerHeight - rect.height - margin);
    return { x: Math.min(Math.max(margin, x), maxX), y: Math.min(Math.max(margin, y), maxY) };
  }

  function applyPanelPosition(panel, x, y){
    const pos = clampPanelPosition(panel, x, y);
    panel.style.left = pos.x + 'px';
    panel.style.top = pos.y + 'px';
    panel.style.right = 'auto';
    panel.style.bottom = 'auto';
  }

  function restorePanelPosition(panel){
    try{
      const raw = localStorage.getItem(POS_KEY);
      if(!raw) return;
      const pos = JSON.parse(raw);
      if(Number.isFinite(pos.x) && Number.isFinite(pos.y)) applyPanelPosition(panel, pos.x, pos.y);
    }catch(_){}
  }

  function savePanelPosition(panel){
    try{
      const rect = panel.getBoundingClientRect();
      localStorage.setItem(POS_KEY, JSON.stringify({ x: Math.round(rect.left), y: Math.round(rect.top) }));
    }catch(_){}
  }

  function resetPanelPosition(){
    const panel = byId(PANEL_ID);
    if(!panel) return;
    panel.style.left = '';
    panel.style.top = '';
    panel.style.right = '';
    panel.style.bottom = '';
    try{ localStorage.removeItem(POS_KEY); }catch(_){}
  }

  function bindPanelDrag(){
    if(dragBound) return;
    const panel = byId(PANEL_ID);
    const head = byId('epg-box-head');
    if(!panel || !head) return;
    dragBound = true;
    head.setAttribute('title', 'ドラッグで移動できます。ダブルクリックで既定位置へ戻します。');
    let dragging = false, startX = 0, startY = 0, baseX = 0, baseY = 0;
    head.addEventListener('pointerdown', ev=>{
      if(ev.button !== 0) return;
      dragging = true;
      const rect = panel.getBoundingClientRect();
      startX = ev.clientX; startY = ev.clientY; baseX = rect.left; baseY = rect.top;
      panel.classList.add('dragging');
      try{ head.setPointerCapture(ev.pointerId); }catch(_){}
      ev.preventDefault();
    });
    head.addEventListener('pointermove', ev=>{
      if(!dragging) return;
      applyPanelPosition(panel, baseX + ev.clientX - startX, baseY + ev.clientY - startY);
    });
    function endDrag(ev){
      if(!dragging) return;
      dragging = false;
      panel.classList.remove('dragging');
      try{ head.releasePointerCapture(ev.pointerId); }catch(_){}
      savePanelPosition(panel);
    }
    head.addEventListener('pointerup', endDrag);
    head.addEventListener('pointercancel', endDrag);
    head.addEventListener('dblclick', ev=>{ ev.preventDefault(); resetPanelPosition(); });
    window.addEventListener('resize', ()=>{ const r=panel.getBoundingClientRect(); if(r.left || r.top) { applyPanelPosition(panel, r.left, r.top); savePanelPosition(panel); } });
  }

  function bindPanelButtons(){
    const start = byId('epg-start-btn');
    const stop = byId('epg-stop-btn');
    const close = byId('epg-close-btn');
    if(start && !start.dataset.tvairBound){ start.dataset.tvairBound='1'; start.addEventListener('click', ()=>startManual(start.dataset.epgScope || lastRequestedScope || 'All')); }
    if(stop && !stop.dataset.tvairBound){ stop.dataset.tvairBound='1'; stop.addEventListener('click', ()=>cancel()); }
    if(close && !close.dataset.tvairBound){ close.dataset.tvairBound='1'; close.addEventListener('click', closePanel); }
  }

  function showPanel(){ ensurePanel().classList.add('show'); }
  function closePanel(){ userClosed = true; const p = byId(PANEL_ID); if(p) p.classList.remove('show'); }
  function stopPolling(){ if(pollTimer){ clearInterval(pollTimer); pollTimer = null; } }
  function startPolling(){ if(pollTimer || document.hidden) return; pollTimer = setInterval(pollStatus, POLL_MS); }
  function setText(id, text){ const e=byId(id); if(e) e.textContent=text; }
  function setWidth(id, width){ const e=byId(id); if(e) e.style.width=width; }
  function setDisplay(id, show){ const e=byId(id); if(e) e.style.display = show ? 'inline-block' : 'none'; }
  function setDisabled(id, disabled){ const e=byId(id); if(e) e.disabled = !!disabled; }
  function hidePanel(){ const p = byId(PANEL_ID); if(p) p.classList.remove('show'); }
  // v0.11.178: EPG専用通知・標準alertを持たず、TvAIr共通の無音通知へ集約する。
  function hideStartBlockedNotice(){
    if(window.TvAIrNotification && typeof window.TvAIrNotification.hide === 'function'){
      window.TvAIrNotification.hide();
    }
  }
  function showStartBlockedNotice(message, guidance){
    const line1 = (message || '開始できません').trim();
    const line2 = (guidance || '時間をおいてお試しください。').trim();
    if(window.TvAIrNotification && typeof window.TvAIrNotification.show === 'function'){
      window.TvAIrNotification.show({ title:'TvAIr', message:line1, subMessage:line2 });
      return;
    }
    // 共通通知JSの読み込み漏れを標準alertで隠さない。読み込み順/HTML側の共通化漏れとして検知できるようにする。
    try{ console.warn('[TvAIrNotification] unavailable', line1, line2); }catch(_){ }
  }

  function updateMenuGuard(s){
    const running = !!(s && s.phase === 'running');
    document.querySelectorAll('[data-epg-action]').forEach(el=>{
      const action = el.dataset.epgAction;
      if(action === 'run') {
        el.classList.toggle('tvair-epg-disabled', running);
        el.setAttribute('aria-disabled', running ? 'true' : 'false');
        if('disabled' in el) el.disabled = running;
        if(running) el.title = 'EPG取得中です。やり直す場合は先にキャンセルしてください。';
        else el.removeAttribute('title');
      }
      if(action === 'cancel') {
        el.classList.toggle('tvair-epg-disabled', !running);
        el.setAttribute('aria-disabled', running ? 'false' : 'true');
        if('disabled' in el) el.disabled = !running;
      }
    });
  }

  function normalizeScope(scope, fallback){
    const raw = (scope === undefined || scope === null) ? '' : String(scope).trim();
    const s = raw.toLowerCase();
    if(s === 'gr' || s === 'terrestrial') return 'GR';
    if(s === 'bs') return 'BS';
    if(s === 'cs') return 'CS';
    if(s === 'bscs' || s === 'bs/cs') return 'BSCS';
    if(s === 'all' || s === '全体' || s === '全局') return 'All';
    return fallback === undefined ? 'All' : fallback;
  }
  function scopeLabel(scope){
    const s = normalizeScope(scope, null);
    if(s === 'GR') return '地上波EPG取得';
    if(s === 'BS') return 'BS EPG取得';
    if(s === 'CS') return 'CS EPG取得';
    if(s === 'BSCS') return 'BS/CS EPG取得';
    if(s === 'All') return '全局EPG取得';
    return 'EPG取得';
  }
  function scopeFromState(s){
    if(!s) return lastRequestedScope;
    const phase = String(s.phase || s.Phase || 'idle').toLowerCase();
    const raw = s.targetScope || s.TargetScope || s.scope || s.Scope;
    const normalized = normalizeScope(raw, null);
    // idle/statusだけの初期描画でバックエンド既定のAllを拾うと、地上波導線でも「全局待機」に見える。
    // 実行中・完了・キャンセル・BLOCKEDなど、実runに紐づく状態だけをscope正本として採用する。
    if(phase && phase !== 'idle' && normalized) return normalized;
    return lastRequestedScope;
  }
  function rememberScope(scope){
    const normalized = normalizeScope(scope, null);
    if(normalized) lastRequestedScope = normalized;
    const start = byId('epg-start-btn');
    if(start){
      if(lastRequestedScope) start.dataset.epgScope = lastRequestedScope;
      else delete start.dataset.epgScope;
    }
    return lastRequestedScope;
  }
  function setStartButtonForScope(scope, fallbackText){
    const normalized = rememberScope(scope);
    setDisplay('epg-start-btn', true);
    setText('epg-start-btn', fallbackText || scopeLabel(normalized));
    setDisabled('epg-start-btn', false);
  }

  function renderIdle(s){
    setText('epg-title', 'EPG取得 状況');
    setWidth('bar-inner','0%');
    setText('epg-pct','0%');
    setText('epg-groups','待機中');
    setText('epg-current', (s && s.nextRunText) ? s.nextRunText : (s && s.lastRunMessage) ? s.lastRunMessage : 'EPG取得は実行されていません');
    setStartButtonForScope(scopeFromState(s), scopeFromState(s) ? undefined : 'EPG取得');
    setDisplay('epg-stop-btn', false);
    setDisplay('epg-close-btn', true);
  }
  function formatSec(sec){
    sec = Math.max(0, Math.floor(Number(sec || 0)));
    const m = Math.floor(sec / 60);
    const s = sec % 60;
    return m + ':' + String(s).padStart(2, '0');
  }
  function renderRunning(s){
    const total = s.totalGroups || 0;
    const done = s.completedGroups || 0;
    const running = s.runningGroups || 0;
    const estimated = Number.isFinite(Number(s.estimatedProgressPercent)) ? Number(s.estimatedProgressPercent) : NaN;
    const pct = total > 0 ? Math.max(0, Math.min(99, Math.round(Number.isNaN(estimated) ? (done / total * 100) : estimated))) : 0;
    const planned = s.activeWorkerPlannedSeconds || 0;
    const elapsed = s.activeWorkerElapsedSeconds || 0;
    const names = s.runningGroupNames || '';
    setText('epg-title', 'EPG取得中');
    setWidth('bar-inner', pct + '%');
    setText('epg-pct', pct + '%');
    const runningText = running > 0 ? (' / 実行中 ' + running + '件') : '';
    const elapsedText = planned > 0 && running > 0 ? (' / 経過 ' + formatSec(elapsed) + '/' + formatSec(planned)) : '';
    setText('epg-groups', total > 0 ? (done + '/' + total + ' グループ完了' + runningText + elapsedText) : ('取得中' + elapsedText));
    setText('epg-current', names ? ('取得中: ' + names) : (s.lastRunMessage || '取得中...'));
    setDisplay('epg-start-btn', false);
    setDisplay('epg-stop-btn', true); setText('epg-stop-btn','キャンセル'); setDisabled('epg-stop-btn', false);
    setDisplay('epg-close-btn', false);
  }
  function renderCompleted(s){
    const total = s.totalGroups || 0;
    const done = s.completedGroups || total;
    setText('epg-title', 'EPG取得 完了');
    setWidth('bar-inner','100%');
    setText('epg-pct','100%');
    setText('epg-groups', total > 0 ? (done + '/' + total + ' グループ完了') : '完了');
    setText('epg-current', s.lastRunMessage || 'EPG取得が完了しました');
    setDisplay('epg-start-btn', false);
    setDisplay('epg-stop-btn', false);
    setDisplay('epg-close-btn', true);
  }
  function renderCancelled(s){
    // v0.11.359 EPG cancel status noise cleanup:
    // Show the cancelled state once only. Do not repeat it in title, group, and detail lines.
    setText('epg-title', 'EPG取得');
    setWidth('bar-inner','0%');
    setText('epg-pct','0%');
    setText('epg-groups','キャンセル済み');
    setText('epg-current', '');
    setDisplay('epg-start-btn', false);
    setDisplay('epg-stop-btn', false);
    setDisplay('epg-close-btn', true);
  }
  function renderBlocked(s){
    setText('epg-title', 'EPG取得 開始できませんでした');
    setWidth('bar-inner','0%');
    setText('epg-pct','0%');
    setText('epg-groups','開始できませんでした');
    setText('epg-current', s.lastRunMessage || '録画開始が近いため開始できません');
    setDisplay('epg-start-btn', false);
    setDisplay('epg-stop-btn', false);
    setDisplay('epg-close-btn', true);
  }
  function renderStatus(s, opts){
    ensurePanel();
    bindPanelButtons();
    updateMenuGuard(s);
    const silentRunning = !!(s && s.phase === 'running' && s.uiVisible === false);
    if(silentRunning){
      // タスクトレイ等から開始されたサイレントEPGは、番組表を開いても手動表示してもツールを出さない。
      const p = byId(PANEL_ID);
      if(p) p.classList.remove('show');
      lastVisiblePhase = 'idle';
      return;
    }
    const visible = !s || s.uiVisible !== false;
    const phase = visible && s ? (s.phase || 'idle') : 'idle';
    if(s && (s.targetScope || s.TargetScope) && phase !== 'idle') rememberScope(scopeFromState(s));
    lastVisiblePhase = phase;
    if(phase === 'running') renderRunning(s);
    else if(phase === 'completed') renderCompleted(s || {});
    else if(phase === 'cancelled') renderCancelled(s || {});
    else if(phase === 'blocked') renderBlocked(s || {});
    else renderIdle(s || {});
    if(opts && opts.forceShow) showPanel();
    else if(phase === 'running' && !userClosed) showPanel();
  }

  async function getStatus(){
    const res = await fetch('/api/epg/status', { cache:'no-store' });
    if(!res.ok) throw new Error('status http ' + res.status);
    return await res.json();
  }
  async function refresh(forceShow){
    try{
      const s = await getStatus();
      renderStatus(s, { forceShow });
      if(s.phase === 'running') startPolling(); else stopPolling();
      return s;
    }catch(e){ return null; }
  }
  async function pollStatus(){
    const s = await refresh(false);
    if(!s) return;
    if(s.phase !== 'running'){
      stopPolling();
      if(typeof window.loadGuide === 'function' && window.currentDate){
        setTimeout(()=>{ try{ window.loadGuide(window.currentDate); }catch(_){} }, 1200);
      }
    }
  }

  async function startManual(scope, opts){
    opts = opts || {};
    scope = rememberScope(scope);
    const current = await getStatus().catch(()=>null);
    if(current && current.phase === 'running'){
      updateMenuGuard(current);
      if(current.uiVisible !== false){
        userClosed = false;
        renderStatus(current, { forceShow:true });
      }
      return { started:false, message:'EPG取得中です。やり直す場合は先にキャンセルしてください。' };
    }
    userClosed = false;
    try{
      let res;
      let body = {};
      if(window.TvAIrManualEpgRunContract && typeof window.TvAIrManualEpgRunContract.run === 'function'){
        const manual = await window.TvAIrManualEpgRunContract.run({ scope, surface:opts.surface || 'epgPanel', silent:false });
        res = manual.response;
        body = manual.body || {};
      }else{
        const qs = new URLSearchParams();
        qs.set('scope', scope);
        qs.set('source', opts.source || 'WebEpgPanel.Epg');
        res = await fetch('/api/epg/run?' + qs.toString(), { method:'POST', cache:'no-store' });
        try{ body = await res.json(); }catch(_){ }
      }
      if(!res.ok || body.started === false){
        hidePanel();
        stopPolling();
        if(body && body.blocked){
          showStartBlockedNotice(body.message, body.guidance);
        } else if(body && body.message) {
          showStartBlockedNotice(body.message, '時間をおいてお試しください。');
        }
        const s = await getStatus().catch(()=>null);
        if(s) updateMenuGuard(s);
        return body;
      }
      ensurePanel();
      showPanel();
      setText('epg-title', scopeLabel(scope));
      setWidth('bar-inner','0%');
      setText('epg-pct','0%');
      setText('epg-groups','0/0 グループ完了');
      setText('epg-current','EPG取得を開始しました');
      setDisplay('epg-start-btn', false);
      setDisplay('epg-stop-btn', true);
      setDisabled('epg-stop-btn', false);
      setText('epg-stop-btn','キャンセル');
      setDisplay('epg-close-btn', false);
      startPolling();
      setTimeout(()=>pollStatus(), 500);
      return body;
    }catch(e){
      hidePanel();
      showStartBlockedNotice('開始できません', '時間をおいてお試しください。');
      return { started:false, message:e.message };
    }
  }
  async function cancel(){
    ensurePanel();
    showPanel();
    const stop = byId('epg-stop-btn');
    if(stop){ stop.disabled = true; stop.textContent = 'キャンセル中...'; }
    try{ await fetch('/api/epg/cancel?source=WebUi.EpgWidgetCancel', { method:'POST', cache:'no-store' }); }
    catch(_){ }
    finally{
      if(stop){ stop.disabled = false; stop.textContent = 'キャンセル'; }
      stopPolling();
      setTimeout(()=>refresh(true), 500);
    }
  }
  function showStatus(){ userClosed = false; refresh(true); }

  function bindMenuActions(){
    document.querySelectorAll('[data-epg-action]').forEach(el=>{
      if(el.dataset.tvairEpgBound) return;
      el.dataset.tvairEpgBound = '1';
      el.addEventListener('click', ev=>{
        ev.preventDefault();
        const action = el.dataset.epgAction;
        const scope = el.dataset.epgScope || 'All';
        if(el.getAttribute('aria-disabled') === 'true') return;
        if(action === 'run') startManual(scope);
        else if(action === 'status') showStatus();
        else if(action === 'cancel') cancel();
        if(typeof window.closeMenu === 'function') { try{ window.closeMenu(); }catch(_){} }
        if(typeof window.closePageMenu === 'function') { try{ window.closePageMenu(); }catch(_){} }
      });
    });
  }

  window.TvAirEpgWidget = { refresh, showStatus, startManual, cancel, closePanel };
  window.openEpgPanel = showStatus;
  window.startEpg = (scope)=>startManual(scope || lastRequestedScope || 'All');
  window.cancelEpg = cancel;
  window.closeEpgPanel = closePanel;
  window.cancelEpgFloating = cancel;
  window.closeEpgFloating = closePanel;

  document.addEventListener('DOMContentLoaded', ()=>{
    ensurePanel();
    bindPanelButtons();
    bindMenuActions();
    refresh(false);
    const params = new URLSearchParams(location.search || '');
    const epg = params.get('epg');
    const open = params.get('open');
    if(epg === 'run') startManual(params.get('scope') || lastRequestedScope || 'All');
    else if(epg === 'status' || open === 'epg') showStatus();
  });
  document.addEventListener('visibilitychange', ()=>{ if(document.hidden) stopPolling(); else refresh(lastVisiblePhase === 'running'); });
  window.addEventListener('focus', ()=>refresh(false));
  window.addEventListener('pageshow', ()=>refresh(false));
})();
