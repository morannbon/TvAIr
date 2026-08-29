/*
 * TvAIr common silent notification dialog - release_contract
 * For short user-operation result notifications. No sound, toast, or balloon.
 */
(function(){
  const OVERLAY_ID = 'tvair-notification-overlay';
  const DEFAULT_TITLE = 'TvAIr';
  let onOk = null;
  let onCancel = null;
  function byId(id){ return document.getElementById(id); }
  function ensure(){
    let overlay = byId(OVERLAY_ID);
    if(overlay) return overlay;
    overlay = document.createElement('div');
    overlay.id = OVERLAY_ID;
    overlay.className = 'tvair-notice-overlay';
    overlay.setAttribute('aria-hidden', 'true');
    overlay.innerHTML = ''+
      '<div class="tvair-notice-card" role="dialog" aria-modal="true" aria-labelledby="tvair-notice-title" aria-describedby="tvair-notice-body">'+
        '<div class="tvair-notice-titlebar"><span id="tvair-notice-title" class="tvair-notice-title">TvAIr</span><button type="button" class="tvair-notice-x" aria-label="閉じる">×</button></div>'+ 
        '<div class="tvair-notice-body" id="tvair-notice-body"><div class="tvair-notice-message"></div><div class="tvair-notice-sub"></div></div>'+ 
        '<div class="tvair-notice-actions"><button type="button" class="tvair-notice-cancel">キャンセル</button><button type="button" class="tvair-notice-ok">OK</button></div>'+ 
      '</div>';
    document.body.appendChild(overlay);
    const closeOk = ()=>hide('ok');
    const closeCancel = ()=>hide('cancel');
    overlay.querySelector('.tvair-notice-x')?.addEventListener('click', closeCancel);
    overlay.querySelector('.tvair-notice-ok')?.addEventListener('click', closeOk);
    overlay.querySelector('.tvair-notice-cancel')?.addEventListener('click', closeCancel);
    overlay.addEventListener('click', ev=>{ if(ev.target === overlay) closeCancel(); });
    overlay.addEventListener('keydown', ev=>{
      if(ev.key === 'Escape'){ ev.preventDefault(); closeCancel(); }
      if(ev.key === 'Enter'){ ev.preventDefault(); closeOk(); }
    });
    return overlay;
  }

  const STATUS_ID = 'toast';
  let statusTimer = null;
  let statusHideTimer = null;
  function ensureStatus(){
    let status = byId(STATUS_ID);
    if(status) return status;
    status = document.createElement('div');
    status.id = STATUS_ID;
    document.body.appendChild(status);
    return status;
  }
  function normalizeStatusKind(kind){
    const value = String(kind || 'success').toLowerCase();
    return value === 'error' || value === 'warning' || value === 'info' ? value : 'success';
  }
  function showStatus(message, kind='success', durationMs=2800){
    const status = ensureStatus();
    const text = String(message || '').trim();
    if(!text) return;
    clearTimeout(statusTimer);
    clearTimeout(statusHideTimer);
    status.classList.remove('show','leaving','status-success','status-info','status-warning','status-error');
    status.textContent = text;
    status.setAttribute('role', normalizeStatusKind(kind) === 'error' ? 'alert' : 'status');
    status.setAttribute('aria-live', normalizeStatusKind(kind) === 'error' ? 'assertive' : 'polite');
    status.classList.add('status-' + normalizeStatusKind(kind));
    // Force a style boundary so repeated updates restart the same shared transition.
    void status.offsetWidth;
    status.classList.add('show');
    statusTimer = setTimeout(()=>{
      status.classList.add('leaving');
      status.classList.remove('show');
      statusHideTimer = setTimeout(()=>status.classList.remove('leaving'), 180);
    }, Math.max(600, Number(durationMs) || 2800));
  }
  function hide(result){
    const overlay = byId(OVERLAY_ID);
    if(overlay){ overlay.classList.remove('show'); overlay.setAttribute('aria-hidden','true'); }
    const okFn = onOk;
    const cancelFn = onCancel;
    onOk = null;
    onCancel = null;
    if(result === 'ok' && typeof okFn === 'function'){
      try{ okFn(); }catch(_){}
    }
    if(result === 'cancel' && typeof cancelFn === 'function'){
      try{ cancelFn(); }catch(_){}
    }
  }
  function splitMessage(text){
    const raw = (text || '').toString().replace(/\r\n/g, '\n');
    const lines = raw.split('\n');
    return { message: (lines.shift() || '').trim(), subMessage: lines.join('\n').trim() };
  }
  function show(input, subMessage, callback){
    const opts = (typeof input === 'object' && input !== null) ? input : { message: input, subMessage, onOk: callback };
    let message = (opts.message || '').toString().trim();
    let sub = (opts.subMessage || opts.guidance || '').toString().trim();
    if(!sub && message.indexOf('\n') >= 0){ const s = splitMessage(message); message = s.message; sub = s.subMessage; }
    const title = (opts.title || DEFAULT_TITLE).toString().trim() || DEFAULT_TITLE;
    const overlay = ensure();
    const titleEl = overlay.querySelector('#tvair-notice-title');
    const msgEl = overlay.querySelector('.tvair-notice-message');
    const subEl = overlay.querySelector('.tvair-notice-sub');
    const okBtn = overlay.querySelector('.tvair-notice-ok');
    const cancelBtn = overlay.querySelector('.tvair-notice-cancel');
    if(titleEl) titleEl.textContent = title;
    if(msgEl) msgEl.textContent = message || '処理できませんでした';
    if(subEl){ subEl.textContent = sub || ''; subEl.style.display = sub ? 'block' : 'none'; }
    const confirmMode = opts.mode === 'confirm' || opts.confirm === true;
    if(okBtn) okBtn.textContent = (opts.okText || 'OK').toString();
    if(cancelBtn){
      cancelBtn.textContent = (opts.cancelText || 'キャンセル').toString();
      cancelBtn.style.display = confirmMode ? 'inline-block' : 'none';
    }
    onOk = typeof opts.onOk === 'function' ? opts.onOk : null;
    onCancel = typeof opts.onCancel === 'function' ? opts.onCancel : null;
    overlay.classList.add('show');
    overlay.setAttribute('aria-hidden', 'false');
    setTimeout(()=>{ try{ overlay.querySelector('.tvair-notice-ok')?.focus(); }catch(_){} }, 0);
  }
  function confirm(input, subMessage){
    const opts = (typeof input === 'object' && input !== null) ? input : { message: input, subMessage };
    return new Promise(resolve=>{
      show(Object.assign({}, opts, { mode:'confirm', confirm:true, onOk:()=>resolve(true), onCancel:()=>resolve(false) }));
    });
  }
  window.TvAIrNotification = { show, showStatus, confirm, hide: ()=>hide('cancel') };
  window.TvAIrNotify = show;
  window.showToast = function(message, durationMs, kind){ showStatus(message, kind || 'success', durationMs); };
})();
