namespace TvAIr.Plugin;

// Shared by shell and direct-content HTML; ES5 syntax for MSHTML.
internal static class RuntimeUiDocumentScript
{
    internal const string Source = """
function tvairFindActionTokenElement(){try{return document.querySelector?document.querySelector('[data-tvair-action-token],[data-tvair-token]'):null;}catch(_){return null;}}
function tvairActionTokenIdentity(){try{var el=tvairFindActionTokenElement();if(!el)return null;var token=tvairGetAttr(el,'data-tvair-action-token')||tvairGetAttr(el,'data-tvair-token');var pluginId=tvairGetAttr(el,'data-tvair-plugin-id');var route=tvairGetAttr(el,'data-tvair-route-segment')||document.body.getAttribute('data-plugin-route')||'';if(!token||!pluginId||!route)return null;return{token:token,pluginId:pluginId,route:route};}catch(_){return null;}}
function tvairRenewActionToken(){try{var id=tvairActionTokenIdentity();if(!id||window.__tvairPageTokenRecoveryInFlight)return;var xhr=new XMLHttpRequest();xhr.open('POST','/api/plugins/action-token/renew',true);xhr.setRequestHeader('Content-Type','application/x-www-form-urlencoded; charset=UTF-8');xhr.onreadystatechange=function(){if(xhr.readyState!==4)return;if(xhr.status>=200&&xhr.status<300)return;tvairRecoverExpiredPageToken();};xhr.send('actionToken='+encodeURIComponent(id.token)+'&pluginId='+encodeURIComponent(id.pluginId)+'&routeSegment='+encodeURIComponent(id.route));}catch(_){}}
function tvairRecoverExpiredPageToken(){try{if(window.__tvairPageTokenRecoveryInFlight)return;var id=tvairActionTokenIdentity();if(!id)return;window.__tvairPageTokenRecoveryInFlight=true;var xhr=new XMLHttpRequest();xhr.open('POST','/api/plugins/action-token/validate',true);xhr.setRequestHeader('Content-Type','application/x-www-form-urlencoded; charset=UTF-8');xhr.onreadystatechange=function(){if(xhr.readyState!==4)return;if(xhr.status>=200&&xhr.status<300){window.__tvairPageTokenRecoveryInFlight=false;return;}if(window.__tvairHostPageNavigationInFlight){window.__tvairPageTokenRecoveryInFlight=false;return;}window.__tvairHostPageNavigationInFlight='action_token_recovery';var path='/plugin/'+encodeURIComponent(id.route);var query=window.location&&window.location.search?window.location.search:'';window.location.replace(path+query);};xhr.onerror=function(){window.__tvairPageTokenRecoveryInFlight=false;};xhr.send('actionToken='+encodeURIComponent(id.token)+'&pluginId='+encodeURIComponent(id.pluginId)+'&routeSegment='+encodeURIComponent(id.route));}catch(_){window.__tvairPageTokenRecoveryInFlight=false;}}
function tvairStartActionTokenKeepalive(){try{if(window.__tvairActionTokenKeepaliveStarted)return;window.__tvairActionTokenKeepaliveStarted=true;tvairRenewActionToken();window.setInterval(tvairRenewActionToken,300000);if(document.addEventListener)document.addEventListener('visibilitychange',function(){if(!document.hidden)tvairRecoverExpiredPageToken();},false);if(window.addEventListener){window.addEventListener('focus',tvairRecoverExpiredPageToken,false);window.addEventListener('pageshow',tvairRecoverExpiredPageToken,false);}else if(window.attachEvent)window.attachEvent('onfocus',tvairRecoverExpiredPageToken);}catch(_){}}
function tvairApplyUiPatches(patches){if(!patches||Object.prototype.toString.call(patches)!=='[object Array]')return -1;for(var v=0;v<patches.length&&v<64;v++){var vp=patches[v]||{};if(!document.getElementById(vp.elementId||vp.ElementId||''))return -2;}for(var i=0;i<patches.length&&i<64;i++){var p=patches[i]||{};var id=p.elementId||p.ElementId||'';if(!id)continue;var target=document.getElementById(id);if(!target)continue;var text=(typeof p.textContent!=='undefined')?p.textContent:p.TextContent;if(text!==null&&typeof text!=='undefined'){if('textContent' in target)target.textContent=String(text);else target.innerText=String(text);}var cls=(typeof p.className!=='undefined')?p.className:p.ClassName;if(cls!==null&&typeof cls!=='undefined')target.className=String(cls);var remove=p.removeClasses||p.RemoveClasses||[];for(var r=0;r<remove.length;r++){var rc=String(remove[r]||'');if(!rc)continue;if(target.classList)target.classList.remove(rc);else target.className=(' '+target.className+' ').replace(' '+rc+' ',' ').replace(/^\s+|\s+$/g,'');}var add=p.addClasses||p.AddClasses||[];for(var a=0;a<add.length;a++){var ac=String(add[a]||'');if(!ac)continue;if(target.classList)target.classList.add(ac);else if((' '+target.className+' ').indexOf(' '+ac+' ')<0)target.className=(target.className?target.className+' ':'')+ac;}var disabled=(typeof p.disabled!=='undefined')?p.disabled:p.Disabled;if(disabled!==null&&typeof disabled!=='undefined'){target.disabled=!!disabled;if(disabled)target.setAttribute('disabled','disabled');else target.removeAttribute('disabled');}var hidden=(typeof p.hidden!=='undefined')?p.hidden:p.Hidden;if(hidden!==null&&typeof hidden!=='undefined'){if('hidden' in target)target.hidden=!!hidden;else{if(hidden){if(typeof target.__tvairPreviousDisplay==='undefined')target.__tvairPreviousDisplay=target.style.display;target.style.display='none';}else{target.style.display=target.__tvairPreviousDisplay||'';delete target.__tvairPreviousDisplay;}}if(hidden)target.setAttribute('hidden','hidden');else target.removeAttribute('hidden');}var checked=(typeof p.checked!=='undefined')?p.checked:p.Checked;if(checked!==null&&typeof checked!=='undefined'){target.checked=!!checked;if(checked)target.setAttribute('checked','checked');else target.removeAttribute('checked');}var value=(typeof p.value!=='undefined')?p.value:p.Value;if(value!==null&&typeof value!=='undefined')target.value=String(value);var attrs=p.attributes||p.Attributes||{};for(var name in attrs){if(!Object.prototype.hasOwnProperty.call(attrs,name))continue;var lower=String(name).toLowerCase();if(!(lower==='title'||lower.indexOf('aria-')===0||lower.indexOf('data-')===0))continue;var attrValue=attrs[name];if(attrValue===null||typeof attrValue==='undefined')target.removeAttribute(name);else target.setAttribute(name,String(attrValue));} } return Math.min(patches.length,64);}
function tvairApplyUiPatchesJson(json){try{return tvairApplyUiPatches(JSON.parse(String(json||'[]')));}catch(_){return -1;}}
function tvairApplyActionResponsePatches(patches,el,eventName,action){var applied=tvairApplyUiPatches(patches);if(applied<0){tvairDebugSet(el,'data-tvair-debug-reason',applied===-2?'patch_target_missing':'patch_invalid');tvairClientBeacon('patch_failed',eventName,action,el);}return applied;}
function tvairHostStateContext(){
  var q=String(location.search||'').substring(1).split('&'),keep=[],i;
  for(i=0;i<q.length;i++)if(q[i]&&!/^_+tvair/i.test(q[i]))keep.push(q[i]);
  return location.pathname+'?'+keep.sort().join('&');
}
function tvairHostFormIdentity(form){
  if(!form)return '';
  var a=[],es=form.elements||[],i,e;
  for(i=0;i<es.length;i++){e=es[i];if(String(e.type).toLowerCase()==='hidden'&&e.name&&!/token|csrf|action|window|refresh|scroll|revision|route/i.test(e.name))a.push(e.name+'='+e.value);}
  return a.sort().join('&');
}
function tvairHostControlKey(e){
  if(e.id)return {id:e.id};
  if(!e.name||!e.form)return null;
  var forms=document.forms||[],fi=-1,es=e.form.elements||[],ordinal=0,i;
  for(i=0;i<forms.length;i++)if(forms[i]===e.form){fi=i;break;}
  for(i=0;i<es.length;i++){if(es[i]===e)break;if(es[i].name===e.name)ordinal++;}
  return fi<0?null:{form:fi,name:e.name,ordinal:ordinal};
}
function tvairHostFindControl(k){
  if(k.id)return document.getElementById(k.id);
  var f=(document.forms||[])[k.form],es=f?f.elements:[],n=0,i;
  for(i=0;i<es.length;i++)if(es[i].name===k.name){if(n++===k.ordinal)return es[i];}
  return null;
}
function tvairHostControlState(e,defaults){
  var type=String(e.type||e.tagName||'').toLowerCase(),a=[],i;
  if(type==='password'||type==='file'||type==='hidden'||type==='button'||type==='submit'||type==='reset'||type==='image')return null;
  if(type==='radio'||type==='checkbox')return defaults?!!e.defaultChecked:!!e.checked;
  if(e.options){for(i=0;i<e.options.length;i++)if(defaults?e.options[i].defaultSelected:e.options[i].selected)a.push(i);return a;}
  return String(defaults?(e.defaultValue==null?'':e.defaultValue):(e.value==null?'':e.value));
}
function tvairCaptureHostInteractionStateJson(scroll,interaction){
  var state={context:tvairHostStateContext(),controls:[],regions:[],focus:null},i,e,k,b,v,all=document.getElementsByTagName('*');
  if(scroll){state.x=window.pageXOffset||Math.max(document.documentElement.scrollLeft||0,document.body.scrollLeft||0);state.y=window.pageYOffset||Math.max(document.documentElement.scrollTop||0,document.body.scrollTop||0);}
  for(i=0;i<all.length;i++){
    e=all[i];k=tvairHostControlKey(e);
    if(scroll&&e.id&&(e.scrollTop||e.scrollLeft)&&state.regions.length<128)state.regions.push({id:e.id,x:e.scrollLeft,y:e.scrollTop});
    if(!interaction||!k||state.controls.length>=512)continue;
    if(String(e.tagName).toLowerCase()==='details'){state.controls.push({key:k,kind:'details',open:!!e.open});continue;}
    if(!/^(input|textarea|select)$/i.test(e.tagName))continue;
    b=tvairHostControlState(e,true);v=tvairHostControlState(e,false);if(b===null)continue;
    if(JSON.stringify(b)!==JSON.stringify(v))state.controls.push({key:k,kind:'control',type:String(e.type||''),identity:tvairHostFormIdentity(e.form),baseline:b,value:v,options:e.options?Array.prototype.map.call(e.options,function(o){return o.value;}).join('\u0001'):null});
  }
  e=document.activeElement;k=e?tvairHostControlKey(e):null;
  if(interaction&&k)state.focus={key:k,identity:tvairHostFormIdentity(e.form),start:typeof e.selectionStart==='number'?e.selectionStart:null,end:typeof e.selectionEnd==='number'?e.selectionEnd:null};
  return JSON.stringify(state);
}
function tvairRestoreHostInteractionStateJson(json,focus){
  var s=JSON.parse(String(json||'{}')),i,j,c,e,b;
  if(s.context!==tvairHostStateContext())return 'context_changed';
  for(i=0;i<(s.controls||[]).length;i++){
    c=s.controls[i];e=tvairHostFindControl(c.key);if(!e)continue;
    if(c.kind==='details'){e.open=c.open;continue;}
    if(String(e.type||'')!==c.type||tvairHostFormIdentity(e.form)!==c.identity)continue;
    b=tvairHostControlState(e,true);if(JSON.stringify(b)!==JSON.stringify(c.baseline))continue;
    if(e.options){if(Array.prototype.map.call(e.options,function(o){return o.value;}).join('\u0001')!==c.options)continue;for(j=0;j<e.options.length;j++)e.options[j].selected=c.value.indexOf(j)>=0;}
    else if(typeof c.value==='boolean')e.checked=c.value;else e.value=c.value;
  }
  if(focus&&s.focus){e=tvairHostFindControl(s.focus.key);if(e&&tvairHostFormIdentity(e.form)===s.focus.identity&&e.focus){e.focus();if(e.setSelectionRange&&s.focus.start!==null)try{e.setSelectionRange(s.focus.start,s.focus.end);}catch(_){}}}
  for(i=0;i<(s.regions||[]).length;i++){c=s.regions[i];e=document.getElementById(c.id);if(e){e.scrollLeft=c.x;e.scrollTop=c.y;}}
  if(typeof s.x==='number')window.scrollTo(s.x,s.y);
  return 'restored';
}
function tvairHostBrowserCapabilitiesJson(){return JSON.stringify({documentMode:document.documentMode||0,textContent:'textContent' in document.documentElement,hidden:'hidden' in document.documentElement,classList:!!document.documentElement.classList,json:typeof JSON==='object',engine:'host_document'});}

""";
}
