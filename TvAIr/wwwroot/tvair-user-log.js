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

  function renderResultCell(entry, esc){
    const result = valueOrDash(entry && (entry.result || entry.severity));
    return `<td class="col-result tvair-user-log-result-cell" data-cell-role="log-result" title="${esc(result)}">${esc(result)}</td>`;
  }

  function renderTargetCell(entry, esc){
    const target = valueOrDash(entry && entry.target);
    return `<td class="col-target tvair-user-log-target-cell" data-cell-role="log-target" title="${esc(target)}">${esc(target)}</td>`;
  }

  function renderMessageCell(entry, esc){
    const message = valueOrDash(entry && entry.message);
    return `<td class="col-main tvair-user-log-message-cell" data-cell-role="log-content" title="${esc(message)}"><span class="tvair-user-log-message-text">${esc(message)}</span></td>`;
  }


  window.TvAIrUserLog = Object.freeze({
    renderResultCell,
    renderTargetCell,
    renderMessageCell
  });
})();
