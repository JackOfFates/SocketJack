'use strict';
const $ = id => document.getElementById(id);
let socket, snapshot, selected = 'NVDA', period = 'DAY', view = 'movers', currentChart, retry = 1000, requestId = 0, chartGeneration = 0, searchGeneration = 0;
const requests = new Map();
const money = value => value == null ? '—' : new Intl.NumberFormat('en-US', {style:'currency',currency:'USD',maximumFractionDigits:2}).format(value);
const percent = value => value == null ? '—' : `${value >= 0 ? '+' : ''}${value.toFixed(2)}%`;
const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function setHtml(element, html){if(element.dataset.rendered!==html){element.innerHTML=html;element.dataset.rendered=html;}}
const when = value => value ? new Date(value).toLocaleString([], {month:'short',day:'numeric',hour:'numeric',minute:'2-digit'}) : '—';
function toast(text) { $('toast').textContent=text; $('toast').hidden=false; clearTimeout(toast.timer); toast.timer=setTimeout(()=> $('toast').hidden=true,5000); }
function send(type, data={}) {
 if (!socket || socket.readyState !== WebSocket.OPEN) return Promise.reject(new Error('Reconnecting to the app. Please try again in a moment.'));
 const id=String(++requestId);
 return new Promise((resolve,reject)=> { const timer=setTimeout(()=> {requests.delete(id);reject(new Error('The request timed out. Check Settings and try again.'));},60000); requests.set(id,{resolve,reject,timer}); socket.send(JSON.stringify({type,id,...data})); });
}
function connect() {
 const wsPath=location.pathname.toLowerCase().startsWith('/stocks')?'/Stocks/ws':'/ws';
 socket=new WebSocket(`${location.protocol==='https:'?'wss':'ws'}://${location.host}${wsPath}`);
 socket.onopen=()=> {retry=1000;$('transport').textContent='● WebSocket connected';$('transport').className='connection online';};
 socket.onmessage=event=> {
  let message;try{message=JSON.parse(event.data);}catch{return;}
  if(message.type==='snapshot'){const first=!snapshot;snapshot=message.data;render();if(first)loadChart();}
  if(message.id&&requests.has(message.id)){const p=requests.get(message.id);requests.delete(message.id);clearTimeout(p.timer);message.error?p.reject(new Error(message.error)):p.resolve(message.data);}
 };
 socket.onclose=()=> {$('transport').textContent='Reconnecting…';$('transport').className='connection';for(const p of requests.values()){clearTimeout(p.timer);p.reject(new Error('Connection interrupted. Reconnecting…'));}requests.clear();setTimeout(connect,retry);retry=Math.min(retry*2,15000);};
 socket.onerror=()=>socket.close();
}
function render(){
 const s=snapshot;$('status').textContent=s.status;$('statusDetail').textContent=s.connected?'Prices refresh automatically. Source timestamps appear with each quote.':s.configured?'Your key is saved. Check Settings if the connection does not recover.':'Add your API key to bring prices, charts, and discovery to life.';
 $('connectOpen').textContent=s.configured?'Connection settings ↗':'Connect Public ↗';$('watchCount').textContent=s.watchlist.length;
 $('removeKey').hidden=!s.configured;$('replaceKey').hidden=!s.configured;$('saveKey').hidden=s.configured;
 $('searchNote').textContent=s.configured?'Search Public’s instrument catalog.':'Connect Public to search beyond NVIDIA and AMD.';
 setHtml($('watchlist'),s.watchlist.map(w=>`<div><button class="watch-item ${selected===w.symbol?'active':''}" data-symbol="${escape(w.symbol)}"><div class="watch-top"><strong>${escape(w.symbol)}</strong><span>${money(w.quote?.last)}</span></div><div class="watch-bottom"><span class="name">${escape(w.name)}</span><span class="${w.quote?.percent==null?'subtle':w.quote.percent>=0?'positive':'negative'}">${percent(w.quote?.percent)}</span></div>${w.sparkline?.length>1?`<svg class="watch-spark" viewBox="0 0 110 27" aria-label="${escape(w.symbol)} sparkline"><path d="${line(w.sparkline,110,27)}" fill="none" stroke="#a4ed69" stroke-width="1.5"/></svg>`:''}</button><button class="watch-remove" data-remove="${escape(w.symbol)}" aria-label="Remove ${escape(w.symbol)} from watchlist">Remove</button></div>`).join('') || '<p class="discovery-empty">Add a stock to begin your watchlist.</p>');
 updateQuote();renderDiscovery();
}
function updateQuote(){
 const w=snapshot?.watchlist.find(w=>w.symbol===selected);
 const discovered=[...(snapshot?.movers||[]),...(snapshot?.sustained||[])].find(w=>w.symbol===selected);
 $('chartName').textContent=w?.name||selected;$('chartSymbol').textContent=selected;
 $('chartPrice').textContent=money(w?.quote?.last??discovered?.price);
 const p=w?.quote?.percent??discovered?.dailyPercent;
 $('chartChange').textContent=p==null?'Awaiting market data':`${percent(p)} today`;
 $('chartChange').className=p==null?'subtle':p>=0?'positive':'negative';
 if(w?.quote){const stale=!snapshot.connected||Date.now()-new Date(w.quote.fetchedAt).getTime()>30000;$('chartQuoteTime').textContent=(stale?'Cached quote · ':'Last trade · ')+when(w.quote.timestamp);}
 else $('chartQuoteTime').textContent=discovered?'Last trade · '+when(discovered.asOf):'';
}
function renderDiscovery(){const items=snapshot?.[view]||[];$('discoveryDescription').textContent=view==='movers'?'Stocks gaining today with an upward intraday price trend.':'Positive 5- and 20-session returns, a rising trend, and price above its 20-session average.';
 const listKey=JSON.stringify([view,snapshot?.configured,items]);if(listKey!==renderDiscovery.key){renderDiscovery.key=listKey;
 if(!items.length)$('discovery').innerHTML=`<div class="discovery-empty"><strong>${snapshot?.configured?'Waiting for qualifying momentum':'A wider view of the market'}</strong>${snapshot?.configured?'Results appear as the scan finds stocks meeting the price, volume, and trend filters.':'Connect Public to discover rising stocks outside your watchlist.'}</div>`;
 else $('discovery').innerHTML=`<div class="table-wrap"><table><thead><tr><th>STOCK</th><th>PRICE</th><th>TODAY</th><th>5 SESSIONS</th><th>20 SESSIONS</th><th>AVG. $ VOLUME</th><th></th></tr></thead><tbody>${items.map(x=>`<tr><td><button class="ticker-link" data-symbol="${escape(x.symbol)}">${escape(x.symbol)} ↗</button></td><td>${money(x.price)}</td><td class="${x.dailyPercent>=0?'positive':'negative'}">${percent(x.dailyPercent)}</td><td class="${x.fiveDayPercent>=0?'positive':'negative'}">${percent(x.fiveDayPercent)}</td><td class="${x.twentyDayPercent>=0?'positive':'negative'}">${percent(x.twentyDayPercent)}</td><td>$${(x.averageDollarVolume/1000000).toFixed(1)}M</td><td><button class="quiet" data-add="${escape(x.symbol)}">+ Watch</button></td></tr>`).join('')}</tbody></table></div>`;
 }
 $('scanStatus').textContent=snapshot?.configured?`${snapshot.scan.scanned.toLocaleString()} / ${snapshot.scan.total.toLocaleString()} stocks scanned · rolling scan`:'Connect Public to begin scanning';$('scanTime').textContent=snapshot?.scan.lastCompleted?`Last complete ${when(snapshot.scan.lastCompleted)}`:'Excludes stocks on your watchlist';}
