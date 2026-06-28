/*
 * TvAIr common silent notification dialog - v0.11.178
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
  async function confirmEpgCancelBeforeReservation(){
    try{
      const res = await fetch('/api/epg/run-state', { cache:'no-store' });
      const state = await res.json();
      if(!state || !state.isRunning) return false;
      return await confirm({
        message:'EPG取得中です',
        subMessage:'予約を優先する場合はEPG取得をキャンセルします。継続する場合はEPG取得を続けたまま予約します。',
        okText:'EPGをキャンセル',
        cancelText:'継続'
      });
    }catch(_){ return false; }
  }
  window.TvAIrNotification = { show, confirm, confirmEpgCancelBeforeReservation, hide: ()=>hide('cancel') };
  window.TvAIrNotify = show;
})();
