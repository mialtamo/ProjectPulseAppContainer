using System.Net;

namespace ProjectPulse.Processor.Web;

public static class UiPages
{
    public static string LandingPage(string? versionValue, string? environmentValue, string? roleValue)
    {
        var version = WebUtility.HtmlEncode(versionValue ?? "0.0.0");
        var environment = WebUtility.HtmlEncode(environmentValue ?? "UNKNOWN");
        var role = WebUtility.HtmlEncode(roleValue ?? "RTA");

        return $$"""
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta name="color-scheme" content="dark" />
    <title>Project Pulse</title>
    <style>
        :root {
            --cyan: #19ddff;
            --blue: #3478ff;
            --violet: #8a4dff;
            --text: #d7e6ff;
            --muted: #6f86a8;
            --green: #39ff9a;
            --red: #ff4d6d;
            --amber: #ffc857;
        }
        * { box-sizing: border-box; }
        html, body {
            width: 100%;
            height: 100%;
            margin: 0;
            overflow: hidden;
            background: #000;
            color: var(--text);
            font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
        }
        body::before {
            content: "";
            position: fixed;
            inset: -35%;
            pointer-events: none;
            background:
                radial-gradient(circle at 50% 44%, rgba(25, 221, 255, .08), transparent 28%),
                radial-gradient(circle at 56% 48%, rgba(138, 77, 255, .07), transparent 34%);
            animation: atmosphere 8s ease-in-out infinite alternate;
        }
        .grid {
            position: fixed;
            inset: 0;
            opacity: .10;
            background-image:
                linear-gradient(rgba(74, 121, 255, .18) 1px, transparent 1px),
                linear-gradient(90deg, rgba(74, 121, 255, .18) 1px, transparent 1px);
            background-size: 72px 72px;
            mask-image: radial-gradient(circle at center, #000 0%, transparent 72%);
            pointer-events: none;
        }
        .shell {
            position: relative;
            width: 100%;
            height: 100%;
            display: grid;
            place-items: center;
            padding: 4vh 4vw;
        }
        .hero {
            width: min(980px, 72vw);
            max-height: 68vh;
            display: flex;
            align-items: center;
            justify-content: center;
            transform: translateY(3vh);
        }
        .logo-wrap {
            position: relative;
            width: 100%;
            animation: float 6s ease-in-out infinite;
        }
        .logo-wrap::before {
            content: "";
            position: absolute;
            inset: 18% 16% 12%;
            z-index: -1;
            background: radial-gradient(
                ellipse at center,
                rgba(20, 213, 255, .18) 0%,
                rgba(64, 99, 255, .09) 40%,
                rgba(137, 65, 255, .06) 58%,
                transparent 76%
            );
            filter: blur(28px);
            animation: glow 3.2s ease-in-out infinite alternate;
        }
        .logo {
            display: block;
            width: 100%;
            max-height: 68vh;
            height: auto;
            object-fit: contain;
            filter: drop-shadow(0 0 18px rgba(41, 197, 255, .13));
            user-select: none;
            -webkit-user-drag: none;
        }
        .status-panel {
            position: fixed;
            top: 24px;
            right: 28px;
            z-index: 20;
            min-width: 390px;
            padding: 16px 18px;
            border: 1px solid rgba(25, 221, 255, .16);
            border-radius: 8px;
            background: rgba(0, 0, 0, .52);
            backdrop-filter: blur(10px);
            box-shadow: 0 0 30px rgba(25, 221, 255, .05), inset 0 0 25px rgba(25, 221, 255, .025);
            font-family: "Courier New", Consolas, monospace;
            font-size: 12px;
            line-height: 1.85;
            color: #8098ba;
        }
        .status-title {
            margin-bottom: 8px;
            color: #d7e6ff;
            font-weight: 700;
            letter-spacing: .18em;
            text-transform: uppercase;
        }
        .status-row { white-space: nowrap; }
        .status-label { color: #8098ba; }
        .connected { color: var(--green); text-shadow: 0 0 9px rgba(57, 255, 154, .68); }
        .disconnected { color: var(--red); text-shadow: 0 0 9px rgba(255, 77, 109, .62); }
        .checking { color: var(--amber); text-shadow: 0 0 8px rgba(255, 200, 87, .35); }
        .last-check {
            margin-top: 8px;
            padding-top: 7px;
            border-top: 1px solid rgba(111, 134, 168, .14);
            color: #526783;
            font-size: 10px;
            letter-spacing: .08em;
        }
        .version {
            position: fixed;
            left: 24px;
            bottom: 19px;
            z-index: 5;
            display: flex;
            align-items: center;
            gap: 10px;
            color: var(--muted);
            font-size: 11px;
            font-weight: 600;
            letter-spacing: .16em;
            text-transform: uppercase;
            user-select: none;
        }
        .version .dot {
            width: 6px;
            height: 6px;
            border-radius: 50%;
            background: var(--cyan);
            box-shadow: 0 0 12px rgba(25, 221, 255, .95);
            animation: blink 2.1s ease-in-out infinite;
        }
        .scanline {
            position: fixed;
            left: 0;
            right: 0;
            height: 1px;
            top: -2px;
            background: linear-gradient(90deg, transparent 10%, rgba(25, 221, 255, .22), rgba(138, 77, 255, .18), transparent 90%);
            box-shadow: 0 0 8px rgba(25, 221, 255, .14);
            animation: scan 8s linear infinite;
            pointer-events: none;
        }
        @keyframes float {
            0%, 100% { transform: translateY(0) scale(1); }
            50% { transform: translateY(-8px) scale(1.003); }
        }
        @keyframes glow {
            from { opacity: .72; transform: scale(.98); }
            to { opacity: 1; transform: scale(1.035); }
        }
        @keyframes blink {
            0%, 100% { opacity: .62; }
            50% { opacity: 1; }
        }
        @keyframes scan {
            from { transform: translateY(0); opacity: 0; }
            8% { opacity: 1; }
            92% { opacity: 1; }
            to { transform: translateY(100vh); opacity: 0; }
        }
        @keyframes atmosphere {
            from { transform: scale(1) rotate(0deg); opacity: .82; }
            to { transform: scale(1.06) rotate(.8deg); opacity: 1; }
        }
        @media (max-width: 700px) {
            .hero { width: min(1100px, 96vw); }
            .version { left: 16px; bottom: 14px; font-size: 9px; }
            .status-panel { top: 12px; right: 12px; left: 12px; min-width: 0; font-size: 10px; }
        }
        @media (prefers-reduced-motion: reduce) {
            *, *::before, *::after { animation: none !important; }
        }
    </style>
</head>
<body>
    <div class="grid" aria-hidden="true"></div>
    <div class="scanline" aria-hidden="true"></div>

    <div class="status-panel">
        <div class="status-title">SYSTEM STATUS</div>
        <div class="status-row">
            <span class="status-label">Checking Claims API / SQL..... </span>
            <span id="appApi" class="checking">&lt;CHECKING...&gt;</span>
        </div>
        <div class="status-row">
            <span class="status-label">Checking APIM / Backend....... </span>
            <span id="apim" class="checking">&lt;CHECKING...&gt;</span>
        </div>
        <div class="status-row">
            <span class="status-label">Checking Service Bus.......... </span>
            <span id="serviceBus" class="checking">&lt;CHECKING...&gt;</span>
        </div>
        <div class="last-check" id="lastCheck">LAST CHECK: awaiting first health probe</div>
    </div>

    <main class="shell">
        <section class="hero" aria-label="Project Pulse">
            <div class="logo-wrap">
                <img class="logo" src="https://raw.githubusercontent.com/mialtamo/Project-Pulse/main/Assets/project-pulse-logo.png" alt="Project Pulse" />
            </div>
        </section>
    </main>

    <div class="version" aria-label="Project Pulse version {{version}}, environment {{environment}}, role {{role}}">
        <span class="dot" aria-hidden="true"></span>
        <span>Project Pulse&nbsp;&nbsp;v{{version}}&nbsp;&nbsp;•&nbsp;&nbsp;{{environment}}&nbsp;&nbsp;•&nbsp;&nbsp;{{role}}</span>
    </div>

    <script>
        function setStatus(id, connected) {
            const element = document.getElementById(id);
            if (connected) {
                element.className = 'connected';
                element.textContent = '<CONNECTED>';
            } else {
                element.className = 'disconnected';
                element.textContent = '<DISCONNECTED>';
            }
        }

        function setChecking() {
            for (const id of ['appApi', 'apim', 'serviceBus']) {
                const element = document.getElementById(id);
                element.className = 'checking';
                element.textContent = '<CHECKING...>';
            }
        }

        async function checkPulseHealth() {
            setChecking();
            try {
                const response = await fetch('/pulse-health', { method: 'GET', cache: 'no-store' });
                if (!response.ok) throw new Error('Health endpoint returned ' + response.status);
                const data = await response.json();
                setStatus('appApi', data.appApi);
                setStatus('apim', data.apim);
                setStatus('serviceBus', data.serviceBus);
                document.getElementById('lastCheck').textContent = 'LAST CHECK: ' + new Date().toLocaleTimeString();
            } catch {
                setStatus('appApi', false);
                setStatus('apim', false);
                setStatus('serviceBus', false);
                document.getElementById('lastCheck').textContent =
                    'LAST CHECK: ' + new Date().toLocaleTimeString() + ' | HEALTH ENDPOINT UNAVAILABLE';
            }
        }

        checkPulseHealth();
        setInterval(checkPulseHealth, 5000);
    </script>
</body>
</html>
""";
    }

