import fs from 'node:fs';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

const [host, work, browser] = process.argv.slice(2);
fs.mkdirSync(work, {recursive:true});
const page=fs.readFileSync(path.join(host,'TvAIr/wwwroot/index.html'),'utf8');
const start=page.indexOf('const SETTINGS_VALUE_CONTRACT =');
const end=page.indexOf('async function refreshBonDriverList',start);
if(start<0 || end<0) throw Error('settings functions not found');
const functions=page.slice(start,end);
const tests=String.raw`
const _bonDriverList=['BonDriver_T.dll','BonDriver_S.dll','BonDriver_H.dll','BonDriver_Cable.dll','BonDriver_View.dll','BonDriver_New.dll'];
function showCfgConfirm(text,callback){callback();}
const cases=[];
function check(value,reason){if(!value)throw Error(reason);}
function test(name,body){try{body();cases.push({name,passed:true});}catch(e){cases.push({name,passed:false,error:e.message});}}
const fixtures=[
 {name:'T1',bonDriverFileName:'BonDriver_T.dll',group:'GR',did:'A',role:'Recording',logicalViewerSlotId:'saved-t',channelCh2Path:''},
 {name:'S1',bonDriverFileName:'BonDriver_S.dll',group:'BSCS',did:'B',role:'Recording',logicalViewerSlotId:'saved-s',channelCh2Path:''},
 {name:'M1',bonDriverFileName:'BonDriver_H.dll',group:'HYBRID',did:'C',role:'Recording',logicalViewerSlotId:'saved-h',channelCh2Path:''},
 {name:'C1',bonDriverFileName:'BonDriver_Cable.dll',group:'CATV',did:'D',role:'Recording',logicalViewerSlotId:'saved-cable',channelCh2Path:'C:/fixture/ケーブル.ch2'},
 {name:'C2',bonDriverFileName:'BonDriver_View.dll',group:'CATV',did:'E',role:'Viewing',logicalViewerSlotId:'saved-view',channelCh2Path:'C:/fixture/view.ch2'}
];
test('CATV normalizes while unknown and blank stay unselected',()=>{
 check(normalizeTunerGroup(' catv ')==='CATV','CATV lost');
 check(normalizeTunerGroup('')==='' && normalizeTunerGroup('UNKNOWN')==='','unknown inferred');
});
test('CATV option is selectable and existing choices retained',()=>{
 const s=buildTunerGroupSelect('CATV');check(s.value==='CATV','CATV unselected');
 check(JSON.stringify([...s.options].map(o=>o.value))===JSON.stringify(['','GR','BSCS','HYBRID','CATV']),'options changed');
});
test('mixed tuner rows preserve DID and per-group ordinals',()=>{
 renderTunerTable(fixtures);const rows=collectTuners();check(validateTuners(rows)==='','validation rejected saved CATV');
 check(rows.map(t=>t.name).join(',')==='T1,S1,M1,C1,C2','wrong display names');
 check(rows.map(t=>t.deviceNumber).join(',')==='1,1,1,1,2','wrong ordinals');
 check(rows.map(t=>t.did).join(',')==='A,B,C,D,E','DID changed');
});
test('unchanged saved rows preserve hidden path and persistent identity',()=>{
 renderTunerTable(fixtures);const rows=collectTuners();
 rows.forEach((t,i)=>{check(t.logicalViewerSlotId===fixtures[i].logicalViewerSlotId,'slot ID lost');check(t.channelCh2Path===fixtures[i].channelCh2Path,'source path lost');});
});
test('driver change clears old table binding and undo restores it',()=>{
 renderTunerTable(fixtures);const row=document.querySelectorAll('#cfg-tuner-tbody tr')[3];
 const select=row.querySelector('td:nth-child(2) select');select.value='BonDriver_New.dll';select.dispatchEvent(new Event('change'));
 let t=collectTuners()[3];check(t.channelCh2Path==='' && t.group==='CATV' && t.did==='D' && t.logicalViewerSlotId==='saved-cable','driver edit corrupted identity or retained foreign source');
 select.value='BonDriver_Cable.dll';select.dispatchEvent(new Event('change'));t=collectTuners()[3];
 check(t.channelCh2Path===fixtures[3].channelCh2Path,'undo lost original source');
});
test('role edit retains same logical slot and source',()=>{
 renderTunerTable(fixtures);const row=document.querySelectorAll('#cfg-tuner-tbody tr')[3];row.querySelector('td:nth-child(4) select').value='Viewing';
 const t=collectTuners()[3];check(t.role==='Viewing' && t.logicalViewerSlotId==='saved-cable' && t.channelCh2Path===fixtures[3].channelCh2Path,'role edit lost stable data');
});
test('deleting one CATV row retains remaining identity and renumbers display only',()=>{
 renderTunerTable(fixtures);const row=document.querySelectorAll('#cfg-tuner-tbody tr')[3];deleteTunerRow(row.querySelector('button'));
 const rows=collectTuners(),t=rows[3];check(rows.length===4 && t.name==='C1' && t.deviceNumber===1 && t.did==='E' && t.logicalViewerSlotId==='saved-view' && t.channelCh2Path===fixtures[4].channelCh2Path,'delete corrupted remaining slot');
});
test('rebuilding rows cannot inherit previous hidden identities',()=>{
 renderTunerTable(fixtures);document.getElementById('cfg-tuner-count').value='2';applyTunerCount();const rows=collectTuners();
 check(rows.length===2 && rows.every(t=>t.group==='' && !t.channelCh2Path && !t.logicalViewerSlotId),'rebuilt rows inherited bindings');
 check(validateTuners(rows)!=='','incomplete new rows admitted');
});
document.getElementById('results').textContent=btoa(unescape(encodeURIComponent(JSON.stringify({cases}))));
`;
const html=`<!doctype html><meta charset="utf-8"><table><tbody id="cfg-tuner-tbody"></tbody></table><input id="cfg-tuner-count"><pre id="results">PENDING</pre><script>${functions}\n${tests}</script>`;
const file=path.join(work,'test.html');fs.writeFileSync(file,html);
const result=spawnSync(browser,['--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check',`--user-data-dir=${path.join(work,'profile')}`,'--dump-dom',new URL('file:///'+file.replaceAll('\\','/')).href],{encoding:'utf8',timeout:30000,maxBuffer:4*1024*1024,windowsHide:true});
fs.writeFileSync(path.join(work,'browser-stderr.txt'),result.stderr??'');
if(result.error)throw result.error;
const encoded=result.stdout?.match(/<pre id="results">([^<]+)<\/pre>/)?.[1];
if(!encoded || encoded==='PENDING')throw Error('browser did not return test results: '+result.status);
const report=JSON.parse(Buffer.from(encoded,'base64').toString('utf8'));
fs.writeFileSync(path.join(work,'results.json'),JSON.stringify(report,null,2));
for(const item of report.cases)console.log((item.passed?'PASS ':'FAIL ')+item.name+(item.error?': '+item.error:''));
const passed=report.cases.filter(c=>c.passed).length;
console.log(`${passed}/${report.cases.length} actual browser DOM cases passed (extracted production settings functions).`);
process.exitCode=passed===report.cases.length?0:1;