function line(bars,width,height){let min=Math.min(...bars.map(b=>b.close)),max=Math.max(...bars.map(b=>b.close));const span=max-min||Math.max(max*.01,1);return bars.map((b,i)=>`${i?'L':'M'}${(i/(bars.length-1)*(width-12)+6).toFixed(2)},${(height-8-(b.close-min)/span*(height-16)).toFixed(2)}`).join(' ');}
function drawChart(){const bars=currentChart?.bars||[];if(bars.length<2){$('chart').innerHTML='';$('chartEmpty').hidden=false;return;}$('chartEmpty').hidden=true;const d=line(bars,900,280);$('chart').innerHTML=`<defs><linearGradient id="areaFill" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stop-color="#a4ed69" stop-opacity=".2"/><stop offset="100%" stop-color="#a4ed69" stop-opacity="0"/></linearGradient></defs><path d="${d} L894,280 L6,280 Z" fill="url(#areaFill)"/><path d="${d}" fill="none" stroke="#a4ed69" stroke-width="2.5" vector-effect="non-scaling-stroke"/>`;
const stale=currentChart.stale || Date.now()-new Date(currentChart.fetchedAt).getTime()>120000;$('chartTime').textContent=`${stale?'Cached · ':''}${when(bars[0].timestamp)} – ${when(bars.at(-1).timestamp)}`;}
async function loadChart(){const generation=++chartGeneration;try{const result=await send('chart',{symbol:selected,period});if(generation!==chartGeneration)return;currentChart=result;drawChart();if(!result.bars.length){$('chartEmpty').querySelector('h3').textContent='No history available';$('chartEmpty').querySelector('p').textContent='Public has no bars for this stock and period.';}}catch(e){if(generation!==chartGeneration)return;currentChart=null;$('chart').innerHTML='';$('chartEmpty').hidden=false;$('chartEmpty').querySelector('h3').textContent='Your next move starts here';$('chartEmpty').querySelector('p').textContent=e.message;$('chartTime').textContent='No market data loaded';}}
function openSettings(){ $('settingsResult').textContent=snapshot?.configured?'A key is saved for this Windows user.':'';$('settings').showModal(); }
for(const id of ['settingsOpen','connectOpen','chartConnect'])$(id).onclick=openSettings;
document.querySelectorAll('.close').forEach(b=>b.onclick=()=>{b.closest('dialog').close();$('secret').value='';});
$('settings').addEventListener('close',()=>{$('secret').value='';});
$('settingsForm').onsubmit=async event=>{event.preventDefault();await saveKey();};
$('replaceKey').onclick=saveKey;
async function saveKey(){let secret=$('secret').value;$('secret').value='';if(!secret.trim()){ $('settingsResult').textContent='Enter your Public secret key first.';$('secret').focus();return; }setBusy(true);$('settingsResult').textContent='Validating your key with Public…';try{const result=await send('saveKey',{secret});secret='';if(Array.isArray(result)){ $('account').innerHTML=result.map(a=>`<option value="${escape(a.accountId)}">${escape(a.accountType)} · ${escape(a.accountId)}</option>`).join('');$('accountArea').hidden=false;$('settingsResult').textContent='Choose the account to use for market data.';}else{$('accountArea').hidden=true;$('settingsResult').textContent=result.message;loadChart();}}catch(e){$('settingsResult').textContent=e.message;}finally{secret='';setBusy(false);}}
function setBusy(busy){for(const id of ['saveKey','replaceKey','removeKey','selectAccount'])$(id).disabled=busy;}
$('selectAccount').onclick=async()=>{setBusy(true);try{const result=await send('selectAccount',{accountId:$('account').value});$('settingsResult').textContent=result.message;$('accountArea').hidden=true;loadChart();}catch(e){$('settingsResult').textContent=e.message;}finally{setBusy(false);}};
$('removeKey').onclick=async()=>{setBusy(true);try{const result=await send('removeKey');$('settingsResult').textContent=result.message;$('accountArea').hidden=true;$('secret').value='';}catch(e){$('settingsResult').textContent=e.message;}finally{setBusy(false);}};
$('addOpen').onclick=()=>{$('searchDialog').showModal();$('search').focus();search();};
async function search(){const generation=++searchGeneration;try{const results=await send('search',{query:$('search').value});if(generation!==searchGeneration)return;$('searchResults').innerHTML=results.map(i=>`<button class="search-result" data-add="${escape(i.symbol)}"><span>${escape(i.symbol)}<small>${escape(i.name)}</small></span><span>+ Watch</span></button>`).join('')||'<p class="field-note">No matching stocks. Connect Public to load the full catalog.</p>';}catch(e){$('searchResults').textContent=e.message;}}
$('search').oninput=()=>{clearTimeout(search.timer);search.timer=setTimeout(search,200);};
document.addEventListener('click',async event=>{const b=event.target.closest('button');if(!b)return;try{if(b.dataset.symbol){selected=b.dataset.symbol;updateQuote();loadChart();if(snapshot)render();}if(b.dataset.remove)await send('remove',{symbol:b.dataset.remove});if(b.dataset.add){await send('add',{symbol:b.dataset.add});toast(`${b.dataset.add} added to your watchlist`);$('searchDialog').close();}if(b.dataset.period){period=b.dataset.period;document.querySelectorAll('[data-period]').forEach(x=>x.classList.toggle('active',x===b));loadChart();}if(b.dataset.view){view=b.dataset.view;document.querySelectorAll('[data-view]').forEach(x=>x.classList.toggle('active',x===b));renderDiscovery();}}catch(e){toast(e.message);}});
$('chart').onpointermove=event=>{const bars=currentChart?.bars;if(!bars?.length)return;const rect=$('chart').getBoundingClientRect();const index=Math.max(0,Math.min(bars.length-1,Math.round((event.clientX-rect.left)/rect.width*(bars.length-1))));$('chartHover').textContent=`${when(bars[index].timestamp)} · ${money(bars[index].close)}`;};
$('chart').onpointerleave=()=>{$('chartHover').textContent='Regular trading hours';};
$('today').textContent=new Date().toLocaleDateString('en-US',{weekday:'long',month:'short',day:'numeric',year:'numeric'});
setInterval(()=>{if(snapshot?.configured&&socket?.readyState===WebSocket.OPEN)loadChart();},60000);
connect();
