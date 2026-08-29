/*
 * TvAIr user operational log renderer - release_contract
 *
 * This module owns only the log-tab cells. It deliberately avoids the generic
 * reservation-list text-cell classes so future changes to program-guide or
 * reservation title truncation cannot change user-log readability.
 */
(function(){
  function valueOrDash(value){
    return value === undefined || value === null || value === '' ? '—' : String(value);
  }


  function normalizeTextMode(value, fallback){
    const v = String(value || '').trim().toLowerCase();
    return (v === 'singleline' || v === 'multiline') ? v : fallback;
  }

  function modeClass(mode){
    return mode === 'multiline' ? 'tvair-text tvair-text-multiline' : 'tvair-text tvair-text-singleline';
  }

  function renderTextCell({ cls, role, mode, text, title, extraAttrs }, esc){
    const safeMode = normalizeTextMode(mode, 'singleline');
    const displayText = valueOrDash(text);
    const attrs = extraAttrs ? ` ${extraAttrs}` : '';
    return `<td class="${cls} ${modeClass(safeMode)}" data-cell-role="${role}" data-text-mode="${safeMode}"${attrs}><span class="tvair-text-body">${esc(displayText)}</span></td>`;
  }

  function renderResultCell(entry, esc){
    const result = valueOrDash(entry && (entry.result || entry.severity));
    const mode = normalizeTextMode(entry && entry.resultTextMode, 'singleline');
    return renderTextCell({ cls:'col-result tvair-user-log-result-cell', role:'log-result', mode, text:result }, esc);
  }

  function renderTargetCell(entry, esc){
    const target = valueOrDash(entry && entry.target);
    const explicit = normalizeTextMode(entry && entry.targetTextMode, '');
    const mode = explicit || (String(target).includes('\n') ? 'multiline' : 'singleline');
    return renderTextCell({ cls:'col-target tvair-user-log-target-cell', role:'log-target', mode, text:target }, esc);
  }

  function renderMessageCell(entry, esc){
    const message = valueOrDash(entry && entry.message);
    const explicit = normalizeTextMode(entry && entry.messageTextMode, '');
    const mode = explicit || (String(message).includes('\n') ? 'multiline' : 'singleline');
    return renderTextCell({ cls:'col-main tvair-user-log-message-cell', role:'log-content', mode, text:message }, esc);
  }


  window.TvAIrUserLog = Object.freeze({
    renderResultCell,
    renderTargetCell,
    renderMessageCell
  });
})();