    public const string RequestHistoryPage = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Project Pulse Requests</title>
<style>
:root{
  color-scheme:dark;
  --bg:#05070b;--panel:#0b111a;--panel2:#0f1722;--border:#1c2a3a;
  --text:#e5eefc;--muted:#8295ad;--cyan:#19ddff;--green:#54e39b;
  --amber:#ffc857;--red:#ff627d;--blue:#68a4ff;--purple:#b58cff
}
*{box-sizing:border-box}
body{margin:0;background:linear-gradient(180deg,#05070b,#07101a 55%,#05070b);color:var(--text);font-family:Inter,Segoe UI,system-ui,sans-serif}
header{position:sticky;top:0;z-index:10;padding:18px 24px;border-bottom:1px solid var(--border);background:rgba(5,7,11,.92);backdrop-filter:blur(10px);display:flex;justify-content:space-between;align-items:center}
h1{margin:0;font-size:19px;letter-spacing:.08em}.sub{color:var(--muted);font-size:12px;margin-top:4px}
a{color:var(--cyan);text-decoration:none}
main{padding:20px 24px 40px;max-width:1800px;margin:0 auto}
.toolbar{display:grid;grid-template-columns:minmax(260px,1fr) auto auto auto;gap:10px;align-items:center;margin-bottom:14px}
input,select,button{background:#0b1520;border:1px solid #24405a;color:var(--text);padding:9px 11px;border-radius:7px}
button{cursor:pointer}.muted{color:var(--muted)}
.summary{display:flex;gap:10px;flex-wrap:wrap;margin:0 0 14px}
.pill{padding:5px 9px;border:1px solid var(--border);border-radius:999px;background:#09111a;font-size:12px;color:var(--muted)}
.table-wrap{border:1px solid var(--border);border-radius:10px;overflow:auto;background:var(--panel);box-shadow:0 18px 60px rgba(0,0,0,.25)}
table{width:100%;border-collapse:collapse;min-width:1150px}
th,td{padding:10px 12px;border-bottom:1px solid var(--border);font-size:12px;vertical-align:top}
th{position:sticky;top:0;background:#0c1621;color:#9fb4cf;text-align:left;white-space:nowrap;cursor:pointer;user-select:none}
th:hover{color:#dceaff}
tr.data-row{cursor:pointer}
tr.data-row:hover{background:#0f1a27}
td.id{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;max-width:260px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.tag{display:inline-block;padding:2px 7px;border-radius:999px;font-size:11px;border:1px solid #29445f;white-space:nowrap}
.tag.APIM{color:var(--green)}.tag.ServiceBus{color:var(--amber)}
.status-Completed{color:var(--green)}.status-Buffered{color:var(--amber)}.status-Alerted,.status-WritebackFailed,.status-Rejected{color:var(--red)}
.expand-row{display:none;background:#07101a}.expand-row.open{display:table-row}
.expand-cell{padding:16px}
.detail-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:10px;margin-bottom:14px}
.card{border:1px solid var(--border);padding:10px;background:#08111a;border-radius:7px}
.label{font-size:10px;color:var(--muted);text-transform:uppercase;letter-spacing:.12em;margin-bottom:4px}
.payload-grid{display:grid;grid-template-columns:1fr 1fr;gap:14px}
.payload-panel{min-width:0}
.payload-title{font-size:12px;font-weight:700;margin:0 0 7px;color:#c7d9f2}
pre{margin:0;white-space:pre-wrap;word-break:break-word;background:#020407;border:1px solid var(--border);padding:12px;border-radius:7px;max-height:420px;overflow:auto;font:12px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace}
.json-key{color:var(--blue)}.json-string{color:var(--green)}.json-number{color:var(--amber)}.json-boolean{color:var(--purple)}.json-null{color:var(--red)}
.payload-note{color:var(--amber);font-size:12px;padding:10px;border:1px dashed #6d5728;border-radius:7px}
.empty{padding:28px;text-align:center;color:var(--muted)}
@media(max-width:900px){.toolbar{grid-template-columns:1fr 1fr}.payload-grid{grid-template-columns:1fr}}
</style>
</head>
<body>
<header>
  <div><h1>PROJECT PULSE REQUESTS</h1><div class="sub">Recent APIM and Service Bus activity</div></div>
  <a href="/">Home</a>
</header>
<main>
  <div class="toolbar">
    <input id="search" type="search" placeholder="Search ID, status, reason, code..." oninput="render()">
    <select id="destination" onchange="render()">
      <option value="">All destinations</option>
      <option value="APIM">APIM</option>
      <option value="ServiceBus">Service Bus</option>
    </select>
    <button onclick="load()">Refresh</button>
    <span id="status" class="muted"></span>
  </div>
  <div id="summary" class="summary"></div>
  <div class="table-wrap">
    <table>
      <thead><tr>
        <th data-key="UpdatedAtUtc">Updated ↕</th>
        <th data-key="ClaimCount">Claims ↕</th>
        <th data-key="UniqueId">Unique ID ↕</th>
        <th data-key="Status">Status ↕</th>
        <th data-key="Destination">Destination ↕</th>
        <th data-key="HttpStatusCode">HTTP ↕</th>
        <th data-key="BusinessResponseCode">Business Code ↕</th>
        <th data-key="Reason">Reason ↕</th>
      </tr></thead>
      <tbody id="rows"></tbody>
    </table>
  </div>
</main>
<script>
let items=[]; let sortKey='UpdatedAtUtc'; let sortDir='desc'; const detailCache=new Map();

function groupByBatch(source){
  const groups=new Map();
  for(const item of source){
    const key=item.BatchId||item.RecordId;
    if(!groups.has(key))groups.set(key,[]);
    groups.get(key).push(item);
  }
  return Array.from(groups.values()).map(group=>{
    const representative=group.slice().sort((a,b)=>Date.parse(b.UpdatedAtUtc||0)-Date.parse(a.UpdatedAtUtc||0))[0];
    const uniqueIds=[...new Set(group.map(x=>x.UniqueId).filter(Boolean))];
    return {...representative,ClaimCount:group.length,UniqueId:uniqueIds.length===1?uniqueIds[0]:uniqueIds.join(', '),RecordId:representative.RecordId};
  });
}
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function syntaxHighlight(value){
  if(value===null||value===undefined||value==='') return '<span class="muted">No payload recorded</span>';
  let text=String(value);
  try{text=JSON.stringify(JSON.parse(text),null,2)}catch{}
  return esc(text).replace(/(&quot;.*?&quot;)(\s*:)?|\b(true|false)\b|\b(null)\b|-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b/g,(m,str,colon,bool,nul)=>{
    if(str) return '<span class="'+(colon?'json-key':'json-string')+'">'+str+'</span>'+(colon||'');
    if(bool) return '<span class="json-boolean">'+bool+'</span>';
    if(nul) return '<span class="json-null">'+nul+'</span>';
    return '<span class="json-number">'+m+'</span>';
  });
}
function compare(a,b){
  let av=a?.[sortKey], bv=b?.[sortKey];
  if(sortKey==='UpdatedAtUtc'){av=Date.parse(av||0);bv=Date.parse(bv||0)}
  else if(sortKey==='HttpStatusCode'||sortKey==='ClaimCount'){av=Number(av??-1);bv=Number(bv??-1)}
  else{av=String(av??'').toLowerCase();bv=String(bv??'').toLowerCase()}
  if(av<bv)return sortDir==='asc'?-1:1;if(av>bv)return sortDir==='asc'?1:-1;return 0;
}
function filtered(){
  const q=document.getElementById('search').value.trim().toLowerCase();
  const dest=document.getElementById('destination').value;
  return groupByBatch(items).filter(x=>(!dest||x.Destination===dest)&&(!q||[x.UniqueId,x.Status,x.Destination,x.HttpStatusCode,x.BusinessResponseCode,x.Reason,x.BatchId,x.ClaimCount].some(v=>String(v??'').toLowerCase().includes(q)))).sort(compare);
}
function renderSummary(list){
  const apim=list.filter(x=>x.Destination==='APIM').length, sb=list.filter(x=>x.Destination==='ServiceBus').length;
  const completed=list.filter(x=>x.Status==='Completed').length, buffered=list.filter(x=>x.Status==='Buffered').length;
  const rejected=list.filter(x=>x.Status==='Rejected').length;
  document.getElementById('summary').innerHTML=
    '<span class="pill">'+list.length+' batch(es) visible</span>'+
    '<span class="pill">'+apim+' APIM</span>'+
    '<span class="pill">'+sb+' Service Bus</span>'+
    '<span class="pill">'+completed+' completed</span>'+
    '<span class="pill">'+buffered+' buffered</span>'+
    '<span class="pill">'+rejected+' rejected</span>';
}
function render(){
  const list=filtered(); renderSummary(list);
  const tbody=document.getElementById('rows');
  if(!list.length){tbody.innerHTML='<tr><td colspan="8" class="empty">No matching requests</td></tr>';return}
  tbody.innerHTML=list.map((x,i)=>{
    const key='row-'+i+'-'+esc(x.RecordId);
    return '<tr class="data-row" onclick="toggleDetail(\''+key+'\',\''+esc(x.RecordId)+'\')">'+
      '<td>'+new Date(x.UpdatedAtUtc).toLocaleString()+'</td>'+
      '<td>'+esc(x.ClaimCount)+'</td>'+
      '<td class="id" title="'+esc(x.UniqueId)+'">'+esc(x.UniqueId)+'</td>'+
      '<td class="status-'+esc(x.Status)+'">'+esc(x.Status)+'</td>'+
      '<td><span class="tag '+esc(x.Destination)+'">'+esc(x.Destination)+'</span></td>'+
      '<td>'+esc(x.HttpStatusCode??'')+'</td>'+
      '<td>'+esc(x.BusinessResponseCode??'')+'</td>'+
      '<td>'+esc(x.Reason??'')+'</td></tr>'+
      '<tr id="'+key+'" class="expand-row"><td colspan="8" class="expand-cell"><div class="muted">Loading details...</div></td></tr>';
  }).join('');
}
function writebackReasonText(reason){
  const messages={
    EmptyApimResponse:'APIM returned an empty response body.',
    InvalidApimResponseShape:'APIM response was not the expected JSON array.',
    ApimResponseCountMismatch:'APIM returned a different number of results than the submitted claim count.',
    MissingCorrelationId:'At least one APIM response item did not contain a correlation ID.',
    UnexpectedCorrelationId:'APIM returned a correlation ID that was not part of the submitted batch.',
    DuplicateCorrelationId:'APIM returned the same correlation ID more than once in the batch.',
    CorrelationIdSetMismatch:'The returned correlation-ID set did not exactly match the submitted batch.',
    RtaStatusWritebackFailed:'The RTA status PUT failed after APIM response validation succeeded.',
    ResultWritebackFailed:'RTA result writeback failed. This older record did not capture a more specific cause.'
  };
  return messages[reason]||('Writeback failed: '+(reason||'Unknown reason'));
}
async function toggleDetail(rowId,recordId){
  const row=document.getElementById(rowId); const opening=!row.classList.contains('open');
  document.querySelectorAll('.expand-row.open').forEach(r=>r.classList.remove('open'));
  if(!opening)return; row.classList.add('open');
  let x=detailCache.get(recordId);
  if(!x){const r=await fetch('/requests/'+encodeURIComponent(recordId),{cache:'no-store'});if(!r.ok){row.querySelector('td').innerHTML='<div class="payload-note">Could not load request details.</div>';return}x=await r.json();detailCache.set(recordId,x)}
  const validationFailure=x.Status==='Rejected'&&x.Reason==='InvalidApimResponseToken'
    ? '<div class="payload-note"><strong>APIM response rejected:</strong> response-token validation failed. The response was not processed and no RTA writeback was attempted.</div><br>'
    : '';
  const writebackFailure=x.Status==='WritebackFailed'
    ? '<div class="payload-note"><strong>Writeback failed:</strong> '+esc(writebackReasonText(x.Reason))+'</div><br>'
    : '';
  const rtaPayloads=x.RtaResponsePayloads||[];
  const rtaPayloadPanels=rtaPayloads.length
    ? rtaPayloads.map((payload,index)=>'<div class="payload-panel"><div class="payload-title">Raw JSON received from RTA'+(rtaPayloads.length>1?' — page '+(index+1):'')+'</div><pre>'+syntaxHighlight(payload)+'</pre></div>').join('')
    : '<div class="payload-panel"><div class="payload-title">Raw JSON received from RTA</div><pre><span class="muted">No payload recorded</span></pre></div>';
  const rtaStatusPayloadPanel=x.RtaStatusPayload
    ? '<div class="payload-panel"><div class="payload-title">JSON sent back to RTA</div><pre>'+syntaxHighlight(x.RtaStatusPayload)+'</pre></div>'
    : '<div class="payload-panel"><div class="payload-title">JSON sent back to RTA</div><pre><span class="muted">No payload recorded</span></pre></div>';
  row.querySelector('td').innerHTML=
    validationFailure+
    writebackFailure+
    '<div class="detail-grid">'+
      '<div class="card"><div class="label">Batch ID</div><div class="id">'+esc(x.BatchId)+'</div></div>'+
      '<div class="card"><div class="label">Claims in Batch</div>'+esc(groupByBatch(items).find(b=>b.BatchId===x.BatchId)?.ClaimCount??1)+'</div>'+
      '<div class="card"><div class="label">Sent to APIM</div>'+(x.SentToApimAtUtc?new Date(x.SentToApimAtUtc).toLocaleString():'N/A')+'</div>'+
      '<div class="card"><div class="label">APIM Response</div>'+(x.ApimResponseAtUtc?new Date(x.ApimResponseAtUtc).toLocaleString():'N/A')+'</div>'+
      '<div class="card"><div class="label">Service Bus</div>'+(x.BufferedToServiceBusAtUtc?new Date(x.BufferedToServiceBusAtUtc).toLocaleString():'N/A')+'</div>'+
    '</div>'+
    (x.PayloadLoggingEnabled
      ? '<div class="payload-grid">'+rtaPayloadPanels+rtaStatusPayloadPanel+'<div class="payload-panel"><div class="payload-title">JSON sent to APIM</div><pre>'+syntaxHighlight(x.ApimRequestPayload)+'</pre></div><div class="payload-panel"><div class="payload-title">JSON received from APIM</div><pre>'+syntaxHighlight(x.ApimResponsePayload)+'</pre></div></div>'
      : '<div class="payload-note">Payload display is disabled because LOG_POLL_PAYLOADS=false.</div>');
}
async function load(){
  document.getElementById('status').textContent='Loading...'; detailCache.clear();
  const r=await fetch('/requests',{cache:'no-store'}); items=await r.json();
  document.getElementById('status').textContent=groupByBatch(items).length+' batch(es) loaded from '+items.length+' claim record(s)'; render();
}
document.querySelectorAll('th[data-key]').forEach(th=>th.addEventListener('click',()=>{const k=th.dataset.key;if(sortKey===k)sortDir=sortDir==='asc'?'desc':'asc';else{sortKey=k;sortDir='asc'}render()}));
load(); setInterval(load,30000);
</script>
</body>
</html>
""";
}

