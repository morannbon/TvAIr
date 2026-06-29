/* TvAIr Menu Spine v1.0.0
   Windows-style menu projection: shared tree, surface attributes, JS-managed flyout.
   MenuLegacyEntryCleanupContract: hamburger/page/context/tray keep a single command spine; page-local onclick menu fallbacks are removed, and only EPG silent remains a surface attribute.
   v1.0.0: menu page-open actions use a shared entry contract; Help opens as a separate tab from every menu surface.
   v1.0.0: Web context is an in-app surface: app.open/app.exit stay tray-only and are not projected into page context menus.
   v1.0.0: Manual EPG run uses the shared scope-based request contract; Web context is visible, tray is silent. */
(function(){
  'use strict';
  if(window.TvAIrMenuSpine && window.TvAIrMenuSpine.version === '1.0.0') return;

  const VERSION = '1.0.0';
  const state = { actions:null, loading:null, epg:null, epgLoading:null, closeTimer:null, activeGroup:null, contract:'release_contract' };
  const q = (s,r=document)=>r.querySelector(s);
  const qa = (s,r=document)=>Array.from(r.querySelectorAll(s));

  const MENU_LABELS = Object.freeze({
    open:'TvAIrを開く',
    epg:'EPG取得',
    plugins:'プラグイン',
    settings:'設定',
    help:'ヘルプ',
    version:'バージョン情報',
    exit:'TvAIr終了',
    cancelEpg:'取得キャンセル'
  });
  const EPG_ITEMS = [
    ['全局取得','All'],
    ['地上波のみ取得','GR'],
    ['BSのみ取得','BS'],
    ['CSのみ取得','CS'],
    ['BS/CSのみ取得','BSCS']
  ];

  function sourceFor(surface){
    if(surface === 'context') return 'context';
    if(surface === 'page') return 'page';
    return 'hamburger';
  }

  async function loadActions(force){
    if(state.actions && !force) return state.actions;
    if(state.loading && !force) return state.loading;
    state.loading = fetch('/api/plugins/menu-actions',{ cache:'no-store' })
      .then(r=>r.ok?r.json():{actions:[]})
      .then(j=>{
        state.contract = j.contract || state.contract;
        state.actions = Array.isArray(j.actions)
          ? j.actions.filter(a=>a && a.visible !== false && String(a.kind || '').toLowerCase() !== 'none')
          : [];
        return state.actions;
      })
      .catch(()=>{ state.actions=[]; return state.actions; })
      .finally(()=>{ state.loading=null; });
    return state.loading;
  }

  async function loadEpgState(force){
    if(state.epg && !force) return state.epg;
    if(state.epgLoading && !force) return state.epgLoading;
    state.epgLoading = fetch('/api/epg/run-state',{ cache:'no-store' })
      .then(r=>r.ok?r.json():null)
      .then(j=>{ state.epg = j || { canStart:true, canCancel:false, isRunning:false }; return state.epg; })
      .catch(()=>{ state.epg = { canStart:true, canCancel:false, isRunning:false }; return state.epg; })
      .finally(()=>{ state.epgLoading=null; });
    return state.epgLoading;
  }

  function epgCanStart(s){ return !!(s && (s.canStart === true || s.CanStart === true)); }
  function epgCanCancel(s){ return !!(s && (s.canCancel === true || s.CanCancel === true)); }
  function epgScope(s){ return (s && (s.targetScope || s.TargetScope || s.scope || s.Scope)) || '-'; }
  function epgUiMode(s){ return (s && (s.uiMode || s.UiMode)) || '-'; }

  function pluginHref(action,surface){
    const route = (action && (action.routeSegment || action.route)) || '';
    return '/plugin-menu/' + encodeURIComponent(route) + '?source=' + encodeURIComponent(sourceFor(surface));
  }

  function clearElement(el){ while(el && el.firstChild) el.removeChild(el.firstChild); }

  function item(tag,label,opts){
    opts = opts || {};
    const el = document.createElement(tag || 'button');
    el.className = 'tvair-menu-item ' + (opts.surface === 'context' ? 'menu-item' : 'page-menu-item');
    el.setAttribute('role','menuitem');
    if(tag === 'button' || !tag){ el.type = 'button'; }
    if(opts.href) el.href = opts.href;
    if(opts.dataset){ Object.keys(opts.dataset).forEach(k=>{ el.dataset[k] = opts.dataset[k]; }); }
    if(opts.title) el.title = opts.title;
    el.textContent = label;
    if(opts.disabled){
      el.disabled = true;
      el.setAttribute('aria-disabled','true');
      el.classList.add('tvair-menu-disabled');
    }
    if(typeof opts.onClick === 'function'){
      el.addEventListener('click', ev=>{
        if(el.disabled || el.getAttribute('aria-disabled') === 'true'){
          ev.preventDefault(); ev.stopPropagation(); return;
        }
        ev.preventDefault(); ev.stopPropagation(); opts.onClick(ev, el);
      });
    }else if(tag === 'a'){
      el.addEventListener('click', ()=>closeAll());
    }
    return el;
  }

  function separator(){
    const s = document.createElement('div');
    s.className = 'tvair-menu-separator menu-sep';
    s.setAttribute('role','separator');
    return s;
  }

  function scheduleCloseSubmenu(group){
    if(state.closeTimer) clearTimeout(state.closeTimer);
    state.closeTimer = setTimeout(()=>{
      const g = group || state.activeGroup;
      if(!g) return;
      if(g.matches(':hover') || g.contains(document.activeElement)) return;
      closeSubmenu(g);
    }, 120);
  }

  function cancelScheduledClose(){
    if(state.closeTimer){ clearTimeout(state.closeTimer); state.closeTimer = null; }
  }

  function isWithinMenuGroup(group, target){
    return !!(group && target && group.contains(target));
  }

  function scheduleCloseSubmenuIfLeaving(group, relatedTarget){
    if(isWithinMenuGroup(group, relatedTarget)) return;
    scheduleCloseSubmenu(group);
  }

  function closeSubmenu(group){
    if(!group) return;
    group.classList.remove('tvair-menu-submenu-open');
    group.setAttribute('aria-expanded','false');
    const flyout = group.querySelector(':scope > .tvair-menu-submenu-list');
    if(flyout){
      flyout.classList.remove('tvair-menu-open-left','tvair-menu-shift-up');
      flyout.style.display = 'none';
    }
    if(state.activeGroup === group) state.activeGroup = null;
  }

  function closeSiblingSubmenus(group){
    const root = group && group.parentElement;
    if(!root) return;
    qa(':scope > .tvair-menu-group.tvair-menu-submenu-open', root).forEach(g=>{ if(g !== group) closeSubmenu(g); });
  }

  function openSubmenu(group){
    if(!group) return;
    cancelScheduledClose();
    closeSiblingSubmenus(group);
    const flyout = group.querySelector(':scope > .tvair-menu-submenu-list');
    if(!flyout) return;
    group.classList.add('tvair-menu-submenu-open');
    group.setAttribute('aria-expanded','true');
    flyout.style.display = 'block';
    state.activeGroup = group;
    positionFlyout(group, flyout);
  }

  function submenu(label, surface, children){
    const group = document.createElement('div');
    group.className = 'page-menu-group tvair-menu-group';
    group.dataset.tvairMenuNode = 'submenu';
    group.dataset.tvairMenuFlyoutContract = 'classic-hover-focus-pane';
    group.setAttribute('role','none');
    group.setAttribute('aria-haspopup','true');
    group.setAttribute('aria-expanded','false');

    const parent = document.createElement('button');
    parent.type = 'button';
    parent.className = 'tvair-menu-summary ' + (surface === 'context' ? 'menu-item' : 'page-menu-item') + ' page-menu-summary';
    parent.setAttribute('role','menuitem');
    parent.setAttribute('aria-haspopup','true');
    parent.dataset.tvairFlyoutTrigger = 'hover-focus-click';
    parent.textContent = label;
    group.appendChild(parent);

    const list = document.createElement('div');
    list.className = 'tvair-menu-submenu-list';
    list.setAttribute('role','menu');
    list.dataset.tvairClassicFlyout = '1';
    list.dataset.tvairFlyoutMode = 'separate-pane';
    (children || []).forEach(c=>list.appendChild(c));
    group.appendChild(list);

    group.addEventListener('pointerenter',()=>openSubmenu(group));
    group.addEventListener('pointerleave',ev=>scheduleCloseSubmenuIfLeaving(group, ev.relatedTarget));
    group.addEventListener('mouseenter',()=>openSubmenu(group));
    group.addEventListener('mouseleave',ev=>scheduleCloseSubmenuIfLeaving(group, ev.relatedTarget));
    group.addEventListener('focusin',()=>openSubmenu(group));
    group.addEventListener('focusout',ev=>scheduleCloseSubmenuIfLeaving(group, ev.relatedTarget));
    parent.addEventListener('click', ev=>{ ev.preventDefault(); ev.stopPropagation(); openSubmenu(group); });
    parent.addEventListener('keydown', ev=>{
      if(ev.key === 'ArrowRight' || ev.key === 'Enter' || ev.key === ' '){ ev.preventDefault(); ev.stopPropagation(); openSubmenu(group); const first = list.querySelector('.tvair-menu-item,.tvair-menu-summary'); if(first) first.focus(); }
    });
    list.addEventListener('pointerenter', cancelScheduledClose);
    list.addEventListener('mouseenter', cancelScheduledClose);
    return group;
  }

  function positionFlyout(parent, flyout){
    if(!parent || !flyout) return;
    flyout.classList.remove('tvair-menu-open-left','tvair-menu-shift-up');
    const root = parent.closest('#menu-dropdown,#page-menu-dropdown,.tvair-context-menu');
    const pr = parent.getBoundingClientRect();
    const fr = flyout.getBoundingClientRect();
    if(pr.right + fr.width > window.innerWidth - 8){ flyout.classList.add('tvair-menu-open-left'); }
    if(pr.top + fr.height > window.innerHeight - 8){ flyout.classList.add('tvair-menu-shift-up'); }
    if(root && root.classList.contains('tvair-context-menu')){
      const rr = root.getBoundingClientRect();
      if(rr.left < 4) root.style.left = '4px';
      if(rr.top < 4) root.style.top = '4px';
    }
  }

  function createManualEpgRun(scope, surface){
    if(window.TvAIrManualEpgRunContract && typeof window.TvAIrManualEpgRunContract.create === 'function'){
      return window.TvAIrManualEpgRunContract.create({ scope, surface });
    }
    const normalizedScope = String(scope || 'All').trim().toUpperCase();
    return Object.freeze({
      scope: normalizedScope === 'GR' || normalizedScope === 'BS' || normalizedScope === 'CS' || normalizedScope === 'BSCS' ? normalizedScope : 'All',
      surface: sourceFor(surface || 'hamburger'),
      silent: sourceFor(surface || 'hamburger') === 'tray',
      source: 'WebMenu.Epg',
      contract: MENU_SURFACE_CONTRACT
    });
  }

  function epgRunItem(label, scope, surface, epgState){
    const request = createManualEpgRun(scope, surface);
    const canStart = epgCanStart(epgState);
    return item('button', label, {
      surface,
      disabled: !canStart,
      title: canStart ? '' : 'EPG取得中です。取得キャンセルのみ実行できます。',
      dataset:commandDataset(surface, MENU_COMMANDS.epgRun, { epgAction:'run', epgScope:request.scope, epgSilent:request.silent?'true':'false', epgRunContract:request.contract }),
      onClick:()=>{ emitCommand(surface, MENU_COMMANDS.epgRun, { scope:request.scope, silent:request.silent, source:request.source, contract:request.contract }); runEpg(request); }
    });
  }

  function epgCancelItem(surface, epgState){
    const canCancel = epgCanCancel(epgState);
    const suffix = canCancel ? '（' + epgUiMode(epgState) + '/' + epgScope(epgState) + '）' : '';
    return item('button', MENU_LABELS.cancelEpg + suffix, {
      surface,
      disabled: !canCancel,
      dataset:commandDataset(surface, MENU_COMMANDS.epgCancel, { epgAction:'cancel' }),
      onClick:()=>{ emitCommand(surface, MENU_COMMANDS.epgCancel); cancelEpg(); }
    });
  }

  function buildEpgSubmenu(surface, epgState){
    const children = EPG_ITEMS.map(x=>epgRunItem(x[0], x[1], surface, epgState));
    children.push(epgCancelItem(surface, epgState));
    return submenu(MENU_LABELS.epg, surface, children);
  }

  function buildPluginSubmenu(actions, surface){
    if(!actions || actions.length === 0) return null;
    const children = actions.map(action=>item('a', action.label || action.name || action.routeSegment || 'Plugin', {
      surface,
      href: pluginHref(action, surface),
      dataset:commandDataset(surface, MENU_COMMANDS.plugin, { tvairPluginMenuItem:'1', routeSegment:action.routeSegment || action.route || '', actionKind:action.kind || '' })
    }));
    return submenu(MENU_LABELS.plugins, surface, children);
  }

  const MENU_SURFACE_CONTRACT = 'release_contract';
  const SETTINGS_ENTRY_CONTRACT = MENU_SURFACE_CONTRACT;
  const MENU_COMMANDS = Object.freeze({
    open:'open-app',
    epgRun:'epg-run',
    epgCancel:'epg-cancel',
    plugin:'plugin-open',
    settings:'settings-open',
    help:'help-open',
    version:'version-open',
    exit:'app-exit'
  });

  const MENU_SURFACES = Object.freeze({
    hamburger:'hamburger',
    page:'page',
    context:'context',
    tray:'tray'
  });
  const MENU_COMMAND_VISIBILITY = Object.freeze({
    [MENU_COMMANDS.open]: [MENU_SURFACES.tray],
    [MENU_COMMANDS.exit]: [MENU_SURFACES.tray],
    [MENU_COMMANDS.epgRun]: [MENU_SURFACES.hamburger, MENU_SURFACES.page, MENU_SURFACES.context, MENU_SURFACES.tray],
    [MENU_COMMANDS.epgCancel]: [MENU_SURFACES.hamburger, MENU_SURFACES.page, MENU_SURFACES.context, MENU_SURFACES.tray],
    [MENU_COMMANDS.plugin]: [MENU_SURFACES.hamburger, MENU_SURFACES.page, MENU_SURFACES.context, MENU_SURFACES.tray],
    [MENU_COMMANDS.settings]: [MENU_SURFACES.hamburger, MENU_SURFACES.page, MENU_SURFACES.context, MENU_SURFACES.tray],
    [MENU_COMMANDS.help]: [MENU_SURFACES.hamburger, MENU_SURFACES.page, MENU_SURFACES.context, MENU_SURFACES.tray],
    [MENU_COMMANDS.version]: [MENU_SURFACES.hamburger, MENU_SURFACES.page, MENU_SURFACES.context, MENU_SURFACES.tray]
  });

  function isCommandVisible(surface, command){
    const visible = MENU_COMMAND_VISIBILITY[command];
    if(!visible || visible.length === 0) return true;
    return visible.indexOf(sourceFor(surface)) >= 0;
  }

  function commandDataset(surface, command, extra){
    return Object.assign({
      tvairMenuCommand: command,
      tvairMenuSurface: sourceFor(surface),
      tvairMenuActionContract: MENU_SURFACE_CONTRACT
    }, extra || {});
  }

  function emitCommand(surface, command, detail){
    try{ window.dispatchEvent(new CustomEvent('tvair-menu-command',{detail:Object.assign({surface:sourceFor(surface), command, contract:MENU_SURFACE_CONTRACT, version:VERSION}, detail || {})})); }catch(_){ }
  }

  function buildSettingsItem(surface){
    return item('button', MENU_LABELS.settings,{
      surface,
      dataset:commandDataset(surface, MENU_COMMANDS.settings, { tvairSettingsEntry:'1', settingsSurface:surface, settingsContract:SETTINGS_ENTRY_CONTRACT }),
      onClick:()=>{ emitCommand(surface, MENU_COMMANDS.settings); closeAll(); openSettings(surface); }
    });
  }
  function buildHelpItem(surface){
    return item('button', MENU_LABELS.help, {
      surface,
      dataset:commandDataset(surface, MENU_COMMANDS.help, { tvairMenuTailRole:'help' }),
      onClick:()=>{ emitCommand(surface, MENU_COMMANDS.help); closeAll(); openHelp(surface); }
    });
  }

  function buildVersionItem(surface){
    return item('button', MENU_LABELS.version, {
      surface,
      dataset:commandDataset(surface, MENU_COMMANDS.version, { tvairMenuTailRole:'about' }),
      onClick:()=>{ emitCommand(surface, MENU_COMMANDS.version); closeAll(); openVersionInfo(); }
    });
  }

  function appendMenuTail(root, surface){
    root.appendChild(separator());
    root.appendChild(buildHelpItem(surface));
    root.appendChild(buildVersionItem(surface));
    if(isCommandVisible(surface, MENU_COMMANDS.exit)){
      root.appendChild(separator());
      root.appendChild(item('button', MENU_LABELS.exit, {
        surface,
        dataset:commandDataset(surface, MENU_COMMANDS.exit, { tvairMenuTailRole:'exit' }),
        onClick:()=>{ emitCommand(surface, MENU_COMMANDS.exit); closeAll(); requestExit(); }
      }));
    }
  }

  async function buildMenu(root, surface){
    if(!root) return;
    const actions = await loadActions(false);
    const epgState = await loadEpgState(true);
    clearElement(root);
    root.classList.add('tvair-menu-surface');
    root.dataset.tvairMenuSurface = surface;
    root.dataset.tvairMenuContract = MENU_SURFACE_CONTRACT;
    root.dataset.tvairMenuFlyout = 'classic-hover-focus-pane';
    root.setAttribute('role','menu');

    if(isCommandVisible(surface, MENU_COMMANDS.open)){
      root.appendChild(item('a', MENU_LABELS.open,{ surface, href:'/', dataset:commandDataset(surface, MENU_COMMANDS.open) }));
      root.appendChild(separator());
    }

    root.appendChild(buildEpgSubmenu(surface, epgState));
    const plugin = buildPluginSubmenu(actions, surface);
    if(plugin) root.appendChild(plugin);
    root.appendChild(buildSettingsItem(surface));
    appendMenuTail(root, surface);

    try{ window.dispatchEvent(new CustomEvent('tvair-menu-projected',{detail:{surface,count:actions.length,contract:state.contract,version:VERSION}})); }catch(_){ }
  }

  function openSettings(surface){
    const entrySurface = sourceFor(surface || 'hamburger');
    if(window.TvAIrSettingsModule && typeof window.TvAIrSettingsModule.open === 'function') return window.TvAIrSettingsModule.open({ source:entrySurface, contract:SETTINGS_ENTRY_CONTRACT });
    if(typeof window.showCfgModal === 'function') return window.showCfgModal({ source:entrySurface, contract:SETTINGS_ENTRY_CONTRACT });
    location.href='/?open=settings&settingsEntry=' + encodeURIComponent(entrySurface);
  }
  function openMenuPage(surface, command, url, opts){
    opts = opts || {};
    const target = opts.target || '_self';
    const finalUrl = url + (url.indexOf('?') >= 0 ? '&' : '?') + 'source=' + encodeURIComponent(sourceFor(surface || 'hamburger')) + '&menuCommand=' + encodeURIComponent(command || 'open');
    if(target === '_blank'){
      const opened = window.open(finalUrl, '_blank', 'noopener');
      if(opened) opened.opener = null;
      return opened;
    }
    location.href = finalUrl;
    return null;
  }
  function openHelp(surface){ return openMenuPage(surface, MENU_COMMANDS.help, '/help.html', { target:'_blank' }); }
  function openVersionInfo(){ if(typeof window.showVersionInfo === 'function') return window.showVersionInfo(); location.href='/?open=version'; }
  async function requestExit(){ try{ await fetch('/api/app/exit?source=WebContextMenu', { method:'POST', cache:'no-store' }); }catch(_){ } }

  async function runEpg(request){
    const req = request && request.scope ? request : createManualEpgRun(request && request.scope, 'hamburger');
    const current = await loadEpgState(true);
    if(!epgCanStart(current)) return;
    closeAll();
    if(!req.silent && window.TvAirEpgWidget && typeof window.TvAirEpgWidget.startManual === 'function'){
      return window.TvAirEpgWidget.startManual(req.scope, { surface:req.surface, source:req.source, contract:req.contract });
    }
    if(window.TvAIrManualEpgRunContract && typeof window.TvAIrManualEpgRunContract.run === 'function'){
      return window.TvAIrManualEpgRunContract.run(req);
    }
    const qs = new URLSearchParams();
    qs.set('scope', req.scope || 'All');
    qs.set('source', req.source || 'WebMenu.Epg');
    if(req.silent) qs.set('silent', 'true');
    return fetch('/api/epg/run?' + qs.toString(), { method:'POST', cache:'no-store' });
  }

  async function cancelEpg(){
    const current = await loadEpgState(true);
    if(!epgCanCancel(current)) return;
    closeAll();
    if(window.TvAirEpgWidget && typeof window.TvAirEpgWidget.cancel === 'function') return window.TvAirEpgWidget.cancel();
    try{ await fetch('/api/epg/cancel?source=WebMenu.EpgCancel', { method:'POST', cache:'no-store' }); }catch(_){ }
  }

  function getMenuRootForButton(btn){
    if(!btn) return null;
    const id = btn.getAttribute('data-target') || btn.getAttribute('aria-controls');
    if(id) return document.getElementById(id.replace(/^#/,''));
    if(btn.id === 'menu-button' || btn.id === 'menu-btn') return q('#menu-dropdown');
    if(btn.id === 'page-menu-button' || btn.id === 'page-menu-btn') return q('#page-menu-dropdown');
    return null;
  }

  function closeAll(except){
    if(state.closeTimer) clearTimeout(state.closeTimer);
    qa('.tvair-menu-group.tvair-menu-submenu-open').forEach(closeSubmenu);
    qa('#menu-dropdown,#page-menu-dropdown,.tvair-menu-surface').forEach(el=>{ if(el !== except){ el.style.display='none'; el.classList.remove('open'); } });
    const ctx = q('#tvair-context-menu'); if(ctx && ctx !== except) ctx.classList.remove('open');
    state.activeGroup = null;
  }

  async function toggleMenu(btn, surface){
    const root = getMenuRootForButton(btn); if(!root) return;
    const open = root.style.display === 'block' || root.classList.contains('open');
    closeAll(root);
    if(open){ root.style.display='none'; root.classList.remove('open'); return; }
    await buildMenu(root, surface);
    root.style.display='block'; root.classList.add('open');
    try{ window.dispatchEvent(new CustomEvent('tvair-menu-opened',{detail:{surface,open:true,version:VERSION}})); }catch(_){ }
  }

  function wireMenuButton(btn, surface){
    if(!btn || btn.dataset.tvairMenuSpine === VERSION) return;
    btn.onclick = null; btn.removeAttribute('onclick'); btn.dataset.tvairMenuSpine = VERSION;
    btn.addEventListener('click', ev=>{ ev.preventDefault(); ev.stopImmediatePropagation(); toggleMenu(btn, surface); }, true);
  }

  function ensureContextMenu(){
    let root = q('#tvair-context-menu'); if(root) return root;
    root = document.createElement('div'); root.id = 'tvair-context-menu'; root.className = 'tvair-context-menu tvair-menu-surface'; root.setAttribute('role','menu'); document.body.appendChild(root); return root;
  }

  async function openContextMenu(ev){
    const t = ev.target;
    if(t && t.closest && t.closest('input,textarea,[contenteditable="true"],select')) return;
    ev.preventDefault(); ev.stopPropagation();
    const root = ensureContextMenu();
    await buildMenu(root, 'context');
    closeAll(root); root.classList.add('open');
    const w = root.offsetWidth || 220; const h = root.offsetHeight || 260;
    root.style.left = Math.max(4, Math.min(ev.clientX, window.innerWidth - w - 6)) + 'px';
    root.style.top = Math.max(4, Math.min(ev.clientY, window.innerHeight - h - 6)) + 'px';
    try{ window.dispatchEvent(new CustomEvent('tvair-menu-opened',{detail:{surface:'context',open:true,version:VERSION}})); }catch(_){ }
  }

  function init(){
    wireMenuButton(q('#menu-button') || q('#menu-btn'), 'hamburger');
    wireMenuButton(q('#page-menu-button') || q('#page-menu-btn'), 'page');
    document.addEventListener('click', ev=>{ if(!ev.target.closest('#menu-dropdown,#page-menu-dropdown,#tvair-context-menu,#menu-button,#menu-btn,#page-menu-button,#page-menu-btn')) closeAll(); }, true);
    document.addEventListener('contextmenu', openContextMenu, true);
    document.addEventListener('keydown', ev=>{ if(ev.key === 'Escape') closeAll(); }, true);
    loadActions(false).then(()=>{ const m=q('#menu-dropdown'); if(m) buildMenu(m,'hamburger'); const p=q('#page-menu-dropdown'); if(p) buildMenu(p,'page'); });
  }

  window.TvAIrMenuSpine = { version:VERSION, project:buildMenu, close:closeAll, reload:()=>loadActions(true), reloadEpg:()=>loadEpgState(true) };
  window.toggleMenu = function(){ const btn=q('#menu-button') || q('#menu-btn'); if(btn) toggleMenu(btn,'hamburger'); };
  window.togglePageMenu = function(){ const btn=q('#page-menu-button') || q('#page-menu-btn'); if(btn) toggleMenu(btn,'page'); };
  if(document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init, { once:true }); else init();
})();
