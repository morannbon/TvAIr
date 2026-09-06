(function(){
  'use strict';

  // Shared Enter-to-search contract. Only explicitly marked search inputs opt in.
  // The existing search button remains the execution source of truth.
  function findTargetInput(eventTarget){
    if(!eventTarget || !eventTarget.closest) return null;
    const input = eventTarget.closest('[data-tvair-search-enter-target]');
    if(!input || input.tagName !== 'INPUT') return null;
    const type = String(input.type || 'text').toLowerCase();
    if(type !== 'text' && type !== 'search') return null;
    return input;
  }

  document.addEventListener('compositionstart', function(event){
    const input = findTargetInput(event.target);
    if(input) input._tvairSearchEnterComposing = true;
  }, true);

  document.addEventListener('compositionend', function(event){
    const input = findTargetInput(event.target);
    if(input) input._tvairSearchEnterComposing = false;
  }, true);

  document.addEventListener('keydown', function(event){
    if(event.key !== 'Enter') return;

    const input = findTargetInput(event.target);
    if(!input || input.disabled || input.readOnly) return;
    if(event.isComposing || event.keyCode === 229 || input._tvairSearchEnterComposing) return;

    const selector = input.getAttribute('data-tvair-search-enter-target');
    if(!selector) return;

    const button = document.querySelector(selector);
    if(!button || button.disabled) return;

    event.preventDefault();
    button.click();
  }, true);
})();
