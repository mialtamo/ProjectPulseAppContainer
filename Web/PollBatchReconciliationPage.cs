namespace ProjectPulse.Processor.Web;

public static class PollBatchReconciliationPage
{
    public static string HtmlFor(string? signedInUser, string buildVersion, string environment, string role)
    {
        var user = System.Net.WebUtility.HtmlEncode(
            string.IsNullOrWhiteSpace(signedInUser) ? "Unknown user" : signedInUser);
        var build = System.Net.WebUtility.HtmlEncode(buildVersion);
        var environmentName = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(environment) ? "UNKNOWN" : environment);
        var roleName = System.Net.WebUtility.HtmlEncode(role);
        return $$$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Project Pulse · Batch reconciliation</title>
<style>
:root{color-scheme:dark;--bg:#05080d;--panel:#09121d;--line:#1b3448;--text:#d7e5f1;--muted:#8197aa;--cyan:#00d8f0;--green:#42dfa0;--amber:#ffc857;--red:#ff697c}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 Segoe UI,Arial,sans-serif}header{display:flex;justify-content:space-between;gap:20px;align-items:center;padding:18px 24px;border-bottom:1px solid var(--line);background:#05080d;position:sticky;top:0;z-index:2}.brand{font-weight:700;letter-spacing:.08em}.sub{color:var(--muted);font-size:12px;margin-top:3px}.right{display:flex;align-items:center;gap:16px;flex-wrap:wrap}.build{border:1px solid #087a8e;border-radius:20px;padding:6px 12px;color:var(--cyan);font-weight:700;font-size:12px}.user{color:#9eb2c4;font-size:12px}a{color:var(--cyan);text-decoration:none}nav{display:flex;gap:16px;flex-wrap:wrap}main{max-width:1600px;margin:22px auto;padding:0 22px}.controls{display:flex;gap:10px;align-items:center;flex-wrap:wrap}.controls input,.controls select,.controls button{background:var(--panel);border:1px solid var(--line);border-radius:7px;color:var(--text);padding:9px 12px;font:inherit}.controls input{min-width:260px;flex:1}.controls button{cursor:pointer}.summary{color:var(--muted);margin:14px 0}.batch{background:var(--panel);border:1px solid var(--line);border-radius:10px;margin:12px 0;overflow:hidden}.batchHead{display:grid;grid-template-columns:minmax(180px,1fr) minmax(500px,3fr) auto;gap:18px;align-items:center;padding:16px;cursor:pointer}.batchHead:hover{background:#0d1a27}.time{font-weight:600}.counts{display:flex;gap:8px;flex-wrap:wrap}.pill{border:1px solid var(--line);border-radius:18px;padding:4px 9px;font-size:12px;white-space:nowrap}.ok{color:var(--green)}.warn{color:var(--amber)}.bad{color:var(--red)}.detail{border-top:1px solid var(--line);padding:14px 16px}.detailTop{display:flex;justify-content:space-between;gap:12px;align-items:center;flex-wrap:wrap;color:var(--muted);font-size:12px;margin-bottom:12px}.recordFilter{display:flex;gap:12px;align-items:center;flex-wrap:wrap}.recordFilter input{background:var(--panel);border:1px solid var(--line);border-radius:6px;color:var(--text);padding:7px}.tableWrap{overflow:auto}table{width:100%;border-collapse:collapse;min-width:850px}th,td{text-align:left;padding:9px;border-bottom:1px solid #14283a;vertical-align:top}th{color:#91a9bc;font-size:11px;text-transform:uppercase;letter-spacing:.05em}td code{color:#aeeaf2;word-break:break-all}.empty{color:var(--muted);padding:20px;text-align:center}.gapnote{border:1px solid #72551e;border-radius:7px;padding:10px 12px;color:#e7c36f;background:#171307;margin:10px 0}.hidden{display:none!important}@media(max-width:900px){header{align-items:flex-start;flex-direction:column}.batchHead{grid-template-columns:1fr}.counts{gap:5px}}
</style>
</head>
<body>
<header><div><div class="brand">PROJECT PULSE · BATCH RECONCILIATION</div><div class="sub">Poll-level record accounting and missing-stage investigation</div></div><div class="right"><span class="build">BUILD {{{build}}} · {{{environmentName}}} · {{{roleName}}}</span><span class="user">Signed in as {{{user}}}</span><nav><a href="/requests.html">Requests</a><a href="/ledger.html">Lifecycle</a><a href="/">Home</a><a href="/.auth/logout">Sign out</a></nav></div></header>
<main>
<div class="controls"><input id="search" placeholder="Search poll run ID"><label><input id="gaps" type="checkbox"> Potential gaps only</label><select id="range"><option value="24h">Last 24 hours</option><option value="1h">Last hour</option><option value="7d">Last 7 days</option><option value="all">All history</option></select><button id="refresh">Refresh</button></div>
<div id="summary" class="summary">Loading poll batches…</div><div id="batches"></div>
</main>
<script>
const $=id=>document.getElementById(id),esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const dt=v=>v?new Date(v).toLocaleString():'—';
let loaded=[];
function stage(r,writeEnabled){
 if(r.RtaWritebackSucceeded===true)return ['Written to RTA','ok'];
 if(r.RtaWritebackSucceeded===false)return ['RTA writeback failed','bad'];
 if(r.ServiceBusBufferedAtUtc)return ['Buffered in Service Bus · '+(r.ServiceBusReason||'retry pending'),'warn'];
 if(r.ApimAttemptedAtUtc&&!r.ApimResponseAtUtc)return ['APIM POST attempt recorded; no response recorded','warn'];
 if(r.ApimResponseAtUtc&&writeEnabled)return ['APIM responded; no RTA writeback recorded','warn'];
 if(r.ApimResponseAtUtc)return ['APIM responded · RTA writeback disabled','ok'];
 if(r.ApimAttemptedAtUtc)return ['APIM POST attempt recorded','warn'];
 return ['Received; no APIM POST attempt recorded','bad'];
}
function render(){
 const query=$('search').value.trim().toLowerCase(), gaps=$('gaps').checked, root=$('batches');
 root.innerHTML='';
 const visible=loaded.filter(b=>(!query||b.PollRunId.toLowerCase().includes(query))&&(!gaps||b.PendingCount>0||b.RtaWritebackFailureCount>0));
 $('summary').textContent='Showing '+visible.length+' poll batch(es). Each batch starts with the records returned by one RTA/MOCK GET.';
 if(!visible.length){root.innerHTML='<div class="empty">No poll batches match this time range.</div>';return;}
 for(const b of visible){
  const box=document.createElement('section');box.className='batch';
  const header=document.createElement('div');header.className='batchHead';
  header.innerHTML='<div><div class="time">'+esc(dt(b.ReceivedAtUtc))+' · '+esc(b.Source)+'</div><div class="sub">Poll run '+esc(b.PollRunId)+' · '+(b.CompletedAtUtc?'completed '+esc(dt(b.CompletedAtUtc)):'still running')+'</div></div>'+
   '<div class="counts">'+
   '<span class="pill">Received <b>'+b.ReceivedCount+'</b></span>'+
   '<span class="pill warn">APIM attempts <b>'+b.ApimAttemptCount+'</b></span>'+
   '<span class="pill">APIM responses <b>'+b.ApimResponseCount+'</b></span>'+
   '<span class="pill warn">Service Bus <b>'+b.ServiceBusBufferedCount+'</b></span>'+
   '<span class="pill ok">RTA PUT success <b>'+b.RtaWritebackSuccessCount+'</b></span>'+
   '<span class="pill bad">RTA PUT failed <b>'+b.RtaWritebackFailureCount+'</b></span>'+
   (b.RtaWritebackEnabled?'<span class="pill warn">No confirmed writeback <b>'+b.PendingCount+'</b></span>':'')+
   '</div><div class="sub">'+(b.RtaPullDurationMs==null?'':esc(b.RtaPullDurationMs)+' ms GET')+'</div>';
  const detail=document.createElement('div');detail.className='detail hidden';
  header.onclick=async()=>{if(!detail.classList.contains('hidden')){detail.classList.add('hidden');return;}detail.classList.remove('hidden');if(!detail.dataset.loaded)await loadDetail(b,detail);};
  box.append(header,detail);root.append(box);
 }
}
async function loadDetail(batch,container){
 container.dataset.loaded='1';container.innerHTML='<div class="empty">Loading received records…</div>';
 try{
  const response=await fetch('/poll-batches/'+encodeURIComponent(batch.PollRunId),{cache:'no-store'});
  if(!response.ok)throw new Error('HTTP '+response.status);
  const detail=await response.json(),s=detail.Summary;
  const search=document.createElement('input');search.placeholder='Filter records by ID or RTA request ID';
  const onlyGaps=document.createElement('label');onlyGaps.innerHTML='<input type="checkbox"> Show potential gaps only';
  onlyGaps.querySelector('input').checked=s.RtaWritebackEnabled;
  const top=document.createElement('div');top.className='detailTop';
  top.innerHTML='<span>Records from the GET response: '+s.ReceivedCount+' · GET '+esc(s.RtaPullDurationMs??'N/A')+' ms</span>';
  const filter=document.createElement('div');filter.className='recordFilter';filter.append(search,onlyGaps);
  const note=document.createElement('div');note.className='gapnote';note.textContent='A missing response or writeback means Project Pulse has no recorded outcome for that stage. It flags a record to investigate; it does not by itself prove RTA or APIM lost it.';
  const wrap=document.createElement('div');wrap.className='tableWrap';
  const table=document.createElement('table');wrap.append(table);
  container.replaceChildren(top,filter,note,wrap);
  function draw(){
   const q=search.value.trim().toLowerCase(),gaps=onlyGaps.querySelector('input').checked;
   const rows=detail.Records.filter(r=>{const [label]=stage(r,s.RtaWritebackEnabled);const isGap=r.RtaWritebackSucceeded!==true&&s.RtaWritebackEnabled;return (!gaps||isGap)&&(!q||(r.UniqueId||'').toLowerCase().includes(q)||(r.RtaRequestId||'').toLowerCase().includes(q)||label.toLowerCase().includes(q));});
   table.innerHTML='<thead><tr><th>Record / Unique ID</th><th>RTA request ID</th><th>Current stage</th><th>APIM HTTP</th><th>RTA PUT HTTP</th><th>Last update</th></tr></thead><tbody>'+
    (rows.length?rows.map(r=>{const [label,tone]=stage(r,s.RtaWritebackEnabled);return '<tr><td><code>'+esc(r.UniqueId)+'</code></td><td>'+esc(r.RtaRequestId||'—')+'</td><td class="'+tone+'">'+esc(label)+(r.ProcessingReason?' · '+esc(r.ProcessingReason):'')+'</td><td>'+esc(r.ApimHttpStatusCode??'—')+'</td><td>'+esc(r.RtaWritebackHttpStatusCode??'—')+'</td><td>'+esc(dt(r.RtaWritebackAtUtc||r.ApimResponseAtUtc||r.ApimAttemptedAtUtc||r.ReceivedAtUtc))+'</td></tr>';}).join(''):'<tr><td colspan="6" class="empty">No records match these filters.</td></tr>')+'</tbody>';
   note.textContent=(s.RtaWritebackEnabled?'Potential gaps: '+detail.Records.filter(r=>r.RtaWritebackSucceeded!==true).length+' record(s) do not have a confirmed successful writeback. ':'RTA status writeback is disabled for this environment. ')+ 'A missing response or writeback is a lead to investigate, not proof by itself that a system lost the record.';
  }
  search.oninput=draw;onlyGaps.querySelector('input').onchange=draw;draw();
 }catch(e){container.innerHTML='<div class="empty">Could not load batch detail: '+esc(e.message)+'</div>';delete container.dataset.loaded;}
}
async function refresh(){
 $('summary').textContent='Loading poll batches…';
 try{
  const data=await fetch('/poll-batches?limit=100&range='+encodeURIComponent($('range').value),{cache:'no-store'});
  if(!data.ok)throw new Error('HTTP '+data.status);
  loaded=await data.json();render();
 }catch(e){$('summary').textContent='Could not load poll batches: '+e.message;}
}
$('refresh').onclick=refresh;$('range').onchange=refresh;$('search').oninput=render;$('gaps').onchange=render;refresh();
</script>
</body></html>
""";
    }
}
